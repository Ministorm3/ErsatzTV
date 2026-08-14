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
    ///     How long a cohort's first playlist request waits for the worker to publish a composed
    ///     playlist before giving up and serving shared. Three worker ticks.
    /// </summary>
    private static readonly TimeSpan ComposedPlaylistWait = TimeSpan.FromSeconds(6);

    /// <summary>
    ///     How often that wait re-checks. Short relative to the worker's own tick, so the playlist
    ///     is served promptly once it lands rather than on a grid of our own.
    /// </summary>
    private static readonly TimeSpan ComposedPlaylistPoll = TimeSpan.FromMilliseconds(200);

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

            // Published by rename, never by truncating the live file.
            //
            // File.WriteAllTextAsync truncates, so between the open and the write the request
            // is present, carries a fresh modified time, and is EMPTY. A worker scanning the
            // folder in that window reads an empty query, canonicalizes it to the default
            // cohort, and reaps the session belonging to the cohort the viewer actually asked
            // for. The fresh modified time means it is not reported as a stale request either.
            //
            // That happened three times over 2026-08-13/14 on a channel being polled every two
            // seconds, each time recovering on the next tick, and once it cost an item its
            // variant when the respawn raced the reaped session's exiting worker for the
            // output folder lock. File.Move with overwrite is rename(2) on Linux, so a reader
            // sees either the previous request or the new one and never a partial file.
            string path = Path.Combine(folder, StableName(rawQuery));
            string temporary = path + ".tmp";
            await File.WriteAllTextAsync(temporary, rawQuery, cancellationToken);
            File.Move(temporary, path, true);
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
    ///     What the worker has said about a raw query, keeping apart the two cases
    ///     <see cref="ReadAnswer" /> deliberately collapses.
    /// </summary>
    /// <remarks>
    ///     A caller that falls straight through to shared content cannot tell "the worker has not
    ///     looked at this yet" from "the worker looked and this query names no cohort", and those
    ///     need opposite handling. The first is a viewer about to be given a composed playlist,
    ///     who must not be handed the shared one meanwhile: the two sit about eleven media
    ///     sequences apart, so switching between them moves a client backwards. The second is a
    ///     viewer whose content is shared, permanently, with nothing to wait for.
    /// </remarks>
    public abstract record CohortAnswer
    {
        /// <summary>No answer file: the worker has not completed a tick since the request.</summary>
        public sealed record Pending : CohortAnswer;

        /// <summary>The worker answered, and this query identifies no cohort.</summary>
        public sealed record NoCohort : CohortAnswer;

        /// <summary>The cohort folder name this query resolves to.</summary>
        public sealed record Cohort(string Name) : CohortAnswer;
    }

    /// <summary>
    ///     The worker's answer for a raw query, with Pending kept separate.
    /// </summary>
    public static async Task<CohortAnswer> ReadAnswerDetailed(
        string outputFolder,
        string rawQuery,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(outputFolder, VariantsFolder, AnswersFolder, StableName(rawQuery));

        try
        {
            if (!File.Exists(path))
            {
                return new CohortAnswer.Pending();
            }

            string answer = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
            return string.IsNullOrEmpty(answer)
                ? new CohortAnswer.NoCohort()
                : new CohortAnswer.Cohort(answer);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // unreadable is not the same as answered; let the caller wait it out
            return new CohortAnswer.Pending();
        }
    }

    /// <summary>
    ///     Reads the composed playlist for a raw query, waiting out the worker's next tick rather
    ///     than falling through to shared while the answer is still pending.
    /// </summary>
    /// <remarks>
    ///     A cohort session is reaped when its viewer stops watching, and the reap deletes the
    ///     composed playlist. So on every fresh tune the file is missing, and without this wait the
    ///     viewer is handed the SHARED playlist, plays from it, and is then moved onto the composed
    ///     playlist about eleven media sequences further back. Measured on channel 11 on
    ///     2026-08-13: media sequence 7632 served twice, then 7622, the two playlists' newest
    ///     segments 40s apart. The client's position is not in the new playlist at all, so it
    ///     stalls and re-syncs backwards, which is the stutter and repeat viewers see on every
    ///     channel change.
    ///     Bounded at three worker ticks so the previous behaviour is still reached, just later: a
    ///     channel whose own worker is starting, or a variant loop that has stopped, degrades to
    ///     shared rather than hanging.
    /// </remarks>
    public static async Task<Option<string>> AwaitComposedPlaylist(
        string outputFolder,
        string rawQuery,
        bool subtitles,
        CancellationToken cancellationToken)
    {
        DateTime deadline = DateTime.UtcNow + ComposedPlaylistWait;

        while (true)
        {
            switch (await ReadAnswerDetailed(outputFolder, rawQuery, cancellationToken))
            {
                // the worker looked and this query names no cohort; shared is the answer and
                // there is nothing to wait for
                case CohortAnswer.NoCohort:
                    return Option<string>.None;

                case CohortAnswer.Cohort cohort:
                    Option<string> maybePlaylist = await ReadComposedPlaylist(
                        outputFolder,
                        cohort.Name,
                        subtitles,
                        cancellationToken);

                    foreach (string playlist in maybePlaylist)
                    {
                        return playlist;
                    }

                    break;
            }

            if (DateTime.UtcNow >= deadline || cancellationToken.IsCancellationRequested)
            {
                return Option<string>.None;
            }

            await Task.Delay(ComposedPlaylistPoll, cancellationToken);
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
