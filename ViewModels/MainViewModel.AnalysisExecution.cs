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
    private bool useAnalysisInputBackground;
    private bool analysisWorkerRunning;
    private bool analysisRunRequested;
    private bool analysisCompositionRequested;
    private bool analysisDisposed;
    private long analysisWorkGeneration;
    private long analysisDisplayRevision;
    private long analysisResultRevision;
    private DispatcherTimer? analysisDebounce;
    private CancellationTokenSource? activeAnalysisCancellation;
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
            InvalidateEdgeAnalysis();
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
            }
            else if (edgesResult is null) {
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

    public bool UseAnalysisInputBackground {
        get => useAnalysisInputBackground;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (SetProperty(ref useAnalysisInputBackground, value)) {
                OnPropertyChanged(nameof(IsAnalysisBlackBackground));
                RequestAnalysisComposition();
            }
        }
    }

    public bool IsAnalysisBlackBackground => !useAnalysisInputBackground;
    public bool IsAnalysisRunning => analysisWorkerRunning;
    public bool CanRunAnalysis => !analysisDisposed && isEdgesAnalysisEnabled && HasAnalysisInput &&
        AreSelectedAnalysisParametersValid && !analysisWorkerRunning;
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
        analysisWorkGeneration++;
        analysisRunRequested = true;
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
        analysisCompositionRequested = false;
        SetEdgesResult(null);
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
        if (IsCannyEdgeMethod) {
            InvalidateEdgeAnalysis();
        }
    }

    // вызывается после согласованной смены входа, метода или его действующих параметров
    private void InvalidateEdgeAnalysis() {
        Dispatcher.UIThread.VerifyAccess();
        if (analysisDisposed) {
            return;
        }
        analysisWorkGeneration++;
        analysisDebounce?.Stop();
        activeAnalysisCancellation?.Cancel();
        analysisRunRequested = false;
        analysisCompositionRequested = false;
        SetEdgesResult(null);
        ReplaceAnalysisComposition(null, null);
        AnalysisStatusMessage = !HasAnalysisInput ? "Откройте изображение"
            : !isEdgesAnalysisEnabled ? "Включите метод анализа"
            : !AreSelectedAnalysisParametersValid ? CannyThresholdError
            : "Вход или параметры изменились, выполните анализ";
        ScheduleAutoAnalysis();
    }

    private void ScheduleAutoAnalysis() {
        if (analysisDisposed || !isAutoAnalysisEnabled || !isEdgesAnalysisEnabled || !HasAnalysisInput ||
            !AreSelectedAnalysisParametersValid) {
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
        if (analysisDisposed || !isAutoAnalysisEnabled || !isEdgesAnalysisEnabled || !HasAnalysisInput ||
            !AreSelectedAnalysisParametersValid) {
            return;
        }
        // один флаг заменяет ожидающие запуски, работающий цикл сам заберет последний вход
        analysisRunRequested = true;
        _ = ProcessAnalysisRequestsAsync();
    }

    private void RequestAnalysisComposition() {
        analysisDisplayRevision++;
        ReplaceAnalysisComposition(null, null);
        analysisCompositionRequested = edgesResult is not null && !analysisDisposed;
        if (analysisCompositionRequested) {
            _ = ProcessAnalysisRequestsAsync();
        }
    }

    private bool IsCurrentAnalysisWork(long generation) =>
        !analysisDisposed && isEdgesAnalysisEnabled && HasAnalysisInput && AreSelectedAnalysisParametersValid &&
        generation == analysisWorkGeneration;

    private async Task ProcessAnalysisRequestsAsync() {
        if (analysisWorkerRunning || analysisDisposed) {
            return;
        }
        analysisWorkerRunning = true;
        NotifyAnalysisControls();
        try {
            // один цикл забирает последнюю потребность, флаги не создают очередь отдельных Task
            // поколение защищает вход и параметры, версия показа отделяет смену подложки и видимости
            while (!analysisDisposed && (analysisRunRequested || analysisCompositionRequested)) {
                bool calculate = analysisRunRequested;
                analysisRunRequested = false;
                analysisCompositionRequested = false;
                long generation = analysisWorkGeneration;
                if (!IsCurrentAnalysisWork(generation)) {
                    continue;
                }
                using CancellationTokenSource cancellation = new CancellationTokenSource();
                activeAnalysisCancellation = cancellation;
                try {
                    if (calculate) {
                        SetEdgesResult(null);
                        ReplaceAnalysisComposition(null, null);
                        AnalysisInputSnapshot input = CaptureAnalysisInput()
                            ?? throw new InvalidOperationException("Вход анализа отсутствует");
                        int expectedWidth = AnalysisInputImage!.PixelSize.Width;
                        int expectedHeight = AnalysisInputImage.PixelSize.Height;
                        AnalysisEdgeMethod method = selectedEdgeMethod;
                        double lowerThreshold = 0;
                        double upperThreshold = 0;
                        if (method == AnalysisEdgeMethod.Canny && !TryGetCannyThresholds(out lowerThreshold, out upperThreshold)) {
                            throw new InvalidOperationException(CannyThresholdError);
                        }
                        AnalysisStatusMessage = method == AnalysisEdgeMethod.Canny
                            ? "Выполняется анализ Canny" : "Выполняется анализ Робертса";
                        // worker получает снимок и значения, свойства ViewModel в Task.Run не читаются
                        AnalysisEdgeResult next = await Task.Run(() => method == AnalysisEdgeMethod.Canny
                            ? CannyAnalysisService.Analyze(input, lowerThreshold, upperThreshold, cancellation.Token)
                            : RobertsAnalysisService.Analyze(input, cancellation.Token), cancellation.Token);
                        if (!IsCurrentAnalysisWork(generation) || next.InputRevision != AnalysisInputRevision) {
                            continue;
                        }
                        if (next.Width != expectedWidth || next.Height != expectedHeight || next.Method != method) {
                            throw new InvalidOperationException("Размер или метод результата не соответствует выбранному входу");
                        }
                        SetEdgesResult(next);
                    }

                    AnalysisEdgeResult? edges = edgesResult;
                    if (edges is null || edges.InputRevision != AnalysisInputRevision || edges.Method != selectedEdgeMethod) {
                        continue;
                    }
                    // после расчета берем последние настройки показа, детектор повторно не вызываем
                    analysisCompositionRequested = false;
                    long displayRevision = analysisDisplayRevision;
                    bool useBackground = useAnalysisInputBackground;
                    bool showEdges = showAnalysisEdges;
                    AnalysisStatusMessage = "Подготовка результата";
                    byte[] png = await Task.Run(
                        () => AnalysisCompositionService.CreatePng(edges, useBackground, showEdges, cancellation.Token),
                        cancellation.Token);
                    if (!IsCurrentAnalysisWork(generation) || !ReferenceEquals(edges, edgesResult) ||
                        displayRevision != analysisDisplayRevision) {
                        continue;
                    }

                    using MemoryStream stream = new MemoryStream(png);
                    Bitmap nextImage = new Bitmap(stream);
                    ReplaceAnalysisComposition(nextImage, png);
                    string methodName = edges.Method == AnalysisEdgeMethod.Canny ? "Canny" : "Робертс";
                    AnalysisStatusMessage = $"Готово: {methodName} · {edges.Width} × {edges.Height}";
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
                    // отмененная работа не публикует картинку и не заменяет сообщение нового состояния
                }
                catch (Exception exception) {
                    if (IsCurrentAnalysisWork(generation)) {
                        if (calculate) {
                            SetEdgesResult(null);
                        }
                        ReplaceAnalysisComposition(null, null);
                        AnalysisStatusMessage = $"Не удалось выполнить анализ: {exception.Message}";
                    }
                }
                finally {
                    activeAnalysisCancellation = null;
                }
            }
        }
        finally {
            analysisWorkerRunning = false;
            NotifyAnalysisControls();
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
