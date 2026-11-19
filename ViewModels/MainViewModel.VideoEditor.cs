using System;
using System.Linq;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private VideoEditorPlan videoEditorPlan = new(0, []);
    private long videoEditorSource;
    private long videoEditorFrame;
    private long videoEditorOutputFrame;
    private bool videoEditorWaitingForFrame;
    private bool videoEditorVisible;
    private bool videoEditorOutputVisible;
    private bool videoEditorFrameDeferred;
    private VideoPreviewExecutor? videoPreviewExecutor;
    private WriteableBitmap? videoEditorOutput;
    private VideoEditorGeometry videoEditorOutputGeometry;
    private string? videoEditorError;
    private string? videoEditorLatestFailure;

    public Bitmap? VideoEditorPreviewFrame => videoEditorVisible && !videoEditorWaitingForFrame
        ? videoEditorPlan.HasSteps ? videoEditorOutputVisible ? videoEditorOutput : null : VideoPreviewFrame
        : null;

    public int VideoEditorPreviewRevision { get; private set; }
    internal VideoPreviewSettings PreviewSettings { get; private set; } = VideoPreviewSettings.Default;

    internal VideoEditorGeometry? CurrentVideoEditorGeometry => VideoEditorPreviewFrame is { } frame
        ? videoEditorPlan.HasSteps ? videoEditorOutputGeometry : new VideoEditorGeometry(frame.PixelSize, 0)
        : null;

    public string? VideoEditorNotice => videoEditorError ?? (videoEditorWaitingForFrame
        ? "Ожидание видеокадра…"
        : videoEditorPlan.HasSteps && !videoEditorOutputVisible ? "Подготовка результата…" : null);

    // останавливаем запросы вне редактора, при возврате берем текущий подтвержденный кадр
    internal void SetVideoEditorVisible(bool visible) {
        if (videoEditorVisible == visible) {
            return;
        }

        videoEditorVisible = visible;
        if (visible) {
            NotifyVideoEditorPreview();
            RequestVideoEditorFrame();
        }
        else {
            videoEditorSource++;
            InvalidateVideoEditorRequests();
            videoEditorOutputVisible = false;
            NotifyVideoEditorPreview();
        }
    }

    // сохраняем порядок, числовые значения и неизменяемые матрицы без ссылок на редактируемые шаги
    private void CaptureVideoEditorPlan() {
        VideoEditorStep[] steps = AdvancedSteps
            .Select(step => new VideoEditorStep(step.Type, step.Parameter, step.ElementSnapshot, step.KernelSnapshot))
            .ToArray();
        videoEditorPlan = new(videoEditorPlan.Revision + 1, steps);
        InvalidateVideoEditorRequests();
        videoEditorOutputVisible = false;
        if (!videoEditorWaitingForFrame) {
            videoEditorError = null;
        }
        NotifyVideoEditorPreview();
        RequestVideoEditorFrame();
    }

    // снимаем старый результат в момент запроса перехода, пока новый кадр не подтвержден
    private void InvalidateVideoEditorSource() {
        videoEditorSource++;
        videoEditorWaitingForFrame = true;
        videoEditorOutputVisible = false;
        videoEditorError = null;
        InvalidateVideoEditorRequests();
        NotifyVideoEditorPreview();
    }

    // связываем очередь с исходным кадром, который только что принял путь предпросмотра
    private void AcceptVideoEditorFrame() {
        videoEditorFrame++;
        videoEditorLatestFailure = null;
        bool updatePreview = videoEditorWaitingForFrame || !videoEditorPlan.HasSteps;
        videoEditorWaitingForFrame = false;
        if (videoEditorVisible && IsVideoOpen) {
            if (updatePreview) {
                videoEditorError = null;
                NotifyVideoEditorPreview();
            }
            RequestVideoEditorFrame();
        }
    }

    // на паузе оставляем видимым только результат последнего принятого кадра
    private void KeepVideoEditorResultForPause() {
        if (videoEditorLatestFailure is { } failure) {
            ShowVideoEditorError(failure);
        }
        else if (videoEditorOutputVisible && videoEditorOutputFrame != videoEditorFrame) {
            videoEditorOutputVisible = false;
            NotifyVideoEditorPreview();
        }
    }

    // передаем собственный снимок только после резерва, при перегрузке держим номер актуального кадра
    private void RequestVideoEditorFrame() {
        if (!videoEditorVisible || videoEditorWaitingForFrame || !videoEditorPlan.HasSteps ||
            CurrentDocument is not { Kind: MediaKind.Video } document || VideoPreviewFrame is not { } bitmap) {
            videoEditorFrameDeferred = false;
            return;
        }

        VideoEditorPlan plan = videoEditorPlan;
        VideoPreviewFrameKey key = new(document, videoEditorSource, plan.Revision, videoEditorFrame);
        VideoPreviewExecutor executor = videoPreviewExecutor ??= new(PreviewSettings,
            action => Dispatcher.UIThread.Post(action, DispatcherPriority.Background),
            DrainVideoEditorResult, RetryDeferredVideoEditorFrame);
        VideoPreviewAdmission admission = executor.TrySubmit(key, bitmap.PixelSize, plan.GetScratchBytes(bitmap.PixelSize),
            () => VideoEditorFrame.Capture(bitmap), plan.Apply);
        videoEditorFrameDeferred = admission == VideoPreviewAdmission.Deferred;
        if (admission == VideoPreviewAdmission.TooLarge) {
            HandleVideoEditorFrameFailure(key, "Бюджета буферов предпросмотра недостаточно для обработки кадра");
        }
        else if (admission == VideoPreviewAdmission.CaptureFailed) {
            HandleVideoEditorFrameFailure(key, "Не удалось скопировать видеокадр для обработки");
        }
    }

    // получаем результат общей очереди и выводим только подходящий кадр или актуальную ошибку
    private void DrainVideoEditorResult(VideoPreviewFrameKey key, VideoEditorFrame? pixels,
        VideoEditorGeometry geometry, Exception? error) {
        if (!IsCurrentVideoEditorRequest(key)) {
            return;
        }
        if (error is not null) {
            HandleVideoEditorFrameFailure(key, "Не удалось обработать видеокадр");
            return;
        }

        try {
            if (pixels is not null) {
                PublishVideoEditorOutput(pixels, geometry, key.Frame);
            }
        }
        catch (Exception) {
            HandleVideoEditorFrameFailure(key, "Не удалось показать обработанный видеокадр");
        }
    }

    // сохраняем единичный сбой текущего кадра для паузы, во время показа оставляем удачный результат
    private void HandleVideoEditorFrameFailure(VideoPreviewFrameKey key, string message) {
        if (key.Frame == videoEditorFrame) {
            videoEditorLatestFailure = message;
        }
        if (!videoWantsToPlay || !videoEditorOutputVisible) {
            ShowVideoEditorError(message);
        }
        else if (key.Frame == videoEditorFrame && videoEditorError != message) {
            videoEditorError = message;
            NotifyVideoEditorPreview();
        }
    }

    // в живом потоке допускаем пропуски кадров, на паузе нужен последний подтвержденный
    private bool IsCurrentVideoEditorRequest(VideoPreviewFrameKey key) =>
        videoEditorVisible && !videoEditorWaitingForFrame &&
        ReferenceEquals(CurrentDocument, key.Document) &&
        videoEditorPlan.Revision == key.Plan && videoEditorSource == key.Source &&
        (videoWantsToPlay || videoEditorFrame == key.Frame);

    // на паузе забираем актуальный кадр после освобождения памяти, даже если VLC больше не обновляет его
    private void RetryDeferredVideoEditorFrame() {
        if (videoEditorFrameDeferred && !videoWantsToPlay) {
            RequestVideoEditorFrame();
        }
    }

    // меняем настройки окна и пересылаем текущий кадр, если его результат еще не показан
    internal void ApplyVideoPreviewSettings(VideoPreviewSettings settings) {
        if (settings == PreviewSettings) {
            return;
        }
        PreviewSettings = settings;
        videoPreviewExecutor?.Configure(settings);
        if (!videoEditorOutputVisible || videoEditorOutputFrame != videoEditorFrame) {
            RequestVideoEditorFrame();
        }
    }

    // переносим строки в bitmap и публикуем геометрию того же результата
    private void PublishVideoEditorOutput(VideoEditorFrame pixels, VideoEditorGeometry geometry, long frame) {
        WriteableBitmap? previous = videoEditorOutput;
        WriteableBitmap next = previous?.PixelSize == pixels.Size
            ? previous
            : new(pixels.Size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
        try {
            using ILockedFramebuffer target = next.Lock();
            pixels.CopyTo(target);
        }
        catch {
            if (!ReferenceEquals(previous, next)) {
                next.Dispose();
            }
            throw;
        }

        videoEditorOutput = next;
        videoEditorOutputGeometry = geometry;
        videoEditorOutputFrame = frame;
        videoEditorOutputVisible = true;
        if (frame == videoEditorFrame) {
            videoEditorLatestFailure = null;
        }
        videoEditorError = videoEditorLatestFailure;
        try {
            NotifyVideoEditorPreview();
        }
        finally {
            if (!ReferenceEquals(previous, next)) {
                previous?.Dispose();
            }
        }
    }

    // сообщаем об ошибке редактора отдельно от состояния исходного плеера
    private void ShowVideoEditorError(string message) {
        if (videoEditorError == message && !videoEditorOutputVisible) {
            return;
        }
        videoEditorOutputVisible = false;
        videoEditorError = message;
        NotifyVideoEditorPreview();
    }

    // снимаем ожидающий повтор и запрещаем выдачу кадров прошлого документа, источника или плана
    private void InvalidateVideoEditorRequests() {
        videoEditorFrameDeferred = false;
        videoEditorLatestFailure = null;
        videoPreviewExecutor?.Invalidate();
    }

    // обновляем источник, статус и отрисовку переиспользуемого изображения
    private void NotifyVideoEditorPreview() {
        VideoEditorPreviewRevision++;
        OnPropertyChanged(nameof(VideoEditorPreviewFrame));
        OnPropertyChanged(nameof(CurrentVideoEditorGeometry));
        OnPropertyChanged(nameof(VideoEditorNotice));
        OnPropertyChanged(nameof(VideoEditorPreviewRevision));
    }

    // освобождаем результат старого документа и возвращаем пустой план
    private void ResetVideoEditorDocument() {
        InvalidateVideoEditorRequests();
        videoEditorPlan = new(videoEditorPlan.Revision + 1, []);
        videoEditorWaitingForFrame = false;
        videoEditorOutputVisible = false;
        videoEditorError = null;
        WriteableBitmap? previous = videoEditorOutput;
        videoEditorOutput = null;
        videoEditorOutputGeometry = default;
        NotifyVideoEditorPreview();
        previous?.Dispose();
    }
}
