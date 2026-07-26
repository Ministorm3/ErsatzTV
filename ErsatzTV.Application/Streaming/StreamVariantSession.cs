using System.IO.Pipelines;
using System.Text;
using CliWrap;
using ErsatzTV.Application.Channels;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Streaming;
using ErsatzTV.Core.Streaming;
using ErsatzTV.FFmpeg;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public class StreamVariantSession : IDisposable
{
    private readonly string _channelNumber;
    private readonly string _folder;
    private readonly ILogger _logger;
    private readonly IReadOnlyDictionary<string, string> _parameters;
    private readonly object _schedulingLock = new();
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly IServiceScopeFactory _serviceScopeFactory;
    private readonly CancellationTokenSource _sessionCts = new();
    private readonly VariantStitchState _state = new();

    private DateTimeOffset? _scheduledWindowStart;
    private DateTimeOffset? _startedWindowStart;
    private Option<StreamVariantWindow> _window = Option<StreamVariantWindow>.None;
    private DateTimeOffset _windowFetchedAt = DateTimeOffset.MinValue;
    private volatile bool _workerDone;

    public StreamVariantSession(
        string channelNumber,
        IReadOnlyDictionary<string, string> parameters,
        string directoryName,
        IServiceScopeFactory serviceScopeFactory,
        ILogger logger)
    {
        _channelNumber = channelNumber;
        _parameters = parameters;
        _serviceScopeFactory = serviceScopeFactory;
        _logger = logger;

        DirectoryName = directoryName;
        _folder = Path.Combine(FileSystemLayout.TranscodeFolder, directoryName);
    }

    public string DirectoryName { get; }

    public DateTimeOffset LastAccess { get; private set; } = DateTimeOffset.Now;

    public void Dispose()
    {
        try
        {
            _sessionCts.Cancel();
            _sessionCts.Dispose();
        }
        catch (ObjectDisposedException)
        {
            // do nothing
        }

        string folder = _folder;
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            try
            {
                if (Directory.Exists(folder))
                {
                    Directory.Delete(folder, true);
                }
            }
            catch (Exception)
            {
                // do nothing
            }
        });

        GC.SuppressFinalize(this);
    }

    public async Task<Option<string>> GetPlaylist(
        IHlsSessionWorker baseWorker,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        LastAccess = now;

        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            await RefreshWindow(now, cancellationToken);
            ScheduleWorker();

            Option<TrimPlaylistResult> maybeTrimmed = await baseWorker.TrimPlaylist(
                now.AddSeconds(-30),
                cancellationToken);

            foreach (TrimPlaylistResult trimmed in maybeTrimmed)
            {
                ParsedMediaPlaylist basePlaylist = MediaPlaylistParser.Parse(trimmed.Playlist.Split('\n'));
                Option<ParsedMediaPlaylist> variantPlaylist = ReadVariantPlaylist(_window);

                return VariantPlaylistStitcher.Stitch(
                    _state,
                    basePlaylist,
                    variantPlaylist,
                    _window,
                    _workerDone,
                    $"../{DirectoryName}/",
                    now);
            }

            return Option<string>.None;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task RefreshWindow(DateTimeOffset now, CancellationToken cancellationToken)
    {
        if (now - _windowFetchedAt < TimeSpan.FromSeconds(10))
        {
            return;
        }

        using IServiceScope scope = _serviceScopeFactory.CreateScope();
        IMediator mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        _window = await mediator.Send(new GetNextStreamVariantWindow(_channelNumber, now), cancellationToken);
        _windowFetchedAt = now;
    }

    private void ScheduleWorker()
    {
        foreach (StreamVariantWindow window in _window)
        {
            lock (_schedulingLock)
            {
                if (_startedWindowStart == window.Start || _scheduledWindowStart == window.Start)
                {
                    continue;
                }

                _scheduledWindowStart = window.Start;

                TimeSpan delay = window.Start - DateTimeOffset.Now - TimeSpan.FromSeconds(1);
                if (delay <= TimeSpan.Zero)
                {
                    StartWorker(window);
                }
                else
                {
                    _ = Task.Delay(delay, _sessionCts.Token).ContinueWith(
                        t =>
                        {
                            if (t.IsCanceled)
                            {
                                return;
                            }

                            lock (_schedulingLock)
                            {
                                bool windowIsCurrent = _window
                                    .Map(w => w.Start == window.Start)
                                    .IfNone(false);
                                if (windowIsCurrent && _startedWindowStart != window.Start)
                                {
                                    StartWorker(window);
                                }
                            }
                        },
                        TaskScheduler.Default);
                }
            }
        }
    }

    private void StartWorker(StreamVariantWindow window)
    {
        _logger.LogInformation(
            "Starting variant playback for channel {Channel} in {Folder}",
            _channelNumber,
            DirectoryName);

        _startedWindowStart = window.Start;
        _workerDone = false;
        _ = Task.Run(() => RunWorker(window, _sessionCts.Token));
    }

    private async Task RunWorker(StreamVariantWindow window, CancellationToken cancellationToken)
    {
        try
        {
            PrepareFolder();

            using IServiceScope scope = _serviceScopeFactory.CreateScope();
            IMediator mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
            IGraphicsEngine graphicsEngine = scope.ServiceProvider.GetRequiredService<IGraphicsEngine>();

            Option<FrameRate> targetFramerate = await mediator.Send(
                new GetChannelFramerate(_channelNumber),
                cancellationToken);

            while (!cancellationToken.IsCancellationRequested &&
                   DateTimeOffset.Now < window.Finish - TimeSpan.FromSeconds(5))
            {
                DateTimeOffset now = DateTimeOffset.Now;
                var request = new GetPlayoutItemProcessByChannelNumber(
                    _channelNumber,
                    StreamingMode.HttpLiveStreamingSegmenter,
                    now < window.Start ? window.Start : now,
                    StartAtZero: false,
                    HlsRealtime: true,
                    window.Start,
                    TimeSpan.Zero,
                    targetFramerate,
                    IsTroubleshooting: false,
                    Option<int>.None,
                    _parameters,
                    DirectoryName);

                Either<BaseError, PlayoutItemProcessModel> result = await mediator.Send(request, cancellationToken);

                foreach (BaseError error in result.LeftAsEnumerable())
                {
                    _logger.LogWarning(
                        "Failed to create variant process for channel {Channel} in {Folder}: {Error}",
                        _channelNumber,
                        DirectoryName,
                        error.ToString());
                    return;
                }

                foreach (PlayoutItemProcessModel processModel in result.RightAsEnumerable())
                {
                    if (!await RunProcess(processModel, graphicsEngine, cancellationToken))
                    {
                        return;
                    }
                }
            }
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            // do nothing
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error in variant session for channel {Channel}", _channelNumber);
        }
        finally
        {
            _workerDone = true;
        }
    }

    private async Task<bool> RunProcess(
        PlayoutItemProcessModel processModel,
        IGraphicsEngine graphicsEngine,
        CancellationToken cancellationToken)
    {
        var stdErrBuffer = new StringBuilder();

        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        Command processWithPipe = processModel.Process;
        foreach (GraphicsEngineContext graphicsEngineContext in processModel.GraphicsEngineContext)
        {
            var pipe = new Pipe();
            processWithPipe = processModel.Process.WithStandardInputPipe(PipeSource.FromStream(pipe.Reader.AsStream()));

            // fire and forget graphics engine task
            _ = graphicsEngine.Run(graphicsEngineContext, pipe.Writer, linkedCts.Token);
        }

        _logger.LogDebug("ffmpeg variant hls arguments {FFmpegArguments}", processWithPipe.Arguments);

        CommandResult commandResult = await processWithPipe
            .WithWorkingDirectory(_folder)
            .WithStandardErrorPipe(PipeTarget.ToStringBuilder(stdErrBuffer))
            .WithValidation(CommandResultValidation.None)
            .ExecuteAsync(linkedCts.Token);

        if (commandResult.ExitCode == 0)
        {
            return true;
        }

        await linkedCts.CancelAsync();

        var errorMessage = stdErrBuffer.ToString();
        if (string.IsNullOrWhiteSpace(errorMessage))
        {
            errorMessage = $"Unknown FFMPEG error; exit code {commandResult.ExitCode}";
        }

        _logger.LogWarning(
            "Variant process for channel {Channel} in {Folder} has terminated unsuccessfully with exit code {ExitCode}: {StandardError}",
            _channelNumber,
            DirectoryName,
            commandResult.ExitCode,
            errorMessage);

        return false;
    }

    private Option<ParsedMediaPlaylist> ReadVariantPlaylist(Option<StreamVariantWindow> maybeWindow)
    {
        // only serve variant content produced for the current window; after a
        // window ends, the schedule rolls to a future window while the previous
        // run's playlist may still be on disk
        StreamVariantWindow window = maybeWindow.IfNoneUnsafe((StreamVariantWindow)null);
        if (window is null || _startedWindowStart != window.Start)
        {
            return Option<ParsedMediaPlaylist>.None;
        }

        string playlistPath = Path.Combine(_folder, "live.m3u8");

        try
        {
            if (!File.Exists(playlistPath))
            {
                return Option<ParsedMediaPlaylist>.None;
            }

            using var fs = new FileStream(
                playlistPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs);
            string contents = reader.ReadToEnd();

            return MediaPlaylistParser.Parse(contents.Split('\n'));
        }
        catch (IOException)
        {
            return Option<ParsedMediaPlaylist>.None;
        }
    }

    private void PrepareFolder()
    {
        try
        {
            if (Directory.Exists(_folder))
            {
                Directory.Delete(_folder, true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Unable to clean variant folder {Folder}", _folder);
        }

        Directory.CreateDirectory(_folder);
    }
}
