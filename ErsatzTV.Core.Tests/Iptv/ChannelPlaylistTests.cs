using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Iptv;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Iptv;

[TestFixture]
public class ChannelPlaylistTests
{
    [Test]
    public void Should_Not_Append_Empty_Extra_Query()
    {
        ChannelPlaylist playlist = PlaylistFor(StreamingMode.HttpLiveStreamingSegmenter, null, null);

        string result = playlist.ToM3U();

        result.ShouldContain("/iptv/channel/1.m3u8?mode=segmenter");
        result.ShouldNotContain("mode=segmenter&");
    }

    [Test]
    public void Should_Append_Extra_Query_To_Existing_Query()
    {
        ChannelPlaylist playlist = PlaylistFor(StreamingMode.HttpLiveStreamingSegmenter, null, "region=midwest");

        string result = playlist.ToM3U();

        result.ShouldContain("/iptv/channel/1.m3u8?mode=segmenter&region=midwest");
    }

    [Test]
    public void Should_Append_Extra_Query_Without_Existing_Query()
    {
        ChannelPlaylist playlist = PlaylistFor(StreamingMode.TransportStreamHybrid, null, "region=midwest");

        string result = playlist.ToM3U();

        result.ShouldContain("/iptv/channel/1.ts?region=midwest");
    }

    [Test]
    public void Should_Append_Extra_Query_After_Access_Token()
    {
        ChannelPlaylist playlist = PlaylistFor(StreamingMode.TransportStreamHybrid, "token", "region=midwest");

        string result = playlist.ToM3U();

        result.ShouldContain("/iptv/channel/1.ts?access_token=token&region=midwest");
    }

    private static ChannelPlaylist PlaylistFor(StreamingMode streamingMode, string accessToken, string extraQuery)
    {
        var channel = new Channel(Guid.Empty)
        {
            Number = "1",
            Name = "Test Channel",
            Group = "Test Group",
            StreamingMode = streamingMode,
            FFmpegProfile = new FFmpegProfile
            {
                VideoFormat = FFmpegProfileVideoFormat.H264,
                AudioFormat = FFmpegProfileAudioFormat.Aac
            }
        };

        return new ChannelPlaylist(
            "http",
            "localhost",
            string.Empty,
            [channel],
            userAgent: null,
            accessToken,
            extraQuery);
    }
}
