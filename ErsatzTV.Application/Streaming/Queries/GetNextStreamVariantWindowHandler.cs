using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Streaming;
using ErsatzTV.Infrastructure.Data;
using ErsatzTV.Infrastructure.Extensions;
using Microsoft.EntityFrameworkCore;

namespace ErsatzTV.Application.Streaming;

public class GetNextStreamVariantWindowHandler(IDbContextFactory<TvContext> dbContextFactory)
    : IRequestHandler<GetNextStreamVariantWindow, Option<StreamVariantWindow>>
{
    public async Task<Option<StreamVariantWindow>> Handle(
        GetNextStreamVariantWindow request,
        CancellationToken cancellationToken)
    {
        await using TvContext dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        Option<Channel> maybeChannel = await dbContext.Channels
            .SelectOneAsync(c => c.Number, c => c.Number == request.ChannelNumber, cancellationToken);

        foreach (Channel channel in maybeChannel)
        {
            TimeSpan playoutOffset = channel.PlayoutOffset ?? TimeSpan.Zero;

            List<PlayoutItem> playoutItems = await dbContext.PlayoutItems
                .AsNoTracking()
                .Include(pi => pi.MediaItem)
                .Where(pi => pi.Playout.ChannelId == channel.Id && pi.Finish >= request.Now.UtcDateTime)
                .OrderBy(pi => pi.Start)
                .Take(50)
                .ToListAsync(cancellationToken);

            foreach (PlayoutItem playoutItem in playoutItems)
            {
                if (playoutItem.MediaItem is RemoteStream remoteStream &&
                    StreamVariableExpander.HasQueryVariables(remoteStream.Url))
                {
                    return new StreamVariantWindow(
                        playoutItem.StartOffset + playoutOffset,
                        playoutItem.FinishOffset + playoutOffset);
                }
            }
        }

        return Option<StreamVariantWindow>.None;
    }
}
