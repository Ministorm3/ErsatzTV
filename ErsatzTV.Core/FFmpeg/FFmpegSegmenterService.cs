using System.Collections.Concurrent;
using ErsatzTV.Core.Interfaces.FFmpeg;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Core.FFmpeg;

public class FFmpegSegmenterService(ILogger<FFmpegSegmenterService> logger) : IFFmpegSegmenterService
{
    private readonly ConcurrentDictionary<string, IHlsSessionWorker> _sessionWorkers = new();

    public event EventHandler OnWorkersChanged;

    public ICollection<IHlsSessionWorker> Workers => _sessionWorkers.Values;

    public bool TryGetWorker(string channelNumber, out IHlsSessionWorker worker) =>
        _sessionWorkers.TryGetValue(channelNumber, out worker);

    public bool TryAddWorker(string channelNumber, IHlsSessionWorker worker)
    {
        // Atomic: an existing entry, even the null reservation of a start
        // still validating, means the channel is taken. The old "if worker
        // is null, pretend we added it" branch made the reservation
        // non-exclusive for the whole validation window, so every request
        // arriving in that window spawned its own worker process; two
        // ersatztv-channel processes then shared /transcode/11 for eleven
        // hours on 2026-08-10. Failed starts must release the reservation
        // with RemoveReservation instead of relying on that branch.
        bool result = _sessionWorkers.TryAdd(channelNumber, worker);

        if (result)
        {
            OnWorkersChanged?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    public bool TryActivateWorker(string channelNumber, IHlsSessionWorker worker)
    {
        // only the start that holds the null reservation may register its
        // worker; a blind overwrite here let a second start replace the
        // first worker's registration while both processes kept running
        bool result = _sessionWorkers.TryUpdate(channelNumber, worker, null);

        if (result)
        {
            OnWorkersChanged?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    public bool RemoveWorker(string channelNumber, IHlsSessionWorker expectedWorker)
    {
        // removes only this exact worker: a dying worker's continuation must
        // never deregister a healthy replacement that now owns the channel
        bool result = _sessionWorkers.TryRemove(
            new KeyValuePair<string, IHlsSessionWorker>(channelNumber, expectedWorker));

        if (result)
        {
            OnWorkersChanged?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    public bool RemoveReservation(string channelNumber)
    {
        // removes only the null placeholder, never a real worker; called by
        // every start path that fails after reserving the channel, closing
        // the leak the old pretend-branch existed to self-heal
        bool result = _sessionWorkers.TryRemove(
            new KeyValuePair<string, IHlsSessionWorker>(channelNumber, null));

        if (result)
        {
            OnWorkersChanged?.Invoke(this, EventArgs.Empty);
        }

        return result;
    }

    public bool IsActive(string channelNumber) => _sessionWorkers.ContainsKey(channelNumber);

    public async Task<bool> StopChannel(string channelNumber, CancellationToken cancellationToken)
    {
        if (_sessionWorkers.TryGetValue(channelNumber, out IHlsSessionWorker worker))
        {
            if (worker != null)
            {
                await worker.Cancel(cancellationToken);
                return true;
            }
        }

        return false;
    }

    public void TouchChannel(string channelNumber, string fileName)
    {
        if (_sessionWorkers.TryGetValue(channelNumber, out IHlsSessionWorker worker))
        {
            worker?.Touch(fileName);
        }
    }

    public void PlayoutUpdated(string channelNumber)
    {
        if (_sessionWorkers.TryGetValue(channelNumber, out IHlsSessionWorker worker))
        {
            if (worker != null)
            {
                logger.LogInformation(
                    "Playout has been updated for channel {ChannelNumber}, HLS segmenter will skip ahead to catch up",
                    channelNumber);

                worker.PlayoutUpdated();
            }
        }
    }
}
