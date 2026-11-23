using System;
using Avalonia.Threading;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private const int MovingBlurView = 3;
    private const int DefaultMovingBlurRadius = 4;
    private bool videoMovingBlurEnabled;
    private int videoMovingBlurRadius = DefaultMovingBlurRadius;
    private int previousMovingBlurView;
    private DispatcherTimer? videoMovingBlurDebounce;

    public bool CanUseVideoMovingBlur => IsVideoOpen && VideoMovingBlurEnabled;
    private bool ShowsVideoMovingBlur => CanUseVideoMovingBlur && VideoForegroundViewIndex == MovingBlurView;

    // метод использует ту же маску MOG2 и сам выбирает свой вид результата
    public bool VideoMovingBlurEnabled {
        get => videoMovingBlurEnabled;
        set {
            if (!IsVideoOpen || value == videoMovingBlurEnabled) {
                return;
            }

            bool wasActive = HasActiveVideoSequenceEffect;
            int? previousRasterView = VideoForegroundRasterView;
            videoMovingBlurEnabled = value;
            if (value) {
                previousMovingBlurView = VideoForegroundViewIndex;
                SelectVideoForegroundView(MovingBlurView);
            }
            else if (VideoForegroundViewIndex == MovingBlurView) {
                SelectVideoForegroundView(
                    CanSelectVideoForegroundView(previousMovingBlurView) ? previousMovingBlurView :
                    FallbackVideoForegroundView());
            }
            OnPropertyChanged(nameof(VideoMovingBlurEnabled));
            NotifyVideoSequenceEffectAvailability();
            RefreshSelectedVideoForegroundView(wasActive, previousRasterView != VideoForegroundRasterView);
        }
    }

    // несколько событий ползунка меняют одну версию прохода после остановки указателя
    public int VideoMovingBlurRadius {
        get => videoMovingBlurRadius;
        set {
            if (!SetProperty(ref videoMovingBlurRadius, Math.Clamp(value, 1, 12)) || !ShowsVideoMovingBlur) {
                return;
            }

            videoMovingBlurDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
                (_, _) => {
                    videoMovingBlurDebounce!.Stop();
                    if (!mediaPreviewsDisposed && ShowsVideoMovingBlur &&
                        (videoForegroundVisible || IsCombinedAnalysisOutputSelected)) {
                        RestartVideoForeground();
                    }
                });
            videoMovingBlurDebounce.Stop();
            videoMovingBlurDebounce.Start();
        }
    }
}
