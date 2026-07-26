using System.Globalization;
using System.Text.RegularExpressions;

namespace ErsatzTV.Core.Streaming;

public record ParsedMediaPlaylist(int TargetDuration, long MediaSequence, List<ParsedMediaSegment> Segments);

public record ParsedMediaSegment(
    long Index,
    string Uri,
    string ExtInf,
    TimeSpan Duration,
    Option<DateTimeOffset> ProgramDateTime,
    bool DiscontinuityBefore);

public static partial class MediaPlaylistParser
{
    public static ParsedMediaPlaylist Parse(string[] lines)
    {
        var targetDuration = 0;
        long mediaSequence = 0;
        List<ParsedMediaSegment> segments = [];

        var pendingDiscontinuity = false;
        string pendingExtInf = null;
        TimeSpan pendingDuration = TimeSpan.Zero;
        Option<DateTimeOffset> pendingProgramDateTime = Option<DateTimeOffset>.None;

        foreach (string rawLine in lines)
        {
            string line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.OrdinalIgnoreCase))
            {
                _ = int.TryParse(line.Split(':')[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out targetDuration);
            }
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.OrdinalIgnoreCase))
            {
                _ = long.TryParse(line.Split(':')[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out mediaSequence);
            }
            else if (line.Equals("#EXT-X-DISCONTINUITY", StringComparison.OrdinalIgnoreCase))
            {
                pendingDiscontinuity = true;
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                pendingExtInf = line;
                string value = line["#EXTINF:".Length..].TrimEnd(',').Split(',')[0];
                if (decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out decimal duration))
                {
                    pendingDuration = TimeSpan.FromTicks((long)(duration * TimeSpan.TicksPerSecond));
                }
            }
            else if (line.StartsWith("#EXT-X-PROGRAM-DATE-TIME:", StringComparison.OrdinalIgnoreCase))
            {
                pendingProgramDateTime = ParseProgramDateTime(line["#EXT-X-PROGRAM-DATE-TIME:".Length..]);
            }
            else if (!line.StartsWith('#'))
            {
                if (pendingExtInf is not null)
                {
                    segments.Add(
                        new ParsedMediaSegment(
                            IndexForUri(line),
                            line,
                            pendingExtInf,
                            pendingDuration,
                            pendingProgramDateTime,
                            pendingDiscontinuity));
                }

                pendingDiscontinuity = false;
                pendingExtInf = null;
                pendingDuration = TimeSpan.Zero;
                pendingProgramDateTime = Option<DateTimeOffset>.None;
            }
        }

        return new ParsedMediaPlaylist(targetDuration, mediaSequence, segments);
    }

    private static long IndexForUri(string uri)
    {
        Match match = SegmentIndexPattern().Match(uri);
        return match.Success
            ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture)
            : -1;
    }

    private static Option<DateTimeOffset> ParseProgramDateTime(string value)
    {
        // normalize a compact utc offset (e.g. -0500) to one with a separator
        string normalized = CompactOffsetPattern().Replace(value.Trim(), "$1:$2");
        return DateTimeOffset.TryParse(
            normalized,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTimeOffset result)
            ? result
            : Option<DateTimeOffset>.None;
    }

    [GeneratedRegex(@"(\d+)\.\w+$")]
    private static partial Regex SegmentIndexPattern();

    [GeneratedRegex(@"([+-]\d{2})(\d{2})$")]
    private static partial Regex CompactOffsetPattern();
}
