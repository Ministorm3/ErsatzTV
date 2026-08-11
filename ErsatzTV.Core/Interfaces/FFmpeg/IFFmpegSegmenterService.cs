namespace ErsatzTV.Core.Interfaces.FFmpeg;

public interface IFFmpegSegmenterService
{
    ICollection<IHlsSessionWorker> Workers { get; }
    event EventHandler OnWorkersChanged;
    bool TryGetWorker(string channelNumber, out IHlsSessionWorker worker);
    bool TryAddWorker(string channelNumber, IHlsSessionWorker worker);
    bool TryActivateWorker(string channelNumber, IHlsSessionWorker worker);
    bool RemoveWorker(string channelNumber, IHlsSessionWorker expectedWorker);
    bool RemoveReservation(string channelNumber);
    bool IsActive(string channelNumber);
    Task<bool> StopChannel(string channelNumber, CancellationToken cancellationToken);
    void TouchChannel(string channelNumber, string fileName);
    void PlayoutUpdated(string channelNumber);
}
