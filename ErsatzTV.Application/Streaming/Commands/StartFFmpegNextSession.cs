using ErsatzTV.Core;

namespace ErsatzTV.Application.Streaming;

/// <param name="PlaylistQuery">
///     The query to put on the media playlist urls: the access token, plus any parameters the
///     channel's worker may recognize as identifying a viewer cohort.
/// </param>
public record StartFFmpegNextSession(
    string ChannelNumber,
    string Mode,
    string Scheme,
    string Host,
    string PathBase,
    string PlaylistQuery) :
    IRequest<Either<BaseError, string>>,
    IFFmpegWorkerRequest;
