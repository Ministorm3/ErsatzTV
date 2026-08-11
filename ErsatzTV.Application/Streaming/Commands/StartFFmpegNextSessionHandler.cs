using System.Globalization;
using System.IO.Abstractions;
using System.Threading.Channels;
using ErsatzTV.Application.Channels;
using ErsatzTV.Application.FFmpegProfiles;
using ErsatzTV.Application.Graphics;
using ErsatzTV.Application.Maintenance;
using ErsatzTV.Core;
using ErsatzTV.Core.Domain;
using ErsatzTV.Core.Errors;
using ErsatzTV.Core.FFmpeg;
using ErsatzTV.Core.Interfaces.FFmpeg;
using ErsatzTV.Core.Interfaces.Metadata;
using ErsatzTV.Core.Interfaces.Repositories;
using ErsatzTV.Core.Next.Config;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Subtitle = ErsatzTV.Core.Next.Config.Subtitle;

namespace ErsatzTV.Application.Streaming;

public class StartFFmpegNextSessionHandler(
    IServiceScopeFactory serviceScopeFactory,
    IFileSystem fileSystem,
    ILocalFileSystem localFileSystem,
    IFFmpegSegmenterService ffmpegSegmenterService,
    IChannelConfigConverter channelConfigConverter,
    IConfigElementRepository configElementRepository,
    IHostApplicationLifetime hostApplicationLifetime,
    IMediator mediator,
    SystemStartup systemStartup,
    ChannelWriter<IBackgroundServiceRequest> workerChannel,
    ILogger<StartFFmpegNextSessionHandler> logger,
    ILogger<NextSessionWorker> sessionWorkerLogger)
    : NextChannelHandlerBase(fileSystem), IRequestHandler<StartFFmpegNextSession, Either<BaseError, string>>
{
    private readonly IFileSystem _fileSystem = fileSystem;

    public async Task<Either<BaseError, string>> Handle(
        StartFFmpegNextSession request,
        CancellationToken cancellationToken)
    {
        // the boot-time transcode purge deletes the per-channel folders; a
        // session that starts before it finishes has its folder deleted out
        // from under its freshly spawned worker
        await systemStartup.WaitForTranscodeFolder(cancellationToken);

        Either<BaseError, string> result;
        try
        {
            result = await Validate(request, cancellationToken)
                .MapT(validationResult => StartProcess(request, validationResult, cancellationToken))
                // this weirdness is needed to maintain the error type (.ToEitherAsync() just gives BaseError)
#pragma warning disable VSTHRD103
                .Bind(v => v.ToEither().MapLeft(seq => seq.Head()).MapAsync<BaseError, Task<string>, string>(identity));
#pragma warning restore VSTHRD103
        }
        catch
        {
            ffmpegSegmenterService.RemoveReservation(request.ChannelNumber);
            throw;
        }

        // a start that failed after reserving the channel must release the
        // null reservation, or the channel is bricked until restart.
        // ChannelSessionAlreadyActive is excluded: on that path the
        // reservation is a CONCURRENT start's, and removing it would reopen
        // the very race the atomic reservation closes. RemoveReservation
        // never touches an activated worker either way.
        foreach (BaseError error in result.LeftToSeq())
        {
            if (error is not ChannelSessionAlreadyActive)
            {
                ffmpegSegmenterService.RemoveReservation(request.ChannelNumber);
            }
        }

        return result;
    }

    private async Task<string> StartProcess(
        StartFFmpegNextSession request,
        ValidationResult validationResult,
        CancellationToken cancellationToken)
    {
        Option<TimeSpan> idleTimeout = Option<TimeSpan>.None;

        // Option<FrameRate> targetFramerate = await mediator.Send(
        //     new GetChannelFramerate(request.ChannelNumber),
        //     cancellationToken);

        // only load timeout when needed
        if (validationResult.Channel.IdleBehavior is not ChannelIdleBehavior.KeepRunning)
        {
            idleTimeout = await configElementRepository
                .GetValue<int>(ConfigElementKey.FFmpegSegmenterTimeout, cancellationToken)
                .Map(maybeTimeout => maybeTimeout.Match(i => TimeSpan.FromSeconds(i), () => TimeSpan.FromMinutes(1)));
        }

        await mediator.Send(new RefreshGraphicsElements(), cancellationToken);

        ChannelConfig config = await channelConfigConverter.ToNext(
            validationResult.Channel,
            validationResult.FfmpegProfile,
            cancellationToken);

        NextSessionWorker worker = new NextSessionWorker(
            validationResult.ChannelBinary,
            config,
            _fileSystem,
            localFileSystem,
            serviceScopeFactory,
            sessionWorkerLogger);

        if (!ffmpegSegmenterService.TryActivateWorker(request.ChannelNumber, worker))
        {
            // another start owns the channel; this worker never ran, so
            // dispose it and hand the caller the playlist like any
            // already-active request
            ((IDisposable)worker).Dispose();
            logger.LogWarning(
                "Channel {ChannelNumber} session was activated by a concurrent start; discarding this one",
                request.ChannelNumber);
            return await GetMultiVariantPlaylist(request);
        }

        // fire and forget worker
        _ = worker.Run(request.ChannelNumber, idleTimeout, hostApplicationLifetime.ApplicationStopping)
            .ContinueWith(
                _ =>
                {
                    // remove and dispose only THIS worker: a dying worker
                    // must never deregister or dispose a replacement that
                    // now owns the channel
                    ffmpegSegmenterService.RemoveWorker(request.ChannelNumber, worker);

                    ((IDisposable)worker).Dispose();

                    workerChannel.TryWrite(new ReleaseMemory(false));
                },
                TaskScheduler.Default);

        int initialSegmentCount = await configElementRepository
            .GetValue<int>(ConfigElementKey.FFmpegInitialSegmentCount, cancellationToken)
            .Map(maybeCount => maybeCount.Match(identity, () => 1));

        await worker.WaitForPlaylistSegments(initialSegmentCount, cancellationToken);

        return await GetMultiVariantPlaylist(request);
    }

    private Task<Validation<BaseError, ValidationResult>> Validate(
        StartFFmpegNextSession request,
        CancellationToken cancellationToken) =>
        SessionMustBeInactive(request)
            .BindT(_ => FolderMustBeEmpty(request))
            .BindT(_ => ChannelBinaryMustExist())
            .BindT(channelBinary => ChannelMustExist(request, new ValidationResult(channelBinary, null, null), cancellationToken))
            .BindT(result => FFmpegProfileMustExist(result, cancellationToken));

    private async Task<Validation<BaseError, Unit>> SessionMustBeInactive(StartFFmpegNextSession request)
    {
        var result = Optional(ffmpegSegmenterService.TryAddWorker(request.ChannelNumber, null))
            .Where(success => success)
            .Map(_ => Unit.Default)
            .ToValidation<BaseError>(new ChannelSessionAlreadyActive(await GetMultiVariantPlaylist(request)));

        if (result.IsFail && ffmpegSegmenterService.TryGetWorker(
                request.ChannelNumber,
                out IHlsSessionWorker worker))
        {
            worker?.Touch(Option<string>.None);
        }

        return result;
    }

    private Task<Validation<BaseError, Unit>> FolderMustBeEmpty(StartFFmpegNextSession request)
    {
        string folder = Path.Combine(FileSystemLayout.TranscodeFolder, request.ChannelNumber);
        logger.LogDebug("Preparing transcode folder {Folder}", folder);

        localFileSystem.EnsureFolderExists(folder);
        localFileSystem.EmptyFolder(folder);

        return Task.FromResult<Validation<BaseError, Unit>>(Unit.Default);
    }

    private async Task<Validation<BaseError, ValidationResult>> ChannelMustExist(
        StartFFmpegNextSession request,
        ValidationResult result,
        CancellationToken cancellationToken)
    {
        Option<ChannelViewModel> maybeChannel = await mediator.Send(
            new GetChannelByNumber(request.ChannelNumber),
            cancellationToken);

        foreach (ChannelViewModel channel in maybeChannel)
        {
            return result with { Channel = channel };
        }

        return BaseError.New($"Channel number {request.ChannelNumber} does not exist");
    }

    private async Task<Validation<BaseError, ValidationResult>> FFmpegProfileMustExist(
        ValidationResult result,
        CancellationToken cancellationToken)
    {
        Option<FFmpegProfileViewModel> maybeFFmpegProfile = await mediator.Send(
            new GetFFmpegProfileById(result.Channel.FFmpegProfileId),
            cancellationToken);

        foreach (FFmpegProfileViewModel ffmpegProfile in maybeFFmpegProfile)
        {
            return result with { FfmpegProfile = ffmpegProfile };
        }

        return BaseError.New($"FFmpeg profile {result.Channel.FFmpegProfileId} not exist");
    }

    private async Task<string> GetMultiVariantPlaylist(StartFFmpegNextSession request)
    {
        var variantPlaylist =
            $"{request.Scheme}://{request.Host}{request.PathBase}/iptv/session/{request.ChannelNumber}/live.m3u8{request.PlaylistQuery}";

        var subtitlePlaylist =
            $"{request.Scheme}://{request.Host}{request.PathBase}/iptv/session/{request.ChannelNumber}/live_sub.m3u8{request.PlaylistQuery}";

        Option<ChannelStreamingSpecsViewModel> maybeStreamingSpecs =
            await mediator.Send(new GetChannelStreamingSpecs(request.ChannelNumber));
        string resolution = string.Empty;
        var bitrate = "10000000";
        foreach (ChannelStreamingSpecsViewModel streamingSpecs in maybeStreamingSpecs)
        {
            string videoCodec = streamingSpecs.VideoFormat switch
            {
                FFmpegProfileVideoFormat.Av1 => "av01.0.01M.08",
                FFmpegProfileVideoFormat.Hevc => "hvc1.1.6.L93.B0",
                FFmpegProfileVideoFormat.H264 => "avc1.4D4028",
                _ => string.Empty
            };

            string audioCodec = streamingSpecs.AudioFormat switch
            {
                FFmpegProfileAudioFormat.Ac3 => "ac-3",
                FFmpegProfileAudioFormat.Aac or FFmpegProfileAudioFormat.AacLatm => "mp4a.40.2",
                _ => string.Empty
            };

            List<string> codecStrings = [];
            if (!string.IsNullOrWhiteSpace(videoCodec))
            {
                codecStrings.Add(videoCodec);
            }

            if (!string.IsNullOrWhiteSpace(audioCodec))
            {
                codecStrings.Add(audioCodec);
            }

            string codecs = codecStrings.Count > 0 ? $",CODECS=\"{string.Join(",", codecStrings)}\"" : string.Empty;
            resolution = $",RESOLUTION={streamingSpecs.Width}x{streamingSpecs.Height}{codecs}";
            bitrate = streamingSpecs.Bitrate.ToString(CultureInfo.InvariantCulture);
        }

        return $@"#EXTM3U
#EXT-X-VERSION:6
#EXT-X-MEDIA:TYPE=SUBTITLES,GROUP-ID=""subs"",NAME=""English"",DEFAULT=YES,AUTOSELECT=YES,FORCED=NO,LANGUAGE=""en"",URI=""{subtitlePlaylist}""
#EXT-X-STREAM-INF:BANDWIDTH={bitrate}{resolution}
{variantPlaylist}";
    }

    private sealed record ValidationResult(
        string ChannelBinary,
        ChannelViewModel Channel,
        FFmpegProfileViewModel FfmpegProfile);
}
