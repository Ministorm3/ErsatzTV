using ErsatzTV.Core.Streaming;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Streaming;

[TestFixture]
public class MediaPlaylistParserTests
{
    [Test]
    public void Should_Parse_Filtered_Playlist_With_ProgramDateTime()
    {
        string[] lines =
        [
            "#EXTM3U",
            "#EXT-X-VERSION:7",
            "#EXT-X-TARGETDURATION:4",
            "#EXT-X-MEDIA-SEQUENCE:120",
            "#EXT-X-INDEPENDENT-SEGMENTS",
            "#EXTINF:4.000000,",
            "#EXT-X-PROGRAM-DATE-TIME:2026-01-01T10:00:00.000-0500",
            "live000120.ts",
            "#EXT-X-DISCONTINUITY",
            "#EXTINF:3.500000,",
            "#EXT-X-PROGRAM-DATE-TIME:2026-01-01T10:00:04.000-0500",
            "live000121.ts"
        ];

        ParsedMediaPlaylist playlist = MediaPlaylistParser.Parse(lines);

        playlist.TargetDuration.ShouldBe(4);
        playlist.MediaSequence.ShouldBe(120);
        playlist.Segments.Count.ShouldBe(2);

        playlist.Segments[0].Index.ShouldBe(120);
        playlist.Segments[0].Uri.ShouldBe("live000120.ts");
        playlist.Segments[0].Duration.ShouldBe(TimeSpan.FromSeconds(4));
        playlist.Segments[0].DiscontinuityBefore.ShouldBeFalse();
        playlist.Segments[0].ProgramDateTime.IfNone(DateTimeOffset.MinValue)
            .ShouldBe(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.FromHours(-5)));

        playlist.Segments[1].Index.ShouldBe(121);
        playlist.Segments[1].DiscontinuityBefore.ShouldBeTrue();
        playlist.Segments[1].Duration.ShouldBe(TimeSpan.FromSeconds(3.5));
    }

    [Test]
    public void Should_Parse_Raw_Playlist_Without_ProgramDateTime()
    {
        string[] lines =
        [
            "#EXTM3U",
            "#EXT-X-VERSION:7",
            "#EXT-X-TARGETDURATION:4",
            "#EXT-X-MEDIA-SEQUENCE:0",
            "#EXTINF:4.000000,",
            "live000000.ts",
            "#EXTINF:4.000000,",
            "live000001.ts",
            "#EXT-X-ENDLIST"
        ];

        ParsedMediaPlaylist playlist = MediaPlaylistParser.Parse(lines);

        playlist.Segments.Count.ShouldBe(2);
        playlist.Segments[0].Index.ShouldBe(0);
        playlist.Segments[0].ProgramDateTime.IsNone.ShouldBeTrue();
        playlist.Segments[1].Index.ShouldBe(1);
    }

    [Test]
    public void Should_Parse_Uri_With_Relative_Path()
    {
        string[] lines =
        [
            "#EXTINF:4.000000,",
            "../1_abcd1234/live000005.ts"
        ];

        ParsedMediaPlaylist playlist = MediaPlaylistParser.Parse(lines);

        playlist.Segments.Count.ShouldBe(1);
        playlist.Segments[0].Index.ShouldBe(5);
    }
}
