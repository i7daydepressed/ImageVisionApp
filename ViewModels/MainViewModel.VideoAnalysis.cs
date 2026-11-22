using System;
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
    private readonly record struct VideoAnalysisRequest(VideoAnalysisFrameKey Original, VideoAnalysisFrameKey Edited,
        VideoAnalysisPipeline Pipeline, VideoFrameAnalysisSettings Settings,
        long EdgesRevision, long SegmentationRevision, long KeypointsRevision, long PlateRevision) {
        public AnalysisImageSource EdgeSource => Pipeline.EdgeSource ?? AnalysisImageSource.Original;
        public AnalysisImageSource SegmentationSource => Pipeline.SegmentationSource ?? AnalysisImageSource.Original;
    }

    private readonly record struct PhotoAnalysisSettings(bool Edges, bool Segmentation, bool Keypoints, bool Plate,
        AnalysisEdgeMethod Method, string Lower, string Upper, string Clusters, string Fast, bool Nonmax,
        AnalysisResultBackground Background, bool ShowEdges, bool ShowKeypoints, bool ShowPlate);

    private bool videoAnalysisVisible;
    private bool videoAnalysisApplyingPlan;
    private bool observedVideoAnalysisPlaying;
    private VideoAnalysisFrameKey? observedAnalysisFrame;
    private VideoAnalysisFrameKey? observedEditedAnalysisFrame;
    private VideoAnalysisRequest? videoAnalysisRequest;
    private AnalysisImageSource videoAnalysisDisplayedSource;
    private AnalysisImageSource videoEdgeSource;
    private AnalysisImageSource videoSegmentationSource;
    private VideoAnalysisFrameKey? videoAnalysisEditedKey;
    private WriteableBitmap? videoAnalysisEditedInput;
    private string? videoAnalysisFailure;
    private PhotoAnalysisSettings? photoAnalysisBeforeVideo;

    public int VideoAnalysisInputRevision { get; private set; }
    public int SelectedVideoAnalysisSourceIndex {
        get => (int)videoAnalysisDisplayedSource;
        set {
            if (!IsVideoOpen || value is < 0 or > 1 ||
                !SetProperty(ref videoAnalysisDisplayedSource, (AnalysisImageSource)value)) {
                return;
            }
            NotifyVideoAnalysisInput();
            RefreshVideoAnalysisComposition();
        }
    }

    public int SelectedVideoEdgeSourceIndex {
        get => (int)videoEdgeSource;
        set {
            if (IsVideoOpen && value is >= 0 and <= 2 && AcceptVideoAnalysisChoice((AnalysisImageSource)value,
                videoSegmentationSource, videoAnalysisBackground) && SetProperty(ref videoEdgeSource, (AnalysisImageSource)value)) {
                InvalidateEdgeAnalysis();
            }
        }
    }

    public int SelectedVideoSegmentationSourceIndex {
        get => (int)videoSegmentationSource;
        set {
            if (IsVideoOpen && value is >= 0 and <= 2 && AcceptVideoAnalysisChoice(videoEdgeSource,
                (AnalysisImageSource)value, videoAnalysisBackground) && SetProperty(ref videoSegmentationSource, (AnalysisImageSource)value)) {
                InvalidateSegmentationAnalysis();
            }
        }
    }

    public bool IsVideoAnalysisEditedInputPending => IsVideoOpen && videoAnalysisDisplayedSource == AnalysisImageSource.Edited &&
        HasVideoAnalysisInput && !TryResolveReadyVideoAnalysisInput(AnalysisImageSource.Edited, out _) && videoAnalysisEditedFailure is null;

    private bool HasAnalysisVideoMethods => isEdgesAnalysisEnabled || IsSegmentationAnalysisEnabled || isKeypointAnalysisEnabled || isPlateAnalysisEnabled;
    private bool HasVideoAnalysisInput => TryResolveReadyVideoAnalysisInput(AnalysisImageSource.Original, out _);
    private bool HasVideoAnalysisResult => VideoAnalysisUsesSequence ? combinedVideoAnalysisResult is not null :
        ShowsFrameAnalysisPlayback ? videoAnalysisPlaybackBitmap is not null : edgesResult is not null || segmentationResult is not null || keypointsResult is not null || isPlateAnalysisEnabled && plateResult is not null;
    internal VideoAnalysisFrameKey? AnalyzedVideoFrame =>
        edgesResult?.VideoFrame ?? segmentationResult?.VideoFrame ?? keypointsResult?.VideoFrame ??
        (isPlateAnalysisEnabled ? plateResult?.VideoFrame : null);

    // оставляем подтвержденный оригинал видимым до готовности выбранного редакторского кадра
    private Bitmap? AnalysisVideoInputImage =>
        TryResolveReadyVideoAnalysisInput(videoAnalysisDisplayedSource, out VideoAnalysisInput input) ||
        TryResolveReadyVideoAnalysisInput(AnalysisImageSource.Original, out input) ? input.Bitmap : null;

    // описываем то, что показано во «Входе», независимо от вычислительных источников
    private string DescribeVideoAnalysisInput() {
        if (TryResolveReadyVideoAnalysisInput(videoAnalysisDisplayedSource, out VideoAnalysisInput input) ||
            TryResolveReadyVideoAnalysisInput(AnalysisImageSource.Original, out input)) {
            string name = input.Key.Source == AnalysisImageSource.Original ? "Оригинал" : "Результат редактирования";
            var size = input.Key.Geometry.OutputSize;
            return $"Вход: {name} · {size.Width} × {size.Height} · {FormatVideoAnalysisTime(input.Key.Position)}";
        }
        return "Вход: ожидание видеокадра";
    }

    // отличаем готовые пиксели от плана, для которого предстоит подготовить кадр
    private bool TryResolveReadyVideoAnalysisInput(AnalysisImageSource source, out VideoAnalysisInput input) =>
        TryResolveVideoAnalysisInput(source, out input) && input.Plan is null;

    // связываем источник с текущей паузой и используем подготовленную или сохраненную редакторскую пару
    private bool TryResolveVideoAnalysisInput(AnalysisImageSource source, out VideoAnalysisInput input) {
        input = default;
        if (source == AnalysisImageSource.VideoSequence || mediaPreviewsDisposed || CurrentDocument is not { Kind: MediaKind.Video } document ||
            videoWantsToPlay || PlaybackStatus.IsPlaying || videoAnalysisWaitingForLivePause ||
            videoPlayback?.State is not (VLCState.Paused or VLCState.Ended) ||
            videoEditorWaitingForFrame || requestedVideoSeek is not null || videoFrameStep is not null ||
            preparedOpening is not null || livePreviewHandoff is not null || VideoPreviewFrame is not { } original ||
            NeedsVerifiedLiveAnalysisFrame(original, PlaybackStatus.Position) ||
            preparedSource is not null && (preparedStartPending || preparedNeedsSeek || preparedPauseOnCurrentPosition ||
                preparedWaitingPosition is not null)) {
            return false;
        }

        bool edited = source == AnalysisImageSource.Edited;
        VideoEditorGeometry geometry = edited ? videoEditorPlan.GetGeometry(original.PixelSize) : new(original.PixelSize, 0);
        VideoFrameCursor? cursor = preparedSource is not null ? preparedPresentedFrame : videoPinnedFrame;
        VideoAnalysisFrameKey originalKey = new(document, AnalysisImageSource.Original, videoEditorSource, videoEditorFrame,
            VideoPreviewRevision, 0, PlaybackStatus.Position, cursor, new(original.PixelSize, 0));
        VideoAnalysisFrameKey key = edited ? GetVideoAnalysisEditedKey(originalKey) : originalKey;
        Bitmap bitmap = original;
        VideoEditorPlan? plan = null;
        if (edited && videoEditorPlan.HasSteps) {
            if (preparedSource is not null) {
                if (!videoEditorOutputVisible || videoEditorOutput is not { } output ||
                    preparedFrom is null || preparedPresentedFrame is null ||
                    videoEditorOutputGeometry != geometry || output.PixelSize != geometry.OutputSize) {
                    return false;
                }
                bitmap = output;
            }
            else if (!videoEditorVisible && SameVideoAnalysisFrame(videoEditorStoppedOutput, key) &&
                videoEditorOutputFrame == key.Frame && videoEditorOutput is { } stoppedOutput &&
                videoEditorOutputGeometry == geometry && stoppedOutput.PixelSize == geometry.OutputSize) {
                // bitmap остается у редактора, анализ снимает собственную копию перед расчетом
                bitmap = stoppedOutput;
            }
            else if (SameVideoAnalysisFrame(videoAnalysisEditedKey, key) && videoAnalysisEditedInput is { } cached) {
                bitmap = cached;
            }
            else if (!HasLivePreviewSourceWork && !IsReadingVideoAnalysisLiveFallback && PreparedVideo is not { ReservedBytes: > 0 }) {
                plan = videoEditorPlan;
            }
            else {
                return false;
            }
        }

        input = new(key, bitmap, plan);
        return true;
    }

    // выводим штамп результата очереди из исходной позы и текущей версии плана
    private VideoAnalysisFrameKey GetVideoAnalysisEditedKey(VideoAnalysisFrameKey original) => original with {
        Source = AnalysisImageSource.Edited,
        PlanRevision = videoEditorPlan.Revision,
        Geometry = videoEditorPlan.GetGeometry(original.Geometry.InputSize)
    };

    // общий снимок по умолчанию дает физический оригинал независимо от режима просмотра
    private AnalysisInputSnapshot? CaptureVideoAnalysisInput() =>
        TryResolveReadyVideoAnalysisInput(AnalysisImageSource.Original, out VideoAnalysisInput input)
            ? CaptureVideoAnalysisInput(input) : null;

    // копируем готовые пиксели на UI, пока общий bitmap принадлежит подтвержденному кадру
    private AnalysisInputSnapshot CaptureVideoAnalysisInput(VideoAnalysisInput input) =>
        new(input.Key.Source, AnalysisInputRevision,
            new VideoAnalysisCapture(input.Key, VideoEditorFrame.Capture(input.Bitmap), input.Plan));

    // готовим редакторский вход лишь для методов текущего ручного запроса, которым он нужен
    private bool NeedsEditedVideoAnalysisInput(VideoAnalysisRequest request) =>
        request.Pipeline.Queue.Stages.Contains(VideoPipelineStage.Editing) && IsCurrentAnalysisLayer(request.Original) && SameVideoAnalysisFrame(request.Edited, GetVideoAnalysisEditedKey(request.Original)) &&
        (request.Settings.EdgeMethod is not null && request.EdgesRevision == edgesWorkRevision && request.EdgeSource == AnalysisImageSource.Edited ||
         request.Settings.Clusters is not null && request.SegmentationRevision == segmentationWorkRevision &&
         request.SegmentationSource == AnalysisImageSource.Edited);

    // просим общий подготовитель отдать актуальные пиксели, оставляя интерфейс и исходные методы доступными
    private async Task WaitForVideoAnalysisEditedInputAsync(VideoAnalysisRequest request, CancellationToken token) {
        while (NeedsEditedVideoAnalysisInput(request)) {
            token.ThrowIfCancellationRequested();
            if (TryResolveReadyVideoAnalysisInput(AnalysisImageSource.Edited, out _)) {
                return;
            }
            if (videoAnalysisEditedFailure is { } error) {
                videoAnalysisFailure = error;
                return;
            }
            await Task.Delay(25, token);
        }
    }

    // запускаем методы с двумя подтвержденными входами и публикуем каждый по его собственной версии
    private async Task RunVideoAnalysisAsync() {
        if (!TryResolveReadyVideoAnalysisInput(AnalysisImageSource.Original, out VideoAnalysisInput original)) {
            return;
        }
        VideoAnalysisPipeline pipeline = CaptureVideoAnalysisPipeline();
        VideoFrameAnalysisSettings settings = CaptureVideoFrameAnalysisSettings(pipeline);
        VideoAnalysisRequest request = new(original.Key, GetVideoAnalysisEditedKey(original.Key), pipeline,
            settings, edgesWorkRevision, segmentationWorkRevision, keypointsWorkRevision, plateWorkRevision);

        analysisWorkerRunning = true;
        videoAnalysisFailure = null;
        videoAnalysisRequest = request;
        using CancellationTokenSource cancellation = new();
        activeAnalysisCancellation = cancellation;
        try {
            if (pipeline.Plate) {
                plateFailure = string.Empty;
                SetPlateResult(null);
            }
            RefreshVideoAnalysisStatus();
            bool needsEdited = NeedsEditedVideoAnalysisInput(request);
            if (needsEdited || pipeline.Plate) {
                RefreshVideoAnalysisComposition();
            }
            if (needsEdited) {
                await WaitForVideoAnalysisEditedInputAsync(request, cancellation.Token);
            }
            if (!TryResolveReadyVideoAnalysisInput(AnalysisImageSource.Original, out original) ||
                !SameVideoAnalysisFrame(original.Key, request.Original)) {
                return;
            }
            VideoAnalysisInput? edited = NeedsEditedVideoAnalysisInput(request) &&
                TryResolveReadyVideoAnalysisInput(AnalysisImageSource.Edited, out VideoAnalysisInput readyEdited)
                ? readyEdited : null;
            settings = settings with {
                EdgeMethod = request.EdgesRevision == edgesWorkRevision &&
                    (request.EdgeSource == AnalysisImageSource.Original || edited is not null) ? settings.EdgeMethod : null,
                Clusters = request.SegmentationRevision == segmentationWorkRevision &&
                    (request.SegmentationSource == AnalysisImageSource.Original || edited is not null) ? settings.Clusters : null,
                FastThreshold = request.KeypointsRevision == keypointsWorkRevision ? settings.FastThreshold : null,
                Plate = request.PlateRevision == plateWorkRevision && settings.Plate
            };
            if (settings is { EdgeMethod: null, Clusters: null, FastThreshold: null, Plate: false }) {
                return;
            }
            RefreshVideoAnalysisStatus();
            int workers;
            long bytes;
            while (true) {
                cancellation.Token.ThrowIfCancellationRequested();
                if (!IsCurrentAnalysisLayer(original.Key) || edited is { } source && !IsCurrentAnalysisLayer(source.Key)) {
                    return;
                }
                (workers, bytes) = VideoFrameAnalysisService.FitWorkers(original.Bitmap.PixelSize, edited?.Bitmap.PixelSize,
                    request.EdgeSource, request.SegmentationSource, settings, PreviewSettings.AnalysisMaximumWorkers,
                    PreviewSettings.AnalysisBudgetBytes - videoAnalysisReservedBytes);
                if (workers > 0) {
                    break;
                }
                if (VideoFrameAnalysisService.FitWorkers(original.Bitmap.PixelSize, edited?.Bitmap.PixelSize,
                    request.EdgeSource, request.SegmentationSource, settings, PreviewSettings.AnalysisMaximumWorkers,
                    PreviewSettings.AnalysisBudgetBytes).Workers == 0) {
                    throw new InvalidOperationException("Недостаточно бюджета буферов анализа для выбранных методов");
                }
                await Task.Delay(25, cancellation.Token);
            }

            VideoFrameAnalysisResult result;
            videoAnalysisReservedBytes += bytes;
            videoAnalysisWorkerLimit = workers;
            try {
                AnalysisInputSnapshot input = CaptureVideoAnalysisInput(original);
                using VideoEditorFrame pixels = input.Video!.Pixels;
                AnalysisInputSnapshot? editedInput = edited is { } ready ? CaptureVideoAnalysisInput(ready) : null;
                using VideoEditorFrame? editedPixels = editedInput?.Video?.Pixels;
                result = await Task.Run(() => VideoFrameAnalysisService.Analyze(input, editedInput, request.EdgeSource,
                    request.SegmentationSource, settings, cancellation.Token, workers), cancellation.Token);
            }
            finally {
                videoAnalysisReservedBytes -= bytes;
                videoAnalysisWorkerLimit = 0;
            }
            if (cancellation.IsCancellationRequested || analysisDisposed || !IsCurrentAnalysisLayer(request.Original)) {
                return;
            }
            if (request.EdgesRevision == edgesWorkRevision && result.Edges is { } edges && IsCurrentAnalysisLayer(edges.VideoFrame)) {
                SetEdgesResult(edges);
            }
            if (request.SegmentationRevision == segmentationWorkRevision && result.Segmentation is { } segmentation &&
                IsCurrentAnalysisLayer(segmentation.VideoFrame)) {
                SetSegmentationResult(segmentation);
            }
            if (request.KeypointsRevision == keypointsWorkRevision && result.Keypoints is { } keypoints) {
                SetKeypointsResult(keypoints);
            }
            if (settings.Plate && request.PlateRevision == plateWorkRevision) {
                plateFailure = result.PlateError ?? string.Empty;
                SetPlateResult(result.Plate);
            }
            RecordVideoAnalysisCalculation(request.Original, workers, bytes, result);
            RecordVideoEditorAnalysisWork(edited);
            RefreshVideoAnalysisComposition();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            // смена позы исключает публикацию прежнего снимка
        }
        catch (Exception error) {
            if (!analysisDisposed && !cancellation.IsCancellationRequested && IsCurrentAnalysisLayer(request.Original)) {
                videoAnalysisFailure = $"Не удалось выполнить анализ: {error.Message}";
            }
        }
        finally {
            activeAnalysisCancellation = null;
            videoAnalysisRequest = null;
            analysisWorkerRunning = false;
            NotifyAnalysisControls();
            if (!IsVideoOpen && !analysisDisposed && (analysisRunRequested || analysisCompositionRequested)) {
                _ = ProcessAnalysisRequestsAsync();
            }
            if (IsVideoOpen && !analysisDisposed) {
                RefreshVideoAnalysisStatus();
            }
        }
    }

    // сначала сверяем текущий документ, физический кадр и план именно этого источника
    private bool IsCurrentAnalysisLayer(VideoAnalysisFrameKey? key) =>
        key is { } frame && TryResolveVideoAnalysisInput(frame.Source, out VideoAnalysisInput current) &&
        SameVideoAnalysisFrame(frame, current.Key);

    // отменяем вычисление методов при смене физического кадра или документа
    private void CancelActiveVideoAnalysis() {
        if (videoAnalysisRequest is not null) {
            videoAnalysisRequest = null;
            activeAnalysisCancellation?.Cancel();
        }
        videoAnalysisFailure = null;
    }

    // снимаем всю композицию предыдущего документа и отменяем его незавершенную работу
    private void ClearVideoAnalysis() {
        InvalidateFrameAnalysisPlayback();
        videoAnalysisPlaybackPlan = null;
        videoAnalysisPlaybackPlanError = null;
        manualAnalysisPlaybackPaused = false;
        CancelVideoAnalysisApply();
        videoAnalysisFailure = null;
        CancelVideoAnalysisComposition();
        if (IsVideoOpen || observedAnalysisFrame is not null) {
            SetEdgesResult(null);
            SetSegmentationResult(null);
            SetKeypointsResult(null);
            SetPlateResult(null);
        }
        plateFailure = string.Empty;
        ReleaseVideoAnalysisEditedInput();
    }

    // реагируем только на смену подтвержденного кадра, плана, паузы или доступа к бюджету
    private void RefreshVideoAnalysisInput() {
        if (analysisDisposed || !IsVideoOpen) {
            return;
        }
        if (!videoAnalysisVisible && observedAnalysisFrame is null && videoAnalysisRequest is null) {
            return;
        }
        VideoAnalysisFrameKey? original = TryResolveVideoAnalysisInput(AnalysisImageSource.Original, out VideoAnalysisInput source)
            ? source.Key : null;
        VideoAnalysisFrameKey? edited = TryResolveVideoAnalysisInput(AnalysisImageSource.Edited, out VideoAnalysisInput output)
            ? output.Key : null;
        bool playing = videoWantsToPlay || PlaybackStatus.IsPlaying;
        if (observedVideoAnalysisPlaying == playing && SameVideoAnalysisFrame(observedAnalysisFrame, original) &&
            SameVideoAnalysisFrame(observedEditedAnalysisFrame, edited) && !ShouldRetryVideoAnalysisEditedInput) {
            return;
        }
        videoAnalysisViewCancellation?.Cancel();
        videoAnalysisFailure = null;
        observedVideoAnalysisPlaying = playing;
        if (!SameVideoAnalysisFrame(observedAnalysisFrame, original)) {
            plateFailure = string.Empty;
        }
        observedAnalysisFrame = original;
        observedEditedAnalysisFrame = edited;
        if (!SameVideoAnalysisFrame(videoAnalysisEditedKey, edited) || ShouldRetryVideoAnalysisEditedInput) {
            ReleaseVideoAnalysisEditedInput();
        }
        if (videoAnalysisRequest is { } request && !IsCurrentAnalysisLayer(request.Original)) {
            CancelActiveVideoAnalysis();
        }
        if (edgesResult is { } edges && !IsCurrentAnalysisLayer(edges.VideoFrame)) {
            SetEdgesResult(null);
        }
        if (segmentationResult is { } segmentation && !IsCurrentAnalysisLayer(segmentation.VideoFrame)) {
            SetSegmentationResult(null);
        }
        if (keypointsResult is { } points && !IsCurrentAnalysisLayer(points.VideoFrame)) {
            SetKeypointsResult(null);
        }
        if (plateResult is { } plate && !IsCurrentAnalysisLayer(plate.VideoFrame)) {
            SetPlateResult(null);
        }
        NotifyVideoAnalysisInput();
        RefreshVideoAnalysisComposition();
        RefreshVideoAnalysisStatus();
    }

    // учитываем даже разные кадры одного PTS и изменения уже переиспользуемого bitmap
    private static bool SameVideoAnalysisFrame(VideoAnalysisFrameKey? left, VideoAnalysisFrameKey? right) =>
        left == right && ReferenceEquals(left?.Document, right?.Document);

    // обновляем привязки входа и доступность ручного анализа после смены кадра
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

    // показываем позу плеера, готовность подложки и методы с действительными результатами
    private void RefreshVideoAnalysisStatus() {
        NotifyAnalysisControls();
        if (VideoAnalysisUsesSequence && videoAnalysisResultTabSelected) {
            AnalysisStatusMessage = CombinedVideoAnalysisNotice ??
                (combinedVideoAnalysisResult is null ? "Ожидание результата очереди видеоряда" : "Готов результат очереди видеоряда");
            return;
        }
        if (ShowsFrameAnalysisPlayback) {
            AnalysisStatusMessage = strictPipelineError ?? videoAnalysisPlaybackPlanError ?? videoAnalysisPlaybackFailure ??
                (videoAnalysisPlaybackStamp is { } stamp
                    ? $"Анализ кадра · {(stamp.Exact ? "" : "позиция плеера ≈ ")}{FormatVideoAnalysisTime((long)stamp.Time.Milliseconds)}"
                    : "Ожидание обработанного кадра…");
            return;
        }
        string? error = videoAnalysisFailure ?? videoEditorSourceError ??
            (WantsVideoAnalysisEditedInput ? videoAnalysisEditedFailure : null) ?? videoAnalysisCompositionFailure ??
            (isPlateAnalysisEnabled && plateFailure.Length > 0 ? plateFailure : null);
        string? pending = VideoAnalysisPendingMessage;
        if (videoAnalysisApply is not null && videoAnalysisRequest is null) {
            AnalysisStatusMessage = "Подготовка анализа: ждём подтверждённый кадр";
        }
        else if (videoWantsToPlay || PlaybackStatus.IsPlaying) {
            AnalysisStatusMessage = "Нажмите «Применить», чтобы остановить видео и выполнить анализ";
        }
        else if (error is not null || videoAnalysisRequest is not null || pending is not null) {
            AnalysisStatusMessage = error ?? (videoAnalysisRequest is { } request
                ? NeedsEditedVideoAnalysisInput(request) && !TryResolveReadyVideoAnalysisInput(AnalysisImageSource.Edited, out _)
                    ? "Подготовка анализа: ждём результат редактирования"
                    : $"{(request.Settings is { Plate: true, EdgeMethod: null, Clusters: null, FastThreshold: null }
                        ? "Поиск областей номера" : "Анализ кадра")} · {FormatVideoAnalysisTime(request.Original.Position)}"
                : pending!);
        }
        else if (isEdgesAnalysisEnabled && !AreSelectedAnalysisParametersValid) {
            AnalysisStatusMessage = CannyThresholdError;
        }
        else if (IsSegmentationAnalysisEnabled && HasSegmentationValidationError) {
            AnalysisStatusMessage = SegmentationValidationError;
        }
        else if (isKeypointAnalysisEnabled && HasFastThresholdError) {
            AnalysisStatusMessage = FastThresholdError;
        }
        else if (AnalyzedVideoFrame is { } frame) {
            string methods = edgesResult is null ? "" : edgesResult.Method == AnalysisEdgeMethod.Canny ? "Canny" : "Робертс";
            if (segmentationResult is { } segmentation) {
                methods += (methods.Length == 0 ? "" : " + ") + $"K-means N={segmentation.ClusterCount}";
            }
            if (keypointsResult is { } points) {
                methods += (methods.Length == 0 ? "" : " + ") + $"FAST: {points.Points.Length} точек";
            }
            if (isPlateAnalysisEnabled && plateResult is { } plate) {
                methods += (methods.Length == 0 ? "" : " + ") + DescribePlateRegions(plate);
            }
            AnalysisStatusMessage = $"Готово: {methods} · {FormatVideoAnalysisTime(frame.Position)}";
        }
        else {
            AnalysisStatusMessage = !HasVideoAnalysisInput ? PlaybackStatus.Notice ?? "Ожидание подтвержденного видеокадра"
                : !HasAnalysisVideoMethods ? "Включите метод анализа" : "Выполните анализ выбранного кадра";
        }
    }

    // форматируем позицию остановленного кадра в миллисекундах даже для длинных роликов
    private static string FormatVideoAnalysisTime(long position) {
        TimeSpan time = TimeSpan.FromMilliseconds(position);
        return $"{(long)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    // обновляем предъявление после входа на страницу и приостанавливаем его вне нее
    internal void SetVideoAnalysisVisible(bool visible) {
        if (visible != videoAnalysisVisible) {
            ResetVisibleVideoOutput();
        }
        videoAnalysisVisible = visible;
        if (visible && IsVideoOpen) {
            RefreshVideoAnalysisInput();
            NotifyVideoAnalysisInput();
            RefreshVideoAnalysisComposition();
            RefreshVideoAnalysisStatus();
        }
        else if (!visible) {
            CancelVideoAnalysisApply();
            CancelVideoAnalysisComposition();
            bool restartedTemporal = UpdateCombinedVideoAnalysisRoute();
            UpdateFrameAnalysisPlaybackPlan(restartStrict: !restartedTemporal);
            if (IsVideoOpen && !analysisDisposed) {
                RefreshVideoAnalysisStatus();
            }
        }
    }

    // запоминаем фото-параметры при входе в видео и восстанавливаем при возвращении
    private void ResetVideoAnalysisAfterLoad() {
        ClearVideoAnalysis();
        observedAnalysisFrame = null;
        observedEditedAnalysisFrame = null;
        observedVideoAnalysisPlaying = false;
        ResetVideoAnalysisViewSelection();
        videoAnalysisDisplayedSource = videoEdgeSource = videoSegmentationSource = AnalysisImageSource.Original;
        OnPropertyChanged(nameof(SelectedVideoAnalysisSourceIndex));
        OnPropertyChanged(nameof(SelectedVideoEdgeSourceIndex));
        OnPropertyChanged(nameof(SelectedVideoSegmentationSourceIndex));
        UpdateVideoAnalysisPipelineDescription();
        OnPropertyChanged(nameof(AnalysisRunButtonText));
        if (IsVideoOpen && photoAnalysisBeforeVideo is null) {
            photoAnalysisBeforeVideo = new(isEdgesAnalysisEnabled, isSegmentationAnalysisEnabled, isKeypointAnalysisEnabled,
                isPlateAnalysisEnabled, selectedEdgeMethod, cannyLowerThresholdText, cannyUpperThresholdText, segmentationClusterCountText,
                fastThresholdText, fastNonmaxSuppression, selectedAnalysisBackground, showAnalysisEdges, showAnalysisKeypoints, showAnalysisPlate);
        }
        else if (!IsVideoOpen && photoAnalysisBeforeVideo is { } saved) {
            photoAnalysisBeforeVideo = null;
            IsEdgesAnalysisEnabled = saved.Edges;
            IsSegmentationAnalysisEnabled = saved.Segmentation;
            IsKeypointAnalysisEnabled = saved.Keypoints;
            IsPlateAnalysisEnabled = saved.Plate;
            SelectedEdgeMethod = saved.Method;
            CannyLowerThresholdText = saved.Lower;
            CannyUpperThresholdText = saved.Upper;
            SegmentationClusterCountText = saved.Clusters;
            FastThresholdText = saved.Fast;
            FastNonmaxSuppression = saved.Nonmax;
            SelectedAnalysisBackground = saved.Background;
            ShowAnalysisEdges = saved.ShowEdges;
            ShowAnalysisKeypoints = saved.ShowKeypoints;
            ShowAnalysisPlate = saved.ShowPlate;
        }
    }
}
