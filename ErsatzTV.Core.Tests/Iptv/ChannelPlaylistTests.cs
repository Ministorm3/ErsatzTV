using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Iptv;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Iptv;

/// <summary>
///     A viewer's parameters have to survive from the playlist url onto each channel url, or the
///     stream can never vary by audience. Nothing downstream can recover a parameter dropped here.
/// </summary>
[TestFixture]
public class ChannelPlaylistTests
{
    private static Channel Channel(StreamingMode streamingMode) =>
        new(Guid.Empty)
        {
            Number = "5",
            Name = "Channel Five",
            Group = "Group",
            StreamingMode = streamingMode,
            Artwork = [],
            FFmpegProfile = new FFmpegProfile
            {
                VideoFormat = FFmpegProfileVideoFormat.H264,
                AudioFormat = FFmpegProfileAudioFormat.Aac
            }
        };

    private static string ChannelUri(
        StreamingMode streamingMode,
        string accessToken,
        string forwardedQuery)
    {
        string m3u = new ChannelPlaylist(
            "http",
            "localhost:8409",
            string.Empty,
            [Channel(streamingMode)],
            string.Empty,
            accessToken,
            forwardedQuery).ToM3U();

        return m3u.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Last(line => line.Contains("/iptv/channel/", StringComparison.Ordinal))
            .Trim();
    }

    [Test]
    public void ShouldLeaveChannelUrisAloneWhenNothingWasForwarded()
    {
        ChannelUri(StreamingMode.HttpLiveStreamingSegmenter, null, null)
            .ShouldBe("http://localhost:8409/iptv/channel/5.m3u8?mode=segmenter");
    }

    [Test]
    public void ShouldCarryForwardedParametersOntoChannelUris()
    {
        ChannelUri(StreamingMode.HttpLiveStreamingSegmenter, null, "zip=15216")
            .ShouldBe("http://localhost:8409/iptv/channel/5.m3u8?mode=segmenter&zip=15216");
    }

    [Test]
    public void ShouldKeepBothTheAccessTokenAndTheForwardedParameters()
    {
        ChannelUri(StreamingMode.HttpLiveStreamingSegmenter, "abc", "zip=15216")
            .ShouldBe("http://localhost:8409/iptv/channel/5.m3u8?mode=segmenter&access_token=abc&zip=15216");
    }

    /// <summary>
    ///     This mode is the only one whose url carries no mode, so without an access token it has
    ///     no query string yet and the forwarded parameters have to open one.
    /// </summary>
    [Test]
    public void ShouldOpenTheQueryStringWhenTheUriHasNoneYet()
    {
        ChannelUri(StreamingMode.TransportStreamHybrid, null, "zip=15216")
            .ShouldBe("http://localhost:8409/iptv/channel/5.ts?zip=15216");
    }

    [Test]
    public void ShouldAppendToTheQueryStringAnAccessTokenAlreadyOpened()
    {
        ChannelUri(StreamingMode.TransportStreamHybrid, "abc", "zip=15216")
            .ShouldBe("http://localhost:8409/iptv/channel/5.ts?access_token=abc&zip=15216");
    }
}
