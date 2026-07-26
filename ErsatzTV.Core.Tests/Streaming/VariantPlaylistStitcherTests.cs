using System.Globalization;
using ErsatzTV.Core.Streaming;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Streaming;

[TestFixture]
public class VariantPlaylistStitcherTests
{
    private static readonly DateTimeOffset WindowStart = new(2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(-5));

    private static readonly StreamVariantWindow Window = new(WindowStart, WindowStart + TimeSpan.FromSeconds(136));

    private const string VariantPrefix = "../1_abcd1234/";

    private static readonly TimeSpan SegmentDuration = TimeSpan.FromSeconds(4);

    // base segment 108 begins the window's item (34 segments of 4s = 2:16) and
    // segment 142 begins the following item; both boundaries carry the base
    // playlist's own discontinuity, like real item transitions do.
    // clockDrift models a long-running session whose playlist timestamps have
    // drifted ahead of the schedule.
    private static ParsedMediaPlaylist BasePlaylist(long firstIndex, long lastIndex, TimeSpan? clockDrift = null)
    {
        TimeSpan drift = clockDrift ?? TimeSpan.Zero;
        List<ParsedMediaSegment> segments = [];
        for (long i = firstIndex; i <= lastIndex; i++)
        {
            DateTimeOffset pdt = WindowStart + SegmentDuration * (i - 108) + drift;
            segments.Add(
                new ParsedMediaSegment(
                    i,
                    $"live{i:000000}.ts",
                    "#EXTINF:4.000000,",
                    SegmentDuration,
                    pdt,
                    DiscontinuityBefore: i is 108 or 142));
        }

        return new ParsedMediaPlaylist(4, firstIndex, segments);
    }

    private static ParsedMediaPlaylist VariantPlaylist(long lastIndex)
    {
        List<ParsedMediaSegment> segments = [];
        for (long i = 0; i <= lastIndex; i++)
        {
            segments.Add(
                new ParsedMediaSegment(
                    i,
                    $"live{i:000000}.ts",
                    "#EXTINF:4.000000,",
                    SegmentDuration,
                    Option<DateTimeOffset>.None,
                    DiscontinuityBefore: false));
        }

        return new ParsedMediaPlaylist(4, 0, segments);
    }

    private static ParsedMediaPlaylist Reparse(string playlist) => MediaPlaylistParser.Parse(playlist.Split('\n'));

    private static long MediaSequenceOf(string playlist) => Reparse(playlist).MediaSequence;

    private static int DiscontinuitySequenceOf(string playlist)
    {
        foreach (string line in playlist.Split('\n'))
        {
            if (line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
            {
                return int.Parse(line.Split(':')[1], CultureInfo.InvariantCulture);
            }
        }

        return 0;
    }

    [Test]
    public void NoWindow_Should_PassThroughBaseSegments()
    {
        var state = new VariantStitchState();

        string playlist = VariantPlaylistStitcher.Stitch(
            state,
            BasePlaylist(100, 107),
            Option<ParsedMediaPlaylist>.None,
            Option<StreamVariantWindow>.None,
            variantDone: false,
            VariantPrefix,
            WindowStart - TimeSpan.FromSeconds(4));

        ParsedMediaPlaylist reparsed = Reparse(playlist);
        reparsed.MediaSequence.ShouldBe(0);
        reparsed.Segments.Count.ShouldBe(8);
        reparsed.Segments[0].Uri.ShouldBe("live000100.ts");
        reparsed.Segments.All(s => s.DiscontinuityBefore).ShouldBeFalse();
    }

    [Test]
    public void Stitch_Should_BeIdempotent_WithoutNewInput()
    {
        var state = new VariantStitchState();

        string first = VariantPlaylistStitcher.Stitch(
            state,
            BasePlaylist(100, 107),
            Option<ParsedMediaPlaylist>.None,
            Option<StreamVariantWindow>.None,
            variantDone: false,
            VariantPrefix,
            WindowStart - TimeSpan.FromSeconds(4));

        string second = VariantPlaylistStitcher.Stitch(
            state,
            BasePlaylist(100, 107),
            Option<ParsedMediaPlaylist>.None,
            Option<StreamVariantWindow>.None,
            variantDone: false,
            VariantPrefix,
            WindowStart - TimeSpan.FromSeconds(2));

        second.ShouldBe(first);
    }

    [Test]
    public void InWindow_Should_SubstituteVariantSegments()
    {
        var state = new VariantStitchState();

        // poll 1: before the window
        VariantPlaylistStitcher.Stitch(
            state,
            BasePlaylist(100, 107),
            Option<ParsedMediaPlaylist>.None,
            Window,
            variantDone: false,
            VariantPrefix,
            WindowStart - TimeSpan.FromSeconds(4));

        // poll 2: inside the window; base has produced its own in-window segments
        string playlist = VariantPlaylistStitcher.Stitch(
            state,
            BasePlaylist(100, 110),
            VariantPlaylist(1),
            Window,
            variantDone: false,
            VariantPrefix,
            WindowStart + TimeSpan.FromSeconds(8));

        ParsedMediaPlaylist reparsed = Reparse(playlist);
        reparsed.Segments.Count.ShouldBe(10);
        reparsed.Segments.Count(s => s.Uri.StartsWith(VariantPrefix, StringComparison.Ordinal)).ShouldBe(2);
        reparsed.Segments.Any(s => s.Uri == "live000108.ts").ShouldBeFalse();

        // discontinuity precedes the first variant segment
        ParsedMediaSegment firstVariant =
            reparsed.Segments.First(s => s.Uri.StartsWith(VariantPrefix, StringComparison.Ordinal));
        firstVariant.DiscontinuityBefore.ShouldBeTrue();

        // variant program date time is anchored to the base playlist's boundary
        firstVariant.ProgramDateTime.IfNone(DateTimeOffset.MinValue).ShouldBe(WindowStart);
    }

    [Test]
    public void PostWindow_Should_WaitForVariantFlush_ThenResumeBase()
    {
        var state = new VariantStitchState();

        VariantPlaylistStitcher.Stitch(
            state,
            BasePlaylist(100, 107),
            Option<ParsedMediaPlaylist>.None,
            Window,
            variantDone: false,
            VariantPrefix,
            WindowStart - TimeSpan.FromSeconds(4));

        // base has moved past the window; variant still has segments pending
        string waiting = VariantPlaylistStitcher.Stitch(
            state,
            BasePlaylist(100, 143),
            VariantPlaylist(33),
            Window,
            variantDone: false,
            VariantPrefix,
            Window.Finish + TimeSpan.FromSeconds(2));

        Reparse(waiting).Segments.Any(s => s.Uri == "live000142.ts").ShouldBeFalse();

        // once the variant is done, base content resumes with a discontinuity
        string resumed = VariantPlaylistStitcher.Stitch(
            state,
            BasePlaylist(100, 143),
            VariantPlaylist(33),
            Window,
            variantDone: true,
            VariantPrefix,
            Window.Finish + TimeSpan.FromSeconds(4));

        ParsedMediaPlaylist reparsed = Reparse(resumed);
        ParsedMediaSegment resumedSegment = reparsed.Segments.First(s => s.Uri == "live000142.ts");
        resumedSegment.DiscontinuityBefore.ShouldBeTrue();
    }

    [Test]
    public void Sequence_Should_BeMonotonic_AndHistoryStable_AcrossPolls()
    {
        var state = new VariantStitchState();
        var renders = new List<string>
        {
            VariantPlaylistStitcher.Stitch(
                state,
                BasePlaylist(100, 107),
                Option<ParsedMediaPlaylist>.None,
                Window,
                variantDone: false,
                VariantPrefix,
                WindowStart - TimeSpan.FromSeconds(4)),
            VariantPlaylistStitcher.Stitch(
                state,
                BasePlaylist(100, 112),
                VariantPlaylist(3),
                Window,
                variantDone: false,
                VariantPrefix,
                WindowStart + TimeSpan.FromSeconds(16)),
            VariantPlaylistStitcher.Stitch(
                state,
                BasePlaylist(104, 143),
                VariantPlaylist(33),
                Window,
                variantDone: true,
                VariantPrefix,
                Window.Finish + TimeSpan.FromSeconds(4))
        };

        long lastSequence = -1;
        List<(long Sequence, string Uri)> previous = null;

        foreach (string render in renders)
        {
            ParsedMediaPlaylist reparsed = Reparse(render);
            reparsed.MediaSequence.ShouldBeGreaterThanOrEqualTo(lastSequence);
            lastSequence = reparsed.MediaSequence;

            List<(long, string)> current = reparsed.Segments
                .Select((s, i) => (reparsed.MediaSequence + i, s.Uri))
                .ToList();

            if (previous is not null)
            {
                // any sequence number present in both renders maps to the same uri
                foreach ((long sequence, string uri) in current)
                {
                    foreach ((long previousSequence, string previousUri) in previous)
                    {
                        if (sequence == previousSequence)
                        {
                            uri.ShouldBe(previousUri);
                        }
                    }
                }
            }

            previous = current;
        }
    }

    [Test]
    public void Trim_Should_AdvanceMediaSequence_AndDiscontinuitySequence()
    {
        var state = new VariantStitchState();

        VariantPlaylistStitcher.Stitch(
            state,
            BasePlaylist(100, 107),
            Option<ParsedMediaPlaylist>.None,
            Window,
            variantDone: false,
            VariantPrefix,
            WindowStart - TimeSpan.FromSeconds(4));

        // 6 base (last two are held awaiting the boundary and scroll away in
        // this synthetic jump) + 34 variant + 2 base appended; base playlist
        // window is 16 segments, so the head (including the splice
        // discontinuity) must roll off
        string playlist = VariantPlaylistStitcher.Stitch(
            state,
            BasePlaylist(128, 143),
            VariantPlaylist(33),
            Window,
            variantDone: true,
            VariantPrefix,
            Window.Finish + TimeSpan.FromSeconds(4));

        ParsedMediaPlaylist reparsed = Reparse(playlist);
        reparsed.Segments.Count.ShouldBe(16);
        MediaSequenceOf(playlist).ShouldBe(26); // 42 appended, 16 kept
        DiscontinuitySequenceOf(playlist).ShouldBe(1); // splice-in discontinuity rolled off
    }

    [Test]
    public void StalledVariant_Should_FallBackToBaseContent()
    {
        var state = new VariantStitchState();

        // well past the window start with no variant segments at all
        string playlist = VariantPlaylistStitcher.Stitch(
            state,
            BasePlaylist(100, 112),
            Option<ParsedMediaPlaylist>.None,
            Window,
            variantDone: false,
            VariantPrefix,
            WindowStart + TimeSpan.FromSeconds(20));

        ParsedMediaPlaylist reparsed = Reparse(playlist);

        // base in-window segments are served as fallback
        reparsed.Segments.Any(s => s.Uri == "live000108.ts").ShouldBeTrue();
        reparsed.Segments.Any(s => s.Uri.StartsWith(VariantPrefix, StringComparison.Ordinal)).ShouldBeFalse();
    }

    [Test]
    public void MidWindowJoin_Should_ServeVariantWithMonotonicProgramDateTime()
    {
        var state = new VariantStitchState();

        // fresh viewer joins one minute into the window; the visible base
        // playlist is entirely in-window content with no boundary in view
        string playlist = VariantPlaylistStitcher.Stitch(
            state,
            BasePlaylist(115, 122),
            VariantPlaylist(14),
            Window,
            variantDone: false,
            VariantPrefix,
            WindowStart + TimeSpan.FromSeconds(60));

        ParsedMediaPlaylist reparsed = Reparse(playlist);
        reparsed.Segments.All(s => s.Uri.StartsWith(VariantPrefix, StringComparison.Ordinal)).ShouldBeTrue();

        // program date times advance by exactly one segment duration
        DateTimeOffset? previous = null;
        foreach (ParsedMediaSegment segment in reparsed.Segments)
        {
            DateTimeOffset pdt = segment.ProgramDateTime.IfNone(DateTimeOffset.MinValue);
            if (previous is not null)
            {
                (pdt - previous.Value).ShouldBe(SegmentDuration);
            }

            previous = pdt;
        }
    }

    [Test]
    public void WindowRollover_Should_NotReplayStaleVariantContent()
    {
        var state = new VariantStitchState();

        // a full splice cycle completes
        VariantPlaylistStitcher.Stitch(
            state,
            BasePlaylist(100, 107),
            Option<ParsedMediaPlaylist>.None,
            Window,
            variantDone: false,
            VariantPrefix,
            WindowStart - TimeSpan.FromSeconds(4));
        VariantPlaylistStitcher.Stitch(
            state,
            BasePlaylist(100, 143),
            VariantPlaylist(33),
            Window,
            variantDone: true,
            VariantPrefix,
            Window.Finish + TimeSpan.FromSeconds(4));

        // the schedule lookup rolls to the NEXT window (far in the future)
        // while the finished variant playlist is still supplied; the show is
        // ~15 seconds in
        var nextWindow = new StreamVariantWindow(
            WindowStart + TimeSpan.FromMinutes(25),
            WindowStart + TimeSpan.FromMinutes(25) + TimeSpan.FromSeconds(136));

        string afterRollover = VariantPlaylistStitcher.Stitch(
            state,
            BasePlaylist(104, 147),
            VariantPlaylist(33),
            nextWindow,
            variantDone: true,
            VariantPrefix,
            Window.Finish + TimeSpan.FromSeconds(14));

        // no stale variant segments are re-appended after the show resumed
        ParsedMediaPlaylist reparsed = Reparse(afterRollover);
        int lastVariantPosition = -1;
        int firstResumedBasePosition = -1;
        for (var i = 0; i < reparsed.Segments.Count; i++)
        {
            if (reparsed.Segments[i].Uri.StartsWith(VariantPrefix, StringComparison.Ordinal))
            {
                lastVariantPosition = i;
            }
            else if (reparsed.Segments[i].Uri == "live000142.ts")
            {
                firstResumedBasePosition = i;
            }
        }

        if (lastVariantPosition >= 0 && firstResumedBasePosition >= 0)
        {
            lastVariantPosition.ShouldBeLessThan(firstResumedBasePosition);
        }

        // and the playlist keeps advancing with base content
        reparsed.Segments.Any(s => s.Uri == "live000147.ts").ShouldBeTrue();
    }

    [Test]
    public void DriftedBaseClock_Should_SpliceOnItemBoundaries()
    {
        // a long-running session whose playlist timestamps run 6s ahead of the
        // schedule; timestamp-based classification would misplace the last
        // in-window base segments after the window and replay them at splice-out
        TimeSpan drift = TimeSpan.FromSeconds(6);
        var state = new VariantStitchState();
        var renders = new List<string>
        {
            // before the window: segments near the boundary are held until the
            // base playlist shows its discontinuity
            VariantPlaylistStitcher.Stitch(
                state,
                BasePlaylist(100, 107, drift),
                Option<ParsedMediaPlaylist>.None,
                Window,
                variantDone: false,
                VariantPrefix,
                WindowStart - TimeSpan.FromSeconds(4)),

            // boundary visible; held segments resolve as pre-window content
            VariantPlaylistStitcher.Stitch(
                state,
                BasePlaylist(100, 112, drift),
                VariantPlaylist(2),
                Window,
                variantDone: false,
                VariantPrefix,
                WindowStart + TimeSpan.FromSeconds(12)),

            // window over: base resumes at the item boundary, not by timestamp
            VariantPlaylistStitcher.Stitch(
                state,
                BasePlaylist(104, 145, drift),
                VariantPlaylist(33),
                Window,
                variantDone: true,
                VariantPrefix,
                Window.Finish + TimeSpan.FromSeconds(6))
        };

        // the drifted in-window tail (140, 141) is never replayed after the splice
        foreach (string render in renders)
        {
            render.ShouldNotContain("live000140.ts");
            render.ShouldNotContain("live000141.ts");
        }

        // every pre-window segment was eventually served
        string all = string.Join('\n', renders);
        foreach (long i in new long[] { 100, 101, 102, 103, 104, 105, 106, 107 })
        {
            all.ShouldContain($"live{i:000000}.ts");
        }

        // base resumes exactly at the next item's first segment, with a discontinuity
        ParsedMediaPlaylist final = Reparse(renders[^1]);
        ParsedMediaSegment resumed = final.Segments.First(s => s.Uri == "live000142.ts");
        resumed.DiscontinuityBefore.ShouldBeTrue();

        // and the variant's timestamps anchor to the base playlist clock (start + drift)
        ParsedMediaPlaylist mid = Reparse(renders[1]);
        ParsedMediaSegment firstVariant =
            mid.Segments.First(s => s.Uri.StartsWith(VariantPrefix, StringComparison.Ordinal));
        firstVariant.ProgramDateTime.IfNone(DateTimeOffset.MinValue).ShouldBe(WindowStart + drift);
    }
}
