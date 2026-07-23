using ErsatzTV.Core.FFmpeg;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.FFmpeg;

[TestFixture]
public class ConcatPlaylistTests
{
    [Test]
    public void Should_Not_Append_Empty_Extra_Query()
    {
        var playlist = new ConcatPlaylist("http", "localhost", "1", "ts-legacy");

        string result = playlist.ToString();

        result.ShouldContain("/ffmpeg/stream/1?mode=ts-legacy");
        result.ShouldNotContain("mode=ts-legacy&");
    }

    [Test]
    public void Should_Append_Extra_Query()
    {
        var playlist = new ConcatPlaylist("http", "localhost", "1", "ts-legacy", "region=midwest");

        string result = playlist.ToString();

        result.ShouldContain("/ffmpeg/stream/1?mode=ts-legacy&region=midwest");
    }
}
