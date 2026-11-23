using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private bool videoBackgroundReplacementEnabled;
    private string? videoBackgroundImageName;
    private string? videoBackgroundSelectionNotice;
    private long videoBackgroundSelectionRevision;

    public string? VideoBackgroundImageName => videoBackgroundImageName;
    public bool HasVideoBackgroundImage => videoSequenceComposition.ReplacementImage is not null;
    public string? VideoBackgroundSelectionNotice {
        get => videoBackgroundSelectionNotice;
        private set => SetProperty(ref videoBackgroundSelectionNotice, value);
    }

    public bool CanUseVideoBackgroundReplacement => IsVideoOpen && VideoBackgroundReplacementEnabled && HasVideoBackgroundImage;
    // выбор картинки делает замену доступной как общую подложку
    public bool VideoBackgroundReplacementEnabled {
        get => videoBackgroundReplacementEnabled;
        set {
            if (!IsVideoOpen || value && !HasVideoBackgroundImage || value == videoBackgroundReplacementEnabled) {
                return;
            }
            VideoSequenceBackdrop backdrop = value ? VideoSequenceBackdrop.Replacement :
                videoSequenceComposition.Backdrop == VideoSequenceBackdrop.Replacement
                    ? VideoSequenceBackdrop.Original : videoSequenceComposition.Backdrop;
            if (TrySetVideoSequenceComposition(videoSequenceComposition with { Backdrop = backdrop })) {
                videoBackgroundReplacementEnabled = value;
                NotifyVideoBackgroundState();
            }
            else {
                OnPropertyChanged(nameof(VideoBackgroundReplacementEnabled));
            }
        }
    }

    // готовим снимок файла и проверяем бюджет до замены активного фона
    internal async Task ChooseVideoBackgroundAsync(IStorageFile file) {
        if (mediaPreviewsDisposed || CurrentDocument is not { Kind: MediaKind.Video } document) {
            return;
        }

        long revision = ++videoBackgroundSelectionRevision;
        string name = file.Name;
        try {
            VideoBackgroundSelectionNotice = "Загружается изображение фона…";
            PixelSize source = VideoFirstFrame?.PixelSize ??
                throw new InvalidOperationException("Видеокадр ещё не подготовлен");
            PixelSize output = CaptureVideoForegroundInput(document, PreviewSettings,
                videoSequenceComposition.ReplacementSource).GetOutputSize(source);
            long budget = PreviewSettings.ForegroundBudgetBytes;
            long decoderBytes = EstimateVideoBackgroundReaderBytes(source);
            await using Stream stream = await file.OpenReadAsync();
            var loaded = await Task.Run(() => {
                VideoBackgroundImage image;
                using (Bitmap bitmap = new(stream)) {
                    long captureBytes = checked(VideoEditorFrame.GetBufferBytes(bitmap.PixelSize) +
                        VideoBackgroundImage.GetCaptureBufferBytes(bitmap.PixelSize));
                    if (captureBytes > budget) {
                        throw new InvalidOperationException("Недостаточно бюджета буферов для загрузки фоновой картинки");
                    }
                    image = VideoBackgroundImage.Capture(bitmap);
                }
                ValidateVideoBackgroundBudget(image, output, decoderBytes, budget);
                return image;
            });
            if (!IsCurrentSelection()) {
                return;
            }
            PixelSize currentOutput = CaptureVideoForegroundInput(document, PreviewSettings,
                videoSequenceComposition.ReplacementSource).GetOutputSize(source);
            ValidateVideoBackgroundBudget(loaded, currentOutput, decoderBytes, PreviewSettings.ForegroundBudgetBytes);
            if (!TrySetVideoSequenceComposition(videoSequenceComposition with {
                Backdrop = VideoSequenceBackdrop.Replacement, ReplacementImage = loaded
            })) {
                VideoBackgroundSelectionNotice = VideoForegroundStatus;
                return;
            }
            videoBackgroundImageName = name;
            videoBackgroundReplacementEnabled = true;
            VideoBackgroundSelectionNotice = null;
            NotifyVideoBackgroundState();
        }
        catch (Exception failure) {
            if (IsCurrentSelection()) {
                VideoBackgroundSelectionNotice = $"Не удалось выбрать фон «{name}». {failure.Message}";
            }
        }

        bool IsCurrentSelection() => !mediaPreviewsDisposed && revision == videoBackgroundSelectionRevision &&
            ReferenceEquals(document, CurrentDocument);
    }

    // пока декодер не открыт, оцениваем резерв по размеру дорожки и её предпросмотра
    private long EstimateVideoBackgroundReaderBytes(PixelSize preview) {
        long previewPixels = (long)preview.Width * preview.Height;
        long sourcePixels = CurrentVideoInfo is { Width: { } width, Height: { } height }
            ? (long)width * height : previewPixels;
        return checked(sourcePixels * 3 + (sourcePixels == previewPixels ? 0 : previewPixels * 3) +
            VideoEditorFrame.GetBufferBytes(preview) + preview.Width * 3L);
    }

    // неизменяемый снимок и его вписанная копия считаются один раз на проход
    private static long GetVideoBackgroundBytes(VideoBackgroundImage image, PixelSize output) =>
        checked(image.BufferBytes + (long)output.Width * output.Height * 3);

    private static void ValidateVideoBackgroundBudget(VideoBackgroundImage image, PixelSize output, long decoderBytes, long budget) {
        long needed = checked(VideoForegroundModel.GetRequiredBufferBytes(output, decoderBytes) + GetVideoBackgroundBytes(image, output));
        if (needed > budget) {
            throw new InvalidOperationException($"Для замены фона нужно от {Math.Ceiling(needed / (1024d * 1024)):0} МиБ буферов видеоряда");
        }
    }

    private void NotifyVideoBackgroundState() {
        OnPropertyChanged(nameof(VideoBackgroundReplacementEnabled));
        OnPropertyChanged(nameof(HasVideoBackgroundImage));
        OnPropertyChanged(nameof(VideoBackgroundImageName));
        NotifyVideoSequenceEffectAvailability();
    }

    // доступность представлений зависит от включённых методов и открытого документа
    private void NotifyVideoSequenceEffectAvailability() {
        OnPropertyChanged(nameof(CanUseVideoBackgroundReplacement));
        OnPropertyChanged(nameof(CanUseVideoMovingBlur));
        OnPropertyChanged(nameof(CanUseVideoOpticalFlow));
        OnPropertyChanged(nameof(CanUseVideoSubtractionBackdrop));
        OnPropertyChanged(nameof(CanApplyVideoForeground));
    }

}
