using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Domain.Filler;
using ErsatzTV.Core.Domain.Scheduling;
using ErsatzTV.Core.Extensions;
using ErsatzTV.Core.Interfaces.Scheduling;
using ErsatzTV.Core.Scheduling.YamlScheduling.Models;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.Scheduling.YamlScheduling.Handlers;

public class YamlPlayoutCountHandler(EnumeratorCache enumeratorCache) : YamlPlayoutContentHandler(enumeratorCache)
{
    public override async Task<bool> Handle(
        YamlPlayoutContext context,
        YamlPlayoutInstruction instruction,
        PlayoutBuildMode mode,
        Func<string, Task> executeSequence,
        ILogger<SequentialPlayoutBuilder> logger,
        CancellationToken cancellationToken)
    {
        if (instruction is not YamlPlayoutCountInstruction count)
        {
            return false;
        }

        Option<IMediaCollectionEnumerator> maybeEnumerator = await GetContentEnumerator(
            context,
            instruction.Content,
            logger,
            cancellationToken);

        Option<IMediaCollectionEnumerator> maybeSlateEnumerator = await GetContentEnumerator(
            context,
            count.Slate,
            logger,
            cancellationToken);

        Option<MediaItem> maybeSlate = ResolveSlate(count.Slate, maybeSlateEnumerator, logger);

        foreach (IMediaCollectionEnumerator enumerator in maybeEnumerator)
        {
            int seed = context.Playout.Seed + context.InstructionIndex + context.CurrentTime.DayOfYear;
            var random = new Random(seed);
            int countValue = CountExpression.Evaluate(count.Count, enumerator, random, cancellationToken);

            await Schedule(
                context,
                count,
                countValue,
                enumerator,
                maybeSlate,
                executeSequence,
                logger);

            return true;
        }

        return false;
    }

    /// <summary>
    ///     Picks the single media item a slate content key stands for. The key is expected to name a
    ///     one item list, so the enumerator's current item is the answer and it never advances: the
    ///     same slate backs every item the instruction schedules.
    /// </summary>
    protected static Option<MediaItem> ResolveSlate(
        string slateContentKey,
        Option<IMediaCollectionEnumerator> maybeSlateEnumerator,
        ILogger<SequentialPlayoutBuilder> logger)
    {
        if (string.IsNullOrWhiteSpace(slateContentKey))
        {
            return Option<MediaItem>.None;
        }

        foreach (IMediaCollectionEnumerator slateEnumerator in maybeSlateEnumerator)
        {
            if (slateEnumerator.Current.IsNone)
            {
                logger.LogWarning(
                    "Slate content with key {SlateContentKey} contains no media items; no slate will be recorded",
                    slateContentKey);

                return Option<MediaItem>.None;
            }

            return slateEnumerator.Current;
        }

        // a key that is not in the content list at all is already reported by GetContentEnumerator
        return Option<MediaItem>.None;
    }

    /// <summary>
    ///     Builds and adds the scheduled items. The slate is recorded on each item and changes nothing
    ///     else about it: the media item id, filler kind, start, finish, in point and out point all
    ///     stay exactly what they would be with no slate declared.
    /// </summary>
    protected static async Task Schedule(
        YamlPlayoutContext context,
        YamlPlayoutCountInstruction count,
        int countValue,
        IMediaCollectionEnumerator enumerator,
        Option<MediaItem> maybeSlate,
        Func<string, Task> executeSequence,
        ILogger<SequentialPlayoutBuilder> logger)
    {
        var warnedAboutUntemplatedContent = false;

        for (var i = 0; i < countValue; i++)
        {
            foreach (string preRollSequence in context.GetPreRollSequence())
            {
                context.PushFillerKind(FillerKind.PreRoll);
                await executeSequence(preRollSequence);
                context.PopFillerKind();
            }

            foreach (MediaItem mediaItem in enumerator.Current)
            {
                TimeSpan itemDuration = mediaItem.GetDurationForPlayout();

                // create a playout item
                var playoutItem = new PlayoutItem
                {
                    PlayoutId = context.Playout.Id,
                    MediaItemId = mediaItem.Id,
                    Start = context.CurrentTime.UtcDateTime,
                    Finish = context.CurrentTime.UtcDateTime + itemDuration,
                    InPoint = TimeSpan.Zero,
                    OutPoint = itemDuration,
                    FillerKind = GetFillerKind(count, context),
                    CustomTitle = string.IsNullOrWhiteSpace(count.CustomTitle) ? null : count.CustomTitle,
                    DisableWatermarks = count.DisableWatermarks,
                    //PreferredAudioLanguageCode = scheduleItem.PreferredAudioLanguageCode,
                    //PreferredAudioTitle = scheduleItem.PreferredAudioTitle,
                    //PreferredSubtitleLanguageCode = scheduleItem.PreferredSubtitleLanguageCode,
                    //SubtitleMode = scheduleItem.SubtitleMode
                    GuideGroup = context.PeekNextGuideGroup(),
                    //GuideStart = effectiveBlock.Start.UtcDateTime,
                    //GuideFinish = blockFinish.UtcDateTime,
                    //BlockKey = JsonConvert.SerializeObject(effectiveBlock.BlockKey),
                    //CollectionKey = JsonConvert.SerializeObject(collectionKey, JsonSettings),
                    //CollectionEtag = collectionEtags[collectionKey],
                    PlayoutItemWatermarks = [],
                    PlayoutItemGraphicsElements = []
                };

                foreach (MediaItem slate in maybeSlate)
                {
                    // only the association is recorded here; the media item id above still names the
                    // scheduled content, and the converter turns this into the item's "slate" source
                    playoutItem.SlateMediaItemId = slate.Id;

                    if (!warnedAboutUntemplatedContent && !IsTemplated(mediaItem))
                    {
                        warnedAboutUntemplatedContent = true;

                        logger.LogWarning(
                            "Slate content with key {SlateContentKey} is declared on content {ContentKey} that is not templated (its source has no query variables), so the slate will never be used",
                            count.Slate,
                            count.Content);
                    }
                }

                foreach (int watermarkId in context.GetChannelWatermarkIds())
                {
                    playoutItem.PlayoutItemWatermarks.Add(
                        new PlayoutItemWatermark
                        {
                            PlayoutItem = playoutItem,
                            WatermarkId = watermarkId
                        });
                }

                foreach ((int graphicsElementId, string variablesJson) in context.GetGraphicsElements())
                {
                    playoutItem.PlayoutItemGraphicsElements.Add(
                        new PlayoutItemGraphicsElement
                        {
                            PlayoutItem = playoutItem,
                            GraphicsElementId = graphicsElementId,
                            Variables = variablesJson
                        });
                }

                await AddItemAndMidRoll(context, playoutItem, mediaItem, executeSequence);
                context.AdvanceGuideGroup();

                // create history record
                List<PlayoutHistory> maybeHistory = GetHistoryForItem(
                    context,
                    count.Content,
                    enumerator,
                    playoutItem,
                    mediaItem,
                    logger);

                foreach (PlayoutHistory history in maybeHistory)
                {
                    context.AddedHistory.Add(history);
                }

                enumerator.MoveNext(playoutItem.StartOffset);
            }

            foreach (string postRollSequence in context.GetPostRollSequence())
            {
                context.PushFillerKind(FillerKind.PostRoll);
                await executeSequence(postRollSequence);
                context.PopFillerKind();
            }
        }
    }

    /// <summary>
    ///     A slate only means anything when the shared session cannot air the item's own source, which
    ///     is the case exactly when that source is templated per viewer. Legacy has no first class
    ///     notion of templating, so the honest test is whether the url carries a query variable.
    /// </summary>
    private static bool IsTemplated(MediaItem mediaItem) =>
        mediaItem is RemoteStream remoteStream &&
        remoteStream.Url?.Contains("{query:", StringComparison.OrdinalIgnoreCase) == true;
}
