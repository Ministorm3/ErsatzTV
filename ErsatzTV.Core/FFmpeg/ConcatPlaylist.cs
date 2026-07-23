namespace ErsatzTV.Core.FFmpeg;

public record ConcatPlaylist(string Scheme, string Host, string ChannelNumber, string Mode, string ExtraQuery = null)
{
    public override string ToString()
    {
        string extraQuery = string.IsNullOrWhiteSpace(ExtraQuery) ? string.Empty : $"&{ExtraQuery}";
        return $@"ffconcat version 1.0
file http://localhost:{Settings.StreamingPort}/ffmpeg/stream/{ChannelNumber}?mode={Mode}{extraQuery}
file http://localhost:{Settings.StreamingPort}/ffmpeg/stream/{ChannelNumber}?mode={Mode}{extraQuery}";
    }
}
