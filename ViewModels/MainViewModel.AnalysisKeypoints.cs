using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private bool isKeypointAnalysisEnabled;
    private bool showAnalysisKeypoints = true;
    private string fastThresholdText = "20";
    private bool fastNonmaxSuppression = true;
    private long keypointsWorkRevision;
    private AnalysisKeypointResult? keypointsResult;
    private string keypointsFailure = string.Empty;

    public bool IsKeypointAnalysisEnabled {
        get => isKeypointAnalysisEnabled;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (!SetProperty(ref isKeypointAnalysisEnabled, value)) {
                return;
            }
            if (value && !showAnalysisKeypoints) {
                showAnalysisKeypoints = true;
                OnPropertyChanged(nameof(ShowAnalysisKeypoints));
            }
            if (value && !isEdgesAnalysisEnabled && !isSegmentationAnalysisEnabled &&
                !isPlateAnalysisEnabled && IsAnalysisBlackBackground) {
                SelectedAnalysisBackground = AnalysisResultBackground.Input;
            }
            InvalidateKeypointAnalysis();
        }
    }

    public bool ShowAnalysisKeypoints {
        get => showAnalysisKeypoints;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (SetProperty(ref showAnalysisKeypoints, value)) {
                RequestAnalysisComposition();
            }
        }
    }

    public string FastThresholdText {
        get => fastThresholdText;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (SetProperty(ref fastThresholdText, value ?? string.Empty)) {
                OnPropertyChanged(nameof(FastThresholdError));
                OnPropertyChanged(nameof(HasFastThresholdError));
                if (isKeypointAnalysisEnabled) {
                    InvalidateKeypointAnalysis();
                }
            }
        }
    }

    public bool FastNonmaxSuppression {
        get => fastNonmaxSuppression;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (SetProperty(ref fastNonmaxSuppression, value) && isKeypointAnalysisEnabled) {
                InvalidateKeypointAnalysis();
            }
        }
    }

    public string FastThresholdError =>
        TryGetFastThreshold(out _) ? string.Empty : "Введите целый порог FAST от 1 до 255";
    public bool HasFastThresholdError => FastThresholdError.Length != 0;
    public AnalysisKeypointResult? KeypointsResult => keypointsResult;

    private bool TryGetFastThreshold(out int threshold) =>
        int.TryParse(fastThresholdText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out threshold) &&
        FastAnalysisService.IsThresholdValid(threshold);

    private void InvalidateKeypointAnalysis() {
        if (analysisDisposed) {
            return;
        }
        keypointsWorkRevision++;
        keypointsFailure = string.Empty;
        analysisDebounce?.Stop();
        analysisRunRequested = false;
        analysisForceRunRequested = false;
        if (activeAnalysisMethod == AnalysisWorkerMethod.Keypoints) {
            activeAnalysisCancellation?.Cancel();
        }
        SetKeypointsResult(null);
        RequestAnalysisComposition();
        RefreshAnalysisStatus();
        ScheduleAutoAnalysis();
    }

    private bool IsCurrentKeypointWork(long revision, long inputRevision) =>
        !analysisDisposed && isKeypointAnalysisEnabled && !HasFastThresholdError &&
        revision == keypointsWorkRevision && inputRevision == AnalysisInputRevision;

    private async Task CalculateKeypointsAsync(AnalysisInputSnapshot input, int width, int height,
        long revision, int threshold, bool nonmaxSuppression) {

        using CancellationTokenSource cancellation = new CancellationTokenSource();
        activeAnalysisCancellation = cancellation;
        activeAnalysisMethod = AnalysisWorkerMethod.Keypoints;
        try {
            AnalysisStatusMessage = "Выполняется FAST";
            AnalysisKeypointResult next = await Task.Run(
                () => FastAnalysisService.Analyze(input, threshold, nonmaxSuppression, cancellation.Token),
                cancellation.Token);
            if (!IsCurrentKeypointWork(revision, input.InputRevision)) {
                return;
            }
            if (next.Width != width || next.Height != height || next.Source != input.Source ||
                next.InputRevision != input.InputRevision || next.Threshold != threshold ||
                next.NonmaxSuppression != nonmaxSuppression) {
                throw new InvalidOperationException("Точки FAST не соответствуют выбранному входу и параметрам");
            }
            SetKeypointsResult(next);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) {
            // отмененные точки не публикуются после смены входа или параметров
        }
        catch (Exception exception) {
            if (IsCurrentKeypointWork(revision, input.InputRevision)) {
                SetKeypointsResult(null);
                keypointsFailure = "FAST: " + exception.Message;
            }
        }
        finally {
            activeAnalysisCancellation = null;
            activeAnalysisMethod = AnalysisWorkerMethod.None;
        }
    }

    private void SetKeypointsResult(AnalysisKeypointResult? result) {
        keypointsResult = result;
        OnPropertyChanged(nameof(KeypointsResult));
    }
}
