using System.IO;
using Avalonia.Media.Imaging;
using ImageVisionApp.Models;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    public MediaDocument? CurrentDocument { get; private set; }
    public bool IsVideoOpen => CurrentDocument?.Kind is MediaKind.Video;
    public Bitmap? VideoFirstFrame { get; private set; }

    // готовим bitmap первого кадра и публикуем видео без входных данных фото
    public void LoadVideoFirstFrame(byte[] framePng, string fileName, string sourcePath) {
        using MemoryStream stream = new(framePng);
        Bitmap firstFrame = new(stream);
        MediaDocument document = new(MediaKind.Video, fileName, sourcePath);
        CommitDocument(document, null, null, null, null, null, firstFrame);
        AttachVideoPlayback();
    }

    // заменяем общий документ только после подготовки всех его данных и сбрасываем состояние прежнего входа
    private void CommitDocument(
        MediaDocument document,
        byte[]? imageData,
        ImageInfo? imageInfo,
        ImageHistogramComparison? comparison,
        Bitmap? original,
        Bitmap? modified,
        Bitmap? videoFirstFrame) {
        Bitmap? previousOriginal = OriginalImage;
        Bitmap? previousModified = ModifiedImage;
        Bitmap? previousVideoFirstFrame = VideoFirstFrame;

        ReleaseVideoPlayback();
        CurrentDocument = document;
        originalImageData = imageData;
        modifiedImageData = imageData;
        originalHistogram = comparison?.OriginalHistogram;
        VideoFirstFrame = videoFirstFrame;
        AdvancedSteps.Clear();
        OriginalImage = original;
        ModifiedImage = modified;
        CurrentImageInfo = imageInfo;
        HistogramComparison = comparison;
        StatusMessage = document.FileName;
        ResetAnalysisInputAfterLoad();

        OnPropertyChanged(nameof(CurrentDocument));
        OnPropertyChanged(nameof(IsVideoOpen));
        OnPropertyChanged(nameof(VideoFirstFrame));
        OnPropertyChanged(nameof(VideoPreviewFrame));

        previousOriginal?.Dispose();
        previousModified?.Dispose();
        previousVideoFirstFrame?.Dispose();
    }

    // освобождаем изображения открытого документа после закрытия окна
    public void DisposeMediaPreviews() {
        mediaPreviewsDisposed = true;
        ReleaseVideoPlayback();
        Bitmap? original = OriginalImage;
        Bitmap? modified = ModifiedImage;
        Bitmap? video = VideoFirstFrame;
        OriginalImage = null;
        ModifiedImage = null;
        VideoFirstFrame = null;
        OnPropertyChanged(nameof(VideoFirstFrame));
        OnPropertyChanged(nameof(VideoPreviewFrame));
        original?.Dispose();
        modified?.Dispose();
        video?.Dispose();
    }
}
