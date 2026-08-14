using ErsatzTV.Core.Next;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Next;

[TestFixture]
public class VariantRequestsTests
{
    [SetUp]
    public void SetUp()
    {
        _folder = Path.Combine(Path.GetTempPath(), $"etv-variant-{Guid.NewGuid():N}");
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

    /// <summary>
    ///     These names are how this process and the channel worker find each other's files, so the
    ///     two implementations have to agree exactly. The expected values come from the rust
    ///     implementation in crates/ersatztv-core/src/variant_request.rs; changing either side
    ///     without the other silently stops every cohort from resolving.
    /// </summary>
    [TestCase("", "cbf29ce484222325")]
    [TestCase("zip=15216", "58e211ca5cd2ba5a")]
    [TestCase("zip=10001", "12bfa0e2b902f061")]
    [TestCase("lang=en&zip=15216", "64ce2f0660d8aff4")]
    [TestCase("cachebust=12345", "a3f69a644be089bd")]
    [TestCase("zip=15216&region=west/east?x=1", "f25178bd25199491")]
    public void StableName_ShouldMatchTheWorkersHash(string input, string expected)
    {
        VariantRequests.StableName(input).ShouldBe(expected);
    }

    [Test]
    public void StableName_ShouldBeFilesystemSafe()
    {
        string name = VariantRequests.StableName("zip=15216&region=west/east?x=1");
        name.ShouldAllBe(c => Uri.IsHexDigit(c));
    }

    [TestCase(false, "live.cafe1234.m3u8")]
    [TestCase(true, "live_sub.cafe1234.m3u8")]
    public void ComposedPlaylistName_ShouldSeparateMediaFromSubtitles(bool subtitles, string expected)
    {
        VariantRequests.ComposedPlaylistName("cafe1234", subtitles).ShouldBe(expected);
    }

    [Test]
    public async Task PublishRequest_ShouldRecordTheRawQueryForTheWorker()
    {
        await VariantRequests.PublishRequest(_folder, "Zip=15216&access_token=abc", CancellationToken.None);

        string path = Path.Combine(
            _folder,
            "variants",
            ".requests",
            VariantRequests.StableName("Zip=15216&access_token=abc"));

        (await File.ReadAllTextAsync(path)).ShouldBe("Zip=15216&access_token=abc");
    }

    /// <summary>
    ///     Republishing must never leave the request empty, even for an instant. A truncating
    ///     write does: between the open and the write the file is present, freshly modified and
    ///     empty, and a worker scanning the folder in that window reads an empty query,
    ///     canonicalizes it to the default cohort, and reaps the session of the cohort the viewer
    ///     actually asked for. That fired three times over 2026-08-13/14 while a viewer polled
    ///     every two seconds, and once cost an item its variant when the respawn raced the reaped
    ///     session's exiting worker for the output folder lock.
    ///     The rename cannot be observed mid-flight from a test, so this pins what it leaves
    ///     behind: the request holds the new query and no temporary file survives beside it.
    /// </summary>
    [Test]
    public async Task PublishRequest_ShouldRepublishWithoutLeavingATemporaryFile()
    {
        string requests = Path.Combine(_folder, "variants", ".requests");

        await VariantRequests.PublishRequest(_folder, "zip=15216", CancellationToken.None);
        await VariantRequests.PublishRequest(_folder, "zip=15216", CancellationToken.None);

        string path = Path.Combine(requests, VariantRequests.StableName("zip=15216"));
        (await File.ReadAllTextAsync(path)).ShouldBe("zip=15216");

        // a leftover temporary would be scanned as a request of its own, and its name would not
        // hash from its contents, so the worker would treat every tick as a torn read
        Directory.GetFiles(requests).ShouldBe([path]);
    }

    /// <summary>
    ///     A reader must never observe the request empty while it is being republished, which is
    ///     the whole point of publishing by rename. This is the reap bug reproduced in miniature:
    ///     the worker is exactly such a reader, scanning the folder every two seconds, and an
    ///     empty read makes it reap the session of a cohort whose viewer is still watching.
    ///     A truncating write fails this reliably within a few hundred republishes; a rename
    ///     cannot fail it at all, because the reader sees either the old file or the new one.
    /// </summary>
    [Test]
    public async Task PublishRequest_ShouldNeverBeObservedEmptyWhileRepublishing()
    {
        const string RawQuery = "zip=15216";
        string path = Path.Combine(
            _folder,
            "variants",
            ".requests",
            VariantRequests.StableName(RawQuery));

        await VariantRequests.PublishRequest(_folder, RawQuery, CancellationToken.None);

        using var done = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var emptyReads = 0;

        Task reader = Task.Run(
            async () =>
            {
                while (!done.IsCancellationRequested)
                {
                    try
                    {
                        if (File.Exists(path) && (await File.ReadAllTextAsync(path)).Length == 0)
                        {
                            Interlocked.Increment(ref emptyReads);
                        }
                    }
                    catch (IOException)
                    {
                        // a locked file is not an empty one; the worker skips and retries
                    }
                }
            },
            CancellationToken.None);

        for (var i = 0; i < 400 && !done.IsCancellationRequested; i++)
        {
            await VariantRequests.PublishRequest(_folder, RawQuery, CancellationToken.None);
        }

        await done.CancelAsync();
        await reader;

        emptyReads.ShouldBe(0, "a republished request was observed empty, so a worker scanning " +
            "in that window would reap the cohort whose viewer is still watching");
    }

    [Test]
    public async Task ReadAnswer_ShouldBeNone_WhenTheWorkerHasNotAnswered()
    {
        Option<string> answer = await VariantRequests.ReadAnswer(_folder, "zip=15216", CancellationToken.None);
        answer.IsNone.ShouldBeTrue();
    }

    [Test]
    public async Task ReadAnswer_ShouldBeNone_WhenTheQueryIdentifiesNoCohort()
    {
        await WriteAnswer("cachebust=1", string.Empty);

        Option<string> answer = await VariantRequests.ReadAnswer(_folder, "cachebust=1", CancellationToken.None);
        answer.IsNone.ShouldBeTrue();
    }

    [Test]
    public async Task ReadAnswer_ShouldBeTheCohort_WhenTheWorkerResolvedOne()
    {
        await WriteAnswer("zip=15216", "58e211ca5cd2ba5a");

        Option<string> answer = await VariantRequests.ReadAnswer(_folder, "zip=15216", CancellationToken.None);
        answer.IfNone(string.Empty).ShouldBe("58e211ca5cd2ba5a");
    }

    [Test]
    public async Task ReadComposedPlaylist_ShouldServeAFreshlyPublishedPlaylist()
    {
        string path = Path.Combine(_folder, VariantRequests.ComposedPlaylistName("cafe1234", false));
        await File.WriteAllTextAsync(path, "#EXTM3U\n");

        Option<string> playlist = await VariantRequests.ReadComposedPlaylist(
            _folder,
            "cafe1234",
            false,
            CancellationToken.None);

        playlist.IfNone(string.Empty).ShouldBe("#EXTM3U\n");
    }

    /// <summary>
    ///     The failure this check exists for: a worker still transcoding, but no longer
    ///     republishing composed playlists. Serving the frozen file would leave a cohort watching
    ///     a playlist that never advances.
    /// </summary>
    [Test]
    public async Task ReadComposedPlaylist_ShouldNotServeAPlaylistTheWorkerStoppedRepublishing()
    {
        string path = Path.Combine(_folder, VariantRequests.ComposedPlaylistName("cafe1234", false));
        await File.WriteAllTextAsync(path, "#EXTM3U\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-5));

        Option<string> playlist = await VariantRequests.ReadComposedPlaylist(
            _folder,
            "cafe1234",
            false,
            CancellationToken.None);

        playlist.IsNone.ShouldBeTrue();
    }

    [Test]
    public async Task ReadComposedPlaylist_ShouldBeNone_WhenNothingWasPublished()
    {
        Option<string> playlist = await VariantRequests.ReadComposedPlaylist(
            _folder,
            "cafe1234",
            false,
            CancellationToken.None);

        playlist.IsNone.ShouldBeTrue();
    }

    private async Task WriteAnswer(string rawQuery, string answer)
    {
        string folder = Path.Combine(_folder, "variants", ".answers");
        Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder, VariantRequests.StableName(rawQuery)), answer);
    }

    [Test]
    public async Task ReadAnswerDetailed_ShouldSeparatePendingFromNoCohort()
    {
        (await VariantRequests.ReadAnswerDetailed(_folder, "zip=15216", CancellationToken.None))
            .ShouldBeOfType<VariantRequests.CohortAnswer.Pending>();

        await WriteAnswer("zip=15216", string.Empty);
        (await VariantRequests.ReadAnswerDetailed(_folder, "zip=15216", CancellationToken.None))
            .ShouldBeOfType<VariantRequests.CohortAnswer.NoCohort>();

        await WriteAnswer("zip=15216", "58e211ca5cd2ba5a");
        var answer = await VariantRequests.ReadAnswerDetailed(_folder, "zip=15216", CancellationToken.None);
        answer.ShouldBeOfType<VariantRequests.CohortAnswer.Cohort>()
            .Name.ShouldBe("58e211ca5cd2ba5a");
    }

    /// <summary>
    ///     The defect this guards: a fresh tune finds no composed playlist, because the reap
    ///     deleted it, and is handed the shared playlist. The two sit about eleven media sequences
    ///     apart, so the client is then moved backwards and replays what it already showed. The
    ///     request has to wait out the worker's next tick instead.
    /// </summary>
    [Test]
    public async Task AwaitComposedPlaylist_ShouldWaitForTheWorker_RatherThanServingShared()
    {
        Task worker = Task.Run(
            async () =>
            {
                await Task.Delay(TimeSpan.FromMilliseconds(400));
                await WriteAnswer("zip=15216", "abc123");
                await File.WriteAllTextAsync(
                    Path.Combine(_folder, VariantRequests.ComposedPlaylistName("abc123", false)),
                    "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:7622\n");
            });

        Option<string> served = await VariantRequests.AwaitComposedPlaylist(
            _folder,
            "zip=15216",
            false,
            CancellationToken.None);

        await worker;

        served.IfNone(string.Empty).ShouldBe(
            "#EXTM3U\n#EXT-X-MEDIA-SEQUENCE:7622\n",
            "a request arriving before the worker's tick must be served the composed playlist, never shared");
    }

    [Test]
    public async Task AwaitComposedPlaylist_ShouldNotWait_WhenTheQueryNamesNoCohort()
    {
        await WriteAnswer("cachebust=1", string.Empty);

        DateTime start = DateTime.UtcNow;
        Option<string> served = await VariantRequests.AwaitComposedPlaylist(
            _folder,
            "cachebust=1",
            false,
            CancellationToken.None);

        served.IsNone.ShouldBeTrue();
        (DateTime.UtcNow - start).ShouldBeLessThan(
            TimeSpan.FromSeconds(1),
            "shared is the answer here and must be served at once");
    }
}
