using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private bool isPlateAnalysisEnabled;
    private bool showAnalysisPlate = true;
    private long plateWorkRevision;
    private AnalysisPlateResult? plateResult;
    private string plateFailure = string.Empty;

    public bool IsPlateAnalysisEnabled {
        get => isPlateAnalysisEnabled;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (!SetProperty(ref isPlateAnalysisEnabled, value)) {
                return;
            }
            if (value && !showAnalysisPlate) {
                showAnalysisPlate = true;
                OnPropertyChanged(nameof(ShowAnalysisPlate));
            }
            if (value && !isEdgesAnalysisEnabled && !isSegmentationAnalysisEnabled &&
                !isKeypointAnalysisEnabled && IsAnalysisBlackBackground) {
                SelectedAnalysisBackground = AnalysisResultBackground.Input;
            }
            InvalidatePlateAnalysis();
        }
    }

    public bool ShowAnalysisPlate {
        get => showAnalysisPlate;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (SetProperty(ref showAnalysisPlate, value)) {
                RequestAnalysisComposition();
            }
        }
    }

    public AnalysisPlateResult? PlateResult => plateResult;

    private void InvalidatePlateAnalysis() {
        if (analysisDisposed) {
            return;
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
        !analysisDisposed && isPlateAnalysisEnabled && revision == plateWorkRevision && inputRevision == AnalysisInputRevision;

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
    }
}
