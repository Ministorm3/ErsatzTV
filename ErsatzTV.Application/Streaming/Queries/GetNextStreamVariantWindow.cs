using ErsatzTV.Core.Streaming;

namespace ErsatzTV.Application.Streaming;

public record GetNextStreamVariantWindow(string ChannelNumber, DateTimeOffset Now)
    : IRequest<Option<StreamVariantWindow>>;
