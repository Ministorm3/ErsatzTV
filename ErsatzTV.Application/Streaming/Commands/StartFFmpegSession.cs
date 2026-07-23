using ErsatzTV.Core;

namespace ErsatzTV.Application.Streaming;

public record StartFFmpegSession(
    string ChannelNumber,
    string Mode,
    string Scheme,
    string Host,
    string PathBase,
    string AccessTokenQuery,
    string ExtraQuery = null) :
    IRequest<Either<BaseError, string>>,
    IFFmpegWorkerRequest;
