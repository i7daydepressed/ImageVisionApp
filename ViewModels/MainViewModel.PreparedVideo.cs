using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageVisionApp.Models;
using ImageVisionApp.Services;
using LibVLCSharp.Shared;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private PreviewCoordinator? preparedVideo;
    private IVideoMediaSource? preparedSource;
    private VideoPreviewMode preparedMode;
    private WriteableBitmap? preparedOriginal;
    private VideoPresentationTime? preparedFrom;
    private VideoPresentationTime? preparedUntil;
    private long preparedGeneration;
    private long preparedPosition;
    private long? preparedWaitingPosition;
    private long preparedSeekDeadline;
    private bool preparedAtEnd;
    private bool preparedNeedsSeek;
    private bool preparedPauseOnCurrentPosition;
    private bool preparedRestarting;
    private bool preparedDetached;
    private PreparedSourceRequest? preparedOpening;
    private bool preparedStartPending;
    private bool preparedInterruptedLiveSeek;
    private bool videoHandoffReplay;
    private string? preparedUnavailable;
    private LivePreviewHandoff? livePreviewHandoff;
    private long? livePreviewAnchor;

    private sealed class PreparedSourceRequest {
        public CancellationTokenSource Stop { get; } = new();
        public Task? Closing { get; set; }
    }

    private readonly record struct LivePreviewHandoff(long Position, bool Seek, bool ReadFrame);

    internal Func<MediaDocument, IVideoMediaSource?>? PreparedVideoSourceFactory { get; set; }
    internal PreparedVideoWindow? PreparedVideo => preparedVideo?.Snapshot;
    internal VideoPresentationTime? PreparedPresentedTime => preparedFrom;

    internal VideoPreviewMode ActiveVideoPreviewMode => preparedSource is null ? VideoPreviewMode.Live : preparedMode;
    private bool IsStrictVideo => ActiveVideoPreviewMode == VideoPreviewMode.Strict;
    internal string PreparedVideoBufferDescription =>
        $"{DescribePreparedVideo()} · {PreparedVideo?.ReadyFrames ?? 0} кадр.";
    internal string? VideoPreviewModeNotice => preparedOpening is not null
        ? "Проверяем исходные метки кадров…" : preparedUnavailable;

    // очищаем источник предыдущего файла и применяем текущий выбор к новому видео
    private void ResetPreparedVideoDocument() {
        preparedAnalysisPausedForApply = false;
        strictPipelineActive = false;
        strictPipelineError = null;
        CancelPreviewModeHandoff();
        preparedVideo?.Cancel();
        preparedSource = null;
        preparedFrom = preparedUntil = null;
        preparedDetached = false;
        preparedWaitingPosition = null;
        preparedUnavailable = null;
        WriteableBitmap? previous = preparedOriginal;
        preparedOriginal = null;
        if (previous is not null) {
            OnPropertyChanged(nameof(VideoPreviewFrame));
            previous.Dispose();
        }
        if (!mediaPreviewsDisposed && IsVideoOpen && PreviewSettings.Mode != VideoPreviewMode.Live) {
            ChangeVideoPreviewMode();
        }
    }

    // берем момент и намерение общего плеера в момент применения настроек
    private void ChangeVideoPreviewMode() {
        if (!IsVideoOpen || mediaPreviewsDisposed) {
            return;
        }
        CancelVideoAnalysisApply();
        liveAnalysisSeekTarget = null;
        preparedPauseOnCurrentPosition = false;
        if (PreviewSettings.Mode == VideoPreviewMode.Live) {
            if (preparedOpening is not null || preparedSource is not null) {
                SwitchPreparedPreviewToLive(PlaybackStatus.Position);
            }
            else {
                preparedUnavailable = null;
            }
            return;
        }
        if (preparedSource is not null) {
            preparedMode = PreviewSettings.Mode;
            if (!RecalculatePinnedPreparedFrame()) {
                RequestPreparedVideo(PlaybackStatus.Position, preparedAtEnd, preservePlay: true);
            }
            EnsureVideoPlaybackTimer();
            return;
        }
        if (preparedOpening is null && preparedSource is null &&
            CurrentDocument is { SourcePath: { } } document) {
            BeginPreparedSource(document, PlaybackStatus.Position,
                videoRestartOnPlay || PlaybackStatus.Duration > 0 && PlaybackStatus.Position >= PlaybackStatus.Duration);
        }
    }

    // сохраняем команду Play, но до проверки нового источника останавливаем звук и прежние расчеты
    private void BeginPreparedSource(MediaDocument document, long position, bool atEnd) {
        CancelVideoFrameStep();
        preparedInterruptedLiveSeek = requestedVideoSeek is not null || livePreviewHandoff is { Seek: true };
        requestedVideoSeek = null;
        verifiedVideoSeekStop?.Cancel();
        livePreviewHandoff = null;
        livePreviewAnchor = null;
        preparedUnavailable = null;
        preparedPosition = position;
        preparedAtEnd = atEnd;
        preparedDetached = false;
        PreparedSourceRequest request = new();
        preparedOpening = request;
        videoPlayback?.SetPaused(true);
        videoWasPlaying = false;
        InvalidateVideoEditorSource();
        EnsureVideoPlaybackTimer();
        RefreshVideoPlayback();
        _ = OpenPreparedSourceAsync(document, request, PreparedVideoSourceFactory, videoFileIdentity);
    }

    // проверяем метаданные вне UI и принимаем только источник последнего запроса открытого документа
    private async Task OpenPreparedSourceAsync(MediaDocument document, PreparedSourceRequest request,
        Func<MediaDocument, IVideoMediaSource?>? createSource, VideoFileIdentity? identity) {
        try {
            IVideoMediaSource? source = await Task.Run(async () => createSource is not null
                ? createSource(document)
                : await FfprobeVideoMediaSource.OpenAsync(document.SourcePath!, identity, request.Stop.Token).ConfigureAwait(false),
                request.Stop.Token);
            if (!ReferenceEquals(preparedOpening, request) || !ReferenceEquals(CurrentDocument, document)) {
                return;
            }
            if (source is null) {
                throw new InvalidDataException("нет проверенной шкалы кадров");
            }
            preparedOpening = null;
            preparedSource = source;
            preparedMode = PreviewSettings.Mode;
            if (!RecalculatePinnedPreparedFrame()) {
                RequestPreparedVideo(preparedPosition, preparedAtEnd,
                    seekPlayer: preparedPosition > 0 || preparedInterruptedLiveSeek || videoPlayback is { Position: > 0 },
                    preservePlay: true);
            }
        }
        catch (Exception error) {
            if (ReferenceEquals(preparedOpening, request) && ReferenceEquals(CurrentDocument, document)) {
                preparedOpening = null;
                FailPreparedVideo(error.Message);
            }
        }
        finally {
            if (request.Closing is { } closing) {
                try {
                    await closing;
                }
                catch (Exception) {
                    // закрытие прежнего запроса не меняет состояние текущего источника
                }
            }
            request.Stop.Dispose();
        }
    }

    // отменяем проверку источника, а ее задача сама освобождает процесс и токен
    private void CancelPreparedSourceOpen() {
        PreparedSourceRequest? old = preparedOpening;
        preparedOpening = null;
        if (old is not null) {
            old.Closing = old.Stop.CancelAsync();
        }
    }

    // при отсоединении или смене файла прекращаем оба незавершенных перехода
    private void CancelPreviewModeHandoff(bool keepReplay = false) {
        if (!keepReplay) {
            videoHandoffReplay = false;
        }
        CancelPreparedSourceOpen();
        preparedStartPending = false;
        preparedInterruptedLiveSeek = false;
        livePreviewHandoff = null;
        livePreviewAnchor = null;
    }

    // возвращаем исходник текущей позиции и ждем опустошения резерва подготовленного режима
    private void SwitchPreparedPreviewToLive(long position, string? reason = null) {
        CancelVideoFrameStep();
        bool wasPrepared = preparedSource is not null;
        bool wasStrict = IsStrictVideo || PreviewSettings.Mode == VideoPreviewMode.Strict;
        preparedAnalysisPausedForApply = false;
        strictPipelineActive = false;
        strictPipelineError = null;
        long duration = PlaybackStatus.Duration;
        bool atEnd = duration > 0 && position >= duration ||
            wasPrepared && preparedAtEnd && position == preparedPosition;
        bool keepOriginal = wasPrepared && position == preparedPosition &&
            (videoPinnedFrame is not null && preparedFrom is not null || PreparedFrameCovers(new(position, 1000)));
        bool keepPriorLivePin = videoPinnedFrame is not null && preparedFrom is null && videoSeekPreview is not null &&
            position == preparedPosition;
        bool readFrame = (wasPrepared && !keepOriginal && !keepPriorLivePin && position > 0) || preparedInterruptedLiveSeek;
        bool seek = readFrame || (wasPrepared && (preparedNeedsSeek || preparedWaitingPosition is not null)) ||
            videoPlayback is { Position: >= 0 } playback && Math.Abs(playback.Position - position) > 25;
        WriteableBitmap? retained = preparedOriginal;
        CancelPreviewModeHandoff(keepReplay: true);
        preparedVideo?.Cancel();
        preparedSource = null;
        preparedOriginal = null;
        preparedFrom = preparedUntil = null;
        preparedWaitingPosition = null;
        preparedDetached = false;
        preparedUnavailable = reason is null ? null : wasStrict
            ? $"Строгий показ недоступен: {reason}" : $"Подготовка недоступна: {reason}";
        videoPlayback?.SetPaused(true);
        videoWasPlaying = false;
        videoRestartOnPlay = atEnd;
        livePreviewAnchor = position;
        livePreviewHandoff = new(position, seek, readFrame);
        InvalidateVideoEditorSource();
        if (keepOriginal && retained is not null) {
            if (preparedPresentedFrame is { } cursor) {
                videoLiveSeekHint = new(cursor.Time.Milliseconds, cursor.Ordinal, FromStart: cursor.Ordinal == 0);
            }
            SetVideoSeekPreview(retained, preparedPresentedFrame);
        }
        else {
            if (position == 0 && !readFrame && videoPinnedFrame is null) {
                ShowFirstLiveVideoFrame();
            }
            else {
                OnPropertyChanged(nameof(VideoPreviewFrame));
            }
            retained?.Dispose();
        }
        EnsureVideoPlaybackTimer();
    }

    private bool HasLivePreviewWork =>
        videoAnalysisApplyingPlan || IsReadingVideoAnalysisLiveFallback ||
        videoAnalysisPlaybackStop is not null || HasLivePreviewSourceWork;
    private bool HasLivePreviewSourceWork => readingVideoSeek || videoPreviewExecutor is { Metrics.ReservedBytes: > 0 };

    private bool WaitingForPreparedVideoBuffers => HasLivePreviewWork ||
        IsStrictPipelineOutputSelected && (videoForegroundStop is not null || videoAnalysisReservedBytes > 0);

    // начинаем новые расчеты только после возврата буферов другого режима
    private bool WaitForVideoModeHandoff(VideoPlaybackSession? playback) {
        if (preparedOpening is not null || preparedStartPending && WaitingForPreparedVideoBuffers) {
            ShowVideoModeHandoff(playback, preparedPosition,
                preparedOpening is not null ? VideoPreviewModeNotice! : "Освобождаем буферы живого предпросмотра…");
            return true;
        }
        if (preparedStartPending) {
            StartPreparedVideoRequest();
        }
        if (livePreviewHandoff is not { } handoff) {
            return false;
        }
        if (PreparedVideo is { ReservedBytes: > 0 } || (handoff.Seek || videoHandoffReplay) && playback is null) {
            ShowVideoModeHandoff(playback, handoff.Position, "Освобождаем буферы подготовленного участка…");
            return true;
        }
        livePreviewHandoff = null;
        if (videoHandoffReplay) {
            return false;
        }
        if (handoff.Seek && playback is not null && CurrentDocument?.SourcePath is { } path) {
            requestedVideoSeek = new VideoSeek(playback, path, handoff.Position, PlaybackStatus.Duration,
                videoRestartOnPlay, KeepPosition: true, ReadFrame: handoff.ReadFrame);
            if (!readingVideoSeek) {
                _ = ReadVideoSeeksAsync();
            }
        }
        else {
            AcceptVideoEditorFrame();
        }
        return false;
    }

    // оставляем кнопку паузы и перемотку доступными, пока выполняется последняя команда режима
    private void ShowVideoModeHandoff(VideoPlaybackSession? playback, long position, string notice) {
        long duration = Math.Max(PlaybackStatus.Duration, playback?.Duration ?? 0);
        bool ready = playback?.State is VLCState.Playing or VLCState.Paused or VLCState.Ended;
        UpdatePlaybackStatus(new(position, duration, videoWantsToPlay, playback is not null && (ready || videoWantsToPlay),
            duration > 0 && (playback?.CanSeek == true || playback?.State == VLCState.Ended), notice));
    }

    // новый явный переход меняет цель и снимает ожидание Play, пока режим переключается
    private bool SeekDuringPreviewModeHandoff(long position, long duration, bool preserveLivePlay = false) {
        if (preparedOpening is not null) {
            preparedPosition = position;
            preparedAtEnd = position == duration;
            preparedInterruptedLiveSeek = true;
        }
        else if (livePreviewHandoff is not null) {
            livePreviewAnchor = position;
            livePreviewHandoff = new(position, Seek: true, ReadFrame: true);
            videoRestartOnPlay = position == duration;
        }
        else if (requestedVideoSeek is { KeepPosition: true } liveSeek) {
            livePreviewAnchor = position;
            requestedVideoSeek = liveSeek with { Position = position, RestartOnPlay = position == duration, ReadFrame = true };
        }
        else {
            return false;
        }
        videoWantsToPlay &= preserveLivePlay && preparedOpening is null && !videoHandoffReplay;
        videoHandoffReplay = false;
        OnPropertyChanged(nameof(VideoPreviewFrame));
        RefreshVideoPlayback();
        return true;
    }

    // замена позиции, плана и бюджета останавливает звук и скрывает кадры прежнего запроса
    private void RequestPreparedVideo(long position, bool atEnd, bool seekPlayer = true, bool reuseReady = false,
        bool preservePlay = false, VideoFrameCursor? strictAnchor = null) {
        preparedPauseOnCurrentPosition = false;
        if (preparedSource is null) {
            return;
        }
        if (!preservePlay) {
            videoHandoffReplay = false;
        }
        preparedAnalysisPausedForApply = false;
        strictPipelineActive = IsStrictPipelineOutputSelected;
        strictPipelineError = null;
        if (strictPipelineActive) {
            videoForegroundStop?.Cancel();
            videoAnalysisPlaybackStop?.Cancel();
        }
        if (IsCombinedStrictOutputSelected) {
            ClearVideoForeground();
        }
        try {
            (preparedSource as FfprobeVideoMediaSource)?.EnsureCurrentFile();
        }
        catch (VideoFileChangedException error) {
            videoWantsToPlay &= preservePlay;
            preparedPosition = position;
            FailPreparedVideo(error.Message);
            return;
        }
        VideoPresentationTime time = new(position, 1000);
        bool keepWindow = !IsStrictVideo && reuseReady && !atEnd && !preparedAtEnd && position >= preparedPosition &&
            preparedWaitingPosition is null && (PreparedFrameCovers(time) ||
                PreparedVideo is { Error: null, First: { } first } window && first.CompareTo(time) <= 0 &&
                (window.Ended || window.Through is { } right && time.CompareTo(right) < 0));
        videoWantsToPlay &= preservePlay;
        videoPlayback?.SetPaused(true);
        preparedPosition = position;
        ResetStrictVideoClock(position, strictAnchor);
        preparedWaitingPosition = null;
        preparedDetached = false;
        preparedAtEnd = atEnd;
        preparedNeedsSeek = seekPlayer;
        preparedSeekDeadline = 0;
        preparedRestarting = false;
        if (!keepWindow || !PreparedFrameCovers(time)) {
            preparedFrom = preparedUntil = null;
            InvalidateVideoEditorSource();
            OnPropertyChanged(nameof(VideoPreviewFrame));
        }
        if (!keepWindow) {
            preparedStartPending = true;
            if (!WaitingForPreparedVideoBuffers) {
                StartPreparedVideoRequest();
                if (preparedSource is null) {
                    return;
                }
            }
        }
        EnsureVideoPlaybackTimer();
        UpdatePlaybackStatus(PlaybackStatus with { Position = preparedPosition, IsPlaying = videoWantsToPlay,
            Notice = strictPipelineError ?? "Подготовка кадра…" });
        if (keepWindow && videoPlayback is { } playback) {
            RefreshPreparedVideo(playback);
        }
    }

    // передаем координатору последний план и позицию после освобождения прежнего резерва
    private void StartPreparedVideoRequest() {
        preparedStartPending = false;
        if (preparedSource is { } source) {
            if (videoHandoffReplay) {
                videoHandoffReplay = false;
                preparedPosition = 0;
                ResetStrictVideoClock(0);
                preparedAtEnd = false;
                preparedNeedsSeek = true;
            }
            try {
                preparedVideo ??= new PreviewCoordinator();
                VideoFrameReadRequest start = new(preparedPosition, preparedAtEnd);
                strictPipelineActive = IsStrictPipelineOutputSelected;
                StrictVideoPipeline? pipeline = null;
                VideoEditorPlan editor = videoEditorPlan;
                if (IsCombinedStrictOutputSelected && CurrentDocument is { } document) {
                    if (source is FfprobeVideoMediaSource concrete) {
                        source = concrete.WithDecoderThreads(PreviewSettings.ForegroundDecoderThreads);
                    }
                    ResetPipelineOutputMeasurements();
                    CombinedVideoWork work = CaptureCombinedVideoWork(document, source);
                    if (!work.NeedsEditing && editor.HasSteps) {
                        editor = new(editor.Revision, []);
                    }
                    long generation = combinedMeasurementGeneration;
                    pipeline = new(work.Create, work.PersistentBytes, work.ScratchBytes, work.SelectedFrameBytes, work.Capacity,
                        (stage, bytes) => combinedMeasurements.Reserve(generation, stage, bytes),
                        work.NeedsPreviousFrame ? VideoPipelineStart.PreviousFrame : VideoPipelineStart.Beginning);
                }
                else if (IsStrictFrameAnalysisSelected && CurrentDocument is { } analysisDocument) {
                    ResetPipelineOutputMeasurements();
                    VideoSequenceAnalysisPlan plan = CaptureFrameAnalysisPlaybackPlan(analysisDocument);
                    pipeline = CaptureStrictFrameAnalysisPipeline(source, plan);
                    if (!plan.NeedsEditing && editor.HasSteps) {
                        editor = new(editor.Revision, []);
                    }
                }
                preparedGeneration = IsStrictVideo
                    ? preparedVideo.RequestStrict(source, start with { Anchor = strictStartAnchor },
                        editor, PreviewSettings.BudgetBytes, PreviewSettings.EffectiveWorkers, pipeline)
                    : preparedVideo.Request(source, start, videoEditorPlan, PreviewSettings.BudgetBytes);
            }
            catch (Exception error) {
                FailPreparedVideo(error.Message);
            }
        }
    }

    // первый готовый кадр может появиться до открытия общего плеера
    private void RefreshPreparedBeforePlayer() {
        try {
            bool covered = IsStrictVideo ? TryShowFirstStrictFrame() : AdvancePreparedVideo(preparedPosition);
            if (preparedSource is not null) {
                UpdatePlaybackStatus(PlaybackStatus with {
                    Notice = strictPipelineError ?? (covered ? "Подготовка воспроизведения…" : "Подготовка кадра…")
                });
            }
        }
        catch (Exception failure) {
            FailPreparedVideo(failure.Message);
        }
    }

    // явная пауза снимает намерение запуска даже когда VLC уже ожидает на паузе
    private void PausePreparedVideo(VideoPlaybackSession? playback, bool keepCurrentPosition = false) {
        if (IsStrictVideo) {
            videoWantsToPlay = false;
            strictClockStartedAt = null;
            preparedVideo?.StopStrictMeasurement(preparedGeneration);
            playback?.SetPaused(true);
            if (playback is not null) {
                RefreshVideoPlayback();
            }
            return;
        }
        if (videoWantsToPlay && !preparedNeedsSeek && !preparedStartPending &&
            preparedWaitingPosition is null && playback?.State == VLCState.Playing) {
            if (keepCurrentPosition) {
                preparedPauseOnCurrentPosition = true;
            }
            else {
                preparedWaitingPosition = Math.Max(preparedPosition, playback.Position);
                preparedNeedsSeek = true;
                preparedSeekDeadline = 0;
            }
        }
        videoWantsToPlay = false;
        playback?.SetPaused(true);
        if (playback is not null) {
            RefreshVideoPlayback();
        }
    }

    // при отсоединении прекращаем независимое чтение, а позицию сохраняем до нового подключения
    private void DetachPreparedVideo() {
        if (preparedSource is not null || preparedOpening is not null) {
            preparedVideo?.Cancel();
            preparedWaitingPosition = null;
            preparedFrom = preparedUntil = null;
            preparedDetached = true;
            OnPropertyChanged(nameof(VideoPreviewFrame));
        }
    }

    // запускаем общий плеер после подтверждения покрытия; у конца начинаем новый запрос с нуля
    private void TogglePreparedVideo(VideoPlaybackSession playback) {
        if (videoWantsToPlay) {
            PausePreparedVideo(playback);
            return;
        }
        VideoFrameCursor? pin = videoPinnedFrame;
        videoPinnedFrame = null;
        if (preparedAtEnd || !IsStrictVideo && playback.State == VLCState.Ended) {
            InvalidateVideoForeground();
            RequestPreparedVideo(0, atEnd: false);
        }
        else if (pin is not null || strictPipelineError is not null || preparedAnalysisPausedForApply) {
            VideoFrameCursor? anchor = pin ?? (preparedAnalysisPausedForApply ? preparedPresentedFrame : null);
            RequestPreparedVideo(preparedPosition, atEnd: false, strictAnchor: IsStrictVideo ? anchor : null);
        }
        if (strictPipelineError is not null) {
            return;
        }
        videoWantsToPlay = true;
        if (IsStrictVideo && !preparedStartPending) {
            preparedVideo?.StartStrictMeasurement(preparedGeneration);
        }
        SetVideoEditorProcessingTime(null);
        RefreshVideoPlayback();
    }

    // учитываем запрос пользователя отдельно от фактического состояния общего плеера
    private void RefreshPreparedVideo(VideoPlaybackSession playback) {
        if (IsStrictVideo) {
            RefreshStrictVideo(playback);
            return;
        }
        if (videoPinnedFrame is not null && preparedFrom is not null && !videoWantsToPlay) {
            UpdatePlaybackStatus(PlaybackStatus with { Notice = videoFrameStepNotice });
            return;
        }
        VLCState state = playback.State;
        long duration = Math.Max(PlaybackStatus.Duration, playback.Duration);
        if (preparedPauseOnCurrentPosition && state is VLCState.Paused or VLCState.Ended) {
            // берем время уже остановленного плеера и ждем покрывающую его пару без нового seek
            preparedPauseOnCurrentPosition = false;
            preparedWaitingPosition = Math.Min(duration, Math.Max(preparedPosition, playback.Position));
        }
        long target = preparedWaitingPosition ?? preparedPosition;
        if (!preparedNeedsSeek && state == VLCState.Ended) {
            videoWantsToPlay = false;
            target = duration;
        }
        else if (!preparedNeedsSeek && preparedWaitingPosition is null && state == VLCState.Playing && videoWantsToPlay) {
            target = Math.Max(preparedPosition, playback.Position);
        }

        bool covered = AdvancePreparedVideo(target, live: state == VLCState.Playing && videoWantsToPlay &&
            !preparedNeedsSeek && preparedWaitingPosition is null);
        if (preparedSource is null) {
            return;
        }
        if (covered) {
            preparedPosition = target;
            preparedWaitingPosition = null;
        }
        bool positioned = covered && ConfirmPreparedPosition(playback, state, duration);
        if (preparedSource is null) {
            return;
        }
        bool canRun = positioned && !preparedAtEnd;
        if (state == VLCState.Playing && (!videoWantsToPlay || !canRun)) {
            playback.SetPaused(true);
            if (videoWantsToPlay && !preparedNeedsSeek) {
                // удерживаем последний подтвержденный момент, пока готовится точка остановки
                preparedWaitingPosition ??= target;
                preparedNeedsSeek = true;
                preparedSeekDeadline = 0;
            }
        }
        else if (state == VLCState.Paused && videoWantsToPlay && canRun) {
            playback.SetPaused(false);
        }

        bool ready = state is VLCState.Playing or VLCState.Paused or VLCState.Ended;
        string notice = videoFrameStepNotice ?? (!covered || !positioned ? "Подготовка кадра…" : DescribePreparedVideo());
        UpdatePlaybackStatus(new(preparedPosition, duration, videoWantsToPlay, ready || videoWantsToPlay,
            duration > 0 && (playback.CanSeek || state == VLCState.Ended), notice));
        if (ready) {
            UpdateVideoInformation(playback, duration);
        }
    }

    // копируем только пару, которая действительно покрывает текущую позицию
    private bool AdvancePreparedVideo(long position, bool live = false) {
        if (preparedVideo is not { } coordinator) {
            return false;
        }
        try {
            using PreparedVideoFrame? frame = coordinator.TakeAt(preparedGeneration, position);
            if (frame is not null) {
                PublishPreparedVideo(frame);
                RecordVisibleOriginalFrame(live);
                RecordVisibleEditedFrame(live);
                return true;
            }
            PreparedVideoWindow snapshot = coordinator.Snapshot;
            if (snapshot.Error is { } error) {
                FailPreparedVideo(error.Message);
                return false;
            }
            VideoPresentationTime time = new(position, 1000);
            bool covered = PreparedFrameCovers(time);
            if (!covered && (snapshot.Ended && snapshot.ReadyFrames == 0 ||
                (preparedFrom is null && !preparedAtEnd && snapshot.First is { } first && first.CompareTo(time) > 0))) {
                FailPreparedVideo("источник не содержит кадра для этой позиции");
            }
            return covered;
        }
        catch (Exception exception) {
            FailPreparedVideo(exception.Message);
            return false;
        }
    }

    // текущее изображение годится только в пределах подтвержденного интервала
    private bool PreparedFrameCovers(VideoPresentationTime time) =>
        preparedFrom is { } from && (preparedAtEnd ||
            (from.CompareTo(time) <= 0 && (preparedUntil is not { } until || time.CompareTo(until) < 0)));

    // у правого края держим плеер на последнем декодированном кадре, оставляя запрос на таймлайне
    private bool ConfirmPreparedPosition(VideoPlaybackSession playback, VLCState state, long duration) {
        if (!preparedNeedsSeek) {
            return true;
        }
        if (state == VLCState.Ended && !preparedRestarting) {
            preparedRestarting = TryRestartCurrentVideo(playback);
            if (!preparedRestarting) {
                FailPreparedVideo("не удалось повторно открыть источник");
            }
            return false;
        }
        if (state != VLCState.Paused || !playback.CanSeek) {
            return false;
        }
        long target = preparedAtEnd && preparedFrom is { } lastFrame
            ? Math.Clamp((long)Math.Round(lastFrame.Milliseconds), 0, Math.Max(0, duration - 1)) : preparedPosition;
        long now = Environment.TickCount64;
        if (preparedSeekDeadline == 0) {
            playback.Seek(target);
            preparedSeekDeadline = now + 3000;
        }
        else if (playback.Position >= 0 && Math.Abs(playback.Position - target) <= 25) {
            preparedNeedsSeek = false;
            preparedRestarting = false;
            return true;
        }
        else if (now >= preparedSeekDeadline) {
            FailPreparedVideo("общий плеер не подтвердил позицию");
        }
        return false;
    }

    // храним только текущие изображения UI, а готовая пара освобождается после копирования
    private void PublishPreparedVideo(PreparedVideoFrame frame) {
        WriteableBitmap? previousOriginal = preparedOriginal;
        WriteableBitmap? previousEdited = videoEditorOutput;
        WriteableBitmap original = GetPreparedBitmap(previousOriginal, frame.Original.Size);
        WriteableBitmap? edited = null;
        try {
            using (ILockedFramebuffer target = original.Lock()) {
                frame.Original.CopyTo(target);
            }
            if (!ReferenceEquals(frame.Original, frame.Edited)) {
                edited = GetPreparedBitmap(previousEdited, frame.Edited.Size);
                using ILockedFramebuffer target = edited.Lock();
                frame.Edited.CopyTo(target);
            }
            preparedOriginal = original;
            videoEditorOutput = edited;
            preparedFrom = frame.Time;
            preparedUntil = frame.Until;
            preparedPresentedFrame = new(frame.Time, frame.Ordinal, frame.Until is null);
            videoEditorOutputGeometry = frame.Geometry;
            videoEditorOutputVisible = edited is not null;
            videoEditorWaitingForFrame = false;
            videoEditorSourceError = null;
            videoEditorError = null;
            VideoPreviewRevision++;
            if (strictPipelineActive && frame.PipelineOutput is { } output) {
                if (IsCombinedStrictOutputSelected) {
                    PublishCombinedOutput(output, hasPreparedInput: true);
                }
                else if (IsStrictFrameAnalysisSelected && output.Analysis is { } analyzed) {
                    PublishFrameAnalysisPlayback(analyzed, preparedPresentedFrame.Value, output.Ready, exactTime: true);
                }
            }
            OnPropertyChanged(nameof(VideoPreviewFrame));
            OnPropertyChanged(nameof(VideoPreviewRevision));
            NotifyVideoEditorSource();
            NotifyVideoEditorPreview();
            SetVideoEditorProcessingTime(videoWantsToPlay ? null : frame.Edited.ProcessingTime);
        }
        finally {
            if (!ReferenceEquals(preparedOriginal, original)) {
                original.Dispose();
            }
            if (!ReferenceEquals(preparedOriginal, previousOriginal)) {
                previousOriginal?.Dispose();
            }
            if (!ReferenceEquals(videoEditorOutput, edited)) {
                edited?.Dispose();
            }
            if (!ReferenceEquals(videoEditorOutput, previousEdited)) {
                previousEdited?.Dispose();
            }
        }
    }

    // переиспользуем bitmap вида, пока не изменились размеры результата
    private static WriteableBitmap GetPreparedBitmap(WriteableBitmap? current, PixelSize size) =>
        current?.PixelSize == size ? current :
        new(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);

    // показываем длину подтвержденного участка, даже если она меньше целевых семи секунд
    private string DescribePreparedVideo() {
        PreparedVideoWindow? snapshot = PreparedVideo;
        if (snapshot is { Ended: true }) {
            return "Подготовлено до конца";
        }
        double edge = (snapshot?.Through ?? preparedUntil)?.Milliseconds ?? preparedPosition;
        return $"Готово {Math.Max(0, edge - preparedPosition) / 1000:0.#} с";
    }

    // при отказе проверенного источника передаем ту же позицию и команду живому предпросмотру
    private void FailPreparedVideo(string reason) {
        if (IsStrictPipelineOutputSelected && strictPipelineActive) {
            videoWantsToPlay = false;
            videoHandoffReplay = false;
            preparedNeedsSeek = false;
            preparedStartPending = false;
            strictClockStartedAt = null;
            videoPlayback?.SetPaused(true);
            preparedVideo?.Cancel();
            strictPipelineError = $"Строгий показ недоступен: {reason}";
            if (IsCombinedStrictOutputSelected) {
                ClearVideoForeground();
                SetVideoForegroundStatus(strictPipelineError);
                combinedVideoAnalysisNotice = strictPipelineError;
            }
            else {
                InvalidateFrameAnalysisPlayback();
            }
            if (videoAnalysisVisible) {
                RefreshVideoAnalysisStatus();
            }
            UpdatePlaybackStatus(PlaybackStatus with { IsPlaying = false, Notice = strictPipelineError });
            return;
        }
        if (IsStrictVideo || PreviewSettings.Mode == VideoPreviewMode.Strict) {
            videoWantsToPlay = false;
            videoHandoffReplay = false;
        }
        SwitchPreparedPreviewToLive(preparedPosition, reason);
    }
}
