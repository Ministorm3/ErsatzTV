using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling.Handlers;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling;

[TestFixture]
public class YamlPlayoutSlateTests
{
    private static readonly DateTimeOffset Start = new(2025, 4, 15, 12, 0, 0, TimeSpan.FromHours(-5));

    private const string TemplatedUrl = "http://weather.example/live.ts?zip={query:zip}";

    [Test]
    public async Task Handle_Should_Record_The_Instructions_Slate_On_The_Items_It_Schedules()
    {
        // the whole seam, from the key on the instruction to the id on the row: the count handler
        // is the only thing that reads count.Slate, and reading count.Content there instead would
        // hand every item its own id back as a slate
        var content = TestRemoteStream(1, TimeSpan.FromMinutes(20), TemplatedUrl);
        var slate = TestMovie(2, TimeSpan.FromMinutes(5));

        HandleResult result = await Handle(
            new YamlPlayoutCountInstruction { Count = "2", Content = "content", Slate = "slate" },
            [("content", [content]), ("slate", [slate])]);

        result.Handled.ShouldBeTrue();
        result.Context.AddedItems.Count.ShouldBe(2);
        result.Context.AddedItems.ShouldAllBe(i => i.MediaItemId == 1);
        result.Context.AddedItems.ShouldAllBe(i => i.SlateMediaItemId == 2);
    }

    [Test]
    public async Task Handle_Should_Not_Record_A_Slate_That_Was_Never_Asked_For()
    {
        var content = TestRemoteStream(1, TimeSpan.FromMinutes(20), TemplatedUrl);
        var slate = TestMovie(2, TimeSpan.FromMinutes(5));

        // the slate content exists; the instruction just does not name it
        HandleResult result = await Handle(
            new YamlPlayoutCountInstruction { Count = "1", Content = "content" },
            [("content", [content]), ("slate", [slate])]);

        result.Context.AddedItems.Count.ShouldBe(1);
        result.Context.AddedItems[0].SlateMediaItemId.ShouldBeNull();
        result.Warnings.ShouldBeEmpty();
    }

    [Test]
    public async Task Handle_Should_Leave_The_Slate_Content_Exactly_Where_It_Found_It()
    {
        // the slate is read from the content, so the cursor that content carries is untouched, and
        // every window gets the same item however many the key holds
        var content = TestRemoteStream(1, TimeSpan.FromMinutes(20), TemplatedUrl);
        var other = TestRemoteStream(2, TimeSpan.FromMinutes(20), TemplatedUrl);
        var third = TestRemoteStream(3, TimeSpan.FromMinutes(20), TemplatedUrl);
        var slate = TestMovie(10, TimeSpan.FromMinutes(5));
        var otherSlate = TestMovie(11, TimeSpan.FromMinutes(5));

        HandleResult result = await Handle(
            new YamlPlayoutCountInstruction { Count = "2", Content = "content", Slate = "slate" },
            [("content", [content, other, third]), ("slate", [slate, otherSlate])]);

        result.Context.AddedItems.Count.ShouldBe(2);
        result.Context.AddedItems.ShouldAllBe(i => i.SlateMediaItemId == 10);

        // the content the instruction scheduled from walked, which is what makes the number below
        // worth asserting
        (await result.CursorFor("content")).ShouldBe(2);
        (await result.CursorFor("slate")).ShouldBe(0);
    }

    [Test]
    public async Task Handle_Should_Keep_Content_And_Slate_Apart_When_One_Key_Is_Both()
    {
        // one key named twice used to mean one cursor: the content walked and dragged the slate
        // along with it, so the second window got a different slate than the first
        var first = TestRemoteStream(1, TimeSpan.FromMinutes(20), TemplatedUrl);
        var second = TestRemoteStream(2, TimeSpan.FromMinutes(20), TemplatedUrl);

        HandleResult result = await Handle(
            new YamlPlayoutCountInstruction { Count = "2", Content = "both", Slate = "both" },
            [("both", [first, second])]);

        result.Context.AddedItems.Count.ShouldBe(2);

        // the content walks
        result.Context.AddedItems[0].MediaItemId.ShouldBe(1);
        result.Context.AddedItems[1].MediaItemId.ShouldBe(2);

        // the slate stands still
        result.Context.AddedItems.ShouldAllBe(i => i.SlateMediaItemId == 1);
    }

    [Test]
    public async Task Every_Instruction_Should_Get_The_Same_Slate_From_The_Same_Key()
    {
        // which media a templated window stands on is the schedule's answer, not the build's
        // position, so two windows naming one key are naming one piece of media however much
        // scheduling happened in between
        var first = TestRemoteStream(1, TimeSpan.FromMinutes(20), TemplatedUrl);
        var second = TestRemoteStream(2, TimeSpan.FromMinutes(20), TemplatedUrl);
        var third = TestRemoteStream(3, TimeSpan.FromMinutes(20), TemplatedUrl);

        HandleResult result = await HandleAll(
            [
                new YamlPlayoutCountInstruction { Count = "2", Content = "both", Slate = "both" },
                new YamlPlayoutCountInstruction { Count = "1", Content = "both", Slate = "both" }
            ],
            [("both", [first, second, third])]);

        result.Context.AddedItems.Count.ShouldBe(3);
        result.Context.AddedItems.Select(i => i.MediaItemId).ShouldBe([1, 2, 3]);
        result.Context.AddedItems.ShouldAllBe(i => i.SlateMediaItemId == 1);
    }

    [Test]
    public async Task Handle_Should_Warn_When_The_Slate_Key_Holds_More_Than_One_Item()
    {
        var content = TestRemoteStream(1, TimeSpan.FromMinutes(20), TemplatedUrl);
        var slate = TestMovie(10, TimeSpan.FromMinutes(5));
        var otherSlate = TestMovie(11, TimeSpan.FromMinutes(5));

        HandleResult result = await Handle(
            new YamlPlayoutCountInstruction { Count = "1", Content = "content", Slate = "slate" },
            [("content", [content]), ("slate", [slate, otherSlate])]);

        result.Warnings.Count.ShouldBe(1);
        result.Warnings[0].ShouldContain("2 media items");

        result.Context.AddedItems[0].SlateMediaItemId.ShouldBe(10);
    }

    [Test]
    public async Task Handle_Should_Warn_When_The_Slate_Key_Names_A_Playlist()
    {
        // a playlist is an order to walk rather than a list to stand on, so it can never answer
        // "what plays across this window"
        var content = TestRemoteStream(1, TimeSpan.FromMinutes(20), TemplatedUrl);
        var slate = TestMovie(10, TimeSpan.FromMinutes(5));

        HandleResult result = await Handle(
            new YamlPlayoutCountInstruction { Count = "1", Content = "content", Slate = "playlist" },
            [("content", [content])],
            playlistContent: ("playlist", slate));

        result.Warnings.Count.ShouldBe(1);
        result.Warnings[0].ShouldContain("playlist or marathon");

        result.Context.AddedItems.Count.ShouldBe(1);
        result.Context.AddedItems[0].SlateMediaItemId.ShouldBeNull();
    }

    [Test]
    public async Task Slate_Should_Be_Recorded_Without_Disturbing_The_Item()
    {
        var content = TestRemoteStream(1, TimeSpan.FromMinutes(20), TemplatedUrl);
        var slate = TestMovie(2, TimeSpan.FromMinutes(5));

        var context = TestContext();

        await TestableCountHandler.Run(
            context,
            new YamlPlayoutCountInstruction { Count = "1", Content = "content", Slate = "slate" },
            countValue: 1,
            new TestEnumerator(content),
            slate);

        context.AddedItems.Count.ShouldBe(1);

        PlayoutItem item = context.AddedItems[0];

        // the slate is a source substitution for the shared session, so everything that says what
        // this item is still describes the templated content
        item.MediaItemId.ShouldBe(content.Id);
        item.FillerKind.ShouldBe(FillerKind.None);
        item.Start.ShouldBe(Start.UtcDateTime);
        item.Finish.ShouldBe(Start.UtcDateTime + TimeSpan.FromMinutes(20));
        item.InPoint.ShouldBe(TimeSpan.Zero);
        item.OutPoint.ShouldBe(TimeSpan.FromMinutes(20));

        item.SlateMediaItemId.ShouldBe(slate.Id);
    }

    [Test]
    public async Task Slate_Should_Back_Every_Item_The_Instruction_Schedules()
    {
        var content = TestRemoteStream(1, TimeSpan.FromMinutes(20), TemplatedUrl);
        var slate = TestMovie(2, TimeSpan.FromMinutes(5));

        var context = TestContext();

        await TestableCountHandler.Run(
            context,
            new YamlPlayoutCountInstruction { Count = "3", Content = "content", Slate = "slate" },
            countValue: 3,
            new TestEnumerator(content),
            slate);

        context.AddedItems.Count.ShouldBe(3);
        context.AddedItems.ShouldAllBe(i => i.SlateMediaItemId == 2);
    }

    [Test]
    public async Task No_Slate_Should_Change_Nothing_About_The_Item()
    {
        var content = TestRemoteStream(1, TimeSpan.FromMinutes(20), TemplatedUrl);

        var context = TestContext();
        var logger = new CapturingLogger();

        await TestableCountHandler.Run(
            context,
            new YamlPlayoutCountInstruction { Count = "1", Content = "content" },
            countValue: 1,
            new TestEnumerator(content),
            Option<MediaItem>.None,
            logger);

        context.AddedItems.Count.ShouldBe(1);

        PlayoutItem item = context.AddedItems[0];
        item.MediaItemId.ShouldBe(content.Id);
        item.FillerKind.ShouldBe(FillerKind.None);
        item.OutPoint.ShouldBe(TimeSpan.FromMinutes(20));
        item.SlateMediaItemId.ShouldBeNull();

        logger.Warnings.ShouldBeEmpty();
    }

    [Test]
    public void Resolving_A_Slate_Should_Not_Advance_The_Content_It_Came_From()
    {
        var slate = TestMovie(2, TimeSpan.FromMinutes(5));
        var otherSlate = TestMovie(3, TimeSpan.FromMinutes(5));
        var enumerator = new TestEnumerator(slate, otherSlate);
        var logger = new CapturingLogger();

        TestableCountHandler.Resolve("slate", enumerator, [slate, otherSlate], logger);

        enumerator.State.Index.ShouldBe(0);
        enumerator.MoveNextCount.ShouldBe(0);
    }

    [Test]
    public void Slate_Key_That_Resolves_To_Zero_Items_Should_Warn_And_Record_Nothing()
    {
        var logger = new CapturingLogger();

        Option<MediaItem> resolved = TestableCountHandler.Resolve(
            "slate",
            new TestEnumerator(),
            [],
            logger);

        resolved.IsNone.ShouldBeTrue();
        logger.Warnings.Count.ShouldBe(1);
        logger.Warnings[0].ShouldContain("contains no media items");
    }

    [Test]
    public void No_Slate_Key_Should_Resolve_To_Nothing_Without_Warning()
    {
        var slate = TestMovie(2, TimeSpan.FromMinutes(5));
        var logger = new CapturingLogger();

        Option<MediaItem> resolved = TestableCountHandler.Resolve(
            slateContentKey: null,
            Option<IMediaCollectionEnumerator>.None,
            [slate],
            logger);

        resolved.IsNone.ShouldBeTrue();
        logger.Warnings.ShouldBeEmpty();
    }

    [Test]
    public void Slate_Key_That_Resolves_Should_Return_The_First_Item()
    {
        var slate = TestMovie(2, TimeSpan.FromMinutes(5));
        var otherSlate = TestMovie(3, TimeSpan.FromMinutes(5));
        var logger = new CapturingLogger();

        // the enumerator is sitting on the second item; the answer is still the first, because it
        // is the content that is asked and not the cursor
        var enumerator = new TestEnumerator(slate, otherSlate);
        enumerator.MoveNext(Option<DateTimeOffset>.None);

        Option<MediaItem> resolved = TestableCountHandler.Resolve(
            "slate",
            enumerator,
            [slate, otherSlate],
            logger);

        foreach (MediaItem mediaItem in resolved)
        {
            mediaItem.Id.ShouldBe(slate.Id);
        }

        resolved.IsSome.ShouldBeTrue();
    }

    [Test]
    public async Task Slate_On_Content_That_Is_Not_Templated_Should_Warn()
    {
        // a plain local movie has no per viewer url, so the shared session can air it directly and
        // the slate would never be reached
        var content = TestMovie(1, TimeSpan.FromMinutes(20));
        var slate = TestMovie(2, TimeSpan.FromMinutes(5));

        var context = TestContext();
        var logger = new CapturingLogger();

        await TestableCountHandler.Run(
            context,
            new YamlPlayoutCountInstruction { Count = "1", Content = "content", Slate = "slate" },
            countValue: 1,
            new TestEnumerator(content),
            slate,
            logger);

        logger.Warnings.Count.ShouldBe(1);
        logger.Warnings[0].ShouldContain("not templated");

        // the warning is advisory; the association is still recorded and the item is untouched
        context.AddedItems[0].SlateMediaItemId.ShouldBe(slate.Id);
        context.AddedItems[0].MediaItemId.ShouldBe(content.Id);
    }

    [Test]
    public async Task Slate_On_Untemplated_Remote_Stream_Should_Warn()
    {
        var content = TestRemoteStream(1, TimeSpan.FromMinutes(20), "http://weather.example/live.ts");
        var slate = TestMovie(2, TimeSpan.FromMinutes(5));

        var context = TestContext();
        var logger = new CapturingLogger();

        await TestableCountHandler.Run(
            context,
            new YamlPlayoutCountInstruction { Count = "1", Content = "content", Slate = "slate" },
            countValue: 1,
            new TestEnumerator(content),
            slate,
            logger);

        logger.Warnings.Count.ShouldBe(1);
        logger.Warnings[0].ShouldContain("not templated");
    }

    [Test]
    public async Task Slate_On_Templated_Content_Should_Not_Warn()
    {
        var content = TestRemoteStream(1, TimeSpan.FromMinutes(20), TemplatedUrl);
        var slate = TestMovie(2, TimeSpan.FromMinutes(5));

        var context = TestContext();
        var logger = new CapturingLogger();

        await TestableCountHandler.Run(
            context,
            new YamlPlayoutCountInstruction { Count = "1", Content = "content", Slate = "slate" },
            countValue: 1,
            new TestEnumerator(content),
            slate,
            logger);

        logger.Warnings.ShouldBeEmpty();
    }

    [Test]
    public async Task Untemplated_Warning_Should_Be_Said_Once_Per_Instruction()
    {
        var content = TestMovie(1, TimeSpan.FromMinutes(20));
        var slate = TestMovie(2, TimeSpan.FromMinutes(5));

        var context = TestContext();
        var logger = new CapturingLogger();

        await TestableCountHandler.Run(
            context,
            new YamlPlayoutCountInstruction { Count = "5", Content = "content", Slate = "slate" },
            countValue: 5,
            new TestEnumerator(content),
            slate,
            logger);

        context.AddedItems.Count.ShouldBe(5);
        logger.Warnings.Count.ShouldBe(1);
    }

    /// <summary>
    ///     Runs the real handler against a real enumerator cache, so what the test exercises is the
    ///     wiring from the instruction to the scheduled rows, not a hand-resolved slate.
    /// </summary>
    private static Task<HandleResult> Handle(
        YamlPlayoutCountInstruction count,
        List<(string Key, List<MediaItem> Items)> content,
        (string Key, MediaItem Item)? playlistContent = null) =>
        HandleAll([count], content, playlistContent);

    /// <summary>
    ///     Runs several instructions through one handler over one cache, the way a build does, so a
    ///     later instruction sees the content exactly as the ones before it left it.
    /// </summary>
    private static async Task<HandleResult> HandleAll(
        List<YamlPlayoutCountInstruction> counts,
        List<(string Key, List<MediaItem> Items)> content,
        (string Key, MediaItem Item)? playlistContent = null)
    {
        var repository = Substitute.For<IMediaCollectionRepository>();

        var definition = new YamlPlayoutDefinition();
        foreach ((string key, List<MediaItem> items) in content)
        {
            repository.GetCollectionItemsByName(key, Arg.Any<CancellationToken>()).Returns(items);
            definition.Content.Add(
                new YamlPlayoutContentCollectionItem
                {
                    Key = key,
                    Collection = key,
                    Order = "chronological"
                });
        }

        if (playlistContent is { } playlist)
        {
            repository.GetPlaylistItemMap("group", playlist.Key, Arg.Any<CancellationToken>())
                .Returns(
                    new Dictionary<PlaylistItem, List<MediaItem>>
                    {
                        [new PlaylistItem
                        {
                            Index = 0,
                            CollectionType = CollectionType.Collection,
                            CollectionId = 1,
                            PlaybackOrder = PlaybackOrder.Chronological,
                            PlayAll = true
                        }] = [playlist.Item]
                    });

            definition.Content.Add(
                new YamlPlayoutContentPlaylistItem
                {
                    Key = playlist.Key,
                    PlaylistGroup = "group",
                    Playlist = playlist.Key
                });
        }

        YamlPlayoutContext context = TestContext(definition);
        var cache = new EnumeratorCache(repository, NullLogger.Instance);
        var logger = new CapturingLogger();
        var handler = new YamlPlayoutCountHandler(cache);

        var handled = true;
        for (var i = 0; i < counts.Count; i++)
        {
            context.InstructionIndex = i;

            handled &= await handler.Handle(
                context,
                counts[i],
                PlayoutBuildMode.Reset,
                _ => Task.CompletedTask,
                logger,
                CancellationToken.None);
        }

        return new HandleResult(handled, context, cache, logger.Warnings);
    }

    private static YamlPlayoutContext TestContext(YamlPlayoutDefinition definition = null) =>
        new(new Playout { Id = 1 }, definition ?? new YamlPlayoutDefinition(), 1) { CurrentTime = Start };

    private static Movie TestMovie(int id, TimeSpan duration) =>
        new()
        {
            Id = id,
            MovieMetadata = [new MovieMetadata { ReleaseDate = new DateTime(2000, 1, 1) }],
            MediaVersions = [new MediaVersion { Duration = duration, Chapters = [] }]
        };

    private static RemoteStream TestRemoteStream(int id, TimeSpan duration, string url) =>
        new()
        {
            Id = id,
            Url = url,
            IsLive = true,
            MediaVersions = [new MediaVersion { Duration = duration, Chapters = [] }]
        };

    private sealed record HandleResult(
        bool Handled,
        YamlPlayoutContext Context,
        EnumeratorCache Cache,
        List<string> Warnings)
    {
        /// <summary>
        ///     Where the content behind a key is sitting now, as the build left it.
        /// </summary>
        public async Task<int> CursorFor(string contentKey)
        {
            Option<IMediaCollectionEnumerator> maybeEnumerator =
                await Cache.GetCachedEnumeratorForContent(Context, contentKey, CancellationToken.None);

            foreach (IMediaCollectionEnumerator enumerator in maybeEnumerator)
            {
                return enumerator.State.Index;
            }

            throw new InvalidOperationException($"no enumerator was ever built for {contentKey}");
        }
    }

    /// <summary>
    ///     Reaches the protected scheduling routines with hand-built enumerators, so the slate path can
    ///     be exercised without a content database.
    /// </summary>
    private class TestableCountHandler : YamlPlayoutCountHandler
    {
        private TestableCountHandler() : base(null)
        {
        }

        public static Option<MediaItem> Resolve(
            string slateContentKey,
            Option<IMediaCollectionEnumerator> maybeSlateEnumerator,
            IReadOnlyList<MediaItem> slateItems,
            ILogger<SequentialPlayoutBuilder> logger) =>
            ResolveSlate(slateContentKey, maybeSlateEnumerator, slateItems, logger);

        public static Task Run(
            YamlPlayoutContext context,
            YamlPlayoutCountInstruction count,
            int countValue,
            IMediaCollectionEnumerator enumerator,
            Option<MediaItem> maybeSlate,
            ILogger<SequentialPlayoutBuilder> logger = null) =>
            Schedule(
                context,
                count,
                countValue,
                enumerator,
                maybeSlate,
                _ => Task.CompletedTask,
                logger ?? NullLogger<SequentialPlayoutBuilder>.Instance);
    }

    /// <summary>
    ///     Walks the items it was handed, and says so: a test that claims nothing advanced needs a
    ///     double that would have noticed.
    /// </summary>
    private class TestEnumerator(params MediaItem[] mediaItems) : IMediaCollectionEnumerator
    {
        public int MoveNextCount { get; private set; }

        public string SchedulingContextName => "test";
        public CollectionEnumeratorState State { get; } = new() { Index = 0, Seed = 0 };

        public Option<MediaItem> Current =>
            mediaItems.Length > 0 ? mediaItems[State.Index] : Option<MediaItem>.None;

        public Option<bool> CurrentIncludeInProgramGuide => Option<bool>.None;
        public int Count => mediaItems.Length;

        public Option<TimeSpan> MinimumDuration =>
            mediaItems.Length > 0
                ? mediaItems.Select(mi => mi.GetDurationForPlayout()).Min()
                : Option<TimeSpan>.None;

        public void ResetState(CollectionEnumeratorState state) => State.Index = state.Index;

        public void MoveNext(Option<DateTimeOffset> scheduledAt)
        {
            MoveNextCount++;

            if (mediaItems.Length > 0)
            {
                State.Index = (State.Index + 1) % mediaItems.Length;
            }
        }
    }

    /// <summary>
    ///     Keeps the formatted warning text so tests can assert on what an operator would actually read.
    /// </summary>
    private class CapturingLogger : ILogger<SequentialPlayoutBuilder>
    {
        public List<string> Warnings { get; } = [];

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception exception,
            Func<TState, Exception, string> formatter)
        {
            if (logLevel is LogLevel.Warning)
            {
                Warnings.Add(formatter(state, exception));
            }
        }
    }
}
