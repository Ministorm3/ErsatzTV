namespace ErsatzTV.Application.Streaming;

public interface IStreamVariantSessionService
{
    Task<Option<string>> GetPlaylist(
        string channelNumber,
        IReadOnlyDictionary<string, string> parameters,
        CancellationToken cancellationToken);
}
