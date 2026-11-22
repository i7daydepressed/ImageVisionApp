using System;
using System.Threading;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private readonly VideoPipelineMeasurements combinedMeasurements = new();
    private long combinedMeasurementGeneration;
    private long recordedCombinedWaitMs;
    private VideoSequenceAnalysisPlan? observedSequenceAnalysisPlan;
    private string? observedSequenceAnalysisError;
    private WriteableBitmap? combinedVideoAnalysisResult;
    private string? combinedVideoAnalysisNotice;
    private bool strictPipelineActive;
    private string? strictPipelineError;

    private bool IsCombinedAnalysisOutputSelected =>
        videoAnalysisVisible && videoAnalysisResultTabSelected && VideoAnalysisUsesSequence;
    private bool WantsVideoTemporalPipeline => IsVideoOpen && HasActiveVideoSequenceEffect &&
        (IsVideoForegroundOutputSelected || IsCombinedAnalysisOutputSelected);
    private bool IsCombinedStrictOutputSelected => IsStrictVideo && WantsVideoTemporalPipeline;
    private bool IsStrictPipelineOutputSelected => IsCombinedStrictOutputSelected || IsStrictFrameAnalysisSelected;
    private string? CombinedVideoAnalysisNotice => !HasActiveVideoSequenceEffect
        ? "Включите вычитание или замену фона на вкладке «Работа с видеорядом»"
        : combinedVideoAnalysisNotice;

    // включаем новый проход только при изменении неизменяемого снимка настроек
    private bool UpdateCombinedVideoAnalysisRoute() {
        VideoSequenceAnalysisPlan? next = null;
        string? error = null;
        if (IsCombinedAnalysisOutputSelected && CurrentDocument is { } document && !mediaPreviewsDisposed) {
            try {
                next = CaptureVideoSequenceAnalysisPlan(document, PreviewSettings);
            }
            catch (InvalidOperationException failure) {
                error = failure.Message;
            }
        }
        if (Equals(next, observedSequenceAnalysisPlan) && error == observedSequenceAnalysisError) {
            return false;
        }
        observedSequenceAnalysisPlan = next;
        observedSequenceAnalysisError = error;
        RestartVideoForeground();
        return true;
    }

    // ручной расчет ставит общий плеер на паузу и дочитывает подтвержденную позицию
    private void ApplyCombinedVideoAnalysis() {
        if (!IsCombinedAnalysisOutputSelected) {
            return;
        }
        videoForegroundFailed = false;
        videoForegroundApplying = true;
        PauseVideoPlayback();
        if (strictPipelineError is not null && IsCombinedStrictOutputSelected) {
            RestartStrictPipelineOutput();
        }
        RefreshVideoForeground();
    }

    // храним отдельно окончательный растр анализа и готовую пару входа и маски видеоряда
    private void PublishCombinedOutput(VideoPipelineOutput output) {
        if (output.Foreground is not { } foreground) {
            return;
        }
        if (output.Analysis is { } pixels && IsCombinedAnalysisOutputSelected) {
            WriteableBitmap? previous = combinedVideoAnalysisResult;
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
            videoForegroundFrame = foreground;
            combinedVideoAnalysisResult = target;
            combinedVideoAnalysisNotice = null;
            NotifyVideoAnalysisPreview();
            RefreshVideoAnalysisStatus();
            if (!ReferenceEquals(previous, target)) {
                previous?.Dispose();
            }
            if (videoWantsToPlay) {
                RecordVisibleVideoOutput();
            }
        }
        else if (IsVideoForegroundOutputSelected) {
            PublishVideoForeground(foreground);
        }
        if (output.Ready is { } sample) {
            combinedMeasurements.RecordPublished(sample);
        }
    }

    // удаляем чужую картинку прежде, чем освободить её пиксели
    private void ClearCombinedAnalysisOutput() {
        WriteableBitmap? previous = combinedVideoAnalysisResult;
        combinedVideoAnalysisResult = null;
        combinedVideoAnalysisNotice = observedSequenceAnalysisError;
        if (previous is not null || IsCombinedAnalysisOutputSelected) {
            NotifyVideoAnalysisPreview();
            RefreshVideoAnalysisStatus();
        }
        previous?.Dispose();
    }

    // снимок ресурсов готового маршрута передается одному живому или строгому читателю
    private CombinedVideoWork CaptureCombinedVideoWork(MediaDocument document, IVideoMediaSource source) {
        VideoPreviewSettings resources = PreviewSettings;
        VideoSequenceAnalysisPlan? downstream = IsCombinedAnalysisOutputSelected
            ? CaptureVideoSequenceAnalysisPlan(document, resources) : null;
        VideoForegroundInputPlan? upstream = CaptureVideoForegroundInput(document, resources);
        upstream?.ValidateBudget(source.FrameSize);
        downstream?.ValidateBudget(source.FrameSize);

        var foregroundSize = upstream?.GetOutputSize(source.FrameSize) ?? source.FrameSize;
        long backgroundBytes = CanUseVideoBackgroundReplacement && videoBackgroundImage is { } image
            ? GetVideoBackgroundBytes(image, foregroundSize) : 0;
        long required = checked(VideoForegroundModel.GetRequiredBufferBytes(foregroundSize, source.ReadScratchBytes) + backgroundBytes);
        if (required > resources.ForegroundBudgetBytes) {
            long needed = (long)Math.Ceiling(required / (1024d * 1024));
            throw new InvalidOperationException($"Недостаточно бюджета видеоряда. Нужно от {needed} МиБ, выбрано " +
                $"{resources.ForegroundBudgetMiB} МиБ. Измените бюджет в настройках медиаконтейнера.");
        }

        long persistent = checked(VideoForegroundModel.GetPersistentBytes(foregroundSize) + backgroundBytes);
        long foregroundFrame = VideoEditorFrame.GetBufferBytes(foregroundSize);
        long analysisFrame = downstream is null ? 0 : VideoEditorFrame.GetBufferBytes(downstream.GetOutputSize(source.FrameSize));
        long analysisScratch = Math.Max(upstream?.GetResources(source.FrameSize).AnalysisBytes ?? 0,
            downstream?.GetResources(source.FrameSize).AnalysisBytes ?? 0);
        long foregroundCapacity = 1 + (resources.ForegroundBudgetBytes - required) / foregroundFrame;
        long analysisCapacity = analysisFrame == 0 ? long.MaxValue :
            1 + (resources.AnalysisBudgetBytes - analysisScratch) / analysisFrame;
        int capacity = (int)Math.Min(512, Math.Min(foregroundCapacity, analysisCapacity));
        long retained = checked(foregroundFrame + analysisFrame);
        long scratch = checked(required - source.ReadScratchBytes - persistent - foregroundFrame +
            Math.Max(0, analysisScratch - analysisFrame));
        long generation = combinedMeasurementGeneration;
        VideoEditorPlan editor = videoEditorPlan;
        bool maskOnly = VideoForegroundViewIndex == 1;
        VideoBackgroundRaster? background = downstream is not null ? GetReplacementBackground(foregroundSize) : null;
        bool needsEditing = upstream?.NeedsEditing == true || downstream?.NeedsEditing == true;
        return new(() => new VideoCombinedFrameProcessor(source.FrameSize, upstream, downstream, editor, maskOnly,
            combinedMeasurements, generation, source.ReadScratchBytes, background), persistent, scratch, retained, capacity, needsEditing);
    }

    private readonly record struct CombinedVideoWork(Func<IVideoStrictPipelineProcessor> Create, long PersistentBytes,
        long ScratchBytes, long SelectedFrameBytes, int Capacity, bool NeedsEditing);

    // перезапрашиваем ту же позицию у существующего строгого координатора
    private void RestartStrictPipelineOutput() {
        if (!IsStrictVideo || preparedSource is null || videoFrameStep is not null ||
            !IsStrictPipelineOutputSelected && !strictPipelineActive) {
            return;
        }
        RequestPreparedVideo(PlaybackStatus.Position, preparedAtEnd, preservePlay: true,
            strictAnchor: videoPinnedFrame);
    }

    // новый seek или маршрут исключает старые публикации и открывает отдельное измерение
    private void ResetPipelineOutputMeasurements() {
        recordedCombinedWaitMs = 0;
        if (WantsVideoTemporalPipeline || IsFrameAnalysisOutputSelected) {
            combinedMeasurementGeneration = combinedMeasurements.StartGeneration();
        }
        else {
            combinedMeasurements.Clear();
        }
    }

    // собираем накопленные интервалы реальной паузы общего строгого плеера
    internal VideoPipelineMeasurements.Snapshot? ReadVideoPipelineMeasurements() {
        if (!WantsVideoTemporalPipeline && !ShowsFrameAnalysisPlayback) {
            return null;
        }
        if (strictPipelineActive && IsStrictVideo) {
            long total = ReadStrictVideoPreviewState().AutomaticWaitMs;
            if (total > recordedCombinedWaitMs) {
                combinedMeasurements.RecordAutomaticWait(combinedMeasurementGeneration,
                    TimeSpan.FromMilliseconds(total - recordedCombinedWaitMs));
                recordedCombinedWaitMs = total;
            }
        }
        var measurement = combinedMeasurements.Read();
        return measurement is { } snapshot && strictPipelineActive && IsStrictVideo &&
            PreparedVideo is { } window && window.Generation == preparedGeneration
            ? snapshot with { ReservedBytes = window.ReservedBytes, PeakReservedBytes = window.PeakReservedBytes }
            : measurement;
    }
}
