using ErsatzTV.Core.Iptv;

namespace ErsatzTV.Application.Channels;

/// <param name="ForwardedQuery">
///     Parameters the playlist was requested with that streaming itself does not consume, carried
///     onto every channel url so a viewer's identity survives to the stream.
/// </param>
public record GetChannelPlaylist(
    string Scheme,
    string Host,
    string BaseUrl,
    string Mode,
    string UserAgent,
    string AccessToken,
    string ForwardedQuery = null) : IRequest<ChannelPlaylist>;
