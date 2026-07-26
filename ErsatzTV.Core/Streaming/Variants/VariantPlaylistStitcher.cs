using System.Globalization;
using System.Text;

namespace ErsatzTV.Core.Streaming;

public record StitchedEntry(string Uri, string ExtInf, DateTimeOffset ProgramDateTime, bool DiscontinuityBefore);

public class VariantStitchState
{
    public List<StitchedEntry> Entries { get; } = [];
    public long NextSequence { get; set; }
    public int DiscontinuitySequence { get; set; }
    public int TargetDuration { get; set; } = 4;
    public long LastBaseIndex { get; set; } = -1;
    public long LastVariantIndex { get; set; } = -1;
    public bool LastAppendWasVariant { get; set; }
    public DateTimeOffset? ActiveWindowStart { get; set; }
    public DateTimeOffset? SpliceAnchor { get; set; }
    public TimeSpan VariantElapsed { get; set; }
    public bool WindowFailed { get; set; }
}

public static class VariantPlaylistStitcher
{
    // if the variant source has produced nothing this long after the window opens,
    // fall back to the base content for the remainder of the window
    private static readonly TimeSpan VariantStartGrace = TimeSpan.FromSeconds(15);

    // if the variant source has not finished this long after the window closes,
    // resume base content anyway so the playlist keeps advancing
    private static readonly TimeSpan WindowEndGrace = TimeSpan.FromSeconds(15);

    // near the window start, wait for the base playlist's item boundary
    // (discontinuity) to appear rather than classifying by timestamp alone;
    // the base session's playlist clock can drift from the schedule
    private static readonly TimeSpan PreBoundaryHold = TimeSpan.FromSeconds(8);

    // a window only takes effect near its start time; after one window ends,
    // the schedule lookup rolls to a future window, and that rollover must not
    // reset per-window state (or replay a finished variant) while the playlist
    // is still serving ordinary content
    private static readonly TimeSpan ActivationLead = TimeSpan.FromSeconds(30);

    private const int MinimumSegmentCount = 6;

    public static string Stitch(
        VariantStitchState state,
        ParsedMediaPlaylist basePlaylist,
        Option<ParsedMediaPlaylist> maybeVariantPlaylist,
        Option<StreamVariantWindow> maybeWindow,
        bool variantDone,
        string variantUriPrefix,
        DateTimeOffset now)
    {
        state.TargetDuration = Math.Max(state.TargetDuration, basePlaylist.TargetDuration);
        foreach (ParsedMediaPlaylist variantPlaylist in maybeVariantPlaylist)
        {
            state.TargetDuration = Math.Max(state.TargetDuration, variantPlaylist.TargetDuration);
        }

        StreamVariantWindow window = maybeWindow.IfNoneUnsafe((StreamVariantWindow)null);
        ParsedMediaPlaylist variant = maybeVariantPlaylist.IfNoneUnsafe((ParsedMediaPlaylist)null);

        if (window is not null && now < window.Start - ActivationLead)
        {
            window = null;
        }

        if (window is not null && state.ActiveWindowStart != window.Start)
        {
            state.ActiveWindowStart = window.Start;
            state.LastVariantIndex = -1;
            state.VariantElapsed = TimeSpan.Zero;
            state.WindowFailed = false;
            state.SpliceAnchor = null;
        }

        // group the base playlist into periods delimited by its own discontinuities
        // (item transitions); the period overlapping the window is the content the
        // variant replaces, and its edges are the authoritative splice points
        int[] periodOf = AssignPeriods(basePlaylist);
        (int FirstTarget, int LastTarget)? target = FindTargetPeriods(basePlaylist, periodOf, window);

        // chronological order within a poll: base before the window, variant
        // content inside the window, base after the window
        AppendBaseSegments(state, basePlaylist, periodOf, target, variant, window, variantDone, now);
        AppendVariantSegments(state, variant, window, variantUriPrefix, now);
        AppendBaseSegments(state, basePlaylist, periodOf, target, variant, window, variantDone, now);

        Trim(state, Math.Max(basePlaylist.Segments.Count, MinimumSegmentCount));

        return Render(state);
    }

    private static int[] AssignPeriods(ParsedMediaPlaylist basePlaylist)
    {
        var periods = new int[basePlaylist.Segments.Count];
        var period = 0;
        for (var i = 0; i < basePlaylist.Segments.Count; i++)
        {
            if (i > 0 && basePlaylist.Segments[i].DiscontinuityBefore)
            {
                period++;
            }

            periods[i] = period;
        }

        return periods;
    }

    private static (int FirstTarget, int LastTarget)? FindTargetPeriods(
        ParsedMediaPlaylist basePlaylist,
        int[] periodOf,
        StreamVariantWindow window)
    {
        if (window is null || basePlaylist.Segments.Count == 0)
        {
            return null;
        }

        int firstTarget = int.MaxValue;
        int lastTarget = int.MinValue;

        int periodCount = periodOf.Length > 0 ? periodOf[^1] + 1 : 0;
        for (var period = 0; period < periodCount; period++)
        {
            DateTimeOffset? start = null;
            DateTimeOffset? end = null;
            for (var i = 0; i < basePlaylist.Segments.Count; i++)
            {
                if (periodOf[i] != period)
                {
                    continue;
                }

                foreach (DateTimeOffset pdt in basePlaylist.Segments[i].ProgramDateTime)
                {
                    start ??= pdt;
                    end = pdt + basePlaylist.Segments[i].Duration;
                }
            }

            if (start is null || end is null)
            {
                continue;
            }

            DateTimeOffset overlapStart = start.Value > window.Start ? start.Value : window.Start;
            DateTimeOffset overlapEnd = end.Value < window.Finish ? end.Value : window.Finish;
            TimeSpan overlap = overlapEnd - overlapStart;
            if (overlap <= TimeSpan.Zero)
            {
                continue;
            }

            TimeSpan periodSpan = end.Value - start.Value;
            TimeSpan windowSpan = window.Finish - window.Start;
            bool qualifies = overlap >= periodSpan / 2 || overlap >= windowSpan / 2;
            if (qualifies)
            {
                firstTarget = Math.Min(firstTarget, period);
                lastTarget = Math.Max(lastTarget, period);
            }
        }

        return firstTarget is int.MaxValue ? null : (firstTarget, lastTarget);
    }

    private static void AppendBaseSegments(
        VariantStitchState state,
        ParsedMediaPlaylist basePlaylist,
        int[] periodOf,
        (int FirstTarget, int LastTarget)? target,
        ParsedMediaPlaylist variant,
        StreamVariantWindow window,
        bool variantDone,
        DateTimeOffset now)
    {
        for (var i = 0; i < basePlaylist.Segments.Count; i++)
        {
            ParsedMediaSegment segment = basePlaylist.Segments[i];
            if (segment.Index <= state.LastBaseIndex)
            {
                continue;
            }

            var inWindow = false;
            var postWindow = false;
            if (window is not null && !state.WindowFailed)
            {
                if (target is not null)
                {
                    inWindow = periodOf[i] >= target.Value.FirstTarget && periodOf[i] <= target.Value.LastTarget;
                    postWindow = periodOf[i] > target.Value.LastTarget;
                }
                else if (now < window.Finish + WindowEndGrace)
                {
                    // the base playlist has not yet shown the item boundary; hold
                    // segments near the window start until it appears, since the
                    // playlist clock may be offset from the schedule
                    var hold = false;
                    foreach (DateTimeOffset programDateTime in segment.ProgramDateTime)
                    {
                        DateTimeOffset midpoint = programDateTime + segment.Duration / 2;
                        hold = midpoint >= window.Start - PreBoundaryHold;
                    }

                    if (hold)
                    {
                        break;
                    }
                }
            }

            if (inWindow)
            {
                if (VariantHasStalled(state, variant, variantDone, window, now))
                {
                    // no variant content is coming; play base content instead
                    state.WindowFailed = true;
                }
                else
                {
                    // this base segment is replaced by variant content; its
                    // timestamp anchors the variant to the base playlist clock
                    foreach (DateTimeOffset programDateTime in segment.ProgramDateTime)
                    {
                        state.SpliceAnchor ??= programDateTime;
                    }

                    state.LastBaseIndex = segment.Index;
                    continue;
                }
            }

            if (postWindow && !VariantIsFlushed(state, variant, variantDone, window, now))
            {
                // wait for the remaining variant segments before resuming base content
                break;
            }

            Append(
                state,
                segment.Uri,
                segment.ExtInf,
                segment.ProgramDateTime.IfNone(now),
                segment.DiscontinuityBefore || state.LastAppendWasVariant,
                isVariant: false);

            state.LastBaseIndex = segment.Index;
        }
    }

    private static void AppendVariantSegments(
        VariantStitchState state,
        ParsedMediaPlaylist variant,
        StreamVariantWindow window,
        string variantUriPrefix,
        DateTimeOffset now)
    {
        if (variant is null || window is null || state.WindowFailed)
        {
            return;
        }

        // never serve variant content before its window has begun
        if (now < window.Start - TimeSpan.FromSeconds(5))
        {
            return;
        }

        foreach (ParsedMediaSegment segment in variant.Segments)
        {
            if (segment.Index <= state.LastVariantIndex)
            {
                continue;
            }

            DateTimeOffset anchor = state.SpliceAnchor ?? window.Start;
            DateTimeOffset programDateTime = anchor + state.VariantElapsed;
            state.VariantElapsed += segment.Duration;

            Append(
                state,
                variantUriPrefix + segment.Uri,
                segment.ExtInf,
                programDateTime,
                segment.DiscontinuityBefore || !state.LastAppendWasVariant,
                isVariant: true);

            state.LastVariantIndex = segment.Index;
        }
    }

    private static bool VariantHasStalled(
        VariantStitchState state,
        ParsedMediaPlaylist variant,
        bool variantDone,
        StreamVariantWindow window,
        DateTimeOffset now)
    {
        bool nothingAppended = state.LastVariantIndex < 0;
        bool nothingAvailable = variant is null || variant.Segments.Count == 0;

        if (!nothingAppended || !nothingAvailable)
        {
            return false;
        }

        return variantDone || now > window.Start + VariantStartGrace;
    }

    private static bool VariantIsFlushed(
        VariantStitchState state,
        ParsedMediaPlaylist variant,
        bool variantDone,
        StreamVariantWindow window,
        DateTimeOffset now)
    {
        if (now > window.Finish + WindowEndGrace)
        {
            return true;
        }

        bool allAppended = variant is null || variant.Segments.All(s => s.Index <= state.LastVariantIndex);
        return variantDone && allAppended;
    }

    private static void Append(
        VariantStitchState state,
        string uri,
        string extInf,
        DateTimeOffset programDateTime,
        bool discontinuityBefore,
        bool isVariant)
    {
        state.Entries.Add(new StitchedEntry(uri, extInf, programDateTime, discontinuityBefore));
        state.NextSequence++;
        state.LastAppendWasVariant = isVariant;
    }

    private static void Trim(VariantStitchState state, int maxSegments)
    {
        while (state.Entries.Count > maxSegments)
        {
            if (state.Entries[0].DiscontinuityBefore)
            {
                state.DiscontinuitySequence++;
            }

            state.Entries.RemoveAt(0);
        }
    }

    private static string Render(VariantStitchState state)
    {
        var output = new StringBuilder();
        output.AppendLine("#EXTM3U");
        output.AppendLine("#EXT-X-VERSION:7");
        output.AppendLine(CultureInfo.InvariantCulture, $"#EXT-X-TARGETDURATION:{state.TargetDuration}");
        output.AppendLine(
            CultureInfo.InvariantCulture,
            $"#EXT-X-MEDIA-SEQUENCE:{state.NextSequence - state.Entries.Count}");

        if (state.DiscontinuitySequence > 0)
        {
            output.AppendLine(
                CultureInfo.InvariantCulture,
                $"#EXT-X-DISCONTINUITY-SEQUENCE:{state.DiscontinuitySequence}");
        }

        output.AppendLine("#EXT-X-INDEPENDENT-SEGMENTS");

        foreach (StitchedEntry entry in state.Entries)
        {
            if (entry.DiscontinuityBefore)
            {
                output.AppendLine("#EXT-X-DISCONTINUITY");
            }

            output.AppendLine(entry.ExtInf);
            string offset = entry.ProgramDateTime
                .ToString("zzz", CultureInfo.InvariantCulture)
                .Replace(":", string.Empty);
            output.AppendLine(
                CultureInfo.InvariantCulture,
                $"#EXT-X-PROGRAM-DATE-TIME:{entry.ProgramDateTime:yyyy-MM-ddTHH:mm:ss.fff}{offset}");
            output.AppendLine(entry.Uri);
        }

        return output.ToString();
    }
}
