using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageVisionApp.Models;
using ImageVisionApp.Services;
using LibVLCSharp.Shared;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private readonly record struct VideoAnalysisInput(VideoAnalysisFrameKey Key, Bitmap Bitmap, VideoEditorPlan? Plan);
    private readonly record struct PhotoAnalysisSettings(bool Edges, bool Segmentation, bool Keypoints,
        AnalysisEdgeMethod Method, string Lower, string Upper, string Clusters, string Fast, bool Nonmax,
        AnalysisResultBackground Background, bool ShowEdges, bool ShowKeypoints);

    private bool videoAnalysisVisible;
    private bool videoAnalysisApplyingPlan;
    private bool observedVideoAnalysisPlaying;
    private VideoAnalysisFrameKey? observedAnalysisFrame;
    private VideoAnalysisFrameKey? requestedAnalysisFrame;
    private WriteableBitmap? videoAnalysisEditedInput;
    private string? videoAnalysisFailure;
    private PhotoAnalysisSettings? photoAnalysisBeforeVideo;

    public int VideoAnalysisInputRevision { get; private set; }
    public int SelectedVideoAnalysisSourceIndex {
        get => IsOriginalAnalysisSource ? 0 : 1;
        set {
            if (value is 0 or 1) {
                SelectedAnalysisSource = value == 0 ? AnalysisImageSource.Original : AnalysisImageSource.Edited;
            }
        }
    }

    public bool IsVideoAnalysisEditedInputPending => IsVideoOpen && videoAnalysisEditedInput is null &&
        TryResolveVideoAnalysisInput(out VideoAnalysisInput input) && input.Plan is not null;

    internal VideoAnalysisFrameKey? AnalyzedVideoFrame => HasAnalysisResult ? requestedAnalysisFrame : null;
    private bool HasAnalysisVideoMethods => isEdgesAnalysisEnabled || isSegmentationAnalysisEnabled || isKeypointAnalysisEnabled;

    // показываем актуальный исходник или фактический результат полного плана
    private Bitmap? AnalysisVideoInputImage {
        get {
            if (!TryResolveVideoAnalysisInput(out VideoAnalysisInput input)) {
                return null;
            }
            return input.Plan is null ? input.Bitmap : videoAnalysisEditedInput;
        }
    }

    // показываем время снимка на шкале приложения и размеры выбранных пикселей
    private string DescribeVideoAnalysisInput() {
        string name = IsOriginalAnalysisSource ? "Оригинал" : "Результат редактирования";
        if (!TryResolveVideoAnalysisInput(out VideoAnalysisInput input)) {
            return name;
        }
        var size = input.Key.Geometry.OutputSize;
        return $"{name} · {size.Width} × {size.Height} · {FormatVideoAnalysisTime(input.Key.Position)}";
    }

    // проверяем, что исходный снимок соответствует остановленному плееру, а подготовленная пара актуальна
    private bool TryResolveVideoAnalysisInput(out VideoAnalysisInput input) {
        input = default;
        if (mediaPreviewsDisposed || CurrentDocument is not { Kind: MediaKind.Video } document ||
            videoWantsToPlay || PlaybackStatus.IsPlaying || videoPlayback?.State is not (VLCState.Paused or VLCState.Ended) ||
            videoEditorWaitingForFrame || requestedVideoSeek is not null || videoFrameStep is not null ||
            preparedOpening is not null || livePreviewHandoff is not null || VideoPreviewFrame is not { } original ||
            preparedSource is not null && (preparedStartPending || preparedNeedsSeek || preparedWaitingPosition is not null)) {
            return false;
        }

        Bitmap bitmap = original;
        VideoEditorPlan? plan = null;
        VideoEditorGeometry geometry = new(original.PixelSize, 0);
        long planRevision = 0;
        if (IsEditedAnalysisSource) {
            planRevision = videoEditorPlan.Revision;
            if (videoEditorPlan.HasSteps) {
                geometry = videoEditorPlan.GetGeometry(original.PixelSize);
                if (preparedSource is not null) {
                    if (!videoEditorOutputVisible || videoEditorOutput is not { } edited ||
                        preparedFrom is null || preparedPresentedFrame is null ||
                        videoEditorOutputGeometry != geometry || edited.PixelSize != geometry.OutputSize) {
                        return false;
                    }
                    bitmap = edited;
                }
                else {
                    if (HasLivePreviewSourceWork || PreparedVideo is { ReservedBytes: > 0 }) {
                        return false;
                    }
                    plan = videoEditorPlan;
                }
            }
        }

        VideoFrameCursor? cursor = preparedSource is not null ? preparedPresentedFrame : videoPinnedFrame;
        VideoAnalysisFrameKey key = new(document, SelectedAnalysisSource, videoEditorSource, videoEditorFrame,
            VideoPreviewRevision, planRevision, PlaybackStatus.Position, cursor, geometry);
        input = new(key, bitmap, plan);
        return true;
    }

    // фиксируем пиксели под UI до передачи владения фоновому вычислению
    private AnalysisInputSnapshot? CaptureVideoAnalysisInput() {
        if (!TryResolveVideoAnalysisInput(out VideoAnalysisInput input)) {
            return null;
        }
        if (input.Plan is { } plan &&
            VideoEditorFrame.GetBufferBytes(input.Bitmap.PixelSize) + plan.GetScratchBytes(input.Bitmap.PixelSize) > PreviewSettings.BudgetBytes) {
            throw new InvalidOperationException("Бюджета буферов недостаточно для редакторского кадра");
        }
        return new(SelectedAnalysisSource, AnalysisInputRevision,
            new VideoAnalysisCapture(input.Key, VideoEditorFrame.Capture(input.Bitmap), input.Plan));
    }

    // выполняем один запрос на снимке, оставляя общий плеер и его звук в текущем состоянии
    private async Task RunVideoAnalysisAsync() {
        analysisWorkerRunning = true;
        NotifyAnalysisControls();
        VideoAnalysisFrameKey? startedFrame = null;
        try {
            AnalysisInputSnapshot input = CaptureVideoAnalysisInput()
                ?? throw new InvalidOperationException("Нет подтвержденного кадра для анализа");
            VideoAnalysisCapture capture = input.Video!;
            using VideoEditorFrame pixels = capture.Pixels;
            ClearVideoAnalysis();
            startedFrame = requestedAnalysisFrame = capture.Key;
            long revision = AnalysisInputRevision;
            TryGetCannyThresholds(out double lower, out double upper);
            TryGetSegmentationClusterCount(out int clusters);
            TryGetFastThreshold(out int threshold);
            VideoFrameAnalysisSettings settings = new(isEdgesAnalysisEnabled ? selectedEdgeMethod : null, lower, upper,
                isSegmentationAnalysisEnabled ? clusters : null, isKeypointAnalysisEnabled ? threshold : null, fastNonmaxSuppression);

            using CancellationTokenSource cancellation = new();
            activeAnalysisCancellation = cancellation;
            try {
                AnalysisStatusMessage = $"Анализ кадра · {FormatVideoAnalysisTime(capture.Key.Position)}";
                if (capture.Plan is { } plan) {
                    await ApplyVideoAnalysisPlanAsync(capture, plan, cancellation.Token);
                }
                VideoFrameAnalysisResult result = await Task.Run(
                    () => VideoFrameAnalysisService.Analyze(input, settings, cancellation.Token), cancellation.Token);
                if (cancellation.IsCancellationRequested || analysisDisposed || revision != AnalysisInputRevision ||
                    !SameVideoAnalysisFrame(requestedAnalysisFrame, capture.Key) ||
                    !TryResolveVideoAnalysisInput(out VideoAnalysisInput current) ||
                    !SameVideoAnalysisFrame(current.Key, capture.Key)) {
                    return;
                }
                PublishVideoAnalysis(result, capture);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
                // старый снимок остается у работника до выхода, но не попадает в интерфейс
            }
            finally {
                activeAnalysisCancellation = null;
            }
        }
        catch (Exception error) {
            if (!analysisDisposed && IsVideoOpen &&
                (startedFrame is null || SameVideoAnalysisFrame(requestedAnalysisFrame, startedFrame))) {
                videoAnalysisFailure = $"Не удалось выполнить анализ: {error.Message}";
                RefreshVideoAnalysisStatus();
            }
        }
        finally {
            analysisWorkerRunning = false;
            NotifyAnalysisControls();
            if (!IsVideoOpen && !analysisDisposed && (analysisRunRequested || analysisCompositionRequested)) {
                _ = ProcessAnalysisRequestsAsync();
            }
        }
    }

    // удерживаем допуск видеоочередей до возврата всех временных буферов плана
    private async Task ApplyVideoAnalysisPlanAsync(VideoAnalysisCapture capture, VideoEditorPlan plan, CancellationToken token) {
        videoAnalysisApplyingPlan = true;
        try {
            VideoEditorGeometry geometry = await Task.Run(() => plan.Apply(capture.Pixels), token);
            if (geometry != capture.Key.Geometry) {
                throw new InvalidOperationException("Геометрия редакторского кадра изменилась");
            }
        }
        finally {
            videoAnalysisApplyingPlan = false;
        }
    }

    // заменяем вход редактора и вывод анализа данными одного кадра
    private void PublishVideoAnalysis(VideoFrameAnalysisResult result, VideoAnalysisCapture capture) {
        WriteableBitmap? preview = null;
        Bitmap? output = null;
        try {
            if (capture.Plan is not null) {
                preview = GetPreparedBitmap(null, capture.Pixels.Size);
                using ILockedFramebuffer target = preview.Lock();
                capture.Pixels.CopyTo(target);
            }
            using MemoryStream stream = new(result.Png);
            output = new Bitmap(stream);
            videoAnalysisEditedInput = preview;
            preview = null;
            SetEdgesResult(result.Edges);
            SetSegmentationResult(result.Segmentation);
            SetKeypointsResult(result.Keypoints);
            ReplaceAnalysisComposition(output, result.Png);
            output = null;
            NotifyVideoAnalysisInput();
            RefreshAnalysisStatus();
        }
        finally {
            preview?.Dispose();
            output?.Dispose();
        }
    }

    // снимаем результат по смене запроса, сохраняя данные незавершенного снимка у работника
    private void ClearVideoAnalysis() {
        bool hadRequest = requestedAnalysisFrame is not null;
        requestedAnalysisFrame = null;
        videoAnalysisFailure = null;
        if (hadRequest) {
            activeAnalysisCancellation?.Cancel();
            SetEdgesResult(null);
            SetSegmentationResult(null);
            SetKeypointsResult(null);
            ReplaceAnalysisComposition(null, null);
        }
        if (videoAnalysisEditedInput is { } previous) {
            videoAnalysisEditedInput = null;
            NotifyVideoAnalysisInput();
            previous.Dispose();
        }
    }

    // замечаем смену кадра, позы или готовности без запуска метода на каждом тике
    private void RefreshVideoAnalysisInput() {
        if (analysisDisposed || !IsVideoOpen || !videoAnalysisVisible && requestedAnalysisFrame is null) {
            return;
        }
        VideoAnalysisFrameKey? current = TryResolveVideoAnalysisInput(out VideoAnalysisInput input) ? input.Key : null;
        bool playing = videoWantsToPlay || PlaybackStatus.IsPlaying;
        if (observedVideoAnalysisPlaying != playing || !SameVideoAnalysisFrame(observedAnalysisFrame, current)) {
            observedVideoAnalysisPlaying = playing;
            observedAnalysisFrame = current;
            if (!SameVideoAnalysisFrame(requestedAnalysisFrame, current)) {
                ClearVideoAnalysis();
            }
            NotifyVideoAnalysisInput();
            RefreshAnalysisStatus();
        }
    }

    // учитываем даже разные кадры одного PTS и изменения уже переиспользуемого bitmap
    private static bool SameVideoAnalysisFrame(VideoAnalysisFrameKey? left, VideoAnalysisFrameKey? right) =>
        left == right && ReferenceEquals(left?.Document, right?.Document);

    // оповещаем привязки после успешного захвата или изменения готовности остановленного кадра
    private void NotifyVideoAnalysisInput() {
        VideoAnalysisInputRevision++;
        OnPropertyChanged(nameof(VideoAnalysisInputRevision));
        OnPropertyChanged(nameof(AnalysisInputImage));
        OnPropertyChanged(nameof(AnalysisInputDescription));
        OnPropertyChanged(nameof(HasAnalysisInput));
        OnPropertyChanged(nameof(IsVideoAnalysisEditedInputPending));
        NotifySegmentationValidation();
        NotifyAnalysisControls();
    }

    // показываем условия ручного запуска и фактический итог видеозапроса
    private void RefreshVideoAnalysisStatus() {
        AnalysisStatusMessage = videoAnalysisFailure ??
            (videoWantsToPlay || PlaybackStatus.IsPlaying ? "Поставьте видео на паузу для анализа кадра"
            : analysisWorkerRunning && requestedAnalysisFrame is not null ? "Выполняется анализ кадра"
            : !HasAnalysisInput ? "Ожидание подтвержденного видеокадра"
            : !HasAnalysisVideoMethods ? "Включите метод анализа"
            : isEdgesAnalysisEnabled && !AreSelectedAnalysisParametersValid ? CannyThresholdError
            : isSegmentationAnalysisEnabled && HasSegmentationValidationError ? SegmentationValidationError
            : isKeypointAnalysisEnabled && HasFastThresholdError ? FastThresholdError
            : "Выполните анализ выбранного кадра");
    }

    // форматируем позицию остановленного кадра в миллисекундах даже для длинных роликов
    private static string FormatVideoAnalysisTime(long position) {
        TimeSpan time = TimeSpan.FromMilliseconds(position);
        return $"{(long)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    // обновляем видеовход при входе на страницу, не создавая спрос на скрытый редактор
    internal void SetVideoAnalysisVisible(bool visible) {
        videoAnalysisVisible = visible;
        if (visible && IsVideoOpen) {
            RefreshVideoAnalysisInput();
            NotifyVideoAnalysisInput();
            RefreshAnalysisStatus();
        }
    }

    // запоминаем фото-параметры при входе в видео и восстанавливаем при возвращении
    private void ResetVideoAnalysisAfterLoad() {
        ClearVideoAnalysis();
        observedAnalysisFrame = null;
        observedVideoAnalysisPlaying = false;
        if (IsVideoOpen && photoAnalysisBeforeVideo is null) {
            photoAnalysisBeforeVideo = new(isEdgesAnalysisEnabled, isSegmentationAnalysisEnabled, isKeypointAnalysisEnabled,
                selectedEdgeMethod, cannyLowerThresholdText, cannyUpperThresholdText, segmentationClusterCountText,
                fastThresholdText, fastNonmaxSuppression, selectedAnalysisBackground, showAnalysisEdges, showAnalysisKeypoints);
        }
        else if (!IsVideoOpen && photoAnalysisBeforeVideo is { } saved) {
            photoAnalysisBeforeVideo = null;
            IsEdgesAnalysisEnabled = saved.Edges;
            IsSegmentationAnalysisEnabled = saved.Segmentation;
            IsKeypointAnalysisEnabled = saved.Keypoints;
            SelectedEdgeMethod = saved.Method;
            CannyLowerThresholdText = saved.Lower;
            CannyUpperThresholdText = saved.Upper;
            SegmentationClusterCountText = saved.Clusters;
            FastThresholdText = saved.Fast;
            FastNonmaxSuppression = saved.Nonmax;
            SelectedAnalysisBackground = saved.Background;
            ShowAnalysisEdges = saved.ShowEdges;
            ShowAnalysisKeypoints = saved.ShowKeypoints;
        }
    }
}
