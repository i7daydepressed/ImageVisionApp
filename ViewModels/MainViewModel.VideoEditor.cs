using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private sealed record VideoEditorRequest(
        MediaDocument Document, VideoEditorPlan Plan, long Source, long Frame, VideoEditorFrame Pixels);

    private VideoEditorPlan videoEditorPlan = new(0, []);
    private long videoEditorSource;
    private long videoEditorFrame;
    private long videoEditorOutputFrame;
    private bool videoEditorWaitingForFrame;
    private bool videoEditorVisible;
    private bool videoEditorOutputVisible;
    private bool processingVideoEditor;
    private VideoEditorRequest? requestedVideoEditor;
    private WriteableBitmap? videoEditorOutput;
    private VideoEditorGeometry videoEditorOutputGeometry;
    private string? videoEditorError;

    public Bitmap? VideoEditorPreviewFrame => videoEditorVisible && !videoEditorWaitingForFrame
        ? videoEditorPlan.HasSteps ? videoEditorOutputVisible ? videoEditorOutput : null : VideoPreviewFrame
        : null;

    public int VideoEditorPreviewRevision { get; private set; }

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
            ClearVideoEditorRequest();
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
        ClearVideoEditorRequest();
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
        ClearVideoEditorRequest();
        NotifyVideoEditorPreview();
    }

    // связываем очередь с исходным кадром, который только что принял путь предпросмотра
    private void AcceptVideoEditorFrame() {
        videoEditorFrame++;
        bool updatePreview = videoEditorWaitingForFrame || !videoEditorPlan.HasSteps || videoEditorError is not null;
        videoEditorWaitingForFrame = false;
        videoEditorError = null;
        if (videoEditorVisible && IsVideoOpen) {
            if (updatePreview) {
                NotifyVideoEditorPreview();
            }
            RequestVideoEditorFrame();
        }
    }

    // на паузе оставляем видимым только результат последнего принятого кадра
    private void KeepVideoEditorResultForPause() {
        if (videoEditorOutputVisible && videoEditorOutputFrame != videoEditorFrame) {
            videoEditorOutputVisible = false;
            NotifyVideoEditorPreview();
        }
    }

    // готовим собственную копию пикселей и заменяем только ожидающий запрос
    private void RequestVideoEditorFrame() {
        if (!videoEditorVisible || videoEditorWaitingForFrame || !videoEditorPlan.HasSteps ||
            CurrentDocument is not { Kind: MediaKind.Video } document || VideoPreviewFrame is not { } bitmap) {
            return;
        }

        ClearVideoEditorRequest();
        try {
            VideoEditorFrame pixels = VideoEditorFrame.Capture(bitmap);
            requestedVideoEditor = new(document, videoEditorPlan, videoEditorSource, videoEditorFrame, pixels);
        }
        catch (Exception) {
            ShowVideoEditorError("Не удалось скопировать видеокадр для обработки");
            return;
        }

        if (!processingVideoEditor) {
            _ = ProcessVideoEditorRequestsAsync();
        }
    }

    // обрабатываем кадры по одному, берем последнюю копию и проверяем ее актуальность перед выводом
    private async Task ProcessVideoEditorRequestsAsync() {
        processingVideoEditor = true;
        try {
            while (requestedVideoEditor is { } request) {
                requestedVideoEditor = null;
                using VideoEditorFrame pixels = request.Pixels;
                try {
                    VideoEditorGeometry geometry = await Task.Run(() => request.Plan.Apply(pixels));
                    if (IsCurrentVideoEditorRequest(request)) {
                        PublishVideoEditorOutput(pixels, geometry, request.Frame);
                    }
                }
                catch (Exception) {
                    if (IsCurrentVideoEditorRequest(request)) {
                        ShowVideoEditorError("Не удалось обработать видеокадр");
                    }
                }
            }
        }
        finally {
            processingVideoEditor = false;
        }
    }

    // в живом потоке допускаем пропуски кадров, на паузе нужен последний подтвержденный
    private bool IsCurrentVideoEditorRequest(VideoEditorRequest request) =>
        videoEditorVisible && !videoEditorWaitingForFrame &&
        ReferenceEquals(CurrentDocument, request.Document) &&
        videoEditorPlan.Revision == request.Plan.Revision && videoEditorSource == request.Source &&
        (videoWantsToPlay || videoEditorFrame == request.Frame);

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
        videoEditorError = null;
        NotifyVideoEditorPreview();
        if (!ReferenceEquals(previous, next)) {
            previous?.Dispose();
        }
    }

    // сообщаем об ошибке редактора отдельно от состояния исходного плеера
    private void ShowVideoEditorError(string message) {
        videoEditorOutputVisible = false;
        videoEditorError = message;
        NotifyVideoEditorPreview();
    }

    // освобождаем только ожидающий снимок, запущенный расчет владеет своим буфером
    private void ClearVideoEditorRequest() {
        requestedVideoEditor?.Pixels.Dispose();
        requestedVideoEditor = null;
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
