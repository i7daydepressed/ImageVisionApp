using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageVisionApp.Models;
using ImageVisionApp.Services;
using LibVLCSharp.Shared;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private VideoSequenceAnalysisPlan? videoAnalysisPlaybackPlan;
    private string? videoAnalysisPlaybackPlanError;
    private string? videoAnalysisPlaybackFailure;
    private CancellationTokenSource? videoAnalysisPlaybackStop;
    private int? submittedAnalysisPreviewRevision;
    private WriteableBitmap? videoAnalysisPlaybackBitmap;
    private (VideoPresentationTime Time, bool Exact)? videoAnalysisPlaybackStamp;
    private bool manualAnalysisPlaybackPaused;
    private bool preparedAnalysisPausedForApply;

    private bool IsFrameAnalysisOutputSelected => IsVideoOpen && videoAnalysisVisible && videoAnalysisResultTabSelected &&
        !VideoAnalysisUsesSequence && HasAnalysisVideoMethods;

    private bool IsStrictFrameAnalysisSelected => IsStrictVideo && IsFrameAnalysisOutputSelected;
    internal bool HasPhysicalAnalysisPlaybackOrdinal => !IsFrameAnalysisOutputSelected || preparedSource is not null;

    private bool ShowsFrameAnalysisPlayback => IsFrameAnalysisOutputSelected && !manualAnalysisPlaybackPaused &&
        (videoWantsToPlay || PlaybackStatus.IsPlaying || videoAnalysisPlaybackBitmap is not null ||
         videoAnalysisPlaybackFailure is not null || IsStrictFrameAnalysisSelected && strictPipelineError is not null);

    // обычному анализу нужны только оригинал и выбранный результат редактора
    private VideoSequenceAnalysisPlan CaptureFrameAnalysisPlaybackPlan(MediaDocument document) =>
        CaptureVideoSequenceAnalysisPlan(document, PreviewSettings) with { SequenceInput = VideoPipelineStage.None };

    // смена источника, подложки или алгоритма отменяет публикации прежнего плана
    private void UpdateFrameAnalysisPlaybackPlan(bool restartStrict = true) {
        VideoSequenceAnalysisPlan? plan = null;
        string? error = null;
        if (IsFrameAnalysisOutputSelected && !analysisDisposed && !mediaPreviewsDisposed &&
            CurrentDocument is { } document) {
            try {
                plan = CaptureFrameAnalysisPlaybackPlan(document);
            }
            catch (InvalidOperationException failure) {
                error = failure.Message;
            }
        }

        if (Equals(plan, videoAnalysisPlaybackPlan) && error == videoAnalysisPlaybackPlanError) {
            return;
        }

        videoAnalysisPlaybackPlan = plan;
        videoAnalysisPlaybackPlanError = error;
        InvalidateFrameAnalysisPlayback();
        ResetVisibleVideoOutput();
        if (!IsCombinedStrictOutputSelected && !WantsVideoTemporalPipeline && IsFrameAnalysisOutputSelected) {
            ResetPipelineOutputMeasurements();
        }
        if (restartStrict && !IsCombinedStrictOutputSelected) {
            RestartStrictPipelineOutput();
        }
        RefreshVideoAnalysisStatus();
    }

    // строгая очередь резервирует готовые композиции в бюджете анализа и читает с выбранного момента
    private StrictVideoPipeline CaptureStrictFrameAnalysisPipeline(IVideoMediaSource source, VideoSequenceAnalysisPlan plan) {
        PixelSize size = source.FrameSize;
        long outputBytes = VideoEditorFrame.GetBufferBytes(plan.GetOutputSize(size));
        long workBytes = plan.GetAnalysisWorkBytes(size);
        int capacity = (int)Math.Min(512, 1 + (PreviewSettings.AnalysisBudgetBytes - workBytes) / outputBytes);
        long generation = combinedMeasurementGeneration;
        return new(() => new VideoAnalysisFrameProcessor(plan, size, combinedMeasurements, generation, source.ReadScratchBytes),
            PersistentBytes: 0, ScratchBytes: Math.Max(0, workBytes - outputBytes), SelectedFrameBytes: outputBytes, Capacity: capacity,
            ReserveFrame: (stage, bytes) => combinedMeasurements.Reserve(generation, stage, bytes), Start: VideoPipelineStart.Target);
    }

    // во время обычного воспроизведения захватываем свежий кадр после освобождения рабочего буфера
    private void RefreshFrameAnalysisPlayback() {
        if (!videoWantsToPlay || mediaPreviewsDisposed || analysisDisposed || !IsFrameAnalysisOutputSelected) {
            return;
        }

        manualAnalysisPlaybackPaused = false;
        if (IsStrictVideo || videoAnalysisPlaybackStop is not null || videoAnalysisPlaybackFailure is not null ||
            submittedAnalysisPreviewRevision == VideoPreviewRevision ||
            videoAnalysisPlaybackPlan is not { } plan || VideoPreviewFrame is not { } source ||
            videoPlayback?.State is not VLCState.Playing || videoEditorWaitingForFrame ||
            videoFrameStep is not null || requestedVideoSeek is not null || videoRestarting ||
            preparedSource is null && HasUnconfirmedLiveAnalysisFrame ||
            preparedOpening is not null || preparedStartPending || preparedNeedsSeek || livePreviewHandoff is not null) {
            return;
        }

        submittedAnalysisPreviewRevision = VideoPreviewRevision;
        VideoFrameCursor? prepared = preparedSource is not null ? preparedPresentedFrame : null;
        long position = videoPlayback.Position >= 0 ? videoPlayback.Position : PlaybackStatus.Position;
        VideoFrameCursor cursor = prepared ?? new(new(position, 1000), VideoPreviewRevision, false);
        _ = ProcessFrameAnalysisPlaybackAsync(source, plan, cursor, exactTime: prepared is not null);
    }

    // один рабочий кадр обрабатывается вне UI, следующий захватывается с последней доступной ревизии
    private async Task ProcessFrameAnalysisPlaybackAsync(Bitmap source, VideoSequenceAnalysisPlan plan,
        VideoFrameCursor cursor, bool exactTime) {
        using CancellationTokenSource stop = new();
        videoAnalysisPlaybackStop = stop;
        long generation = combinedMeasurementGeneration;
        try {
            PixelSize size = source.PixelSize;
            long inputBytes = VideoEditorFrame.GetBufferBytes(size);
            if (checked(inputBytes + plan.GetAnalysisWorkBytes(size)) > plan.Resources.AnalysisBudgetBytes) {
                throw new InvalidOperationException("Недостаточно бюджета кадровых буферов анализа");
            }

            using IDisposable inputReserve = combinedMeasurements.Reserve(generation, VideoPipelineStage.Original, inputBytes);
            using VideoEditorFrame input = VideoEditorFrame.Capture(source);
            using VideoPipelineOutput? output = await Task.Run(async () => {
                await using VideoAnalysisFrameProcessor processor = new(plan, size, combinedMeasurements, generation, readerBytes: 0);
                return await processor.ProcessAsync(new(cursor.Time, cursor.Time, cursor.Ordinal, input), input, null, null, true,
                    stop.Token).ConfigureAwait(false);
            });
            if (IsCurrentFrameAnalysisPlayback(plan, stop.Token) && output?.Analysis is { } pixels) {
                PublishFrameAnalysisPlayback(pixels, cursor.Time, output.Ready, exactTime);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) {
        }
        catch (Exception failure) {
            if (IsCurrentFrameAnalysisPlayback(plan, stop.Token)) {
                videoAnalysisPlaybackFailure = failure.Message;
                RefreshVideoAnalysisStatus();
            }
        }
        finally {
            videoAnalysisPlaybackStop = null;
            RefreshFrameAnalysisPlayback();
        }
    }

    private bool IsCurrentFrameAnalysisPlayback(VideoSequenceAnalysisPlan plan, CancellationToken token) =>
        !token.IsCancellationRequested && !mediaPreviewsDisposed && !analysisDisposed && !IsStrictVideo &&
        IsFrameAnalysisOutputSelected && !manualAnalysisPlaybackPaused && ReferenceEquals(plan, videoAnalysisPlaybackPlan) &&
        ReferenceEquals(plan.Document, CurrentDocument);

    // результат хранит время именно обработанного кадра и обновляется после завершения всей композиции
    private void PublishFrameAnalysisPlayback(VideoEditorFrame pixels, VideoPresentationTime time,
        VideoPipelineMeasurements.ReadyStamp? ready, bool exactTime) {
        if (manualAnalysisPlaybackPaused) {
            return;
        }
        WriteableBitmap? previous = videoAnalysisPlaybackBitmap;
        WriteableBitmap target = GetPreparedBitmap(previous, pixels.Size);
        try {
            using ILockedFramebuffer locked = target.Lock();
            pixels.CopyTo(locked);
        }
        catch {
            if (!ReferenceEquals(previous, target)) {
                target.Dispose();
            }
            throw;
        }

        videoAnalysisPlaybackBitmap = target;
        videoAnalysisPlaybackStamp = (time, exactTime);
        NotifyVideoAnalysisPreview();
        RefreshVideoAnalysisStatus();
        if (!ReferenceEquals(previous, target)) {
            previous?.Dispose();
        }
        if (videoWantsToPlay) {
            RecordVisibleVideoOutput();
        }
        if (ready is { } stamp) {
            combinedMeasurements.RecordPublished(stamp);
        }
    }

    // оставляем подтверждённый исходник на экране и освобождаем резерв предпросмотра для ручного анализа
    private bool IsAnalysisBudgetAvailableForApply() {
        if (IsStrictFrameAnalysisSelected && strictPipelineActive && !preparedAnalysisPausedForApply) {
            preparedAnalysisPausedForApply = true;
            preparedVideo?.Cancel();
        }

        return videoAnalysisPlaybackStop is null &&
            (!preparedAnalysisPausedForApply || PreparedVideo is not { ReservedBytes: > 0 });
    }

    // перед перемоткой, ручным применением или сменой плана снимаем кадр и запрещаем поздний результат
    private void InvalidateFrameAnalysisPlayback() {
        videoAnalysisPlaybackStop?.Cancel();
        submittedAnalysisPreviewRevision = null;
        videoAnalysisPlaybackFailure = null;
        WriteableBitmap? previous = videoAnalysisPlaybackBitmap;
        videoAnalysisPlaybackBitmap = null;
        videoAnalysisPlaybackStamp = null;
        if (previous is not null) {
            NotifyVideoAnalysisPreview();
            previous.Dispose();
        }
    }
}
