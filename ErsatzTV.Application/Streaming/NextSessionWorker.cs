using System.IO.Abstractions;
using CliWrap;
using ErsatzTV.Application.Playouts;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Plex;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Next.Config;
using ErsatzTV.Core.Plex;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ErsatzTV.Application.Streaming;

public class NextSessionWorker(
    string channelBinary,
    ChannelConfig channelConfig,
    IFileSystem fileSystem,
    ILocalFileSystem localFileSystem,
    IServiceScopeFactory serviceScopeFactory,
    ILogger<NextSessionWorker> logger)
    : IHlsSessionWorker
{
    private readonly TimeSpan _checkpointThreshold = TimeSpan.FromMinutes(5);

    private readonly SemaphoreSlim _slim = new(1, 1);
    private CancellationTokenSource _cancellationTokenSource;
    private IServiceScope _serviceScope = serviceScopeFactory.CreateScope();
    private bool _disposedValue;
    private string _channelNumber;
    private string _workingDirectory;
    private string _heartbeatFileName;
    private DateTimeOffset _lastTouch;
    private DateTimeOffset _lastCheckpoint;
    private ChannelPlayoutMode _channelPlayoutMode = ChannelPlayoutMode.Continuous;

    private IMediator Mediator => _serviceScope.ServiceProvider.GetRequiredService<IMediator>();

    void IDisposable.Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposedValue)
        {
            if (disposing)
            {
                // disposing a worker must kill its process: disposing only
                // the scope left the ersatztv-channel process running with
                // no owner, and an orphaned worker kept writing its output
                // folder alongside its replacement for eleven hours on
                // 2026-08-10. Cancellation makes CliWrap kill the child
                // tree; a worker that never ran has no token and nothing
                // to kill.
                try
                {
                    _cancellationTokenSource?.Cancel();
                }
                catch (ObjectDisposedException)
                {
                    // already torn down by a completed Run
                }

                _serviceScope.Dispose();
                _serviceScope = null;
            }

            _disposedValue = true;
        }
    }

    public async Task Cancel(CancellationToken cancellationToken)
    {
        logger.LogInformation("API termination request for HLS session for channel {Channel}", _channelNumber);

        await _slim.WaitAsync(cancellationToken);
        try
        {
            await _cancellationTokenSource.CancelAsync();
        }
        finally
        {
            _slim.Release();
        }
    }

    public void Touch(Option<string> fileName)
    {
        _lastTouch = DateTimeOffset.Now;

        if (!fileSystem.File.Exists(_heartbeatFileName))
        {
            fileSystem.File.WriteAllBytes(_heartbeatFileName, []);
        }
        else
        {
            fileSystem.File.SetLastWriteTimeUtc(_heartbeatFileName, DateTime.UtcNow);
        }
    }

    public Task<Option<TrimPlaylistResult>> TrimPlaylist(
        DateTimeOffset filterBefore,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void PlayoutUpdated()
    {
        // nothing to do here; channel binary should detect that by itself
    }

    public HlsSessionModel GetModel() => throw new NotSupportedException();

    public async Task Run(
        string channelNumber,
        Option<TimeSpan> idleTimeout,
        CancellationToken incomingCancellationToken)
    {
        _cancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(incomingCancellationToken);
        using var checkpointCts = CancellationTokenSource.CreateLinkedTokenSource(_cancellationTokenSource.Token);
        Task checkpointLoop = Task.CompletedTask;

        DateTimeOffset sessionStart = DateTimeOffset.Now;
        _lastTouch = sessionStart;
        _lastCheckpoint = _lastTouch;

        try
        {
            _channelNumber = channelNumber;
            _workingDirectory = fileSystem.Path.Combine(FileSystemLayout.TranscodeFolder, _channelNumber);
            _heartbeatFileName = fileSystem.Path.Combine(_workingDirectory, ".heartbeat");

            Option<PlayoutModeViewModel> maybePlayout = await Mediator.Send(
                new GetPlayoutModeByChannelNumber(_channelNumber),
                _cancellationTokenSource.Token);

            foreach (PlayoutModeViewModel playout in maybePlayout)
            {
                _channelPlayoutMode = playout.PlayoutMode;

                if (_channelPlayoutMode is ChannelPlayoutMode.OnDemand)
                {
                    checkpointLoop = CheckpointLoop(checkpointCts.Token);

                    await Mediator.Send(
                        new TimeShiftOnDemandPlayout(playout.PlayoutId, sessionStart, true),
                        _cancellationTokenSource.Token);

                    // next reads serialized playout files rather than the database, so ensure it
                    // sees the time-shifted items before starting the channel process
                    await Mediator.Send(
                        new SyncNextPlayout(_channelNumber),
                        _cancellationTokenSource.Token);
                }
            }

            List<string> arguments = ["run", "--output-folder", _workingDirectory, "--number", channelNumber, "-"];

            string defaultOverlayFile = fileSystem.Path.Combine(
                FileSystemLayout.NextChannelConfigOverlaysFolder,
                "default.json");
            if (fileSystem.File.Exists(defaultOverlayFile))
            {
                arguments.Add(defaultOverlayFile);
            }

            string channelOverlayFile = fileSystem.Path.Combine(
                FileSystemLayout.NextChannelConfigOverlaysFolder,
                $"{channelNumber}.json");
            if (fileSystem.File.Exists(channelOverlayFile))
            {
                arguments.Add(channelOverlayFile);
            }

            CommandResult commandResult = await Cli.Wrap(channelBinary)
                .WithArguments(arguments)
                .WithEnvironmentVariables(await PlexTokenEnvironment())
                .WithStandardInputPipe(PipeSource.FromString(channelConfig.ToJson()))
                .WithStandardOutputPipe(PipeTarget.ToDelegate(l => NextLogger.LogNextLine(l, logger)))
                .WithStandardErrorPipe(PipeTarget.ToDelegate(l => NextLogger.LogNextLine(l, logger)))
                //.WithStandardOutputPipe(PipeTarget.ToDelegate(progressParser.ParseLine))
                .WithValidation(CommandResultValidation.None)
                .ExecuteAsync(_cancellationTokenSource.Token);

            if (commandResult.ExitCode != 0)
            {
                await _cancellationTokenSource.CancelAsync();

                logger.LogError(
                    "ErsatzTV Next session for channel {Channel} has terminated unsuccessfully with exit code {ExitCode}",
                    _channelNumber,
                    commandResult.ExitCode);
            }
            else
            {
                logger.LogDebug("ErsatzTV Next session has completed for channel {Channel}", _channelNumber);
            }
        }
        catch (Exception ex) when (ex is TaskCanceledException or OperationCanceledException)
        {
            logger.LogInformation("Terminating ErsatzTV Next session for channel {Channel}", _channelNumber);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Error running ErsatzTV Next session");
        }
        finally
        {
            await checkpointCts.CancelAsync();
            try
            {
                await checkpointLoop;
            }
            catch (OperationCanceledException)
            {
                // do nothing
            }

            try
            {
                await UpdateOnDemandCheckpoint(CancellationToken.None);
            }
            catch
            {
                // do nothing
            }

            try
            {
                localFileSystem.EmptyFolder(_workingDirectory);
            }
            catch
            {
                // do nothing
            }
        }
    }

    public async Task WaitForPlaylistSegments(int initialSegmentCount, CancellationToken cancellationToken)
    {
        string readyFileName = fileSystem.Path.Combine(_workingDirectory, ".ready");

        logger.LogDebug("Waiting for ErsatzTV Next channel to be ready");
        while (!fileSystem.File.Exists(readyFileName))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100), cancellationToken);
        }
    }

    /// <summary>
    ///     The plex auth token for each plex media source, named as the playout templates
    ///     reference it.
    /// </summary>
    /// <remarks>
    ///     Playouts emit plex urls with the token as an {{ETV_PLEX_TOKEN_id}} template rather than
    ///     the token itself, so the secret never lands in a playout file. This process already
    ///     holds those tokens and starts the worker, so it supplies them directly; requiring an
    ///     operator to copy them into container configuration would duplicate a secret that is
    ///     already stored, and leave it stale the moment plex reissues one.
    ///     Variant workers inherit this environment from the worker that spawns them.
    /// </remarks>
    private async Task<IReadOnlyDictionary<string, string>> PlexTokenEnvironment()
    {
        var result = new Dictionary<string, string>();

        try
        {
            var mediaSourceRepository =
                _serviceScope.ServiceProvider.GetRequiredService<IMediaSourceRepository>();
            var plexSecretStore = _serviceScope.ServiceProvider.GetRequiredService<IPlexSecretStore>();

            foreach (PlexMediaSource mediaSource in await mediaSourceRepository.GetAllPlex())
            {
                Option<PlexServerAuthToken> maybeToken =
                    await plexSecretStore.GetServerAuthToken(mediaSource.ClientIdentifier);

                foreach (PlexServerAuthToken token in maybeToken)
                {
                    result[$"ETV_PLEX_TOKEN_{mediaSource.Id}"] = token.AuthToken;
                }
            }
        }
        catch (Exception ex)
        {
            // an item that needs a token fails to black filler and names the missing variable, so
            // the channel keeps running and the log says what went wrong
            logger.LogWarning(ex, "Unable to read plex tokens for ErsatzTV Next session");
        }

        return result;
    }

    private async Task CheckpointLoop(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_checkpointThreshold);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            try
            {
                if (_lastTouch > _lastCheckpoint)
                {
                    await UpdateOnDemandCheckpoint(cancellationToken);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to update on demand checkpoint for channel {Channel}", _channelNumber);
            }
        }
    }

    private async Task UpdateOnDemandCheckpoint(CancellationToken cancellationToken)
    {
        if (_channelPlayoutMode is ChannelPlayoutMode.OnDemand)
        {
            await Mediator.Send(
                new UpdateOnDemandCheckpoint(_channelNumber, _lastTouch),
                cancellationToken);
        }

        _lastCheckpoint = _lastTouch;
    }
}
