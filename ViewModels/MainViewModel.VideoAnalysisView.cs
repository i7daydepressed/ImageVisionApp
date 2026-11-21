using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private enum VideoAnalysisBackground { Original, Edited, Segmentation, Black }
    private readonly record struct VideoAnalysisView(VideoAnalysisFrameKey Key, Bitmap? Bitmap,
        AnalysisSegmentationResult? Segmentation, bool WaitForEdited);

    private VideoAnalysisBackground videoAnalysisBackground;
    private VideoAnalysisBackground videoAnalysisBackgroundBeforeSegmentation;
    private bool videoAnalysisResultTabSelected;
    private WriteableBitmap? videoAnalysisComposition;
    private WriteableBitmap? videoAnalysisSegmentationPreview;
    private long videoAnalysisViewRevision;
    private bool videoAnalysisViewRequested;
    private bool videoAnalysisViewRunning;
    private CancellationTokenSource? videoAnalysisViewCancellation;
    private string? videoAnalysisCompositionFailure;
    private string? videoAnalysisEditedFailure;
    private long videoAnalysisEditedBudget;

    public int SelectedVideoAnalysisBackgroundIndex {
        get => (int)videoAnalysisBackground;
        set {
            if (IsVideoOpen && value >= 0 && value <= (int)VideoAnalysisBackground.Black) {
                ChangeVideoAnalysisBackground((VideoAnalysisBackground)value);
            }
        }
    }

    // связываем галочку сегментации с подложкой и сохраняем последний выбор остальных подложек
    private void ChangeVideoAnalysisBackground(VideoAnalysisBackground background) {
        VideoAnalysisBackground previous = videoAnalysisBackground;
        if (previous == background) {
            return;
        }
        if (previous != VideoAnalysisBackground.Segmentation) {
            videoAnalysisBackgroundBeforeSegmentation = previous;
        }
        videoAnalysisBackground = background;
        OnPropertyChanged(nameof(SelectedVideoAnalysisBackgroundIndex));
        if (background == VideoAnalysisBackground.Segmentation || previous == VideoAnalysisBackground.Segmentation) {
            OnPropertyChanged(nameof(IsSegmentationAnalysisEnabled));
            InvalidateSegmentationAnalysis();
        }
        else {
            RefreshVideoAnalysisComposition();
            RefreshVideoAnalysisStatus();
        }
    }

    // сохраняем выбранную вкладку, чтобы готовить Edited только для видимого ракурса
    internal void SetVideoAnalysisResultTabSelected(bool result) {
        if (videoAnalysisResultTabSelected != result) {
            videoAnalysisResultTabSelected = result;
            if (IsVideoOpen) {
                RefreshVideoAnalysisComposition();
            }
        }
    }

    // сбрасываем подложку при смене документа, не затрагивая фото-настройку
    private void ResetVideoAnalysisViewSelection() {
        videoAnalysisBackgroundBeforeSegmentation = videoAnalysisBackground = VideoAnalysisBackground.Original;
        OnPropertyChanged(nameof(SelectedVideoAnalysisBackgroundIndex));
        OnPropertyChanged(nameof(IsSegmentationAnalysisEnabled));
    }

    private bool VideoAnalysisManualNeedsEditedInput =>
        videoAnalysisRequest is { } request && NeedsEditedVideoAnalysisInput(request);
    private bool WantsVideoAnalysisEditedInput => VideoAnalysisManualNeedsEditedInput ||
        videoAnalysisVisible && (videoAnalysisResultTabSelected
            ? videoAnalysisBackground == VideoAnalysisBackground.Edited ||
              videoAnalysisBackground == VideoAnalysisBackground.Segmentation && videoSegmentationSource == AnalysisImageSource.Edited
            : videoAnalysisDisplayedSource == AnalysisImageSource.Edited);
    private bool ShouldRetryVideoAnalysisEditedInput =>
        videoAnalysisEditedFailure is not null && videoAnalysisEditedBudget != PreviewSettings.BudgetBytes;
    private Bitmap? VideoAnalysisResultImage => videoAnalysisComposition ??
        (TryResolveVideoAnalysisView(out VideoAnalysisView view) ? view.Bitmap : null);

    // показываем ожидание вычислений отдельно от уже доступных пикселей подложки
    private string? VideoAnalysisPendingMessage {
        get {
            if (TryResolveVideoAnalysisView(out VideoAnalysisView view)) {
                if (view.WaitForEdited) {
                    return "Подложка: оригинал. Результат редактирования готовится";
                }
                if (videoAnalysisBackground == VideoAnalysisBackground.Segmentation && view.Segmentation is null) {
                    return "Подложка: оригинал. Выполните сегментацию";
                }
                if (HasVideoAnalysisResult) {
                    if (isEdgesAnalysisEnabled && edgesResult is null) {
                        return "Границы ещё не рассчитаны";
                    }
                    if (isKeypointAnalysisEnabled && keypointsResult is null) {
                        return "Ключевые точки ещё не рассчитаны";
                    }
                }
            }
            return null;
        }
    }

    // определяем геометрию выбранной подложки и оставляем исходник видимым, пока она готовится
    private bool TryResolveVideoAnalysisView(out VideoAnalysisView view) {
        view = default;
        if (!TryResolveVideoAnalysisInput(AnalysisImageSource.Original, out VideoAnalysisInput original)) {
            return false;
        }
        view = new(original.Key, original.Bitmap, null, false);
        switch (videoAnalysisBackground) {
            case VideoAnalysisBackground.Edited:
                view = TryResolveVideoAnalysisInput(AnalysisImageSource.Edited, out VideoAnalysisInput edited) && edited.Plan is null
                    ? new(edited.Key, edited.Bitmap, null, false)
                    : view with { WaitForEdited = true };
                break;
            case VideoAnalysisBackground.Segmentation:
                if (segmentationResult?.VideoFrame is { } segmentationKey && IsCurrentAnalysisLayer(segmentationKey)) {
                    view = new(segmentationKey, videoAnalysisSegmentationPreview ?? original.Bitmap, segmentationResult, false);
                }
                break;
            case VideoAnalysisBackground.Black:
                view = view with { Bitmap = null };
                break;
        }
        return true;
    }

    // меняем только показ и сразу возвращаем подложку, если прежние отметки уже не актуальны
    private void RefreshVideoAnalysisComposition() {
        videoAnalysisViewRevision++;
        videoAnalysisCompositionFailure = null;
        videoAnalysisViewRequested = (videoAnalysisVisible || VideoAnalysisManualNeedsEditedInput) && IsVideoOpen && !analysisDisposed;
        if (videoAnalysisApplyingPlan && !WantsVideoAnalysisEditedInput) {
            videoAnalysisViewCancellation?.Cancel();
        }
        ReplaceVideoAnalysisComposition(null);
        if (videoAnalysisViewRequested && !videoAnalysisViewRunning) {
            _ = ProcessVideoAnalysisViewAsync();
        }
    }

    // последняя настройка показа заменяет ожидающую, готовые алгоритмические слои остаются на месте
    private async Task ProcessVideoAnalysisViewAsync() {
        videoAnalysisViewRunning = true;
        try {
            while (videoAnalysisViewRequested && !analysisDisposed && IsVideoOpen) {
                videoAnalysisViewRequested = false;
                long revision = videoAnalysisViewRevision;
                using CancellationTokenSource stop = new();
                videoAnalysisViewCancellation = stop;
                try {
                    if (WantsVideoAnalysisEditedInput && videoAnalysisEditedFailure is null &&
                        TryResolveVideoAnalysisInput(AnalysisImageSource.Edited, out VideoAnalysisInput edited) &&
                        edited.Plan is { } plan) {
                        await PrepareVideoAnalysisEditedInputAsync(edited, plan, stop.Token);
                    }
                    if (stop.IsCancellationRequested || revision != videoAnalysisViewRevision || !videoAnalysisVisible ||
                        !TryResolveVideoAnalysisView(out VideoAnalysisView view) || view.WaitForEdited) {
                        continue;
                    }

                    if (view.Segmentation is { } segmentation && videoAnalysisSegmentationPreview is null) {
                        using VideoEditorFrame pixels = await Task.Run(() => VideoEditorFrame.FromRgb(view.Key.Geometry.OutputSize,
                            segmentation.SegmentedRgb.Span, stop.Token), stop.Token);
                        if (stop.IsCancellationRequested || !ReferenceEquals(segmentation, segmentationResult) ||
                            !IsCurrentAnalysisLayer(view.Key)) {
                            continue;
                        }
                        videoAnalysisSegmentationPreview = CreateVideoAnalysisBitmap(pixels);
                        NotifyVideoAnalysisPreview();
                        if (revision != videoAnalysisViewRevision) {
                            continue;
                        }
                    }
                    AnalysisEdgeResult? edges = isEdgesAnalysisEnabled && IsCurrentAnalysisLayer(edgesResult?.VideoFrame)
                        ? edgesResult : null;
                    AnalysisKeypointResult? points = isKeypointAnalysisEnabled && IsCurrentAnalysisLayer(keypointsResult?.VideoFrame)
                        ? keypointsResult : null;
                    if (edges is null && (points is null || points.Points.IsEmpty) && view.Bitmap is not null) {
                        continue;
                    }
                    using VideoEditorFrame? background = view.Bitmap is not null && view.Segmentation is null
                        ? VideoEditorFrame.Capture(view.Bitmap) : null;
                    using VideoEditorFrame composed = await Task.Run(() => AnalysisCompositionService.ComposeVideoFrame(view.Key,
                        background, view.Segmentation, edges, points, stop.Token), stop.Token);
                    if (!stop.IsCancellationRequested && revision == videoAnalysisViewRevision && IsCurrentAnalysisLayer(view.Key)) {
                        ReplaceVideoAnalysisComposition(CreateVideoAnalysisBitmap(composed));
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) {
                    // изменившийся кадр освобождает временные пиксели без публикации
                }
                catch (Exception error) {
                    if (!stop.IsCancellationRequested && revision == videoAnalysisViewRevision) {
                        videoAnalysisCompositionFailure = $"Не удалось собрать показ: {error.Message}";
                    }
                }
                finally {
                    videoAnalysisViewCancellation = null;
                    if (IsVideoOpen && videoAnalysisVisible && !analysisDisposed) {
                        RefreshVideoAnalysisStatus();
                    }
                }
            }
        }
        finally {
            videoAnalysisViewRunning = false;
        }
    }

    // готовим один измененный кадр для входа или подложки, сохраняя допуск буферов до окончания Apply
    private async Task PrepareVideoAnalysisEditedInputAsync(VideoAnalysisInput input, VideoEditorPlan plan, CancellationToken token) {
        videoAnalysisEditedKey = input.Key;
        videoAnalysisEditedBudget = PreviewSettings.BudgetBytes;
        try {
            if (VideoEditorFrame.GetBufferBytes(input.Bitmap.PixelSize) + plan.GetScratchBytes(input.Bitmap.PixelSize) >
                videoAnalysisEditedBudget) {
                throw new InvalidOperationException("Бюджета буферов недостаточно для редакторского кадра");
            }
            using VideoEditorFrame pixels = VideoEditorFrame.Capture(input.Bitmap);
            VideoEditorGeometry geometry;
            videoAnalysisApplyingPlan = true;
            try {
                geometry = await Task.Run(() => plan.Apply(pixels), token);
            }
            finally {
                videoAnalysisApplyingPlan = false;
            }
            token.ThrowIfCancellationRequested();
            if (!IsCurrentAnalysisLayer(input.Key)) {
                return;
            }
            if (geometry != input.Key.Geometry) {
                throw new InvalidOperationException("Геометрия редакторского кадра изменилась");
            }
            videoAnalysisEditedInput = CreateVideoAnalysisBitmap(pixels);
            NotifyVideoAnalysisInput();
            NotifyVideoAnalysisPreview();
        }
        catch (Exception error) when (!token.IsCancellationRequested && IsCurrentAnalysisLayer(input.Key)) {
            videoAnalysisEditedFailure = $"Не удалось подготовить изменённый кадр: {error.Message}";
            NotifyVideoAnalysisInput();
        }
    }

    // переносим готовый BGRA в bitmap без промежуточного PNG
    private static WriteableBitmap CreateVideoAnalysisBitmap(VideoEditorFrame pixels) {
        WriteableBitmap bitmap = GetPreparedBitmap(null, pixels.Size);
        try {
            using ILockedFramebuffer target = bitmap.Lock();
            pixels.CopyTo(target);
            return bitmap;
        }
        catch {
            bitmap.Dispose();
            throw;
        }
    }

    // убираем работу старого вида и освобождаем его результат после оповещения привязок
    private void CancelVideoAnalysisComposition() {
        videoAnalysisViewRevision++;
        videoAnalysisViewRequested = false;
        videoAnalysisViewCancellation?.Cancel();
        videoAnalysisCompositionFailure = null;
        ReplaceVideoAnalysisComposition(null);
    }

    // удаляем подложку только при замене самой карты сегментации
    private void ReleaseVideoAnalysisSegmentationPreview() {
        if (videoAnalysisSegmentationPreview is { } previous) {
            videoAnalysisSegmentationPreview = null;
            NotifyVideoAnalysisPreview();
            previous.Dispose();
        }
    }

    // освобождаем только принадлежащую анализу копию измененного кадра
    private void ReleaseVideoAnalysisEditedInput() {
        WriteableBitmap? previous = videoAnalysisEditedInput;
        videoAnalysisEditedInput = null;
        videoAnalysisEditedKey = null;
        videoAnalysisEditedFailure = null;
        if (previous is not null) {
            NotifyVideoAnalysisInput();
            NotifyVideoAnalysisPreview();
            previous.Dispose();
        }
    }

    // передаем UI новые пиксели либо возвращаем его к разрешенной подложке
    private void ReplaceVideoAnalysisComposition(WriteableBitmap? next) {
        WriteableBitmap? previous = videoAnalysisComposition;
        videoAnalysisComposition = next;
        NotifyVideoAnalysisPreview();
        previous?.Dispose();
    }

    // переиспользуем сигнал перерисовки при смене пикселей общего видеокадра
    private void NotifyVideoAnalysisPreview() {
        VideoAnalysisInputRevision++;
        OnPropertyChanged(nameof(VideoAnalysisInputRevision));
        OnPropertyChanged(nameof(AnalysisResultImage));
        NotifyAnalysisControls();
    }
}
