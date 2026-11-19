using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ImageVisionApp.Models;
using ImageVisionApp.Services;
using LibVLCSharp.Shared;

namespace ImageVisionApp.ViewModels;

internal readonly record struct VideoPlaybackStatus(
    long Position, long Duration, bool IsPlaying, bool CanPlay, bool CanSeek, string? Notice);

public partial class MainViewModel {
    private readonly record struct VideoSeek(
        VideoPlaybackSession Session, string Path, long Position, long Duration, bool RestartOnPlay,
        bool KeepPosition = false, bool ReadFrame = true);
    private const int VideoTrackReadLimit = 4;
    private const int VideoTrackReadIntervalMilliseconds = 250;

    private VideoPlaybackSession? videoPlayback;
    private DispatcherTimer? videoPlaybackTimer;
    private WriteableBitmap? videoLiveFrame;
    private Bitmap? videoSeekPreview;
    private long videoLiveRevision;
    private bool videoWantsToPlay;
    private bool videoWasPlaying;
    private bool videoRestarting;
    private bool videoRestartOnPlay;
    private bool videoPlaybackIdentityPending;
    private bool mediaPreviewsDisposed;
    private int videoPlaybackGeneration;
    private int videoTrackReadsRemaining;
    private long nextVideoTrackRead;
    private VideoSeek? requestedVideoSeek;
    private bool readingVideoSeek;

    public Bitmap? VideoPreviewFrame => preparedSource is not null
        ? preparedFrom is not null ? preparedOriginal : null
        : livePreviewHandoff is { ReadFrame: true } || requestedVideoSeek is { KeepPosition: true, ReadFrame: true }
            ? null : videoSeekPreview ?? (Bitmap?)videoLiveFrame ?? VideoFirstFrame;
    public int VideoPreviewRevision { get; private set; }
    internal VideoPlaybackStatus PlaybackStatus { get; private set; }

    // подготавливает плеер для уже открытого видео, когда модель получает окно
    internal void AttachVideoPlayback() {
        if (!mediaPreviewsDisposed && videoPlayback is null && CurrentDocument is { Kind: MediaKind.Video, SourcePath: not null } document &&
            VideoFirstFrame is { } firstFrame) {
            if (preparedDetached) {
                if (preparedSource is not null) {
                    RequestPreparedVideo(preparedPosition, preparedAtEnd);
                }
                else if (PreviewSettings.Mode == VideoPreviewMode.Prepared) {
                    BeginPreparedSource(document, preparedPosition, preparedAtEnd);
                }
            }
            if (preparedSource is null && preparedOpening is null && videoEditorWaitingForFrame) {
                AcceptVideoEditorFrame();
            }
            _ = PrepareVideoPlaybackAsync(document, firstFrame.PixelSize);
        }
    }

    // прекращает вывод и инвалидирует подготовку после отсоединения окна от модели
    internal void DetachVideoPlayback() => ReleaseVideoPlayback();

    // запускает подготовку одного плеера за пределами UI и подключает только к активному документу
    private async Task PrepareVideoPlaybackAsync(MediaDocument document, PixelSize visibleSize) {
        int generation = ++videoPlaybackGeneration;
        UpdatePlaybackStatus(new(0, 0, false, false, false, "Подготовка воспроизведения…"));
        VideoPlaybackSession? candidate = null;
        VideoFileIdentity? identity = videoFileIdentity;
        try {
            candidate = await Task.Run(() => {
                identity?.EnsureCurrent(document.SourcePath!);
                return new VideoPlaybackSession(document.SourcePath!, visibleSize);
            });
            if (videoPlaybackGeneration != generation || !ReferenceEquals(CurrentDocument, document)) {
                return;
            }

            identity?.EnsureCurrent(document.SourcePath!);
            if (!candidate.Prepare()) {
                throw new InvalidDataException("Не удалось подготовить плеер");
            }

            videoPlayback = candidate;
            videoPlaybackIdentityPending = identity is not null;
            candidate = null;
            videoTrackReadsRemaining = VideoTrackReadLimit;
            nextVideoTrackRead = 0;
            EnsureVideoPlaybackTimer();
        }
        catch (Exception error) {
            if (videoPlaybackGeneration == generation && ReferenceEquals(CurrentDocument, document)) {
                if (preparedSource is not null) {
                    FailPreparedVideo("не удалось подготовить воспроизведение");
                }
                ReleaseVideoPlayback();
                AcceptVideoEditorFrame();
                UpdatePlaybackStatus(new(0, 0, false, false, false,
                    error is VideoFileChangedException ? error.Message : "Не удалось подготовить воспроизведение"));
            }
        }
        finally {
            candidate?.Dispose();
        }
    }

    // одним таймером публикуем готовые кадры и опрашиваем общий плеер
    private void EnsureVideoPlaybackTimer() {
        if (videoPlaybackTimer is null) {
            videoPlaybackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            videoPlaybackTimer.Tick += VideoPlaybackTimer_OnTick;
            videoPlaybackTimer.Start();
        }
    }

    // переключает воспроизведение по явной команде и позволяет начать заново после окончания
    internal void ToggleVideoPlayback() {
        if (videoPlayback is not { } playback || !PlaybackStatus.CanPlay) {
            return;
        }
        if (preparedOpening is not null || preparedStartPending || livePreviewHandoff is not null ||
            requestedVideoSeek is { KeepPosition: true }) {
            if (videoWantsToPlay) {
                PauseVideoPlayback();
                return;
            }
            videoWantsToPlay = true;
            videoHandoffReplay = preparedOpening is not null || preparedStartPending ? preparedAtEnd :
                livePreviewHandoff is not null ? videoRestartOnPlay : requestedVideoSeek is { RestartOnPlay: true };
            SetVideoEditorProcessingTime(null);
            RefreshVideoPlayback();
            return;
        }
        if (preparedSource is not null) {
            TogglePreparedVideo(playback);
            return;
        }

        if (PlaybackStatus.IsPlaying) {
            PauseVideoPlayback();
            return;
        }

        videoWantsToPlay = true;
        videoEditorPauseObservation++;
        SetVideoEditorProcessingTime(null);
        if (playback.State == VLCState.Ended || videoRestartOnPlay) {
            RestartLiveVideo(playback);
        }
        RefreshVideoPlayback();
    }

    // начинаем новый проход в том же живом плеере после последнего кадра
    private void RestartLiveVideo(VideoPlaybackSession playback) {
        livePreviewAnchor = null;
        videoRestartOnPlay = false;
        InvalidateVideoEditorSource();
        videoRestarting = TryRestartCurrentVideo(playback);
        if (!videoRestarting) {
            videoWantsToPlay = false;
            ShowVideoEditorSourceError(PlaybackStatus.Notice == VideoFileIdentity.ChangedMessage
                ? VideoFileIdentity.ChangedMessage : "Не удалось получить видеокадр");
        }
    }

    // прекращает вывод звука и продвижение общего времени при смене вкладки
    internal void PauseVideoPlayback() {
        videoHandoffReplay = false;
        if (preparedSource is not null) {
            PausePreparedVideo(videoPlayback);
            return;
        }
        videoWantsToPlay = false;
        StopVideoEditorLiveInput();
        if (videoPlayback is { } playback) {
            playback.SetPaused(true);
            RefreshVideoPlayback();
        }
        RetryDeferredVideoEditorFrame();
    }

    // внутри ролика сохраняем состояние плеера, на паузе и у правой границы читаем подтвержденный кадр
    internal void SeekVideoByKeyboard(long offset) {
        if (videoPlayback is not { } playback || !PlaybackStatus.CanSeek) {
            return;
        }

        long position = Math.Clamp(PlaybackStatus.Position + offset, 0, PlaybackStatus.Duration);
        if (SeekDuringPreviewModeHandoff(position, PlaybackStatus.Duration,
            preserveLivePlay: position < PlaybackStatus.Duration)) {
            return;
        }
        if (preparedSource is not null) {
            RequestPreparedVideo(position, position == PlaybackStatus.Duration, reuseReady: true);
            return;
        }
        bool isPlaying = PlaybackStatus.IsPlaying;
        if (isPlaying && position < PlaybackStatus.Duration) {
            livePreviewAnchor = null;
            InvalidateVideoEditorSource();
            playback.Seek(position);
            UpdatePlaybackStatus(PlaybackStatus with { Position = position });
        }
        else {
            RequestVideoSeek(position, restartOnPlay: isPlaying);
        }
    }

    // принимает последнее запрошенное время, у конца по стрелке сохраняет возможность повтора
    internal void RequestVideoSeek(long position, bool restartOnPlay = false) {
        if (videoPlayback is not { } playback || CurrentDocument?.SourcePath is not { } sourcePath ||
            !PlaybackStatus.CanSeek) {
            return;
        }

        long duration = PlaybackStatus.Duration;
        long target = Math.Clamp(position, 0, duration);
        if (SeekDuringPreviewModeHandoff(target, duration)) {
            return;
        }
        if (preparedSource is not null) {
            RequestPreparedVideo(target, target == duration, reuseReady: true);
            return;
        }
        if (target == duration && (videoRestartOnPlay || requestedVideoSeek is { RestartOnPlay: true })) {
            // повтор стрелки сохраняет чтение последнего кадра и запланированный повтор
            return;
        }

        videoRestartOnPlay = false;
        livePreviewAnchor = null;
        requestedVideoSeek = new VideoSeek(playback, sourcePath, target, duration, restartOnPlay);
        InvalidateVideoEditorSource();
        PauseVideoPlayback();
        if (!readingVideoSeek) {
            _ = ReadVideoSeeksAsync();
        }
    }

    // готовит кадры последовательно, пропуская промежуточные запросы и результаты старого документа
    private async Task ReadVideoSeeksAsync() {
        readingVideoSeek = true;
        try {
            while (requestedVideoSeek is { } request) {
                Bitmap? preparedFrame = null;
                try {
                    long seekPosition = request.Position;
                    if (request.ReadFrame) {
                        VideoFileIdentity? identity = videoFileIdentity;
                        (byte[] png, long framePosition) = await Task.Run(() => {
                            identity?.EnsureCurrent(request.Path);
                            var loaded = VideoFirstFrameReader.ReadPreview(request.Path, request.Position, request.Duration);
                            identity?.EnsureCurrent(request.Path);
                            return loaded;
                        });
                        if (requestedVideoSeek != request || !ReferenceEquals(videoPlayback, request.Session)) {
                            continue;
                        }

                        using MemoryStream stream = new(png);
                        preparedFrame = new Bitmap(stream);
                        if (!request.KeepPosition) {
                            seekPosition = framePosition;
                        }
                    }
                    if (request.KeepPosition && request.Duration > 0) {
                        seekPosition = Math.Min(seekPosition, request.Duration - 1);
                    }
                    if (request.Session.State == VLCState.Ended) {
                        videoRestarting = TryRestartCurrentVideo(request.Session);
                        if (!videoRestarting) {
                            throw new InvalidDataException("Не удалось возобновить чтение видео");
                        }

                    }

                    await WaitForPausedVideoAsync(request);
                    if (requestedVideoSeek != request || !ReferenceEquals(videoPlayback, request.Session)) {
                        continue;
                    }

                    request.Session.Seek(seekPosition);
                    await WaitForPausedVideoAsync(request, seekPosition);
                    if (requestedVideoSeek != request || !ReferenceEquals(videoPlayback, request.Session)) {
                        continue;
                    }

                    videoWasPlaying = false;
                    requestedVideoSeek = null;
                    if (preparedFrame is not null) {
                        SetVideoSeekPreview(preparedFrame);
                        preparedFrame = null;
                    }
                    videoRestartOnPlay = request.RestartOnPlay;
                    UpdatePlaybackStatus(PlaybackStatus with {
                        Position = request.KeepPosition ? request.Position : seekPosition,
                        IsPlaying = videoWantsToPlay, CanPlay = true, Notice = preparedUnavailable
                    });
                    AcceptVideoEditorFrame();
                }
                catch (Exception error) {
                    if (requestedVideoSeek == request) {
                        requestedVideoSeek = null;
                        videoWantsToPlay = false;
                        videoHandoffReplay = false;
                        bool changed = error is VideoFileChangedException || PlaybackStatus.Notice == VideoFileIdentity.ChangedMessage;
                        UpdatePlaybackStatus(PlaybackStatus with {
                            IsPlaying = false, CanPlay = true,
                            Notice = changed ? VideoFileIdentity.ChangedMessage : "Не удалось перейти к кадру"
                        });
                        ShowVideoEditorSourceError(changed ? VideoFileIdentity.ChangedMessage : "Не удалось получить видеокадр");
                    }
                }
                finally {
                    preparedFrame?.Dispose();
                }
            }
        }
        finally {
            readingVideoSeek = false;
        }
    }

    // ждёт подтверждения паузы, включая случай повторного открытия после конца видео
    private async Task WaitForPausedVideoAsync(VideoSeek request, long? framePosition = null) {
        long deadline = Environment.TickCount64 + 3000;
        while (requestedVideoSeek == request && ReferenceEquals(videoPlayback, request.Session)) {
            if (request.Session.State == VLCState.Paused && request.Session.CanSeek &&
                (framePosition is null || Math.Abs(request.Session.Position - framePosition.Value) <= 25)) {
                return;
            }
            if (request.Session.State == VLCState.Error || Environment.TickCount64 >= deadline) {
                throw new InvalidDataException("Источник видео не подготовился к перемотке");
            }
            await Task.Delay(25);
        }
    }

    // получает состояние источника и новые кадры с общей частотой для всех вкладок
    private void VideoPlaybackTimer_OnTick(object? sender, EventArgs e) => RefreshVideoPlayback();

    // согласует состояние native-плеера с командами пользователя и публикует один снимок UI
    private void RefreshVideoPlayback() {
        if (!ConfirmOpenedVideoIdentity() || WaitForVideoModeHandoff(videoPlayback)) {
            return;
        }
        if (videoPlayback is not { } playback) {
            if (preparedSource is not null && !preparedDetached) {
                RefreshPreparedBeforePlayer();
            }
            else {
                RetryVideoEditorAfterPreparedBudget();
            }
            return;
        }

        VLCState state = playback.State;
        if (preparedSource is not null && state != VLCState.Error) {
            RefreshPreparedVideo(playback);
            return;
        }
        if (state == VLCState.Error) {
            if (preparedSource is not null || preparedOpening is not null) {
                FailPreparedVideo("не удалось воспроизвести видео");
            }
            ReleaseVideoPlayback();
            AcceptVideoEditorFrame();
            UpdatePlaybackStatus(PlaybackStatus with { Notice = "Не удалось воспроизвести видео" });
            return;
        }

        if (videoHandoffReplay && requestedVideoSeek is null) {
            videoHandoffReplay = false;
            RestartLiveVideo(playback);
            if (videoRestarting) {
                livePreviewAnchor = 0;
                ShowFirstLiveVideoFrame();
            }
            state = playback.State;
        }

        bool isPlaying = state == VLCState.Playing;
        bool allowPlay = videoWantsToPlay && requestedVideoSeek is null;
        if (isPlaying && !allowPlay) {
            playback.SetPaused(true);
        }
        else if (state == VLCState.Paused && allowPlay) {
            playback.SetPaused(false);
        }

        if (state == VLCState.Paused || isPlaying) {
            videoRestarting = false;
        }
        bool ended = state == VLCState.Ended && !videoRestarting;
        bool justEnded = ended && videoWantsToPlay;
        if (ended && !videoHandoffReplay) {
            videoWantsToPlay = false;
        }

        bool live = isPlaying && videoWantsToPlay && requestedVideoSeek is null && !videoRestarting;
        UpdateVideoPreviewMonitoring(live);
        if (requestedVideoSeek is null && (isPlaying || videoWasPlaying)) {
            PublishVideoFrame(playback, live);
        }
        RetryVideoEditorAfterPreparedBudget(live);
        videoWasPlaying = isPlaying;
        if (!videoWantsToPlay) {
            KeepVideoEditorResultForPause();
            if (justEnded) {
                RetryDeferredVideoEditorFrame();
            }
        }

        long duration = Math.Max(PlaybackStatus.Duration, playback.Duration);
        if (livePreviewAnchor is { } anchor && isPlaying && allowPlay && playback.Position >= anchor) {
            livePreviewAnchor = null;
        }
        long position = ended ? duration : livePreviewAnchor ?? Math.Clamp(playback.Position, 0, Math.Max(0, duration));
        bool preparing = state is VLCState.NothingSpecial or VLCState.Opening or VLCState.Buffering || videoRestarting;
        bool ready = isPlaying || state == VLCState.Paused || ended;
        if (requestedVideoSeek is { } seek) {
            UpdatePlaybackStatus(new(seek.Position, duration, seek.KeepPosition && videoWantsToPlay,
                seek.KeepPosition && (ready || videoWantsToPlay), true, "Переход к кадру…"));
        }
        else {
            string? notice = preparing ? "Подготовка воспроизведения…" :
                PlaybackStatus.Notice is "Не удалось перейти к кадру" or VideoFileIdentity.ChangedMessage
                    ? PlaybackStatus.Notice : preparedUnavailable;
            UpdatePlaybackStatus(new(position, duration, videoWantsToPlay, ready && !videoRestarting,
                duration > 0 && (playback.CanSeek || ended), notice));
        }

        if (ready) {
            UpdateVideoInformation(playback, duration);
        }
    }

    // VLC открывает путь асинхронно, поэтому проверяем версию и при первом готовом состоянии
    private bool ConfirmOpenedVideoIdentity() {
        if (!videoPlaybackIdentityPending || videoPlayback?.State is not (VLCState.Paused or VLCState.Playing or VLCState.Ended)) {
            return true;
        }
        videoPlaybackIdentityPending = false;
        try {
            if (CurrentDocument?.SourcePath is { } path) {
                videoFileIdentity?.EnsureCurrent(path);
            }
            return true;
        }
        catch (VideoFileChangedException error) {
            if (preparedSource is not null || preparedOpening is not null) {
                FailPreparedVideo(error.Message);
            }
            ReleaseVideoPlayback();
            AcceptVideoEditorFrame();
            UpdatePlaybackStatus(PlaybackStatus with { Notice = error.Message });
            return false;
        }
    }

    // проверяем файл перед операциями, заново открывающими путь в общей сессии
    private bool TryRestartCurrentVideo(VideoPlaybackSession playback) {
        try {
            if (CurrentDocument?.SourcePath is { } path) {
                videoFileIdentity?.EnsureCurrent(path);
            }
            videoPlaybackIdentityPending = videoFileIdentity is not null;
            return playback.Restart();
        }
        catch (VideoFileChangedException error) {
            UpdatePlaybackStatus(PlaybackStatus with { Notice = error.Message });
            return false;
        }
    }

    // дополняем сведения действующего документа длительностью и дорожками готового плеера
    private void UpdateVideoInformation(VideoPlaybackSession playback, long duration) {
        if (!ReferenceEquals(videoPlayback, playback) || CurrentVideoInfo is not { } currentInfo) {
            return;
        }

        VideoInfo nextInfo = duration > 0 && currentInfo.DurationMilliseconds != duration
            ? currentInfo with { DurationMilliseconds = duration } : currentInfo;
        long now = Environment.TickCount64;
        if (videoTrackReadsRemaining > 0 && now >= nextVideoTrackRead) {
            videoTrackReadsRemaining--;
            nextVideoTrackRead = now + VideoTrackReadIntervalMilliseconds;
            try {
                if (playback.TryReadVideoTracks(nextInfo) is { } infoWithTracks) {
                    nextInfo = infoWithTracks;
                    videoTrackReadsRemaining = 0;
                }
            }
            catch (Exception) {
                // необязательные сведения остаются неизвестными при ошибке запроса к источнику
            }
        }

        if (nextInfo != currentInfo) {
            CurrentVideoInfo = nextInfo;
            OnPropertyChanged(nameof(CurrentVideoInfo));
        }
    }

    // показывает новый живой кадр и освобождает старое изображение после обновления привязок
    private void PublishVideoFrame(VideoPlaybackSession playback, bool live) {
        WriteableBitmap? previous = videoLiveFrame;
        if (!playback.CopyFrame(ref videoLiveFrame, ref videoLiveRevision)) {
            return;
        }

        if (videoSeekPreview is not null || !ReferenceEquals(previous, videoLiveFrame)) {
            SetVideoSeekPreview(null);
        }
        if (!ReferenceEquals(previous, videoLiveFrame)) {
            previous?.Dispose();
        }
        VideoPreviewRevision++;
        OnPropertyChanged(nameof(VideoPreviewRevision));
        AcceptVideoEditorFrame(live);
    }

    // при возврате к нулю убираем более старые картинки, чтобы выбрать первый кадр документа
    private void ShowFirstLiveVideoFrame() {
        WriteableBitmap? previous = videoLiveFrame;
        videoLiveFrame = null;
        SetVideoSeekPreview(null);
        previous?.Dispose();
    }

    // меняет неподвижный кадр перемотки и возвращает живой кадр только после первого обновления
    private void SetVideoSeekPreview(Bitmap? bitmap) {
        Bitmap? previous = videoSeekPreview;
        videoSeekPreview = bitmap;
        OnPropertyChanged(nameof(VideoPreviewFrame));
        previous?.Dispose();
    }

    // обновляет таймлайн одним связным изменением
    private void UpdatePlaybackStatus(VideoPlaybackStatus status) {
        if (PlaybackStatus != status) {
            PlaybackStatus = status;
            OnPropertyChanged(nameof(PlaybackStatus));
        }
    }

    // прекращает публикацию и освобождает один источник перед заменой файла или закрытием окна
    private void ReleaseVideoPlayback() {
        DetachPreparedVideo();
        CancelPreviewModeHandoff();
        videoPlaybackGeneration++;
        InvalidateVideoEditorSource();
        if (videoPlaybackTimer is not null) {
            videoPlaybackTimer.Stop();
            videoPlaybackTimer.Tick -= VideoPlaybackTimer_OnTick;
            videoPlaybackTimer = null;
        }

        requestedVideoSeek = null;
        videoWantsToPlay = false;
        videoWasPlaying = false;
        videoRestarting = false;
        videoRestartOnPlay = false;
        videoPlaybackIdentityPending = false;
        VideoPlaybackSession? previous = videoPlayback;
        videoPlayback = null;
        previous?.Dispose();

        WriteableBitmap? previousFrame = videoLiveFrame;
        videoLiveFrame = null;
        videoLiveRevision = 0;
        SetVideoSeekPreview(null);
        previousFrame?.Dispose();
        UpdatePlaybackStatus(default);
    }
}
