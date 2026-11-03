using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private bool isEdgesAnalysisEnabled;
    private bool isAutoAnalysisEnabled;
    private bool showAnalysisEdges = true;
    private AnalysisResultBackground selectedAnalysisBackground = AnalysisResultBackground.Black;
    private bool analysisWorkerRunning;
    private bool analysisRunRequested;
    private bool analysisForceRunRequested;
    private bool analysisCompositionRequested;
    private bool analysisDisposed;
    private long edgesWorkRevision;
    private long analysisDisplayRevision;
    private long analysisResultRevision;
    private DispatcherTimer? analysisDebounce;
    private CancellationTokenSource? activeAnalysisCancellation;
    private enum AnalysisWorkerMethod { None, Edges, Segmentation, Composition }
    private AnalysisWorkerMethod activeAnalysisMethod;
    private string edgesFailure = string.Empty;
    private AnalysisEdgeMethod selectedEdgeMethod = AnalysisEdgeMethod.Roberts;
    private string cannyLowerThresholdText = "50";
    private string cannyUpperThresholdText = "150";
    private AnalysisEdgeResult? edgesResult;
    private Bitmap? analysisResultImage;
    private byte[]? analysisResultPng;
    private string analysisStatusMessage = "Включите метод и выполните анализ";

    public bool IsEdgesAnalysisEnabled {
        get => isEdgesAnalysisEnabled;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (!SetProperty(ref isEdgesAnalysisEnabled, value)) {
                return;
            }
            if (value) {
                ShowAnalysisEdges = true;
            }
            InvalidateEdgeAnalysis();
        }
    }

    public AnalysisEdgeMethod SelectedEdgeMethod {
        get => selectedEdgeMethod;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (value != AnalysisEdgeMethod.Roberts && value != AnalysisEdgeMethod.Canny) {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (!SetProperty(ref selectedEdgeMethod, value)) {
                return;
            }
            OnPropertyChanged(nameof(SelectedEdgeMethodIndex));
            OnPropertyChanged(nameof(IsRobertsEdgeMethod));
            OnPropertyChanged(nameof(IsCannyEdgeMethod));
            if (isEdgesAnalysisEnabled) {
                InvalidateEdgeAnalysis();
            }
        }
    }

    public int SelectedEdgeMethodIndex {
        get => selectedEdgeMethod == AnalysisEdgeMethod.Canny ? 1 : 0;
        set {
            // -1 при подготовке ComboBox не сбрасывает уже выбранный метод
            if (value == 0 || value == 1) {
                SelectedEdgeMethod = value == 1 ? AnalysisEdgeMethod.Canny : AnalysisEdgeMethod.Roberts;
            }
        }
    }

    public bool IsRobertsEdgeMethod => selectedEdgeMethod == AnalysisEdgeMethod.Roberts;
    public bool IsCannyEdgeMethod => selectedEdgeMethod == AnalysisEdgeMethod.Canny;

    public string CannyLowerThresholdText {
        get => cannyLowerThresholdText;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (SetProperty(ref cannyLowerThresholdText, value ?? string.Empty)) {
                NotifyCannyThresholdsChanged();
            }
        }
    }

    public string CannyUpperThresholdText {
        get => cannyUpperThresholdText;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (SetProperty(ref cannyUpperThresholdText, value ?? string.Empty)) {
                NotifyCannyThresholdsChanged();
            }
        }
    }

    public string CannyThresholdError => TryGetCannyThresholds(out _, out _) ? string.Empty
        : "Введите конечные числа: 0 ≤ нижний < верхний ≤ 32767";
    public bool HasCannyThresholdError => !TryGetCannyThresholds(out _, out _);
    private bool AreSelectedAnalysisParametersValid => IsRobertsEdgeMethod || !HasCannyThresholdError;
    private bool HasEnabledAnalysisMethods => isEdgesAnalysisEnabled || isSegmentationAnalysisEnabled;
    private bool AreAllAnalysisParametersValid => (!isEdgesAnalysisEnabled || AreSelectedAnalysisParametersValid) &&
        (!isSegmentationAnalysisEnabled || !HasSegmentationValidationError);
    private bool HasMissingAnalysisResults => (isEdgesAnalysisEnabled && edgesResult is null) ||
        (isSegmentationAnalysisEnabled && segmentationResult is null);

    public bool IsAutoAnalysisEnabled {
        get => isAutoAnalysisEnabled;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (!SetProperty(ref isAutoAnalysisEnabled, value)) {
                return;
            }
            analysisDebounce?.Stop();
            if (!value) {
                analysisRunRequested = false;
                analysisForceRunRequested = false;
            }
            else if (HasMissingAnalysisResults) {
                ScheduleAutoAnalysis();
            }
        }
    }

    public bool ShowAnalysisEdges {
        get => showAnalysisEdges;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (SetProperty(ref showAnalysisEdges, value)) {
                RequestAnalysisComposition();
            }
        }
    }

    public AnalysisResultBackground SelectedAnalysisBackground {
        get => selectedAnalysisBackground;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (value != AnalysisResultBackground.Black && value != AnalysisResultBackground.Input &&
                value != AnalysisResultBackground.Segmentation) {
                throw new ArgumentOutOfRangeException(nameof(value));
            }
            if (value == AnalysisResultBackground.Segmentation && !isSegmentationAnalysisEnabled) {
                return;
            }
            if (!SetProperty(ref selectedAnalysisBackground, value)) {
                return;
            }
            OnPropertyChanged(nameof(UseAnalysisInputBackground));
            OnPropertyChanged(nameof(IsAnalysisBlackBackground));
            OnPropertyChanged(nameof(IsAnalysisSegmentationBackground));
            RequestAnalysisComposition();
        }
    }

    public bool UseAnalysisInputBackground {
        get => selectedAnalysisBackground == AnalysisResultBackground.Input;
        set => SelectedAnalysisBackground = value ? AnalysisResultBackground.Input : AnalysisResultBackground.Black;
    }
    public bool IsAnalysisBlackBackground => selectedAnalysisBackground == AnalysisResultBackground.Black;
    public bool IsAnalysisSegmentationBackground => selectedAnalysisBackground == AnalysisResultBackground.Segmentation;
    public bool IsAnalysisRunning => analysisWorkerRunning;
    public bool CanRunAnalysis => !analysisDisposed && HasEnabledAnalysisMethods && HasAnalysisInput &&
        AreAllAnalysisParametersValid && !analysisWorkerRunning;
    public bool HasAnalysisResult => analysisResultImage is not null && analysisResultPng is not null;
    public bool IsAnalysisResultEmpty => !HasAnalysisResult;
    public bool CanSaveAnalysisResult => !analysisDisposed && HasAnalysisResult && !analysisWorkerRunning;
    public Bitmap? AnalysisResultImage => analysisResultImage;
    public AnalysisEdgeResult? EdgesResult => edgesResult;
    public long AnalysisResultRevision => analysisResultRevision;
    public string AnalysisStatusMessage {
        get => analysisStatusMessage;
        private set => SetProperty(ref analysisStatusMessage, value);
    }

    public async Task RunSelectedAnalysisAsync() {
        Dispatcher.UIThread.VerifyAccess();
        if (!CanRunAnalysis) {
            return;
        }
        analysisDebounce?.Stop();
        analysisRunRequested = true;
        analysisForceRunRequested = true;
        await ProcessAnalysisRequestsAsync();
    }

    public byte[]? CaptureAnalysisResultPng() {
        Dispatcher.UIThread.VerifyAccess();
        return CanSaveAnalysisResult ? (byte[])analysisResultPng!.Clone() : null;
    }

    // вызывается при закрытии окна, переход между страницами не завершает состояние анализа
    public void DisposeAnalysis() {
        Dispatcher.UIThread.VerifyAccess();
        if (analysisDisposed) {
            return;
        }
        analysisDisposed = true;
        analysisDebounce?.Stop();
        if (analysisDebounce is not null) {
            analysisDebounce.Tick -= AnalysisDebounce_OnTick;
        }
        activeAnalysisCancellation?.Cancel();
        analysisRunRequested = false;
        analysisForceRunRequested = false;
        analysisCompositionRequested = false;
        SetEdgesResult(null);
        SetSegmentationResult(null);
        ReplaceAnalysisComposition(null, null);
    }

    private bool TryGetCannyThresholds(out double lowerThreshold, out double upperThreshold) {
        bool lowerParsed = double.TryParse(cannyLowerThresholdText.Trim().Replace(',', '.'),
            NumberStyles.Float, CultureInfo.InvariantCulture, out lowerThreshold);
        bool upperParsed = double.TryParse(cannyUpperThresholdText.Trim().Replace(',', '.'),
            NumberStyles.Float, CultureInfo.InvariantCulture, out upperThreshold);
        return lowerParsed && upperParsed && CannyAnalysisService.AreThresholdsValid(lowerThreshold, upperThreshold);
    }

    private void NotifyCannyThresholdsChanged() {
        OnPropertyChanged(nameof(CannyThresholdError));
        OnPropertyChanged(nameof(HasCannyThresholdError));
        if (IsCannyEdgeMethod && isEdgesAnalysisEnabled) {
            InvalidateEdgeAnalysis();
        }
    }

    private void InvalidateAllAnalysis() {
        if (analysisDisposed) {
            return;
        }
        edgesWorkRevision++;
        segmentationWorkRevision++;
        edgesFailure = string.Empty;
        segmentationFailure = string.Empty;
        analysisDebounce?.Stop();
        activeAnalysisCancellation?.Cancel();
        analysisRunRequested = false;
        analysisForceRunRequested = false;
        SetEdgesResult(null);
        SetSegmentationResult(null);
        NotifySegmentationValidation();
        RequestAnalysisComposition();
        RefreshAnalysisStatus();
        ScheduleAutoAnalysis();
    }

    // вызывается после смены метода границ, его включения или действующих параметров
    private void InvalidateEdgeAnalysis() {
        if (analysisDisposed) {
            return;
        }
        edgesWorkRevision++;
        edgesFailure = string.Empty;
        analysisDebounce?.Stop();
        analysisRunRequested = false;
        analysisForceRunRequested = false;
        if (activeAnalysisMethod == AnalysisWorkerMethod.Edges) {
            activeAnalysisCancellation?.Cancel();
        }
        SetEdgesResult(null);
        RequestAnalysisComposition();
        RefreshAnalysisStatus();
        ScheduleAutoAnalysis();
    }

    private void ScheduleAutoAnalysis() {
        if (analysisDisposed || !isAutoAnalysisEnabled || !HasAnalysisInput ||
            !HasEnabledAnalysisMethods || !AreAllAnalysisParametersValid || !HasMissingAnalysisResults) {
            return;
        }
        if (analysisDebounce is null) {
            analysisDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            analysisDebounce.Tick += AnalysisDebounce_OnTick;
        }
        analysisDebounce.Stop();
        analysisDebounce.Start();
    }

    private void AnalysisDebounce_OnTick(object? sender, EventArgs e) {
        analysisDebounce?.Stop();
        if (analysisDisposed || !isAutoAnalysisEnabled || !HasAnalysisInput ||
            !HasEnabledAnalysisMethods || !AreAllAnalysisParametersValid || !HasMissingAnalysisResults) {
            return;
        }
        // один флаг заменяет ожидающие запуски, работающий цикл сам заберет последний вход
        analysisRunRequested = true;
        _ = ProcessAnalysisRequestsAsync();
    }

    private void RequestAnalysisComposition() {
        analysisDisplayRevision++;
        ReplaceAnalysisComposition(null, null);
        analysisCompositionRequested = !analysisDisposed;
        if (analysisCompositionRequested) {
            _ = ProcessAnalysisRequestsAsync();
        }
    }

    private bool IsCurrentEdgeWork(long revision, long inputRevision) =>
        !analysisDisposed && isEdgesAnalysisEnabled && AreSelectedAnalysisParametersValid &&
        revision == edgesWorkRevision && inputRevision == AnalysisInputRevision;

    private async Task ProcessAnalysisRequestsAsync() {
        if (analysisWorkerRunning || analysisDisposed) {
            return;
        }
        analysisWorkerRunning = true;
        NotifyAnalysisControls();
        try {
            // один цикл забирает последнюю потребность, флаги не создают очередь отдельных Task
            // версии методов защищают параметры, версия входа общая, версия показа отделяет смену подложки и видимости
            while (!analysisDisposed && (analysisRunRequested || analysisCompositionRequested)) {
                bool calculate = analysisRunRequested;
                bool force = analysisForceRunRequested;
                analysisRunRequested = false;
                analysisForceRunRequested = false;
                analysisCompositionRequested = false;
                if (calculate && HasAnalysisInput && HasEnabledAnalysisMethods && AreAllAnalysisParametersValid) {
                    // worker получает снимок и значения, свойства ViewModel в Task.Run не читаются
                    AnalysisInputSnapshot input = CaptureAnalysisInput()
                        ?? throw new InvalidOperationException("Вход анализа отсутствует");
                    int width = AnalysisInputImage!.PixelSize.Width;
                    int height = AnalysisInputImage.PixelSize.Height;
                    long edgeRevision = edgesWorkRevision;
                    long segmentRevision = segmentationWorkRevision;
                    AnalysisEdgeMethod method = selectedEdgeMethod;
                    TryGetCannyThresholds(out double lower, out double upper);
                    TryGetSegmentationClusterCount(out int clusters);
                    bool calculateEdges = isEdgesAnalysisEnabled && (force || edgesResult is null);
                    bool calculateSegmentation = isSegmentationAnalysisEnabled && (force || segmentationResult is null);
                    // ручная кнопка повторяет выбранные методы, авто пересчитывает только недействительные
                    if (calculateEdges) {
                        SetEdgesResult(null);
                        edgesFailure = string.Empty;
                    }
                    if (calculateSegmentation) {
                        SetSegmentationResult(null);
                        segmentationFailure = string.Empty;
                    }
                    ReplaceAnalysisComposition(null, null);
                    if (calculateEdges && IsCurrentEdgeWork(edgeRevision, input.InputRevision)) {
                        await CalculateEdgesAsync(input, width, height, edgeRevision, method, lower, upper);
                    }
                    if (calculateSegmentation && IsCurrentSegmentationWork(segmentRevision, input.InputRevision)) {
                        await CalculateSegmentationAsync(input, width, height, segmentRevision, clusters);
                    }
                }
                if (analysisDisposed) {
                    break;
                }

                // после расчета берем последние настройки показа, детектор повторно не вызываем
                // композиция берет последние настройки показа и только согласованные текущие карты
                analysisCompositionRequested = false;
                AnalysisEdgeResult? edges = edgesResult;
                AnalysisSegmentationResult? segmentation = segmentationResult;
                bool missingVisibleEdges = isEdgesAnalysisEnabled && showAnalysisEdges && edges is null;
                bool missingBackground = IsAnalysisSegmentationBackground && segmentation is null;
                if (missingVisibleEdges || missingBackground || (edges is null && segmentation is null)) {
                    ReplaceAnalysisComposition(null, null);
                    RefreshAnalysisStatus();
                    continue;
                }
                long inputRevision = AnalysisInputRevision;
                long displayRevision = analysisDisplayRevision;
                AnalysisResultBackground background = selectedAnalysisBackground;
                bool showEdges = showAnalysisEdges;
                using CancellationTokenSource cancellation = new CancellationTokenSource();
                activeAnalysisCancellation = cancellation;
                activeAnalysisMethod = AnalysisWorkerMethod.Composition;
                try {
                    AnalysisStatusMessage = "Подготовка результата";
                    byte[] png = await Task.Run(() => AnalysisCompositionService.CreatePng(
                        edges, segmentation, background, showEdges, cancellation.Token), cancellation.Token);
                    if (analysisDisposed || inputRevision != AnalysisInputRevision || displayRevision != analysisDisplayRevision ||
                        !ReferenceEquals(edges, edgesResult) || !ReferenceEquals(segmentation, segmentationResult)) {
                        continue;
                    }
                    using MemoryStream stream = new MemoryStream(png);
                    Bitmap nextImage = new Bitmap(stream);
                    ReplaceAnalysisComposition(nextImage, png);
                    RefreshAnalysisStatus();
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
                    // смена входа и закрытие не публикуют старый PNG
                }
                catch (Exception exception) {
                    if (!analysisDisposed && inputRevision == AnalysisInputRevision && displayRevision == analysisDisplayRevision) {
                        ReplaceAnalysisComposition(null, null);
                        AnalysisStatusMessage = $"Не удалось подготовить результат: {exception.Message}";
                    }
                }
                finally {
                    activeAnalysisCancellation = null;
                    activeAnalysisMethod = AnalysisWorkerMethod.None;
                }
            }
        }
        catch (Exception exception) {
            if (!analysisDisposed) {
                ReplaceAnalysisComposition(null, null);
                AnalysisStatusMessage = $"Не удалось подготовить анализ: {exception.Message}";
            }
        }
        finally {
            analysisWorkerRunning = false;
            NotifyAnalysisControls();
        }
    }

    private async Task CalculateEdgesAsync(
        AnalysisInputSnapshot input, int width, int height, long revision,
        AnalysisEdgeMethod method, double lower, double upper) {

        using CancellationTokenSource cancellation = new CancellationTokenSource();
        activeAnalysisCancellation = cancellation;
        activeAnalysisMethod = AnalysisWorkerMethod.Edges;
        try {
            AnalysisStatusMessage = method == AnalysisEdgeMethod.Canny ? "Выполняется анализ Canny" : "Выполняется анализ Робертса";
            AnalysisEdgeResult next = await Task.Run(() => method == AnalysisEdgeMethod.Canny
                ? CannyAnalysisService.Analyze(input, lower, upper, cancellation.Token)
                : RobertsAnalysisService.Analyze(input, cancellation.Token), cancellation.Token);
            if (!IsCurrentEdgeWork(revision, input.InputRevision)) {
                return;
            }
            if (next.Width != width || next.Height != height || next.Method != method || next.InputRevision != input.InputRevision) {
                throw new InvalidOperationException("Карта границ не соответствует выбранному входу и методу");
            }
            SetEdgesResult(next);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            // отмененная работа не публикует картинку и не заменяет сообщение нового состояния
            // отмененная карта границ не принимается за актуальную
        }
        catch (Exception exception) {
            if (IsCurrentEdgeWork(revision, input.InputRevision)) {
                SetEdgesResult(null);
                edgesFailure = "Границы: " + exception.Message;
            }
        }
        finally {
            activeAnalysisCancellation = null;
            activeAnalysisMethod = AnalysisWorkerMethod.None;
        }
    }

    private async Task CalculateSegmentationAsync(
        AnalysisInputSnapshot input, int width, int height, long revision, int clusters) {

        using CancellationTokenSource cancellation = new CancellationTokenSource();
        activeAnalysisCancellation = cancellation;
        activeAnalysisMethod = AnalysisWorkerMethod.Segmentation;
        try {
            AnalysisStatusMessage = $"Выполняется K-means · N={clusters}";
            AnalysisSegmentationResult next = await Task.Run(
                () => KMeansAnalysisService.Analyze(input, clusters, cancellation.Token), cancellation.Token);
            if (!IsCurrentSegmentationWork(revision, input.InputRevision)) {
                return;
            }
            if (next.Width != width || next.Height != height || next.ClusterCount != clusters || next.InputRevision != input.InputRevision) {
                throw new InvalidOperationException("Сегментация не соответствует выбранному входу и N");
            }
            SetSegmentationResult(next);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            // native-вызов завершается сам, отмененный результат не принимается
        }
        catch (Exception exception) {
            if (IsCurrentSegmentationWork(revision, input.InputRevision)) {
                SetSegmentationResult(null);
                segmentationFailure = "Сегментация: " + exception.Message;
            }
        }
        finally {
            activeAnalysisCancellation = null;
            activeAnalysisMethod = AnalysisWorkerMethod.None;
        }
    }

    private void RefreshAnalysisStatus() {
        string issue = isEdgesAnalysisEnabled && !AreSelectedAnalysisParametersValid ? CannyThresholdError
            : isSegmentationAnalysisEnabled && HasSegmentationValidationError ? SegmentationValidationError
            : string.Join("; ", new[] { edgesFailure, segmentationFailure }).Trim(' ', ';');
        if (HasAnalysisResult) {
            string methods = edgesResult is null ? string.Empty : edgesResult.Method == AnalysisEdgeMethod.Canny ? "Canny" : "Робертс";
            if (segmentationResult is not null) {
                methods += (methods.Length == 0 ? string.Empty : " + ") + $"K-means N={segmentationResult.ClusterCount}";
            }
            AnalysisStatusMessage = $"Готово: {methods} · {AnalysisInputImage!.PixelSize.Width} × {AnalysisInputImage.PixelSize.Height}" +
                (issue.Length == 0 ? string.Empty : " · " + issue);
        }
        else {
            AnalysisStatusMessage = !HasAnalysisInput ? "Откройте изображение"
                : !HasEnabledAnalysisMethods ? "Включите метод анализа"
                : issue.Length != 0 ? issue : "Вход или параметры изменились, выполните анализ";
        }
    }

    private void SetEdgesResult(AnalysisEdgeResult? result) {
        edgesResult = result;
        OnPropertyChanged(nameof(EdgesResult));
    }

    private void ReplaceAnalysisComposition(Bitmap? image, byte[]? png) {
        Bitmap? previous = analysisResultImage;
        analysisResultImage = image;
        analysisResultPng = png;
        analysisResultRevision++;
        OnPropertyChanged(nameof(AnalysisResultImage));
        OnPropertyChanged(nameof(AnalysisResultRevision));
        NotifyAnalysisControls();
        previous?.Dispose();
    }

    private void NotifyAnalysisControls() {
        OnPropertyChanged(nameof(IsAnalysisRunning));
        OnPropertyChanged(nameof(CanRunAnalysis));
        OnPropertyChanged(nameof(HasAnalysisResult));
        OnPropertyChanged(nameof(IsAnalysisResultEmpty));
        OnPropertyChanged(nameof(CanSaveAnalysisResult));
    }
}
