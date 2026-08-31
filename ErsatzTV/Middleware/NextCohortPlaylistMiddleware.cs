using ErsatzTV.Core;
using ErsatzTV.Core.Next;

namespace ErsatzTV.Middleware;

/// <summary>
///     Serves a next channel's per-cohort media playlist, when its worker has published one.
/// </summary>
/// <remarks>
///     A next channel's media playlist is a file in the transcode folder, served by static file
///     middleware. That runs before endpoint routing, so a controller could never answer these
///     requests; this has to be registered ahead of it instead.
///     Everything this does is publish the query, wait out the worker's answer, and serve the
///     named file. The
///     worker resolves the cohort, spawns its transcode and composes its playlist, because which
///     parameters identify a cohort depends on the playout the worker is currently running. Any
///     miss falls through to the shared playlist, so the stream is never worse than it would have
///     been without this.
/// </remarks>
public static class NextCohortPlaylistMiddleware
{
    private const string SessionPath = "/iptv/session";

    public static IApplicationBuilder UseNextCohortPlaylists(this IApplicationBuilder app)
    {
        ILogger logger = app.ApplicationServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(NextCohortPlaylistMiddleware));

        return app.Use(
            async (context, next) =>
            {
                Option<CohortPlaylistRequest> maybeRequest = Match(context.Request);

                foreach (CohortPlaylistRequest request in maybeRequest)
                {
                    string outputFolder = Path.Combine(
                        FileSystemLayout.TranscodeFolder,
                        request.ChannelNumber);

                    if (await TryServeComposedPlaylist(context, request, outputFolder, logger))
                    {
                        return;
                    }
                }

                await next();
            });
    }

    /// <summary>
    ///     Publishes the query, waits out the worker's answer, and writes the composed playlist
    ///     to the response. True means the request needs nothing further, either because the
    ///     playlist was served or because the viewer hung up; false means the caller should fall
    ///     through to the shared playlist.
    /// </summary>
    public static async Task<bool> TryServeComposedPlaylist(
        HttpContext context,
        CohortPlaylistRequest request,
        string outputFolder,
        ILogger logger)
    {
        try
        {
            // publishing both asks the worker to resolve the query and keeps the
            // resulting cohort's transcode alive
            await VariantRequests.PublishRequest(
                outputFolder,
                request.Query,
                context.RequestAborted);

            // waits out the worker's next tick rather than falling through to shared
            // while the answer is still pending: a fresh tune always finds the composed
            // playlist missing, because reaping the cohort deleted it, and serving shared
            // in the meantime moves the client backwards when the composed one arrives
            Option<string> maybePlaylist = await VariantRequests.AwaitComposedPlaylist(
                outputFolder,
                request.Query,
                request.Subtitles,
                context.RequestAborted);

            foreach (string playlist in maybePlaylist)
            {
                context.Response.ContentType = "application/vnd.apple.mpegurl";
                await context.Response.WriteAsync(playlist, context.RequestAborted);
                return true;
            }
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // the viewer hung up while the wait above was still polling. Every await here
            // carries RequestAborted, so the hangup arrives as a cancellation from any of
            // them; without this catch it left the middleware as an unhandled exception and
            // was logged as a 500. The filter keeps every other cancellation loud.
            logger.LogDebug(
                "Viewer disconnected while waiting for the composed playlist for channel {ChannelNumber}",
                request.ChannelNumber);
            return true;
        }

        return false;
    }

    /// <summary>
    ///     Matches <c>/iptv/session/{channel}/live.m3u8</c> and its subtitle rendition, carrying a
    ///     query. Those two names belong to the next engine; the legacy engine serves
    ///     <c>hls.m3u8</c> through a controller, so matching on them is all the scoping needed.
    /// </summary>
    public static Option<CohortPlaylistRequest> Match(HttpRequest request)
    {
        if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
        {
            return Option<CohortPlaylistRequest>.None;
        }

        if (!request.Path.StartsWithSegments(SessionPath, out PathString remaining))
        {
            return Option<CohortPlaylistRequest>.None;
        }

        string[] segments = remaining.Value?.Split('/', StringSplitOptions.RemoveEmptyEntries) ?? [];
        if (segments.Length != 2)
        {
            return Option<CohortPlaylistRequest>.None;
        }

        bool subtitles;
        switch (segments[1])
        {
            case "live.m3u8":
                subtitles = false;
                break;
            case "live_sub.m3u8":
                subtitles = true;
                break;
            default:
                return Option<CohortPlaylistRequest>.None;
        }

        // no query means no cohort to resolve, so the shared playlist already answers it
        string query = request.QueryString.HasValue
            ? request.QueryString.Value?.TrimStart('?') ?? string.Empty
            : string.Empty;

        if (string.IsNullOrEmpty(query))
        {
            return Option<CohortPlaylistRequest>.None;
        }

        return new CohortPlaylistRequest(segments[0], subtitles, query);
    }

    public sealed record CohortPlaylistRequest(string ChannelNumber, bool Subtitles, string Query);
}
