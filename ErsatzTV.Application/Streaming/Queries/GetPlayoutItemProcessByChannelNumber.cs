using ErsatzTV.Core.Domain;
using ErsatzTV.FFmpeg;

namespace ErsatzTV.Application.Streaming;

public record GetPlayoutItemProcessByChannelNumber(
    string ChannelNumber,
    StreamingMode Mode,
    DateTimeOffset Now,
    bool StartAtZero,
    bool HlsRealtime,
    DateTimeOffset ChannelStart,
    TimeSpan PtsOffset,
    Option<FrameRate> TargetFramerate,
    bool IsTroubleshooting,
    Option<int> FFmpegProfileId,
    IReadOnlyDictionary<string, string> QueryParameters = null) : FFmpegProcessRequest(
    ChannelNumber,
    Mode,
    Now,
    StartAtZero,
    HlsRealtime,
    ChannelStart,
    PtsOffset,
    FFmpegProfileId);
