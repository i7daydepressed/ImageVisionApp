using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using ImageVisionApp.Models;
using ImageVisionApp.Services;
using LibVLCSharp.Shared;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private (MediaDocument Document, TaskCompletionSource Completion)? videoAnalysisApply;
    private bool videoAnalysisApplyRunning;
    private bool videoAnalysisWaitingForLivePause;
    private long? videoAnalysisLiveFallbackPosition;
    private CancellationTokenSource? videoAnalysisLiveFallbackCancellation;
    private bool IsReadingVideoAnalysisLiveFallback => videoAnalysisLiveFallbackCancellation is not null;

    private bool CanApplyVideoAnalysis => videoAnalysisVisible &&
        videoPlayback is { State: not VLCState.Error } && (PlaybackStatus.CanPlay || HasVideoAnalysisInput);

    private bool IsWaitingForVideoAnalysisFrame => videoAnalysisWaitingForLivePause || liveAnalysisSeekTarget is not null ||
        requestedVideoSeek is not null || videoFrameStep is not null ||
        preparedOpening is not null || livePreviewHandoff is not null || preparedSource is not null &&
        (preparedStartPending || preparedNeedsSeek || preparedPauseOnCurrentPosition || preparedWaitingPosition is not null);

    private bool HasUnconfirmedLiveAnalysisFrame => liveAnalysisSeekTarget is not null || liveAnalysisSeekRecovery is not null;

    // после живой перемотки буфер VLC не содержит надежной метки позиции
    private bool NeedsVerifiedLiveAnalysisFrame(Bitmap frame, long position) => preparedSource is null &&
        (HasUnconfirmedLiveAnalysisFrame || position > 0 && ReferenceEquals(frame, VideoFirstFrame));

    // заменяем ожидающий запрос и ставим плеер на паузу перед выбором кадра
    private Task ApplyVideoAnalysisAsync() {
        if (CurrentDocument is not { Kind: MediaKind.Video } document) {
            return Task.CompletedTask;
        }

        CancelVideoAnalysisApply();
        TaskCompletionSource completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        videoAnalysisApply = (document, completion);
        BeginVideoAnalysisMeasurement();
        try {
            PauseVideoPlayback(forAnalysis: true);
        }
        catch (Exception error) {
            if (videoAnalysisApply?.Completion == completion) {
                CancelVideoAnalysisApply();
                videoAnalysisFailure = $"Не удалось остановить видео: {error.Message}";
                RefreshVideoAnalysisStatus();
            }
            return completion.Task;
        }
        RefreshVideoAnalysisStatus();
        if (!videoAnalysisApplyRunning) {
            _ = ProcessVideoAnalysisAppliesAsync();
        }
        return completion.Task;
    }

    // оставляем только последний запрос и ждем подтвержденного кадра в той же сессии
    private async Task ProcessVideoAnalysisAppliesAsync() {
        videoAnalysisApplyRunning = true;
        try {
            while (videoAnalysisApply is { } request) {
                long pauseDeadline = Environment.TickCount64 + 3000;
                try {
                    while (videoAnalysisApply?.Completion == request.Completion && ReferenceEquals(CurrentDocument, request.Document)) {
                        if (videoPlayback is not { } playback || playback.State == VLCState.Error || videoEditorSourceError is not null) {
                            throw new InvalidOperationException(videoEditorSourceError ?? "Не удалось получить видеокадр");
                        }
                        if (Environment.TickCount64 >= pauseDeadline) {
                            if (liveAnalysisSeekTarget is not null) {
                                throw new InvalidOperationException("Плеер не подтвердил позицию после перемотки");
                            }
                            if (playback.State is not (VLCState.Paused or VLCState.Ended)) {
                                throw new InvalidOperationException("Не удалось подтвердить паузу видео");
                            }
                        }
                        if (videoAnalysisLiveFallbackPosition is { } position && !HasLivePreviewWork &&
                            PreparedVideo is not { ReservedBytes: > 0 }) {
                            await ReadVideoAnalysisLiveFallbackAsync(request, playback, position);
                            continue;
                        }
                        bool hasInput = HasVideoAnalysisInput;
                        if (!hasInput && !IsWaitingForVideoAnalysisFrame && playback.State is VLCState.Paused or VLCState.Ended) {
                            throw new InvalidOperationException("Нет подтверждённого видеокадра на этой позиции");
                        }
                        if (!analysisWorkerRunning && hasInput &&
                            TryResolveReadyVideoAnalysisInput(AnalysisImageSource.Original, out VideoAnalysisInput input)) {
                            await RunVideoAnalysisAsync();
                            if (IsCurrentAnalysisLayer(input.Key)) {
                                break;
                            }
                            continue;
                        }
                        await Task.Delay(25);
                    }
                }
                catch (Exception error) {
                    if (videoAnalysisApply?.Completion == request.Completion) {
                        videoAnalysisApply = null;
                        videoAnalysisWaitingForLivePause = false;
                        videoAnalysisLiveFallbackPosition = null;
                        videoAnalysisFailure = $"Не удалось применить анализ: {error.Message}";
                    }
                }
                finally {
                    if (videoAnalysisApply?.Completion == request.Completion) {
                        videoAnalysisApply = null;
                    }
                    request.Completion.TrySetResult();
                    if (!analysisDisposed && IsVideoOpen) {
                        RefreshVideoAnalysisStatus();
                    }
                }
            }
        }
        finally {
            videoAnalysisApplyRunning = false;
        }
    }

    // запасной кадр читаем независимо от VLC, пока его первая живая картинка еще не пришла
    private async Task ReadVideoAnalysisLiveFallbackAsync((MediaDocument Document, TaskCompletionSource Completion) request,
        VideoPlaybackSession playback, long position) {
        if (request.Document.SourcePath is not { } path) {
            throw new InvalidOperationException("Не задан источник видео");
        }
        int revision = VideoPreviewRevision;
        long duration = PlaybackStatus.Duration;
        VideoFileIdentity? identity = videoFileIdentity;
        using CancellationTokenSource stop = new();
        videoAnalysisLiveFallbackCancellation = stop;
        long reservedBytes = 0;
        try {
            FfprobeVideoMediaSource source = await Task.Run(
                () => FfprobeVideoMediaSource.OpenAsync(path, identity, stop.Token), stop.Token);
            if (!IsCurrentFallback()) {
                return;
            }
            // резервируем рабочие пиксели чтения и снимок, который остается в предпросмотре
            long frameBytes = source.ReadScratchBytes + 2 * VideoEditorFrame.GetBufferBytes(source.FrameSize);
            if (frameBytes > PreviewSettings.AnalysisBudgetBytes - videoAnalysisReservedBytes) {
                throw new InvalidOperationException("Бюджета анализа недостаточно, чтобы прочитать остановленный кадр");
            }
            reservedBytes = frameBytes;
            videoAnalysisReservedBytes += reservedBytes;
            TimedVideoInput frame = await Task.Run(
                () => ReadConfirmedVideoFrameAsync(source, position, duration, stop.Token), stop.Token);
            using VideoEditorFrame pixels = frame.Pixels;
            if (!IsCurrentFallback()) {
                return;
            }
            identity?.EnsureCurrent(path);
            SetVideoSeekPreview(CreateVideoAnalysisBitmap(pixels));
            videoPinnedFrame = new(frame.Time, frame.Ordinal, frame.IsLast);
            videoLiveSeekHint = null;
            VideoPreviewRevision++;
            OnPropertyChanged(nameof(VideoPreviewRevision));
            videoAnalysisWaitingForLivePause = false;
            videoAnalysisLiveFallbackPosition = null;
            AcceptVideoEditorFrame();
            RefreshVideoAnalysisInput();
        }
        catch (Exception) when (!IsCurrentFallback()) {
            // при позднем живом кадре или новой команде запасные пиксели и ошибка больше не нужны
        }
        finally {
            videoAnalysisReservedBytes -= reservedBytes;
            videoAnalysisLiveFallbackCancellation = null;
        }

        // выдаем снимок только прежнему запросу, если его еще не заменили нативные пиксели
        bool IsCurrentFallback() => videoAnalysisApply?.Completion == request.Completion &&
            ReferenceEquals(CurrentDocument, request.Document) && ReferenceEquals(videoPlayback, playback) &&
            !videoWantsToPlay && playback.State is VLCState.Paused or VLCState.Ended &&
            videoAnalysisLiveFallbackPosition == position && PlaybackStatus.Position == position &&
            (HasUnconfirmedLiveAnalysisFrame || VideoPreviewRevision == revision &&
                ReferenceEquals(VideoPreviewFrame, VideoFirstFrame));
    }

    // проверяем, что интервал декодированного кадра действительно покрывает позицию остановки
    private static async Task<TimedVideoInput> ReadConfirmedVideoFrameAsync(FfprobeVideoMediaSource source,
        long position, long duration, CancellationToken token) {
        bool atEnd = duration > 0 && position >= duration;
        VideoPresentationTime target = new(position, 1000);
        await foreach (TimedVideoInput frame in source.ReadFramesAsync(new(position, atEnd) { SingleFrame = true }, token)
            .ConfigureAwait(false)) {
            if (atEnd ? frame.IsLast : frame.Time.CompareTo(target) <= 0 &&
                (frame.Until is not { } until || target.CompareTo(until) < 0)) {
                return frame;
            }
            frame.Pixels.Dispose();
            break;
        }
        throw new InvalidDataException("На позиции остановки ещё нет подтверждённого видеокадра");
    }

    // снимаем ожидание и старый расчет без изменения уже опубликованных совместимых слоев
    private void CancelVideoAnalysisApply() {
        ClearVideoAnalysisMeasurement();
        videoAnalysisWaitingForLivePause = false;
        videoAnalysisLiveFallbackPosition = null;
        videoAnalysisLiveFallbackCancellation?.Cancel();
        if (videoAnalysisApply is not null || videoAnalysisRequest is not null) {
            TaskCompletionSource? pending = videoAnalysisApply?.Completion;
            videoAnalysisApply = null;
            pending?.TrySetResult();
            CancelActiveVideoAnalysis();
            NotifyAnalysisControls();
        }
    }
}
