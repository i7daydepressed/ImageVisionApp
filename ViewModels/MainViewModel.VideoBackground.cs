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
    private const int ReplacedBackgroundView = 2;
    private bool videoBackgroundReplacementEnabled;
    private VideoBackgroundImage? videoBackgroundImage;
    private VideoBackgroundRaster? videoBackgroundRaster;
    private string? videoBackgroundImageName;
    private string? videoBackgroundSelectionNotice;
    private long videoBackgroundSelectionRevision;
    private int previousSubtractionView;

    public string? VideoBackgroundImageName => videoBackgroundImageName;
    public bool HasVideoBackgroundImage => videoBackgroundImage is not null;
    public string? VideoBackgroundSelectionNotice {
        get => videoBackgroundSelectionNotice;
        private set => SetProperty(ref videoBackgroundSelectionNotice, value);
    }

    public bool CanUseVideoBackgroundReplacement => IsVideoOpen && VideoBackgroundReplacementEnabled && HasVideoBackgroundImage;
    private bool HasActiveVideoSequenceEffect => VideoForegroundEnabled || CanUseVideoBackgroundReplacement || CanUseVideoMovingBlur || CanUseVideoOpticalFlow;
    private bool ShowsReplacedVideoBackground => CanUseVideoBackgroundReplacement && VideoForegroundViewIndex == ReplacedBackgroundView;

    // отключение замены сохраняет выбранную картинку и возвращает предыдущий вид вычитания
    public bool VideoBackgroundReplacementEnabled {
        get => videoBackgroundReplacementEnabled;
        set {
            if (!IsVideoOpen || value && !HasVideoBackgroundImage || value == videoBackgroundReplacementEnabled) {
                return;
            }

            bool wasActive = HasActiveVideoSequenceEffect;
            int? previousRasterView = VideoForegroundRasterView;
            videoBackgroundReplacementEnabled = value;
            if (value) {
                SelectVideoForegroundView(ReplacedBackgroundView);
            }
            else if (VideoForegroundViewIndex == ReplacedBackgroundView) {
                SelectVideoForegroundView(VideoForegroundEnabled ? previousSubtractionView : FallbackVideoForegroundView());
            }
            NotifyVideoBackgroundState();
            RefreshSelectedVideoForegroundView(wasActive, previousRasterView != VideoForegroundRasterView);
        }
    }

    // готовим снимок файла и вписанную копию до замены активного фона
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
            PixelSize output = videoForegroundFrame?.Size ??
                CaptureVideoForegroundInput(document, PreviewSettings, VideoForegroundSource).GetOutputSize(source);
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
                return (Image: image, Raster: image.PrepareCover(output));
            });
            if (!IsCurrentSelection()) {
                return;
            }
            ValidateVideoBackgroundBudget(loaded.Image, videoForegroundFrame?.Size ?? output,
                decoderBytes, PreviewSettings.ForegroundBudgetBytes);

            bool wasActive = HasActiveVideoSequenceEffect;
            int? previousRasterView = VideoForegroundRasterView;
            videoBackgroundImage = loaded.Image;
            videoBackgroundRaster = loaded.Raster;
            videoBackgroundImageName = name;
            videoBackgroundReplacementEnabled = true;
            SelectVideoForegroundView(ReplacedBackgroundView);
            VideoBackgroundSelectionNotice = null;
            NotifyVideoBackgroundState();
            RefreshSelectedVideoForegroundView(wasActive, previousRasterView != VideoForegroundRasterView);
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

    // поворот или другая подложка анализа могут изменить геометрию после выбора файла
    private VideoBackgroundRaster? GetReplacementBackground(PixelSize size) {
        if (!ShowsReplacedVideoBackground || videoBackgroundImage is not { } image) {
            return null;
        }
        if (videoBackgroundRaster is not { } raster || raster.Size != size) {
            videoBackgroundRaster = raster = image.PrepareCover(size);
        }
        return raster;
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
        OnPropertyChanged(nameof(CanDisplayVideoForegroundEffect));
        OnPropertyChanged(nameof(CanApplyVideoForeground));
    }

    // при выборе вида на вкладке видеоряда повторно используем готовую маску
    private void RefreshSelectedVideoForegroundView(bool wasActive, bool rasterViewChanged = false) {
        if (wasActive != HasActiveVideoSequenceEffect || IsCombinedAnalysisOutputSelected || videoForegroundFailed ||
            rasterViewChanged) {
            RestartVideoForeground();
        }
        else if (videoForegroundFrame is { } frame && videoForegroundOutput is { } output) {
            PaintVideoForeground(output, frame, result: true, maskOnly: VideoForegroundViewIndex == 1,
                GetReplacementBackground(frame.Size), motionBlur: ShowsVideoMovingBlur);
            NotifyVideoForegroundPixelsChanged();
            DescribeVideoForegroundFrame(frame);
        }
    }
}
