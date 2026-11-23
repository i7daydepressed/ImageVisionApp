namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private const int OpticalFlowView = 4;
    private bool videoOpticalFlowEnabled;
    private int previousOpticalFlowView;

    public bool CanUseVideoOpticalFlow => IsVideoOpen && VideoOpticalFlowEnabled;
    private bool ShowsVideoOpticalFlow => CanUseVideoOpticalFlow && VideoForegroundViewIndex == OpticalFlowView;
    private int? VideoForegroundRasterView => ShowsVideoOpticalFlow ? OpticalFlowView : ShowsVideoMovingBlur ? MovingBlurView : null;

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
