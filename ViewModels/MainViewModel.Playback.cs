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
        VideoPlaybackSession Session, string Path, long Position, long Duration, bool RestartOnPlay);
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
    private bool mediaPreviewsDisposed;
    private int videoPlaybackGeneration;
    private int videoTrackReadsRemaining;
    private long nextVideoTrackRead;
    private VideoSeek? requestedVideoSeek;
    private bool readingVideoSeek;

    public Bitmap? VideoPreviewFrame => videoSeekPreview ?? (Bitmap?)videoLiveFrame ?? VideoFirstFrame;
    public int VideoPreviewRevision { get; private set; }
    internal VideoPlaybackStatus PlaybackStatus { get; private set; }

    // подготавливает плеер для уже открытого видео, когда модель получает окно
    internal void AttachVideoPlayback() {
        if (!mediaPreviewsDisposed && videoPlayback is null && CurrentDocument is { Kind: MediaKind.Video, SourcePath: not null } document &&
            VideoFirstFrame is { } firstFrame) {
            if (videoEditorWaitingForFrame) {
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
        try {
            candidate = await Task.Run(() => new VideoPlaybackSession(document.SourcePath!, visibleSize));
            if (videoPlaybackGeneration != generation || !ReferenceEquals(CurrentDocument, document)) {
                return;
            }

            if (!candidate.Prepare()) {
                throw new InvalidDataException("Не удалось подготовить плеер");
            }

            videoPlayback = candidate;
            candidate = null;
            videoTrackReadsRemaining = VideoTrackReadLimit;
            nextVideoTrackRead = 0;
            videoPlaybackTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(25) };
            videoPlaybackTimer.Tick += VideoPlaybackTimer_OnTick;
            videoPlaybackTimer.Start();
        }
        catch (Exception) {
            if (videoPlaybackGeneration == generation && ReferenceEquals(CurrentDocument, document)) {
                ReleaseVideoPlayback();
                AcceptVideoEditorFrame();
                UpdatePlaybackStatus(new(0, 0, false, false, false, "Не удалось подготовить воспроизведение"));
            }
        }
        finally {
            candidate?.Dispose();
        }
    }

    // переключает воспроизведение по явной команде и позволяет начать заново после окончания
    internal void ToggleVideoPlayback() {
        if (videoPlayback is not { } playback || !PlaybackStatus.CanPlay) {
            return;
        }

        if (PlaybackStatus.IsPlaying) {
            PauseVideoPlayback();
            return;
        }

        videoWantsToPlay = true;
        if (playback.State == VLCState.Ended || videoRestartOnPlay) {
            videoRestartOnPlay = false;
            InvalidateVideoEditorSource();
            videoRestarting = playback.Restart();
            if (!videoRestarting) {
                videoWantsToPlay = false;
                ShowVideoEditorError("Не удалось получить видеокадр");
            }
        }
        RefreshVideoPlayback();
    }

    // прекращает вывод звука и продвижение общего времени при смене вкладки
    internal void PauseVideoPlayback() {
        videoWantsToPlay = false;
        if (videoPlayback is { } playback) {
            playback.SetPaused(true);
            RefreshVideoPlayback();
        }
    }

    // внутри ролика сохраняем состояние плеера, на паузе и у правой границы читаем подтвержденный кадр
    internal void SeekVideoByKeyboard(long offset) {
        if (videoPlayback is not { } playback || !PlaybackStatus.CanSeek) {
            return;
        }

        long position = Math.Clamp(PlaybackStatus.Position + offset, 0, PlaybackStatus.Duration);
        bool isPlaying = PlaybackStatus.IsPlaying;
        if (isPlaying && position < PlaybackStatus.Duration) {
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
        if (target == duration && (videoRestartOnPlay || requestedVideoSeek is { RestartOnPlay: true })) {
            // повтор стрелки сохраняет чтение последнего кадра и запланированный повтор
            return;
        }

        videoRestartOnPlay = false;
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
                    (byte[] png, long framePosition) = await Task.Run(() =>
                        VideoFirstFrameReader.ReadPreview(request.Path, request.Position, request.Duration));
                    if (requestedVideoSeek != request || !ReferenceEquals(videoPlayback, request.Session)) {
                        continue;
                    }

                    using MemoryStream stream = new(png);
                    preparedFrame = new Bitmap(stream);
                    if (request.Session.State == VLCState.Ended) {
                        videoRestarting = request.Session.Restart();
                        if (!videoRestarting) {
                            throw new InvalidDataException("Не удалось возобновить чтение видео");
                        }

                    }

                    await WaitForPausedVideoAsync(request);
                    if (requestedVideoSeek != request || !ReferenceEquals(videoPlayback, request.Session)) {
                        continue;
                    }

                    request.Session.Seek(framePosition);
                    await WaitForPausedVideoAsync(request, framePosition);
                    if (requestedVideoSeek != request || !ReferenceEquals(videoPlayback, request.Session)) {
                        continue;
                    }

                    videoWasPlaying = false;
                    SetVideoSeekPreview(preparedFrame);
                    preparedFrame = null;
                    requestedVideoSeek = null;
                    videoRestartOnPlay = request.RestartOnPlay;
                    UpdatePlaybackStatus(PlaybackStatus with {
                        Position = framePosition, IsPlaying = false, CanPlay = true, Notice = null
                    });
                    AcceptVideoEditorFrame();
                }
                catch (Exception) {
                    if (requestedVideoSeek == request) {
                        requestedVideoSeek = null;
                        UpdatePlaybackStatus(PlaybackStatus with { CanPlay = true, Notice = "Не удалось перейти к кадру" });
                        ShowVideoEditorError("Не удалось получить видеокадр");
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
        if (videoPlayback is not { } playback) {
            return;
        }

        VLCState state = playback.State;
        if (state == VLCState.Error) {
            ReleaseVideoPlayback();
            AcceptVideoEditorFrame();
            UpdatePlaybackStatus(PlaybackStatus with { Notice = "Не удалось воспроизвести видео" });
            return;
        }

        bool isPlaying = state == VLCState.Playing;
        if (isPlaying && !videoWantsToPlay) {
            playback.SetPaused(true);
        }
        else if (state == VLCState.Paused && videoWantsToPlay) {
            playback.SetPaused(false);
        }

        if (state == VLCState.Paused || isPlaying) {
            videoRestarting = false;
        }
        bool ended = state == VLCState.Ended && !videoRestarting;
        if (ended) {
            videoWantsToPlay = false;
        }

        if (requestedVideoSeek is null && (isPlaying || videoWasPlaying)) {
            PublishVideoFrame(playback);
        }
        videoWasPlaying = isPlaying;
        if (!videoWantsToPlay) {
            KeepVideoEditorResultForPause();
        }

        long duration = Math.Max(PlaybackStatus.Duration, playback.Duration);
        long position = ended ? duration : Math.Clamp(playback.Position, 0, Math.Max(0, duration));
        bool preparing = state is VLCState.NothingSpecial or VLCState.Opening or VLCState.Buffering || videoRestarting;
        bool ready = isPlaying || state == VLCState.Paused || ended;
        if (requestedVideoSeek is { } seek) {
            UpdatePlaybackStatus(new(seek.Position, duration, false, false, true, "Переход к кадру…"));
        }
        else {
            string? notice = preparing ? "Подготовка воспроизведения…" :
                PlaybackStatus.Notice == "Не удалось перейти к кадру" ? PlaybackStatus.Notice : null;
            UpdatePlaybackStatus(new(position, duration, videoWantsToPlay, ready && !videoRestarting,
                duration > 0 && (playback.CanSeek || ended), notice));
        }

        if (ready) {
            UpdateVideoInformation(playback, duration);
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
    private void PublishVideoFrame(VideoPlaybackSession playback) {
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
        AcceptVideoEditorFrame();
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
