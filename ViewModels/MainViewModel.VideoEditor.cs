using System;
using System.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private VideoEditorPlan videoEditorPlan = new(0, []);
    private long videoEditorSource;
    private long videoEditorFrame;
    private long videoEditorOutputFrame;
    private VideoAnalysisFrameKey? videoEditorStoppedOutput;
    private bool videoEditorWaitingForFrame;
    private bool videoEditorVisible;
    private bool videoEditorOutputVisible;
    private bool videoEditorFrameDeferred;
    private bool videoEditorWaitingForBudget;
    private readonly VideoPreviewMeasurements videoPreviewMeasurements = new();
    private readonly VideoPreviewAutoController videoPreviewAuto = new(Environment.ProcessorCount);
    private long videoEditorObservation;
    private long videoEditorOfferedAt;
    private long videoEditorPauseObservation = 1;
    private long videoEditorLiveInput;
    private long videoEditorLiveRevision;
    private int? videoPreviewPeakAtStop;
    private VideoPreviewExecutor? videoPreviewExecutor;
    private WriteableBitmap? videoEditorOutput;
    private VideoEditorGeometry videoEditorOutputGeometry;
    private string? videoEditorSourceError;
    private string? videoEditorError;
    private string? videoEditorLatestFailure;
    private int videoEditorPreviewTabIndex = 1;

    public Bitmap? VideoEditorPreviewFrame => videoEditorVisible && !videoEditorWaitingForFrame
        ? videoEditorPlan.HasSteps ? videoEditorOutputVisible ? videoEditorOutput : null : VideoPreviewFrame
        : null;

    public int VideoEditorPreviewRevision { get; private set; }
    public int VideoEditorPreviewTabIndex {
        get => videoEditorPreviewTabIndex;
        set {
            if ((value is 0 or 1) && SetProperty(ref videoEditorPreviewTabIndex, value)) {
                ResetVisibleVideoOutput();
                OnPropertyChanged(nameof(VideoEditorVisibleProcessingTime));
            }
        }
    }

    public bool IsVideoEditorSourceReady => !videoEditorWaitingForFrame;
    public string? VideoEditorSourceNotice => videoEditorSourceError ??
        (videoEditorWaitingForFrame ? "Ожидание видеокадра…" : null);

    internal long VideoEditorChainInput { get; private set; }
    internal VideoPreviewSettings PreviewSettings { get; private set; } = VideoPreviewSettings.Default;
    private VideoPreviewSettings RuntimePreviewSettings => PreviewSettings.MaximumWorkers == 0
        ? PreviewSettings with { MaximumWorkers = videoPreviewAuto.Workers } : PreviewSettings;

    internal VideoEditorGeometry? CurrentVideoEditorGeometry => VideoEditorPreviewFrame is { } frame
        ? videoEditorPlan.HasSteps ? videoEditorOutputGeometry : new VideoEditorGeometry(frame.PixelSize, 0)
        : null;

    public string? VideoEditorNotice => VideoEditorSourceNotice ?? videoEditorError ??
        (videoEditorPlan.HasSteps && !videoEditorOutputVisible ? "Подготовка результата…" : null);

    public string? VideoEditorProcessingTime { get; private set; }
    public string? VideoEditorVisibleProcessingTime => VideoEditorPreviewTabIndex == 1 ? VideoEditorProcessingTime : null;

    // останавливаем запросы вне редактора, при возврате берем текущий подтвержденный кадр
    internal void SetVideoEditorVisible(bool visible) {
        if (videoEditorVisible == visible) {
            return;
        }

        videoEditorVisible = visible;
        ResetVisibleVideoOutput();
        if (preparedSource is not null) {
            NotifyVideoEditorPreview();
            return;
        }
        if (visible) {
            videoEditorStoppedOutput = null;
            NotifyVideoEditorPreview();
            RequestVideoEditorFrame();
        }
        else {
            videoEditorStoppedOutput = CaptureStoppedVideoEditorOutput();
            videoEditorSource++;
            InvalidateVideoEditorRequests();
            videoEditorOutputVisible = false;
            NotifyVideoEditorPreview();
        }
    }

    // связываем готовый остановленный выход с поколением после выхода с вкладки редактора
    private VideoAnalysisFrameKey? CaptureStoppedVideoEditorOutput() {
        if (!videoEditorPlan.HasSteps || !videoEditorOutputVisible || videoEditorOutputFrame != videoEditorFrame ||
            videoEditorOutput is not { } output || !TryResolveReadyVideoAnalysisInput(AnalysisImageSource.Original, out VideoAnalysisInput original)) {
            return null;
        }
        VideoAnalysisFrameKey key = GetVideoAnalysisEditedKey(original.Key);
        return key.Geometry == videoEditorOutputGeometry && key.Geometry.OutputSize == output.PixelSize
            ? key with { SourceRevision = videoEditorSource + 1 } : null;
    }

    // сохраняем порядок, числовые значения и неизменяемые матрицы без ссылок на редактируемые шаги
    private void CaptureVideoEditorPlan() {
        CancelVideoAnalysisApply();
        ResetVisibleVideoOutput();
        VideoEditorStep[] steps = AdvancedSteps
            .Select(step => new VideoEditorStep(step.Type, step.Parameter, step.ElementSnapshot, step.KernelSnapshot))
            .ToArray();
        videoEditorPlan = new(videoEditorPlan.Revision + 1, steps);
        videoEditorStoppedOutput = null;
        InvalidateVideoEditorRequests();
        ResetVideoEditorChainTotals();
        ResetVideoPreviewAuto();
        videoEditorOutputVisible = false;
        if (!videoEditorWaitingForFrame) {
            videoEditorError = null;
        }
        NotifyVideoEditorPreview();
        if (videoFrameStep is not null) {
            return;
        }
        if (preparedSource is not null) {
            if (!RecalculatePinnedPreparedFrame()) {
                RequestPreparedVideo(preparedPosition, preparedAtEnd);
            }
        }
        else {
            RequestVideoEditorFrame();
        }
    }

    // снимаем старый результат в момент запроса перехода, пока новый кадр не подтвержден
    private void InvalidateVideoEditorSource() {
        ResetVisibleVideoOutput();
        videoEditorStoppedOutput = null;
        videoEditorSource++;
        videoEditorWaitingForFrame = true;
        videoEditorOutputVisible = false;
        videoEditorSourceError = null;
        videoEditorError = null;
        InvalidateVideoEditorRequests();
        NotifyVideoEditorSource();
        NotifyVideoEditorPreview();
    }

    // связываем очередь с исходным кадром, который только что принял путь предпросмотра
    private void AcceptVideoEditorFrame(bool live = false) {
        videoEditorFrame++;
        videoEditorLatestFailure = null;
        videoEditorObservation = 0;
        long now = Environment.TickCount64;
        if (live && videoPreviewMeasurements.RecordInput(videoEditorFrame, now)) {
            videoEditorObservation = videoPreviewMeasurements.Id;
            videoEditorOfferedAt = now;
        }
        bool updateSource = videoEditorWaitingForFrame || videoEditorSourceError is not null;
        videoEditorWaitingForFrame = false;
        videoEditorSourceError = null;
        if (updateSource) {
            NotifyVideoEditorSource();
        }
        if (videoEditorVisible && IsVideoOpen) {
            if (live && videoEditorPlan.HasSteps) {
                VideoEditorChainInput++;
            }
            if (updateSource || !videoEditorPlan.HasSteps) {
                videoEditorError = null;
                NotifyVideoEditorPreview();
            }
            RequestVideoEditorFrame(live);
        }
    }

    // на паузе оставляем видимым только результат последнего принятого кадра
    private void KeepVideoEditorResultForPause() {
        if (videoEditorLatestFailure is { } failure) {
            ShowVideoEditorError(failure);
        }
        else if (videoEditorOutputVisible && videoEditorOutputFrame != videoEditorFrame) {
            videoEditorOutputVisible = false;
            NotifyVideoEditorPreview();
        }
    }

    // передаем собственный снимок только после резерва, при перегрузке держим номер актуального кадра
    private void RequestVideoEditorFrame(bool liveInput = false) {
        if (preparedSource is not null || preparedOpening is not null || livePreviewHandoff is not null || videoFrameStep is not null) {
            return;
        }
        SetVideoEditorProcessingTime(null);
        if (!videoEditorVisible || videoEditorWaitingForFrame || !videoEditorPlan.HasSteps ||
            CurrentDocument is not { Kind: MediaKind.Video } document || VideoPreviewFrame is not { } bitmap) {
            videoEditorFrameDeferred = false;
            videoEditorWaitingForBudget = false;
            return;
        }

        videoEditorWaitingForBudget = IsLiveVideoEditorBudgetHeld;
        if (videoEditorWaitingForBudget) {
            EnsureVideoPlaybackTimer();
            return;
        }

        if (liveInput && videoEditorLiveInput == 0) {
            videoEditorLiveInput = ++videoEditorLiveRevision;
            videoPreviewExecutor?.SetLiveInput(videoEditorLiveInput);
        }
        VideoEditorPlan plan = videoEditorPlan;
        VideoPreviewFrameKey key = new(document, videoEditorSource, plan.Revision, videoEditorFrame) {
            Observation = liveInput ? videoEditorObservation : 0,
            OfferedAt = videoEditorOfferedAt,
            PauseObservation = videoWantsToPlay ? 0 : videoEditorPauseObservation,
            LiveInput = liveInput ? videoEditorLiveInput : 0,
            InputGroup = liveInput && videoEditorObservation != 0 ? videoPreviewMeasurements.InputGroup : 0
        };
        if (videoPreviewExecutor is null) {
            videoPreviewExecutor = new(RuntimePreviewSettings,
                action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background),
                DrainVideoEditorResult, RetryDeferredVideoEditorFrame);
            videoPreviewExecutor.SetLiveInput(videoEditorLiveInput);
            if (videoPreviewMeasurements.IsActive) {
                videoPreviewExecutor.BeginObservation(videoPreviewMeasurements.InputGroup);
            }
        }
        VideoPreviewExecutor executor = videoPreviewExecutor;
        VideoPreviewAdmission admission = executor.TrySubmit(key, bitmap.PixelSize, plan.GetScratchBytes(bitmap.PixelSize),
            () => VideoEditorFrame.Capture(bitmap), plan.Apply);
        videoEditorFrameDeferred = admission == VideoPreviewAdmission.Deferred;
        if (admission == VideoPreviewAdmission.TooLarge) {
            HandleVideoEditorFrameFailure(key, "Бюджета буферов предпросмотра недостаточно для обработки кадра");
        }
        else if (admission == VideoPreviewAdmission.CaptureFailed) {
            HandleVideoEditorFrameFailure(key, "Не удалось скопировать видеокадр для обработки");
        }
    }

    // получаем результат общей очереди и выводим только подходящий кадр или актуальную ошибку
    private void DrainVideoEditorResult(VideoPreviewFrameKey key, VideoEditorFrame? pixels,
        VideoEditorGeometry geometry, Exception? error) {
        if (!IsCurrentVideoEditorRequest(key)) {
            return;
        }
        if (error is not null) {
            HandleVideoEditorFrameFailure(key, "Не удалось обработать видеокадр");
            return;
        }

        try {
            if (pixels is not null) {
                PublishVideoEditorOutput(pixels, geometry, key);
            }
        }
        catch (Exception) {
            HandleVideoEditorFrameFailure(key, "Не удалось показать обработанный видеокадр");
        }
    }

    // сохраняем единичный сбой текущего кадра для паузы, во время показа оставляем удачный результат
    private void HandleVideoEditorFrameFailure(VideoPreviewFrameKey key, string message) {
        if (key.Frame == videoEditorFrame) {
            videoEditorLatestFailure = message;
        }
        if (!videoWantsToPlay || !videoEditorOutputVisible) {
            ShowVideoEditorError(message);
        }
        else if (key.Frame == videoEditorFrame && videoEditorError != message) {
            videoEditorError = message;
            NotifyVideoEditorPreview();
        }
    }

    // в живом потоке допускаем пропуски кадров, на паузе нужен последний подтвержденный
    private bool IsCurrentVideoEditorRequest(VideoPreviewFrameKey key) =>
        videoEditorVisible && !videoEditorWaitingForFrame &&
        ReferenceEquals(CurrentDocument, key.Document) &&
        videoEditorPlan.Revision == key.Plan && videoEditorSource == key.Source &&
        (videoWantsToPlay || videoEditorFrame == key.Frame);

    // на паузе забираем актуальный кадр после освобождения памяти, даже если VLC больше не обновляет его
    private void RetryDeferredVideoEditorFrame() {
        if (videoEditorFrameDeferred && !videoWantsToPlay) {
            RequestVideoEditorFrame();
        }
    }

    private bool IsLiveVideoEditorBudgetHeld =>
        videoAnalysisApplyingPlan || IsReadingVideoAnalysisLiveFallback || PreparedVideo is { ReservedBytes: > 0 };

    // после освобождения буферов другой ветви берем актуальный кадр
    private void RetryVideoEditorAfterBudget(bool liveInput = false) {
        if (videoEditorWaitingForBudget && !IsLiveVideoEditorBudgetHeld) {
            videoEditorWaitingForBudget = false;
            RequestVideoEditorFrame(liveInput);
        }
    }

    // меняем настройки окна и пересылаем текущий кадр, если его результат еще не показан
    internal void ApplyVideoPreviewSettings(VideoPreviewSettings settings) {
        VideoPreviewSettings previous = PreviewSettings;
        bool analysisChanged = previous.AnalysisMaximumWorkers != settings.AnalysisMaximumWorkers ||
            previous.AnalysisBudgetMiB != settings.AnalysisBudgetMiB;
        bool foregroundSettingsChanged = previous.ForegroundBudgetMiB != settings.ForegroundBudgetMiB ||
            previous.ForegroundDecoderThreads != settings.ForegroundDecoderThreads;
        bool foregroundWorkChanged = foregroundSettingsChanged ||
            VideoForegroundSource != VideoPipelineStage.Original && previous.BudgetMiB != settings.BudgetMiB ||
            VideoForegroundSource == VideoPipelineStage.Analysis && analysisChanged ||
            IsCombinedAnalysisOutputSelected && (analysisChanged || previous.BudgetMiB != settings.BudgetMiB);
        bool retrySource = !analysisChanged && !foregroundSettingsChanged && settings.Mode != VideoPreviewMode.Live && preparedSource is null &&
            preparedOpening is null && IsVideoOpen;
        if (settings == PreviewSettings && !retrySource) {
            return;
        }
        if (!retrySource && previous.Mode == settings.Mode && previous.MaximumWorkers == settings.MaximumWorkers &&
            previous.BudgetMiB == settings.BudgetMiB) {
            PreviewSettings = settings;
            if (foregroundWorkChanged) {
                RestartVideoForeground();
            }
            return;
        }
        StopVideoPreviewMonitoring(keepLast: false);
        PreviewSettings = settings;
        ResetVideoPreviewAuto();
        if (previous.Mode != settings.Mode || retrySource) {
            InvalidateVideoForeground();
            ResetVisibleVideoOutput();
            ChangeVideoPreviewMode();
            return;
        }
        if (foregroundWorkChanged) {
            RestartVideoForeground();
        }
        if (preparedSource is not null) {
            if (!(foregroundWorkChanged && IsCombinedStrictOutputSelected) &&
                (previous.BudgetMiB != settings.BudgetMiB ||
                 IsStrictVideo && previous.MaximumWorkers != settings.MaximumWorkers) &&
                videoFrameStep is null && !RecalculatePinnedPreparedFrame()) {
                RequestPreparedVideo(preparedPosition, preparedAtEnd, preservePlay: true);
            }
            return;
        }
        if (!videoEditorOutputVisible || videoEditorOutputFrame != videoEditorFrame) {
            RequestVideoEditorFrame(liveInput: videoEditorLiveInput != 0);
        }
    }

    // возвращаем действующие параметры и последнее завершенное окно для открытого диалога
    internal (VideoPreviewSettings Settings, int Effective, VideoPreviewExecutorMetrics Resources,
        int? PeakActive, VideoPreviewMeasurements.Window? Window, string Status) ReadVideoPreviewState() {
        VideoPreviewMeasurements.Window? window = videoPreviewMeasurements.Last;
        VideoPreviewExecutorMetrics resources = videoPreviewExecutor?.Metrics ?? default;
        int? peakActive = videoPreviewMeasurements.IsActive ? resources.PeakActive : videoPreviewPeakAtStop;
        string duration = window is { } sample ? $" · последний замер {sample.DurationMs / 1000.0:0.##} с" : "";
        string status = !IsVideoOpen || mediaPreviewsDisposed ? "Видео не открыто" :
            !videoEditorVisible ? "Вкладка «Редактирование» не активна" :
            !videoEditorPlan.HasSteps ? "В очереди нет операций" :
            videoEditorWaitingForFrame ? "Ожидание подтвержденного кадра" :
            !videoPreviewMeasurements.IsActive ? videoWantsToPlay ? $"Ожидание живого кадра{duration}" :
                window is null ? "Пауза · нет измерений" : $"Пауза{duration}" :
            window is null ? "Измеряем живой предпросмотр…" : $"Живой предпросмотр{duration}";
        if (videoPreviewAuto.IsTrial) {
            status += " · проба «Авто»";
        }
        int effective = RuntimePreviewSettings.EffectiveWorkers;
        if (videoEditorPlan.HasSteps && VideoPreviewFrame is { } frame) {
            effective = (int)Math.Min(effective, PreviewSettings.BudgetBytes / GetVideoEditorWorkBytes(frame.PixelSize));
        }
        return (PreviewSettings, effective, resources, peakActive, window, status);
    }

    // на подтвержденном живом просмотре собираем окно, а для режима «Авто» сравниваем рабочие пределы
    private void UpdateVideoPreviewMonitoring(bool live) {
        if (!live || !videoEditorVisible || videoEditorWaitingForFrame || !videoEditorPlan.HasSteps ||
            VideoPreviewFrame is not { } bitmap) {
            StopVideoEditorLiveInput();
            if (videoPreviewMeasurements.IsActive) {
                StopVideoPreviewMonitoring(keepLast: true);
            }
            return;
        }

        long now = Environment.TickCount64;
        int previousLimit = RuntimePreviewSettings.EffectiveWorkers;
        if (PreviewSettings.MaximumWorkers == 0) {
            int capacity = GetVideoPreviewCapacity(bitmap.PixelSize);
            videoPreviewAuto.FitCapacity(capacity);
            if (previousLimit == videoPreviewAuto.Workers &&
                TryCompleteVideoPreviewMeasurement(now, out var window)) {
                videoPreviewAuto.Observe(window, capacity, now);
            }
        }
        else {
            TryCompleteVideoPreviewMeasurement(now, out _);
        }

        if (!videoPreviewMeasurements.IsActive || previousLimit != RuntimePreviewSettings.EffectiveWorkers) {
            videoPreviewMeasurements.Start(now, settleMs: 750);
            videoEditorObservation = 0;
            videoPreviewExecutor?.BeginObservation(videoPreviewMeasurements.InputGroup);
            videoPreviewExecutor?.Configure(RuntimePreviewSettings);
        }
    }

    // закрываем группу входов в момент снимка счетчиков, до обработки следующего кадра
    private bool TryCompleteVideoPreviewMeasurement(long now, out VideoPreviewMeasurements.Window window) {
        window = default;
        if (!videoPreviewMeasurements.IsDue(now)) {
            return false;
        }
        VideoPreviewExecutorMetrics metrics =
            videoPreviewExecutor?.AdvanceInputGroup(videoPreviewMeasurements.InputGroup + 1) ?? default;
        return videoPreviewMeasurements.TryComplete(now, metrics, out window);
    }

    // не смешиваем отмену живого потока с вытеснением кадров при перегрузке
    private void StopVideoEditorLiveInput() {
        if (videoEditorLiveInput != 0) {
            videoEditorLiveInput = 0;
            videoPreviewExecutor?.SetLiveInput(0);
        }
    }

    // закрываем измерение, восстанавливая принятый предел после прерванной пробы
    private void StopVideoPreviewMonitoring(bool keepLast) {
        videoEditorObservation = 0;
        videoPreviewPeakAtStop = keepLast && videoPreviewMeasurements.Last is not null
            ? videoPreviewExecutor?.Metrics.PeakActive : null;
        videoPreviewMeasurements.Stop(keepLast);
        int previousLimit = RuntimePreviewSettings.EffectiveWorkers;
        videoPreviewAuto.CancelTrial();
        if (!keepLast) {
            videoPreviewExecutor?.BeginObservation(0);
        }
        else {
            videoPreviewExecutor?.AdvanceInputGroup(0);
        }
        if (previousLimit != RuntimePreviewSettings.EffectiveWorkers) {
            videoPreviewExecutor?.Configure(RuntimePreviewSettings);
        }
    }

    // новая цепочка и примененные настройки заново подбирают число обработчиков
    private void ResetVideoPreviewAuto() {
        videoPreviewAuto.Reset();
        if (PreviewSettings.MaximumWorkers == 0 && videoEditorPlan.HasSteps && VideoPreviewFrame is { } bitmap) {
            videoPreviewAuto.FitCapacity(GetVideoPreviewCapacity(bitmap.PixelSize));
        }
        videoPreviewExecutor?.Configure(RuntimePreviewSettings);
    }

    // считаем, сколько активных кадров с рабочими буферами помещается в бюджет
    private int GetVideoPreviewCapacity(PixelSize size) {
        return (int)Math.Min(VideoPreviewSettings.AutoWorkerLimit, PreviewSettings.BudgetBytes / GetVideoEditorWorkBytes(size));
    }

    // учитываем снимок и дополнительные полноразмерные буферы операции
    private long GetVideoEditorWorkBytes(PixelSize size) =>
        VideoEditorFrame.GetBufferBytes(size) + videoEditorPlan.GetScratchBytes(size);

    // переносим строки в bitmap и измеряем выдачу после обновления привязок
    private void PublishVideoEditorOutput(VideoEditorFrame pixels, VideoEditorGeometry geometry, VideoPreviewFrameKey key) {
        WriteableBitmap? previous = videoEditorOutput;
        WriteableBitmap next = previous?.PixelSize == pixels.Size
            ? previous
            : new(pixels.Size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        try {
            using ILockedFramebuffer target = next.Lock();
            pixels.CopyTo(target);
        }
        catch {
            if (!ReferenceEquals(previous, next)) {
                next.Dispose();
            }
            throw;
        }

        videoEditorOutput = next;
        videoEditorOutputGeometry = geometry;
        videoEditorOutputFrame = key.Frame;
        videoEditorOutputVisible = true;
        if (key.Frame == videoEditorFrame) {
            videoEditorLatestFailure = null;
        }
        videoEditorError = videoEditorLatestFailure;
        try {
            NotifyVideoEditorPreview();
            videoPreviewMeasurements.RecordOutput(key.Observation, key.Frame, key.OfferedAt, Environment.TickCount64);
            RecordVisibleEditedFrame(key.LiveInput != 0 && key.LiveInput == videoEditorLiveInput && videoWantsToPlay);
            if (!videoWantsToPlay && key.PauseObservation == videoEditorPauseObservation && IsCurrentVideoEditorRequest(key)) {
                SetVideoEditorProcessingTime(pixels.ProcessingTime);
            }
        }
        finally {
            if (!ReferenceEquals(previous, next)) {
                previous?.Dispose();
            }
        }
    }

    // сообщаем об ошибке редактора отдельно от состояния исходного плеера
    private void ShowVideoEditorError(string message) {
        SetVideoEditorProcessingTime(null);
        if (videoEditorError == message && !videoEditorOutputVisible) {
            return;
        }
        videoEditorOutputVisible = false;
        videoEditorError = message;
        NotifyVideoEditorPreview();
    }

    // показываем ошибку получения кадра в обоих видах редактора
    private void ShowVideoEditorSourceError(string message) {
        videoEditorSourceError = message;
        OnPropertyChanged(nameof(VideoEditorSourceNotice));
        OnPropertyChanged(nameof(VideoEditorNotice));
    }

    // оригинал становится видимым после подтверждения кадра общим плеером
    private void NotifyVideoEditorSource() {
        OnPropertyChanged(nameof(IsVideoEditorSourceReady));
        OnPropertyChanged(nameof(VideoEditorSourceNotice));
    }

    // снимаем ожидающий повтор и запрещаем выдачу кадров прошлого документа, источника или плана
    private void InvalidateVideoEditorRequests() {
        SetVideoEditorProcessingTime(null);
        StopVideoEditorLiveInput();
        videoEditorFrameDeferred = false;
        videoEditorWaitingForBudget = false;
        videoEditorLatestFailure = null;
        videoPreviewExecutor?.Invalidate();
        StopVideoPreviewMonitoring(keepLast: false);
    }

    // обновляем источник, статус и отрисовку переиспользуемого изображения
    private void NotifyVideoEditorPreview() {
        VideoEditorPreviewRevision++;
        OnPropertyChanged(nameof(VideoEditorPreviewFrame));
        OnPropertyChanged(nameof(CurrentVideoEditorGeometry));
        OnPropertyChanged(nameof(VideoEditorNotice));
        OnPropertyChanged(nameof(VideoEditorPreviewRevision));
        RefreshVideoAnalysisInput();
    }

    // обнуляем накопленные входы и пропуски для новой цепочки или файла
    private void ResetVideoEditorChainTotals() {
        VideoEditorChainInput = 0;
        videoPreviewExecutor?.ResetChainDrops();
    }

    // выводим время расчета только рядом с актуальным результатом на паузе
    private void SetVideoEditorProcessingTime(TimeSpan? elapsed) {
        string? text = elapsed is { } time ? $"Время пересчёта кадра: {time.TotalMilliseconds:0.##} мс" : null;
        if (VideoEditorProcessingTime != text) {
            VideoEditorProcessingTime = text;
            OnPropertyChanged(nameof(VideoEditorProcessingTime));
            OnPropertyChanged(nameof(VideoEditorVisibleProcessingTime));
        }
    }

    // освобождаем результат старого документа и возвращаем пустой план
    private void ResetVideoEditorDocument() {
        InvalidateVideoEditorRequests();
        ResetVideoEditorChainTotals();
        videoPreviewExecutor?.ResetBudgetPeak();
        videoEditorPlan = new(videoEditorPlan.Revision + 1, []);
        ResetVideoPreviewAuto();
        videoEditorWaitingForFrame = false;
        videoEditorOutputVisible = false;
        videoEditorSourceError = null;
        videoEditorError = null;
        WriteableBitmap? previous = videoEditorOutput;
        videoEditorOutput = null;
        videoEditorOutputGeometry = default;
        NotifyVideoEditorSource();
        NotifyVideoEditorPreview();
        previous?.Dispose();
    }
}
