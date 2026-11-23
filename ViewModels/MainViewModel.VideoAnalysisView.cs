using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private enum VideoAnalysisBackground { Original, Edited, Segmentation, Black, VideoSequence }
    private readonly record struct VideoAnalysisView(VideoAnalysisFrameKey Key, Bitmap? Bitmap,
        AnalysisSegmentationResult? Segmentation, bool WaitForEdited);

    private VideoAnalysisBackground videoAnalysisBackground;
    private VideoAnalysisBackground videoAnalysisBackgroundBeforeSegmentation;
    private bool videoAnalysisResultTabSelected;
    internal bool VideoAnalysisResultTabSelected => videoAnalysisResultTabSelected;
    private WriteableBitmap? videoAnalysisComposition;
    private WriteableBitmap? videoAnalysisSegmentationPreview;
    private long videoAnalysisViewRevision;
    private bool videoAnalysisViewRequested;
    private bool videoAnalysisViewRunning;
    private CancellationTokenSource? videoAnalysisViewCancellation;
    private string? videoAnalysisCompositionFailure;
    private string? videoAnalysisEditedFailure;
    private long videoAnalysisEditedBudget;
    private long videoAnalysisEditedReservedBytes;
    private TimeSpan? videoAnalysisEditedTime;
    private long videoAnalysisEditedReadyAt;
    private (VideoAnalysisFrameKey Frame, TimeSpan? Calculation)? videoAnalysisEditorStage;

    // редакторский шаг для анализа использует и показывает свой резерв, даже вне вкладки редактора
    internal (long Reserved, TimeSpan? Calculation, bool Reused) ReadVideoEditorAnalysisWork() {
        if (!videoAnalysisVisible) {
            return (videoAnalysisEditedReservedBytes, null, false);
        }
        if (videoAnalysisEditorStage is { } stage && IsCurrentAnalysisLayer(stage.Frame)) {
            return (videoAnalysisEditedReservedBytes, stage.Calculation, stage.Calculation is null);
        }
        return (videoAnalysisEditedReservedBytes, videoAnalysisEditedInput is not null &&
            IsCurrentAnalysisLayer(videoAnalysisEditedKey) ? videoAnalysisEditedTime : null, false);
    }

    // различаем расчет после Apply и использование подготовленного/ранее посчитанного стоп-кадра
    private void RecordVideoEditorAnalysisWork(VideoAnalysisInput? edited) {
        videoAnalysisEditorStage = edited is { } input && videoEditorPlan.HasSteps
            ? (input.Key, ReferenceEquals(input.Bitmap, videoAnalysisEditedInput) &&
                videoAnalysisStartedAt is { } started && videoAnalysisEditedReadyAt >= started ? videoAnalysisEditedTime : null)
            : null;
    }

    public int SelectedVideoAnalysisBackgroundIndex {
        get => (int)videoAnalysisBackground;
        set {
            if (IsVideoOpen && value >= 0 && value <= (int)VideoAnalysisBackground.VideoSequence) {
                ChangeVideoAnalysisBackground((VideoAnalysisBackground)value, fromSelector: true);
            }
        }
    }

    // связываем галочку сегментации с подложкой и сохраняем последний выбор остальных подложек
    private void ChangeVideoAnalysisBackground(VideoAnalysisBackground background, bool fromSelector = false) {
        VideoAnalysisBackground previous = videoAnalysisBackground;
        if (previous == background) {
            return;
        }
        // пока была выбрана сегментация, встречная связь могла сделать запомненную подложку недоступной
        if (!fromSelector && previous == VideoAnalysisBackground.Segmentation && background == VideoAnalysisBackground.VideoSequence &&
            VideoForegroundSource == VideoPipelineStage.Analysis) {
            background = VideoAnalysisBackground.Original;
        }
        if (!AcceptVideoAnalysisChoice(videoEdgeSource, videoSegmentationSource, background)) {
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
            ? !VideoAnalysisUsesSequence && !ShowsFrameAnalysisPlayback && (videoAnalysisBackground == VideoAnalysisBackground.Edited ||
              videoAnalysisBackground == VideoAnalysisBackground.Segmentation && videoSegmentationSource == AnalysisImageSource.Edited)
            : videoAnalysisDisplayedSource == AnalysisImageSource.Edited);
    private bool ShouldRetryVideoAnalysisEditedInput =>
        videoAnalysisEditedFailure is not null && videoAnalysisEditedBudget != PreviewSettings.BudgetBytes;
    private Bitmap? VideoAnalysisResultImage => VideoAnalysisUsesSequence ? combinedVideoAnalysisResult :
        ShowsFrameAnalysisPlayback ? videoAnalysisPlaybackBitmap : videoAnalysisComposition ??
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
                    if (isPlateAnalysisEnabled && plateResult is null) {
                        return "Области номера ещё не рассчитаны";
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
        bool restartedTemporal = UpdateCombinedVideoAnalysisRoute();
        UpdateFrameAnalysisPlaybackPlan(restartStrict: !restartedTemporal);
        UpdateVideoAnalysisPipelineDescription();
        videoAnalysisViewRevision++;
        videoAnalysisCompositionFailure = null;
        videoAnalysisViewRequested = (videoAnalysisVisible && !ShowsFrameAnalysisPlayback || VideoAnalysisManualNeedsEditedInput) &&
            IsVideoOpen && !analysisDisposed;
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
                long reserved = 0;
                try {
                    if (WantsVideoAnalysisEditedInput && videoAnalysisEditedFailure is null &&
                        TryResolveVideoAnalysisInput(AnalysisImageSource.Edited, out VideoAnalysisInput edited) &&
                        edited.Plan is { } plan) {
                        await PrepareVideoAnalysisEditedInputAsync(edited, plan, stop.Token);
                    }
                    if (stop.IsCancellationRequested || revision != videoAnalysisViewRevision || !videoAnalysisVisible || VideoAnalysisUsesSequence ||
                        !TryResolveVideoAnalysisView(out VideoAnalysisView view) || view.WaitForEdited) {
                        continue;
                    }

                    AnalysisEdgeResult? edges = isEdgesAnalysisEnabled && IsCurrentAnalysisLayer(edgesResult?.VideoFrame)
                        ? edgesResult : null;
                    AnalysisKeypointResult? points = isKeypointAnalysisEnabled && IsCurrentAnalysisLayer(keypointsResult?.VideoFrame)
                        ? keypointsResult : null;
                    AnalysisPlateResult? plate = isPlateAnalysisEnabled && IsCurrentAnalysisLayer(plateResult?.VideoFrame)
                        ? plateResult : null;
                    int plateLimit = VisiblePlateRegionLimit;
                    bool prepareSegmentation = view.Segmentation is not null && videoAnalysisSegmentationPreview is null;
                    bool compose = edges is not null || points is { Points.IsEmpty: false } ||
                        plate is { Rectangles.Count: > 0 } || view.Bitmap is null && view.Segmentation is null;
                    long frameBytes = VideoEditorFrame.GetBufferBytes(view.Key.Geometry.OutputSize);
                    long bytes = compose ? frameBytes + frameBytes * 3 / 4 : prepareSegmentation ? frameBytes : 0;
                    if (compose && view.Bitmap is not null && view.Segmentation is null) {
                        bytes += frameBytes;
                    }
                    if (compose && points?.VideoFrame is { } pointFrame &&
                        pointFrame.Geometry.ClockwiseQuarterTurns != view.Key.Geometry.ClockwiseQuarterTurns) {
                        bytes += 8L * points.Points.Length;
                    }
                    if (bytes > PreviewSettings.AnalysisBudgetBytes) {
                        throw new InvalidOperationException("Недостаточно бюджета буферов анализа для композиции");
                    }
                    if (bytes > PreviewSettings.AnalysisBudgetBytes - videoAnalysisReservedBytes) {
                        videoAnalysisViewRequested = true;
                        await Task.Delay(25, stop.Token);
                        continue;
                    }
                    videoAnalysisReservedBytes += bytes;
                    reserved = bytes;
                    long composing = Stopwatch.GetTimestamp();

                    if (view.Segmentation is { } segmentation && prepareSegmentation) {
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
                    if (compose) {
                        using VideoEditorFrame? background = view.Bitmap is not null && view.Segmentation is null
                            ? VideoEditorFrame.Capture(view.Bitmap) : null;
                        using VideoEditorFrame composed = await Task.Run(() => AnalysisCompositionService.ComposeVideoFrame(view.Key,
                            background, view.Segmentation, edges, points, plate, plateLimit, stop.Token), stop.Token);
                        if (stop.IsCancellationRequested || revision != videoAnalysisViewRevision || !IsCurrentAnalysisLayer(view.Key)) {
                            continue;
                        }
                        ReplaceVideoAnalysisComposition(CreateVideoAnalysisBitmap(composed));
                    }
                    RecordVideoAnalysisPresentation(revision, view.Key, Stopwatch.GetElapsedTime(composing));
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
                    videoAnalysisReservedBytes -= reserved;
                    videoAnalysisViewCancellation = null;
                    if (IsVideoOpen && videoAnalysisVisible && !analysisDisposed) {
                        RefreshVideoAnalysisStatus();
                    }
                }
            }
        }
        finally {
            videoAnalysisViewRunning = false;
            OnPropertyChanged(nameof(AnalysisResultImage));
        }
    }

    // готовим один измененный кадр для входа или подложки, сохраняя допуск буферов до окончания Apply
    private async Task PrepareVideoAnalysisEditedInputAsync(VideoAnalysisInput input, VideoEditorPlan plan, CancellationToken token) {
        videoAnalysisEditedKey = input.Key;
        videoAnalysisEditedBudget = PreviewSettings.BudgetBytes;
        long bytes = VideoEditorFrame.GetBufferBytes(input.Bitmap.PixelSize) + plan.GetScratchBytes(input.Bitmap.PixelSize);
        try {
            if (bytes > videoAnalysisEditedBudget) {
                throw new InvalidOperationException("Бюджета буферов недостаточно для редакторского кадра");
            }
            videoAnalysisEditedReservedBytes = bytes;
            videoAnalysisApplyingPlan = true;
            using VideoEditorFrame pixels = VideoEditorFrame.Capture(input.Bitmap);
            var (geometry, elapsed) = await Task.Run(() => {
                long started = Stopwatch.GetTimestamp();
                return (plan.Apply(pixels), Stopwatch.GetElapsedTime(started));
            }, token);
            token.ThrowIfCancellationRequested();
            if (!IsCurrentAnalysisLayer(input.Key)) {
                return;
            }
            if (geometry != input.Key.Geometry) {
                throw new InvalidOperationException("Геометрия редакторского кадра изменилась");
            }
            videoAnalysisEditedInput = CreateVideoAnalysisBitmap(pixels);
            videoAnalysisEditedTime = elapsed;
            videoAnalysisEditedReadyAt = Stopwatch.GetTimestamp();
            NotifyVideoAnalysisInput();
            NotifyVideoAnalysisPreview();
        }
        catch (Exception error) when (!token.IsCancellationRequested && IsCurrentAnalysisLayer(input.Key)) {
            videoAnalysisEditedFailure = $"Не удалось подготовить изменённый кадр: {error.Message}";
            NotifyVideoAnalysisInput();
        }
        finally {
            videoAnalysisApplyingPlan = false;
            videoAnalysisEditedReservedBytes = 0;
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
        videoAnalysisEditedTime = null;
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
