using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using ErsatzTV.Core.Interfaces.FFmpeg;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public class StreamVariantSessionService : IStreamVariantSessionService
{
    private static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    private readonly IFFmpegSegmenterService _ffmpegSegmenterService;
    private readonly ILogger<StreamVariantSession> _sessionLogger;
    private readonly ConcurrentDictionary<string, StreamVariantSession> _sessions = new();
    private readonly IServiceScopeFactory _serviceScopeFactory;

    public StreamVariantSessionService(
        IFFmpegSegmenterService ffmpegSegmenterService,
        IServiceScopeFactory serviceScopeFactory,
        ILogger<StreamVariantSession> sessionLogger)
    {
        _ffmpegSegmenterService = ffmpegSegmenterService;
        _serviceScopeFactory = serviceScopeFactory;
        _sessionLogger = sessionLogger;
    }

    public async Task<Option<string>> GetPlaylist(
        string channelNumber,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        if (!_ffmpegSegmenterService.TryGetWorker(channelNumber, out IHlsSessionWorker worker) || worker is null)
        {
            return Option<string>.None;
        }

        // keep the base session alive; variant viewers may not fetch base
        // segments while variant content is playing
        worker.Touch(Option<string>.None);

        DateTimeOffset now = DateTimeOffset.Now;
        ReapIdleSessions(now);

        string canonical = CanonicalQuery(parameters);
        string key = $"{channelNumber}|{canonical}";

        StreamVariantSession session = _sessions.GetOrAdd(
            key,
            _ => new StreamVariantSession(
                channelNumber,
                parameters,
                $"{channelNumber}_{ShortHash(canonical)}",
                _serviceScopeFactory,
                _sessionLogger));

        return await session.GetPlaylist(worker, now, cancellationToken);
    }

    private void ReapIdleSessions(DateTimeOffset now)
    {
        foreach ((string key, StreamVariantSession session) in _sessions)
        {
            if (now - session.LastAccess > IdleTimeout && _sessions.TryRemove(key, out StreamVariantSession removed))
            {
                _sessionLogger.LogDebug("Stopping idle variant session {Folder}", removed.DirectoryName);
                removed.Dispose();
            }
        }
    }

    private static string CanonicalQuery(IReadOnlyDictionary<string, string> parameters) =>
        string.Join(
            '&',
            parameters
                .OrderBy(kvp => kvp.Key, StringComparer.OrdinalIgnoreCase)
                .Select(kvp => $"{kvp.Key.ToLowerInvariant()}={kvp.Value}"));

    private static string ShortHash(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..8].ToLowerInvariant();
}
