using ErsatzTV.Core.Domain;

namespace ErsatzTV.Application.Streaming;

public record GetConcatProcessByChannelNumber : FFmpegProcessRequest
{
    public GetConcatProcessByChannelNumber(
        string scheme,
        string host,
        string channelNumber,
        string extraQuery = null) : base(
        channelNumber,
        StreamingMode.TransportStream,
        DateTimeOffset.Now,
        false,
        true,
        DateTimeOffset.Now, // unused
        TimeSpan.Zero,
        Option<int>.None)
    {
        Scheme = scheme;
        Host = host;
        ExtraQuery = extraQuery;
    }

    public string Scheme { get; }
    public string Host { get; }
    public string ExtraQuery { get; }
}
