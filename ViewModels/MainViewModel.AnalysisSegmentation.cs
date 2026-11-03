using System;
using System.Globalization;
using Avalonia.Threading;
using ImageVisionApp.Models;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private bool isSegmentationAnalysisEnabled;
    private string segmentationClusterCountText = "4";
    private long segmentationWorkRevision;
    private AnalysisSegmentationResult? segmentationResult;
    private string segmentationFailure = string.Empty;

    public bool IsSegmentationAnalysisEnabled {
        get => isSegmentationAnalysisEnabled;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (!SetProperty(ref isSegmentationAnalysisEnabled, value)) {
                return;
            }
            if (value) {
                SelectedAnalysisBackground = AnalysisResultBackground.Segmentation;
            }
            else if (IsAnalysisSegmentationBackground) {
                SelectedAnalysisBackground = AnalysisResultBackground.Input;
            }
            InvalidateSegmentationAnalysis();
        }
    }

    public string SegmentationClusterCountText {
        get => segmentationClusterCountText;
        set {
            Dispatcher.UIThread.VerifyAccess();
            if (!SetProperty(ref segmentationClusterCountText, value ?? string.Empty)) {
                return;
            }
            NotifySegmentationValidation();
            if (isSegmentationAnalysisEnabled) {
                InvalidateSegmentationAnalysis();
            }
        }
    }

    public string SegmentationValidationError {
        get {
            if (!TryGetSegmentationClusterCount(out int count)) {
                return "Введите целое N от 2 до 20";
            }
            if (HasAnalysisInput) {
                long pixels = (long)AnalysisInputImage!.PixelSize.Width * AnalysisInputImage.PixelSize.Height;
                if (pixels < count) {
                    return $"Число пикселей входа: {pixels}; для N={count} их недостаточно";
                }
            }
            return string.Empty;
        }
    }

    public bool HasSegmentationValidationError => SegmentationValidationError.Length != 0;
    public AnalysisSegmentationResult? SegmentationResult => segmentationResult;

    private bool TryGetSegmentationClusterCount(out int count) =>
        int.TryParse(segmentationClusterCountText.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out count) &&
        count >= 2 && count <= 20;

    private void NotifySegmentationValidation() {
        OnPropertyChanged(nameof(SegmentationValidationError));
        OnPropertyChanged(nameof(HasSegmentationValidationError));
    }

    private void InvalidateSegmentationAnalysis() {
        if (analysisDisposed) {
            return;
        }
        segmentationWorkRevision++;
        segmentationFailure = string.Empty;
        analysisDebounce?.Stop();
        analysisRunRequested = false;
        analysisForceRunRequested = false;
        if (activeAnalysisMethod == AnalysisWorkerMethod.Segmentation) {
            activeAnalysisCancellation?.Cancel();
        }
        SetSegmentationResult(null);
        RequestAnalysisComposition();
        RefreshAnalysisStatus();
        ScheduleAutoAnalysis();
    }

    private bool IsCurrentSegmentationWork(long revision, long inputRevision) =>
        !analysisDisposed && isSegmentationAnalysisEnabled && !HasSegmentationValidationError &&
        revision == segmentationWorkRevision && inputRevision == AnalysisInputRevision;

    private void SetSegmentationResult(AnalysisSegmentationResult? result) {
        segmentationResult = result;
        OnPropertyChanged(nameof(SegmentationResult));
    }
}
