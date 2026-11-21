using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    internal const int MaximumPlateDisplayLimit = 100;
    private bool isPlateAnalysisEnabled;
    private bool showAnalysisPlate = true;
    private long plateWorkRevision;
    private AnalysisPlateResult? plateResult;
    private string plateFailure = string.Empty;
    private int plateDisplayModeIndex;
    private int plateDisplayLimit = 3;

    public bool IsPlateAnalysisEnabled {
        get => isPlateAnalysisEnabled;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (!SetProperty(ref isPlateAnalysisEnabled, value)) {
                return;
            }
            OnPropertyChanged(nameof(IsPlateAnalysisSelected));
            if (!IsVideoOpen && value && !showAnalysisPlate) {
                showAnalysisPlate = true;
                OnPropertyChanged(nameof(ShowAnalysisPlate));
            }
            if (!IsVideoOpen && value && !isEdgesAnalysisEnabled && !isSegmentationAnalysisEnabled &&
                !isKeypointAnalysisEnabled && IsAnalysisBlackBackground) {
                SelectedAnalysisBackground = AnalysisResultBackground.Input;
            }
            if (IsVideoOpen && plateResult is { } cached && IsCurrentAnalysisLayer(cached.VideoFrame)) {
                // галочка скрывает готовые рамки без отмены других методов или сброса замера
                RefreshPlateDisplay();
                RefreshVideoAnalysisStatus();
                return;
            }
            InvalidatePlateAnalysis();
        }
    }

    public bool IsPlateAnalysisSelected {
        get => isPlateAnalysisEnabled;
        set => IsPlateAnalysisEnabled = value;
    }

    public int PlateDisplayModeIndex {
        get => plateDisplayModeIndex;
        set {
            if (value is 0 or 1 && SetProperty(ref plateDisplayModeIndex, value)) {
                OnPropertyChanged(nameof(HasPlateDisplayLimit));
                RefreshPlateDisplay();
            }
        }
    }

    public bool HasPlateDisplayLimit => plateDisplayModeIndex == 1;

    public int PlateDisplayLimit {
        get => plateDisplayLimit;
        set {
            if (SetProperty(ref plateDisplayLimit, Math.Clamp(value, 1, MaximumPlateDisplayLimit)) && HasPlateDisplayLimit) {
                RefreshPlateDisplay();
            }
        }
    }

    private int VisiblePlateRegionLimit => HasPlateDisplayLimit ? plateDisplayLimit : int.MaxValue;

    public string? PlateDisplaySummary => plateResult is { } result
        ? $"Показано {((IsVideoOpen ? isPlateAnalysisEnabled : showAnalysisPlate) ?
            Math.Min(VisiblePlateRegionLimit, result.Rectangles.Count) : 0)} " +
          $"из {result.Rectangles.Count} · {result.Width} × {result.Height}" : null;

    // переключаем только слой готовых рамок, не меняя версию детектора
    private void RefreshPlateDisplay() {
        OnPropertyChanged(nameof(PlateDisplaySummary));
        RequestAnalysisComposition();
    }

    // оставляем прежнее сообщение для одной области и отличаем ее от успешного нуля
    private static string DescribePlateRegions(AnalysisPlateResult result) => result.Rectangles.Count switch {
        0 => "Номер не найден",
        1 => "Найден кандидат на номер",
        _ => $"Областей номера: {result.Rectangles.Count}"
    };

    public bool ShowAnalysisPlate {
        get => showAnalysisPlate;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (SetProperty(ref showAnalysisPlate, value) && !IsVideoOpen) {
                RefreshPlateDisplay();
            }
        }
    }

    public AnalysisPlateResult? PlateResult => plateResult;

    private void InvalidatePlateAnalysis() {
        if (analysisDisposed) {
            return;
        }
        if (IsVideoOpen) {
            CancelVideoAnalysisApply();
            videoAnalysisFailure = null;
        }
        plateWorkRevision++;
        plateFailure = string.Empty;
        analysisDebounce?.Stop();
        analysisRunRequested = false;
        analysisForceRunRequested = false;
        if (activeAnalysisMethod == AnalysisWorkerMethod.Plate) {
            activeAnalysisCancellation?.Cancel();
        }
        SetPlateResult(null);
        RequestAnalysisComposition();
        RefreshAnalysisStatus();
        ScheduleAutoAnalysis();
    }

    private bool IsCurrentPlateWork(long revision, long inputRevision) =>
        !analysisDisposed && !IsVideoOpen && isPlateAnalysisEnabled && revision == plateWorkRevision && inputRevision == AnalysisInputRevision;

    private async Task CalculatePlateAsync(AnalysisInputSnapshot input, int width, int height, long revision) {
        using CancellationTokenSource cancellation = new CancellationTokenSource();
        activeAnalysisCancellation = cancellation;
        activeAnalysisMethod = AnalysisWorkerMethod.Plate;
        try {
            AnalysisStatusMessage = "Выполняется поиск номера";
            AnalysisPlateResult next = await Task.Run(
                () => PlateAnalysisService.Analyze(input, cancellation.Token), cancellation.Token);
            if (!IsCurrentPlateWork(revision, input.InputRevision)) {
                return;
            }
            if (next.Width != width || next.Height != height || next.Source != input.Source ||
                next.InputRevision != input.InputRevision) {
                throw new InvalidOperationException("Область номера не соответствует выбранному входу");
            }
            SetPlateResult(next);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            // результат прежнего входа или выключенного метода не публикуется
        }
        catch (Exception exception) {
            if (IsCurrentPlateWork(revision, input.InputRevision)) {
                SetPlateResult(null);
                plateFailure = "Поиск номера: " + exception.Message;
            }
        }
        finally {
            activeAnalysisCancellation = null;
            activeAnalysisMethod = AnalysisWorkerMethod.None;
        }
    }

    private void SetPlateResult(AnalysisPlateResult? result) {
        plateResult = result;
        OnPropertyChanged(nameof(PlateResult));
        OnPropertyChanged(nameof(PlateDisplaySummary));
    }
}
