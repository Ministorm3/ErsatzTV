using System.Text.Json;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Interfaces.Emby;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Jellyfin;
using ErsatzTV.Core.Interfaces.Plex;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Scheduling;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Testably.Abstractions.Testing;

namespace ErsatzTV.Core.Tests.Scheduling;

/// <summary>
///     The next engine reads the emitted json, so what matters here is the exact shape that reaches
///     the wire: a "slate" sibling of "source" that is present only when a slate was scheduled.
/// </summary>
[TestFixture]
public class PlayoutItemConverterSlateTests
{
    private const string SlatePath = "/bumps/fallback/WeatherSlateStatic.mp4";
    private const string TemplatedUrl = "http://weather.example/live.ts?zip={query:zip}";

    private static readonly DateTimeOffset Start = new(2026, 8, 11, 20, 20, 0, TimeSpan.FromHours(-4));

    [Test]
    public async Task Emitted_Item_Should_Carry_Slate_With_Path_And_Probe_Hint()
    {
        PlayoutItem playoutItem = TemplatedPlayoutItem();
        playoutItem.SlateMediaItemId = 2;
        playoutItem.SlateMediaItem = SlateVideo(2);

        Option<Core.Next.PlayoutItem> maybeNext = await Convert(playoutItem);

        maybeNext.IsSome.ShouldBeTrue();

        foreach (Core.Next.PlayoutItem next in maybeNext)
        {
            next.Slate.ShouldNotBeNull();
            next.Slate.SourceType.ShouldBe(Core.Next.SourceType.Local);
            next.Slate.Path.ShouldBe(SlatePath);

            next.Slate.ProbeHint.ShouldNotBeNull();
            next.Slate.ProbeHint.DurationMs.ShouldBe((long)TimeSpan.FromMinutes(5).TotalMilliseconds);
            next.Slate.ProbeHint.Video.Count.ShouldBe(1);
            next.Slate.ProbeHint.Video[0].Codec.ShouldBe("h264");
            next.Slate.ProbeHint.Video[0].Width.ShouldBe(1920);
            next.Slate.ProbeHint.Video[0].Height.ShouldBe(1080);
            next.Slate.ProbeHint.Audio.Count.ShouldBe(1);
            next.Slate.ProbeHint.Audio[0].Codec.ShouldBe("aac");

            // in and out points belong to the scheduled content, not to the thing standing in for it
            next.Slate.InPointMs.ShouldBeNull();
            next.Slate.OutPointMs.ShouldBeNull();
        }
    }

    [Test]
    public async Task Item_Should_Keep_Its_Own_Source_And_Identity_Alongside_The_Slate()
    {
        PlayoutItem playoutItem = TemplatedPlayoutItem();
        playoutItem.SlateMediaItemId = 2;
        playoutItem.SlateMediaItem = SlateVideo(2);

        Option<Core.Next.PlayoutItem> maybeNext = await Convert(playoutItem);

        foreach (Core.Next.PlayoutItem next in maybeNext)
        {
            // cohort viewers only reach the live presentation because the templated url is still here
            next.Source.ShouldNotBeNull();
            next.Source.SourceType.ShouldBe(Core.Next.SourceType.Http);
            next.Source.Uri.ShouldBe(TemplatedUrl);
            next.Source.IsLive.ShouldBe(true);

            next.Id.ShouldBe("77");
            next.Start.ShouldBe(Start);
        }
    }

    [Test]
    public async Task No_Slate_Should_Leave_The_Key_Out_Of_The_Json_Entirely()
    {
        PlayoutItem playoutItem = TemplatedPlayoutItem();

        Option<Core.Next.PlayoutItem> maybeNext = await Convert(playoutItem);

        foreach (Core.Next.PlayoutItem next in maybeNext)
        {
            next.Slate.ShouldBeNull();

            string json = JsonSerializer.Serialize(next, Core.Next.Converter.Settings);
            json.ShouldNotContain("slate");
        }
    }

    [Test]
    public async Task Slate_Should_Be_Emitted_As_A_Sibling_Of_Source()
    {
        PlayoutItem playoutItem = TemplatedPlayoutItem();
        playoutItem.SlateMediaItemId = 2;
        playoutItem.SlateMediaItem = SlateVideo(2);

        Option<Core.Next.PlayoutItem> maybeNext = await Convert(playoutItem);

        foreach (Core.Next.PlayoutItem next in maybeNext)
        {
            string json = JsonSerializer.Serialize(next, Core.Next.Converter.Settings);

            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            root.TryGetProperty("source", out JsonElement source).ShouldBeTrue();
            source.GetProperty("source_type").GetString().ShouldBe("http");

            root.TryGetProperty("slate", out JsonElement slate).ShouldBeTrue();
            slate.GetProperty("source_type").GetString().ShouldBe("local");
            slate.GetProperty("path").GetString().ShouldBe(SlatePath);
            slate.TryGetProperty("probe_hint", out _).ShouldBeTrue();
        }
    }

    [Test]
    public async Task Slate_Id_Without_A_Loaded_Slate_Should_Emit_Nothing()
    {
        // a query that does not ask for the slate leaves the navigation null; that is "not loaded",
        // not "no slate", and guessing either way would be wrong
        PlayoutItem playoutItem = TemplatedPlayoutItem();
        playoutItem.SlateMediaItemId = 2;

        Option<Core.Next.PlayoutItem> maybeNext = await Convert(playoutItem);

        foreach (Core.Next.PlayoutItem next in maybeNext)
        {
            next.Slate.ShouldBeNull();
        }
    }

    private static Task<Option<Core.Next.PlayoutItem>> Convert(PlayoutItem playoutItem)
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Initialize().WithFile(SlatePath);

        var converter = new PlayoutItemConverter(
            fileSystem,
            Substitute.For<IPlexPathReplacementService>(),
            Substitute.For<IJellyfinPathReplacementService>(),
            Substitute.For<IEmbyPathReplacementService>(),
            Substitute.For<ICustomStreamSelector>(),
            Substitute.For<IFFmpegStreamSelector>(),
            Substitute.For<IWatermarkSelector>(),
            Substitute.For<IDbContextFactory<TvContext>>());

        return converter.ToNext(
            Option<Channel>.None,
            Option<ChannelWatermark>.None,
            TimeSpan.Zero,
            playoutItem,
            Option<List<Subtitle>>.None,
            shouldLogMessages: false,
            CancellationToken.None);
    }

    private static PlayoutItem TemplatedPlayoutItem() =>
        new()
        {
            Id = 77,
            MediaItemId = 1,
            MediaItem = new RemoteStream
            {
                Id = 1,
                Url = TemplatedUrl,
                IsLive = true,
                MediaVersions = [Version(TimeSpan.FromMinutes(103), path: null)]
            },
            Start = Start.UtcDateTime,
            Finish = Start.UtcDateTime + TimeSpan.FromMinutes(103),
            InPoint = TimeSpan.Zero,
            OutPoint = TimeSpan.FromMinutes(103)
        };

    private static OtherVideo SlateVideo(int id) =>
        new()
        {
            Id = id,
            MediaVersions = [Version(TimeSpan.FromMinutes(5), SlatePath)]
        };

    private static MediaVersion Version(TimeSpan duration, string path) =>
        new()
        {
            Duration = duration,
            Width = 1920,
            Height = 1080,
            RFrameRate = "30000/1001",
            SampleAspectRatio = "1:1",
            DisplayAspectRatio = "16:9",
            VideoScanKind = VideoScanKind.Progressive,
            Chapters = [],
            MediaFiles = path is null ? [] : [new MediaFile { Path = path }],
            Streams =
            [
                new MediaStream
                {
                    Index = 0,
                    MediaStreamKind = MediaStreamKind.Video,
                    Codec = "h264",
                    Profile = "high",
                    PixelFormat = "yuv420p"
                },
                new MediaStream
                {
                    Index = 1,
                    MediaStreamKind = MediaStreamKind.Audio,
                    Codec = "aac",
                    Channels = 2
                }
            ]
        };
}
