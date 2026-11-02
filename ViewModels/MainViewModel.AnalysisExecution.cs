using System;
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
    private AnalysisEdgeResult? robertsResult;
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
            InvalidateRobertsAnalysis();
        }
    }

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
            else if (robertsResult is null) {
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
    public bool CanRunAnalysis => !analysisDisposed && isEdgesAnalysisEnabled && HasAnalysisInput && !analysisWorkerRunning;
    public bool HasAnalysisResult => analysisResultImage is not null && analysisResultPng is not null;
    public bool IsAnalysisResultEmpty => !HasAnalysisResult;
    public bool CanSaveAnalysisResult => !analysisDisposed && HasAnalysisResult && !analysisWorkerRunning;
    public Bitmap? AnalysisResultImage => analysisResultImage;
    public AnalysisEdgeResult? RobertsResult => robertsResult;
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
        SetRobertsResult(null);
        ReplaceAnalysisComposition(null, null);
    }

    // вызывается после согласованной замены байтов документа в partial-файле входа
    private void InvalidateRobertsAnalysis() {
        Dispatcher.UIThread.VerifyAccess();
        if (analysisDisposed) {
            return;
        }
        analysisWorkGeneration++;
        analysisDebounce?.Stop();
        activeAnalysisCancellation?.Cancel();
        analysisRunRequested = false;
        analysisCompositionRequested = false;
        SetRobertsResult(null);
        ReplaceAnalysisComposition(null, null);
        AnalysisStatusMessage = !HasAnalysisInput ? "Откройте изображение"
            : !isEdgesAnalysisEnabled ? "Включите метод анализа"
            : "Вход изменился, выполните анализ";
        ScheduleAutoAnalysis();
    }

    private void ScheduleAutoAnalysis() {
        if (analysisDisposed || !isAutoAnalysisEnabled || !isEdgesAnalysisEnabled || !HasAnalysisInput) {
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
        if (analysisDisposed || !isAutoAnalysisEnabled || !isEdgesAnalysisEnabled || !HasAnalysisInput) {
            return;
        }
        // один флаг заменяет ожидающие запуски, работающий цикл сам заберет последний вход
        analysisRunRequested = true;
        _ = ProcessAnalysisRequestsAsync();
    }

    private void RequestAnalysisComposition() {
        analysisDisplayRevision++;
        ReplaceAnalysisComposition(null, null);
        analysisCompositionRequested = robertsResult is not null && !analysisDisposed;
        if (analysisCompositionRequested) {
            _ = ProcessAnalysisRequestsAsync();
        }
    }

    private bool IsCurrentAnalysisWork(long generation) =>
        !analysisDisposed && isEdgesAnalysisEnabled && HasAnalysisInput && generation == analysisWorkGeneration;

    private async Task ProcessAnalysisRequestsAsync() {
        if (analysisWorkerRunning || analysisDisposed) {
            return;
        }
        analysisWorkerRunning = true;
        NotifyAnalysisControls();
        try {
            // один цикл забирает последнюю потребность, флаги не создают очередь отдельных Task
            // поколение защищает смену входа, версия показа отделяет ее от смены подложки и видимости
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
                        SetRobertsResult(null);
                        ReplaceAnalysisComposition(null, null);
                        AnalysisInputSnapshot input = CaptureAnalysisInput()
                            ?? throw new InvalidOperationException("Вход анализа отсутствует");
                        int expectedWidth = AnalysisInputImage!.PixelSize.Width;
                        int expectedHeight = AnalysisInputImage.PixelSize.Height;
                        AnalysisStatusMessage = "Выполняется анализ Робертса";
                        AnalysisEdgeResult next = await Task.Run(
                            () => RobertsAnalysisService.Analyze(input, cancellation.Token), cancellation.Token);
                        if (!IsCurrentAnalysisWork(generation) || next.InputRevision != AnalysisInputRevision) {
                            continue;
                        }
                        if (next.Width != expectedWidth || next.Height != expectedHeight) {
                            throw new InvalidOperationException("Размер декодированного входа не совпадает с предпросмотром");
                        }
                        SetRobertsResult(next);
                    }

                    AnalysisEdgeResult? edges = robertsResult;
                    if (edges is null || edges.InputRevision != AnalysisInputRevision) {
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
                    if (!IsCurrentAnalysisWork(generation) || !ReferenceEquals(edges, robertsResult) ||
                        displayRevision != analysisDisplayRevision) {
                        continue;
                    }

                    using MemoryStream stream = new MemoryStream(png);
                    Bitmap nextImage = new Bitmap(stream);
                    ReplaceAnalysisComposition(nextImage, png);
                    AnalysisStatusMessage = $"Готово: Робертс · {edges.Width} × {edges.Height}";
                }
                catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
                    // отмененный вход не публикует картинку и не заменяет сообщение нового состояния
                }
                catch (Exception exception) {
                    if (IsCurrentAnalysisWork(generation)) {
                        if (calculate) {
                            SetRobertsResult(null);
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

    private void SetRobertsResult(AnalysisEdgeResult? result) {
        robertsResult = result;
        OnPropertyChanged(nameof(RobertsResult));
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
