using System;
using Avalonia.Threading;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private const int DefaultMovingBlurRadius = 4;
    private int videoMovingBlurRadius = DefaultMovingBlurRadius;
    private DispatcherTimer? videoMovingBlurDebounce;

    public bool CanUseVideoMovingBlur => IsVideoOpen && VideoMovingBlurEnabled;
    private bool ShowsVideoMovingBlur => CanUseVideoMovingBlur;

    // размытие меняет выбранную подложку независимо от включения стрелок
    public bool VideoMovingBlurEnabled {
        get => videoSequenceComposition.BlurRadius.HasValue;
        set {
            if (IsVideoOpen) {
                TrySetVideoSequenceComposition(videoSequenceComposition with { BlurRadius = value ? VideoMovingBlurRadius : null },
                    nameof(VideoMovingBlurEnabled));
                OnPropertyChanged(nameof(VideoMovingBlurEnabled));
            }
        }
    }

    // несколько событий ползунка меняют одну версию прохода после остановки указателя
    public int VideoMovingBlurRadius {
        get => videoMovingBlurRadius;
        set {
            if (!SetProperty(ref videoMovingBlurRadius, Math.Clamp(value, 1, 12)) || !ShowsVideoMovingBlur) {
                return;
            }

            TrySetVideoSequenceComposition(videoSequenceComposition with { BlurRadius = VideoMovingBlurRadius }, refresh: false);
            videoMovingBlurDebounce ??= new DispatcherTimer(TimeSpan.FromMilliseconds(200), DispatcherPriority.Background,
                (_, _) => {
                    videoMovingBlurDebounce!.Stop();
                    if (!mediaPreviewsDisposed && ShowsVideoMovingBlur) {
                        RestartVideoForeground();
                    }
                });
            videoMovingBlurDebounce.Stop();
            videoMovingBlurDebounce.Start();
        }
    }
}
