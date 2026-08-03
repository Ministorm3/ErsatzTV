using System.Globalization;
using System.Text;

namespace ErsatzTV.Core.Next;

/// <summary>
///     The on-disk request protocol between a playlist requester and a channel worker.
/// </summary>
/// <remarks>
///     Which query parameters identify a viewer cohort depends on the playout a channel is
///     currently running, and only the worker tracks that. So a requester publishes the raw query
///     it received and reads back the cohort the worker resolved it to. Nothing here interprets a
///     query, composes a playlist, or knows anything about HLS.
///     This mirrors crates/ersatztv-core/src/variant_request.rs in the next repository. Both sides
///     must agree on the folder names, the file names, the freshness window and the hash.
/// </remarks>
public static class VariantRequests
{
    private const string VariantsFolder = "variants";
    private const string RequestsFolder = ".requests";
    private const string AnswersFolder = ".answers";

    /// <summary>
    ///     How old a composed playlist may be and still be served. The worker republishes every
    ///     live cohort's playlists on each tick, so an older file means the loop producing them has
    ///     stopped and its content is frozen.
    /// </summary>
    private static readonly TimeSpan PlaylistFreshness = TimeSpan.FromSeconds(15);

    /// <summary>
    ///     A short, deterministic, filesystem-safe name for an arbitrary string (fnv-1a), so a name
    ///     never has to carry query syntax onto the filesystem.
    /// </summary>
    public static string StableName(string input)
    {
        const ulong FnvOffset = 0xcbf29ce484222325;
        const ulong FnvPrime = 0x00000100000001b3;

        ulong hash = FnvOffset;
        foreach (byte value in Encoding.UTF8.GetBytes(input))
        {
            unchecked
            {
                hash ^= value;
                hash *= FnvPrime;
            }
        }

        return hash.ToString("x16", CultureInfo.InvariantCulture);
    }

    /// <summary>
    ///     The composed playlist a cohort's viewers read. It sits beside the shared playlist rather
    ///     than inside the cohort's folder so the segment paths the worker emits resolve the same
    ///     way for both.
    /// </summary>
    public static string ComposedPlaylistName(string cohort, bool subtitles) =>
        subtitles ? $"live_sub.{cohort}.m3u8" : $"live.{cohort}.m3u8";

    /// <summary>
    ///     Records that a viewer is asking for this raw query right now. The file holds the raw
    ///     query, since only the worker can resolve it; its modified time is the liveness signal
    ///     that keeps the resulting cohort's transcode alive.
    /// </summary>
    public static async Task PublishRequest(
        string outputFolder,
        string rawQuery,
        CancellationToken cancellationToken)
    {
        try
        {
            string folder = Path.Combine(outputFolder, VariantsFolder, RequestsFolder);
            Directory.CreateDirectory(folder);
            await File.WriteAllTextAsync(
                Path.Combine(folder, StableName(rawQuery)),
                rawQuery,
                cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // the viewer simply receives shared content
        }
    }

    /// <summary>
    ///     The cohort folder the worker resolved this raw query to. None covers every case where
    ///     the caller should serve shared content: the worker has not answered yet, or it answered
    ///     that the query identifies no cohort.
    /// </summary>
    public static async Task<Option<string>> ReadAnswer(
        string outputFolder,
        string rawQuery,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(outputFolder, VariantsFolder, AnswersFolder, StableName(rawQuery));

        try
        {
            if (!File.Exists(path))
            {
                return Option<string>.None;
            }

            string answer = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
            return string.IsNullOrEmpty(answer) ? Option<string>.None : answer;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Option<string>.None;
        }
    }

    /// <summary>
    ///     Reads a cohort's composed playlist, but only while its worker is still republishing it.
    ///     Reading and checking freshness are one operation on purpose: reading the file directly
    ///     would serve frozen content without noticing.
    /// </summary>
    public static async Task<Option<string>> ReadComposedPlaylist(
        string outputFolder,
        string cohort,
        bool subtitles,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(outputFolder, ComposedPlaylistName(cohort, subtitles));

        try
        {
            if (!File.Exists(path))
            {
                return Option<string>.None;
            }

            // a modified time in the future is a clock that ran ahead of ours, never a playlist
            // that stopped being written
            TimeSpan age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
            if (age > PlaylistFreshness)
            {
                return Option<string>.None;
            }

            return await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Option<string>.None;
        }
    }
}
