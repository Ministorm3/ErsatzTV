using ErsatzTV.Core.Next;
using ErsatzTV.Middleware;
using LanguageExt;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
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
    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"etv-cohort-middleware-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_folder);
    }

    [TearDown]
    public void TearDown()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, true);
        }
    }

    private string _folder;

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

    [Test]
    public async Task ShouldServeAFreshComposedPlaylist()
    {
        string answers = Path.Combine(_folder, "variants", ".answers");
        Directory.CreateDirectory(answers);
        await File.WriteAllTextAsync(
            Path.Combine(answers, VariantRequests.StableName("zip=15216")),
            "cafe1234");

        const string Playlist = "#EXTM3U\nseg1.ts\nseg2.ts\nseg3.ts\nseg4.ts\n";
        await File.WriteAllTextAsync(Path.Combine(_folder, "live.cafe1234.m3u8"), Playlist);

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        var request = new NextCohortPlaylistMiddleware.CohortPlaylistRequest("5", false, "zip=15216");

        bool handled = await NextCohortPlaylistMiddleware.TryServeComposedPlaylist(
            context,
            request,
            _folder,
            NullLogger.Instance);

        handled.ShouldBeTrue();
        context.Response.ContentType.ShouldBe("application/vnd.apple.mpegurl");
        context.Response.Body.Position = 0;
        (await new StreamReader(context.Response.Body).ReadToEndAsync()).ShouldBe(Playlist);
    }

    [Test]
    public async Task ShouldFallThroughWhenTheWorkerAnswersNoCohort()
    {
        string answers = Path.Combine(_folder, "variants", ".answers");
        Directory.CreateDirectory(answers);
        await File.WriteAllTextAsync(
            Path.Combine(answers, VariantRequests.StableName("zip=15216")),
            string.Empty);

        var context = new DefaultHttpContext();
        var request = new NextCohortPlaylistMiddleware.CohortPlaylistRequest("5", false, "zip=15216");

        bool handled = await NextCohortPlaylistMiddleware.TryServeComposedPlaylist(
            context,
            request,
            _folder,
            NullLogger.Instance);

        handled.ShouldBeFalse();
    }

    /// <summary>
    ///     A viewer who hangs up cancels RequestAborted, and every await on the serve path
    ///     carries that token. Before the catch in TryServeComposedPlaylist, the cancellation
    ///     left the middleware as an unhandled exception and every hangup was logged as a 500.
    /// </summary>
    [Test]
    public async Task ShouldAbandonTheRequestWhenTheViewerHangsUp()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var context = new DefaultHttpContext { RequestAborted = cancellation.Token };
        var request = new NextCohortPlaylistMiddleware.CohortPlaylistRequest("5", false, "zip=15216");

        bool handled = await NextCohortPlaylistMiddleware.TryServeComposedPlaylist(
            context,
            request,
            _folder,
            NullLogger.Instance);

        handled.ShouldBeTrue();
    }
}
