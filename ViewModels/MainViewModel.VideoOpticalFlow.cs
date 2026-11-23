using ImageVisionApp.Models;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private const int OpticalFlowView = 4;
    private bool videoOpticalFlowEnabled;
    private int previousOpticalFlowView;
    private int videoOpticalFlowBackdropIndex;

    public bool CanUseVideoOpticalFlow => IsVideoOpen && VideoOpticalFlowEnabled;
    private bool ShowsVideoOpticalFlow => CanUseVideoOpticalFlow && VideoForegroundViewIndex == OpticalFlowView;
    private int? VideoForegroundRasterView => ShowsVideoOpticalFlow ? OpticalFlowView : ShowsVideoMovingBlur ? MovingBlurView : null;

    private VideoPipelineStage ConfiguredVideoOpticalFlowBackdrop => GetVideoOpticalFlowBackdrop(videoOpticalFlowBackdropIndex);
    private VideoPipelineStage ConfiguredVideoSequenceInputs => VideoForegroundSource | ConfiguredVideoOpticalFlowBackdrop;
    public bool CanUseVideoSequenceAsAnalysisSource => (ConfiguredVideoSequenceInputs & VideoPipelineStage.Analysis) == 0;
    private VideoPipelineStage VideoOpticalFlowBackdrop => ShowsVideoOpticalFlow && ConfiguredVideoOpticalFlowBackdrop != VideoForegroundSource
        ? ConfiguredVideoOpticalFlowBackdrop : VideoPipelineStage.None;
    private VideoPipelineStage VideoSequenceInputs => VideoForegroundSource | VideoOpticalFlowBackdrop;
    private VideoPipelineStage VideoSequenceOutputGeometrySource => VideoOpticalFlowBackdrop == VideoPipelineStage.None
        ? VideoForegroundSource : VideoOpticalFlowBackdrop;

    // источник расчета остается прежним при выборе другого растра под стрелками
    public int SelectedVideoOpticalFlowBackdropIndex {
        get => videoOpticalFlowBackdropIndex;
        set {
            if (!IsVideoOpen || value is < 0 or > 3 || value == videoOpticalFlowBackdropIndex) {
                return;
            }
            if (!TryValidateVideoSequenceSources(VideoForegroundSource | GetVideoOpticalFlowBackdrop(value), out string? error)) {
                SetVideoForegroundStatus(error!);
                OnPropertyChanged(nameof(SelectedVideoOpticalFlowBackdropIndex));
                return;
            }
            VideoPipelineStage previous = VideoOpticalFlowBackdrop;
            SetProperty(ref videoOpticalFlowBackdropIndex, value);
            OnPropertyChanged(nameof(CanUseVideoSequenceAsAnalysisSource));
            if (previous != VideoOpticalFlowBackdrop) {
                RestartVideoForeground();
            }
        }
    }

    // первый пункт использует растр, с которого рассчитаны смещения
    private static VideoPipelineStage GetVideoOpticalFlowBackdrop(int index) => index switch {
        1 => VideoPipelineStage.Original,
        2 => VideoPipelineStage.Editing,
        3 => VideoPipelineStage.Analysis,
        _ => VideoPipelineStage.None
    };

    // при выборе потока новый проход собирает соседние кадры выбранного входа
    public bool VideoOpticalFlowEnabled {
        get => videoOpticalFlowEnabled;
        set {
            if (!IsVideoOpen || value == videoOpticalFlowEnabled) {
                return;
            }

            bool wasActive = HasActiveVideoSequenceEffect;
            int? previousRasterView = VideoForegroundRasterView;
            videoOpticalFlowEnabled = value;
            if (value) {
                previousOpticalFlowView = VideoForegroundViewIndex;
                SelectVideoForegroundView(OpticalFlowView);
            }
            else if (VideoForegroundViewIndex == OpticalFlowView) {
                SelectVideoForegroundView(CanSelectVideoForegroundView(previousOpticalFlowView)
                    ? previousOpticalFlowView : FallbackVideoForegroundView());
            }
            OnPropertyChanged(nameof(VideoOpticalFlowEnabled));
            NotifyVideoSequenceEffectAvailability();
            RefreshSelectedVideoForegroundView(wasActive, previousRasterView != VideoForegroundRasterView);
        }
    }
}
