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
}
