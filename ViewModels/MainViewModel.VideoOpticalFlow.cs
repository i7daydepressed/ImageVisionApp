namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    public bool CanUseVideoOpticalFlow => IsVideoOpen && VideoOpticalFlowEnabled;
    private bool ShowsVideoOpticalFlow => CanUseVideoOpticalFlow;

    // стрелки дополняют текущую подложку и другие включенные слои
    public bool VideoOpticalFlowEnabled {
        get => videoSequenceComposition.ShowFlow;
        set {
            if (IsVideoOpen) {
                TrySetVideoSequenceComposition(videoSequenceComposition with { ShowFlow = value }, nameof(VideoOpticalFlowEnabled));
                OnPropertyChanged(nameof(VideoOpticalFlowEnabled));
            }
        }
    }
}
