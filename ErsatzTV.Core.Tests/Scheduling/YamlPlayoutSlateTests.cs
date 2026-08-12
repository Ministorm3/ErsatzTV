using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling.Handlers;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace ErsatzTV.Core.Tests.Scheduling;

[TestFixture]
public class YamlPlayoutSlateTests
{
    private static readonly DateTimeOffset Start = new(2025, 4, 15, 12, 0, 0, TimeSpan.FromHours(-5));

    private const string TemplatedUrl = "http://weather.example/live.ts?zip={query:zip}";

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
    public void Slate_Key_That_Resolves_To_Zero_Items_Should_Warn_And_Record_Nothing()
    {
        var logger = new CapturingLogger();

        Option<MediaItem> resolved = TestableCountHandler.Resolve(
            "slate",
            Option<IMediaCollectionEnumerator>.Some(new TestEnumerator(Option<MediaItem>.None)),
            logger);

        resolved.IsNone.ShouldBeTrue();
        logger.Warnings.Count.ShouldBe(1);
        logger.Warnings[0].ShouldContain("contains no media items");
    }

    [Test]
    public void No_Slate_Key_Should_Resolve_To_Nothing_Without_Warning()
    {
        var logger = new CapturingLogger();

        Option<MediaItem> resolved = TestableCountHandler.Resolve(
            slateContentKey: null,
            Option<IMediaCollectionEnumerator>.None,
            logger);

        resolved.IsNone.ShouldBeTrue();
        logger.Warnings.ShouldBeEmpty();
    }

    [Test]
    public void Slate_Key_That_Resolves_Should_Return_The_Item()
    {
        var slate = TestMovie(2, TimeSpan.FromMinutes(5));
        var logger = new CapturingLogger();

        Option<MediaItem> resolved = TestableCountHandler.Resolve(
            "slate",
            Option<IMediaCollectionEnumerator>.Some(new TestEnumerator(slate)),
            logger);

        foreach (MediaItem mediaItem in resolved)
        {
            mediaItem.Id.ShouldBe(slate.Id);
        }

        resolved.IsSome.ShouldBeTrue();
        logger.Warnings.ShouldBeEmpty();
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

    private static YamlPlayoutContext TestContext() =>
        new(new Playout { Id = 1 }, new YamlPlayoutDefinition(), 1) { CurrentTime = Start };

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
            ILogger<SequentialPlayoutBuilder> logger) =>
            ResolveSlate(slateContentKey, maybeSlateEnumerator, logger);

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

    private class TestEnumerator(Option<MediaItem> mediaItem) : IMediaCollectionEnumerator
    {
        public string SchedulingContextName => "test";
        public CollectionEnumeratorState State { get; } = new() { Index = 0, Seed = 0 };
        public Option<MediaItem> Current => mediaItem;
        public Option<bool> CurrentIncludeInProgramGuide => Option<bool>.None;
        public int Count => mediaItem.IsSome ? 1 : 0;

        public Option<TimeSpan> MinimumDuration =>
            mediaItem.Map(mi => mi.GetDurationForPlayout());

        public void ResetState(CollectionEnumeratorState state) { }
        public void MoveNext(Option<DateTimeOffset> scheduledAt) { }
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
