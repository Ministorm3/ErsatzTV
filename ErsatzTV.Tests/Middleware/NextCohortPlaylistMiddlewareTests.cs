using ErsatzTV.Middleware;
using LanguageExt;
using Microsoft.AspNetCore.Http;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Tests.Middleware;

/// <summary>
///     A next channel's media playlist is served as a static file, so this matcher is the only
///     thing standing between a cohort request and the shared playlist. Matching too narrowly
///     silently disables per-cohort streams; matching too widely intercepts requests the static
///     file handler should answer.
/// </summary>
[TestFixture]
public class NextCohortPlaylistMiddlewareTests
{
    private static HttpRequest Request(string path, string queryString, string method = "GET")
    {
        var context = new DefaultHttpContext();
        context.Request.Method = method;
        context.Request.Path = path;
        context.Request.QueryString = new QueryString(queryString);
        return context.Request;
    }

    private static NextCohortPlaylistMiddleware.CohortPlaylistRequest Matched(HttpRequest request)
    {
        Option<NextCohortPlaylistMiddleware.CohortPlaylistRequest> match =
            NextCohortPlaylistMiddleware.Match(request);

        return match.IfNone(() => throw new InvalidOperationException("expected a match"));
    }

    [Test]
    public void ShouldMatchAMediaPlaylistCarryingAQuery()
    {
        NextCohortPlaylistMiddleware.CohortPlaylistRequest request =
            Matched(Request("/iptv/session/5/live.m3u8", "?zip=15216"));

        request.ChannelNumber.ShouldBe("5");
        request.Subtitles.ShouldBeFalse();
        request.Query.ShouldBe("zip=15216");
    }

    [Test]
    public void ShouldMatchTheSubtitleRendition()
    {
        Matched(Request("/iptv/session/5/live_sub.m3u8", "?zip=15216")).Subtitles.ShouldBeTrue();
    }

    [Test]
    public void ShouldPassTheWholeQueryThroughForTheWorkerToResolve()
    {
        Matched(Request("/iptv/session/5/live.m3u8", "?access_token=abc&zip=15216"))
            .Query.ShouldBe("access_token=abc&zip=15216");
    }

    [Test]
    public void ShouldNotMatchWithoutAQuery()
    {
        NextCohortPlaylistMiddleware.Match(Request("/iptv/session/5/live.m3u8", string.Empty))
            .IsNone.ShouldBeTrue();
    }

    /// <summary>
    ///     hls.m3u8 belongs to the legacy engine and is answered by a controller. Matching on the
    ///     next engine's file names is what scopes this to next channels.
    /// </summary>
    [Test]
    public void ShouldNotMatchTheLegacyEnginesPlaylist()
    {
        NextCohortPlaylistMiddleware.Match(Request("/iptv/session/5/hls.m3u8", "?zip=15216"))
            .IsNone.ShouldBeTrue();
    }

    [Test]
    public void ShouldNotMatchSegments()
    {
        NextCohortPlaylistMiddleware.Match(Request("/iptv/session/5/live000001.ts", "?zip=15216"))
            .IsNone.ShouldBeTrue();
    }

    [Test]
    public void ShouldNotMatchAVariantsSubfolder()
    {
        NextCohortPlaylistMiddleware.Match(
                Request("/iptv/session/5/variants/cafe1234/live.m3u8", "?zip=15216"))
            .IsNone.ShouldBeTrue();
    }

    [Test]
    public void ShouldNotMatchOtherPaths()
    {
        NextCohortPlaylistMiddleware.Match(Request("/iptv/channel/5.m3u8", "?zip=15216"))
            .IsNone.ShouldBeTrue();
    }

    [Test]
    public void ShouldNotMatchNonReadMethods()
    {
        NextCohortPlaylistMiddleware.Match(Request("/iptv/session/5/live.m3u8", "?zip=15216", "POST"))
            .IsNone.ShouldBeTrue();
    }

    [Test]
    public void ShouldMatchHeadSoPlayersCanProbe()
    {
        NextCohortPlaylistMiddleware.Match(Request("/iptv/session/5/live.m3u8", "?zip=15216", "HEAD"))
            .IsSome.ShouldBeTrue();
    }
}
