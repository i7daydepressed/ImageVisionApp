using System.IO;
using Avalonia.Media.Imaging;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    public MediaDocument? CurrentDocument { get; private set; }
    public bool IsVideoOpen => CurrentDocument?.Kind is MediaKind.Video;
    public Bitmap? VideoFirstFrame { get; private set; }
    public VideoInfo? CurrentVideoInfo { get; private set; }
    private VideoFileIdentity? videoFileIdentity;

    // отдаем только снимок принятых байтов того же фотодокумента для фонового чтения
    internal (MediaDocument Document, byte[] Bytes)? ReadFacePhotoSource() =>
        !mediaPreviewsDisposed && CurrentDocument is { Kind: MediaKind.Image } photo && originalImageData is { } bytes
            ? (photo, bytes) : null;

    // готовим bitmap первого кадра и публикуем видео без входных данных фото
    public void LoadVideoFirstFrame(byte[] framePng, string fileName, string sourcePath, long? fileSizeBytes = null) =>
        LoadVideoFirstFrame(framePng, fileName, sourcePath, fileSizeBytes, VideoFileIdentity.TryCapture(sourcePath));

    // привязываем уже прочитанный первый кадр к той же версии файла перед заменой документа
    internal void LoadVideoFirstFrame(byte[] framePng, string fileName, string sourcePath, long? fileSizeBytes,
        VideoFileIdentity? identity) {
        identity?.EnsureCurrent(sourcePath);
        using MemoryStream stream = new(framePng);
        Bitmap firstFrame = new(stream);
        MediaDocument document = new(MediaKind.Video, fileName, sourcePath);
        CommitDocument(document, null, null, null, null, null, firstFrame, new VideoInfo { FileSizeBytes = fileSizeBytes }, identity);
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
        Bitmap? videoFirstFrame,
        VideoInfo? videoInfo = null, VideoFileIdentity? identity = null) {
        Bitmap? previousOriginal = OriginalImage;
        Bitmap? previousModified = ModifiedImage;
        Bitmap? previousVideoFirstFrame = VideoFirstFrame;

        ReleaseVideoPlayback();
        CurrentDocument = document;
        videoFileIdentity = identity;
        VideoEditorPreviewTabIndex = 1;
        CurrentVideoInfo = videoInfo;
        originalImageData = imageData;
        modifiedImageData = imageData;
        originalHistogram = comparison?.OriginalHistogram;
        VideoFirstFrame = videoFirstFrame;
        AdvancedSteps.Clear();
        OriginalImage = original;
        ModifiedImage = modified;
        CurrentImageInfo = imageInfo;
        HistogramComparison = comparison;
        StatusMessage = string.Empty;
        ResetAnalysisInputAfterLoad();
        ResetVideoEditorDocument();
        ResetPreparedVideoDocument();

        OnPropertyChanged(nameof(CurrentDocument));
        OnPropertyChanged(nameof(IsVideoOpen));
        VideoBackgroundSelectionNotice = null;
        NotifyVideoBackgroundState();
        OnPropertyChanged(nameof(CurrentVideoInfo));
        OnPropertyChanged(nameof(VideoFirstFrame));
        OnPropertyChanged(nameof(VideoPreviewFrame));
        RefreshVideoForeground();

        previousOriginal?.Dispose();
        previousModified?.Dispose();
        previousVideoFirstFrame?.Dispose();
    }

    // освобождаем изображения открытого документа после закрытия окна
    public void DisposeMediaPreviews() {
        mediaPreviewsDisposed = true;
        ReleaseVideoPlayback();
        videoPreviewExecutor?.Dispose();
        Bitmap? original = OriginalImage;
        Bitmap? modified = ModifiedImage;
        Bitmap? video = VideoFirstFrame;
        OriginalImage = null;
        ModifiedImage = null;
        VideoFirstFrame = null;
        ResetVideoEditorDocument();
        ResetPreparedVideoDocument();
        _ = preparedVideo?.DisposeAsync();
        OnPropertyChanged(nameof(VideoFirstFrame));
        OnPropertyChanged(nameof(VideoPreviewFrame));
        original?.Dispose();
        modified?.Dispose();
        video?.Dispose();
    }
}
