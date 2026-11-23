using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal sealed record VideoPipelineOutput(VideoForegroundFrame? Foreground, VideoEditorFrame? Analysis,
    VideoPipelineMeasurements.ReadyStamp? Ready, IDisposable? ForegroundLease, IDisposable? AnalysisLease,
    VideoFrameCursor? AnalysisFrame = null) : IDisposable {
    // готовая картинка и ее резерв имеют один срок жизни у получателя
    public void Dispose() {
        Analysis?.Dispose();
        AnalysisLease?.Dispose();
        ForegroundLease?.Dispose();
    }
}

internal sealed class VideoCombinedFrameProcessor : IVideoStrictPipelineProcessor {
    private readonly VideoForegroundModel? model;
    private readonly VideoOpticalFlowModel? opticalFlow;
    private readonly VideoForegroundInputPlan? upstream;
    private readonly VideoForegroundInputPlan? flowBackdrop;
    private readonly VideoEditorGeometry inputGeometry;
    private readonly VideoEditorGeometry backdropGeometry;
    private readonly VideoSequenceAnalysisPlan? downstream;
    private readonly VideoEditorPlan? editor;
    private readonly bool maskOnly;
    private readonly VideoBackgroundRaster? background;
    private readonly int? motionBlurRadius;
    private readonly VideoPipelineMeasurements? measurements;
    private readonly long generation;
    private readonly IDisposable? originalReserve;
    private readonly IDisposable? modelReserve;
    private readonly long foregroundBytes;
    private readonly long motionBlurBytes;
    private readonly long editingBytes;
    private readonly long analysisBytes;

    // отдельно запоминаем геометрию расчета потока и растра показа для одного прохода
    public VideoCombinedFrameProcessor(PixelSize size, VideoForegroundInputPlan? upstream = null,
        VideoSequenceAnalysisPlan? downstream = null, VideoEditorPlan? editor = null, bool maskOnly = false,
        VideoPipelineMeasurements? measurements = null, long generation = 0, long readerBytes = 0,
        VideoBackgroundRaster? background = null, int? motionBlurRadius = null, bool useOpticalFlow = false,
        VideoForegroundInputPlan? flowBackdrop = null) {
        this.upstream = upstream;
        this.flowBackdrop = useOpticalFlow ? flowBackdrop : null;
        this.downstream = downstream;
        this.editor = editor;
        this.maskOnly = maskOnly;
        this.measurements = measurements;
        this.generation = generation;
        var first = upstream?.GetResources(size) ?? default;
        var backdropResources = this.flowBackdrop?.GetResources(size) ?? default;
        var last = downstream?.GetResources(size) ?? default;
        editingBytes = Math.Max(Math.Max(first.EditorBytes, backdropResources.EditorBytes), last.EditorBytes);
        analysisBytes = Math.Max(Math.Max(first.AnalysisBytes, backdropResources.AnalysisBytes), last.AnalysisBytes);
        inputGeometry = upstream?.GetGeometry(size) ?? new(size, 0);
        backdropGeometry = this.flowBackdrop?.GetGeometry(size) ?? inputGeometry;
        PixelSize modelSize = inputGeometry.OutputSize;
        this.motionBlurRadius = maskOnly ? null : motionBlurRadius;
        this.background = maskOnly || this.motionBlurRadius is not null ? null : background;
        if (this.background is { } fitted && fitted.Size != modelSize) {
            throw new ArgumentException("Подготовленный фон не совпадает с размером видеоряда", nameof(background));
        }
        long persistent = useOpticalFlow ? VideoOpticalFlowModel.GetPersistentBytes(modelSize) :
            VideoForegroundModel.GetPersistentBytes(modelSize);
        foregroundBytes = (useOpticalFlow ? VideoOpticalFlowModel.GetRequiredBufferBytes(modelSize, 0) :
            VideoForegroundModel.GetRequiredBufferBytes(modelSize, 0)) - persistent;
        motionBlurBytes = this.motionBlurRadius is { } radius
            ? checked(VideoMotionBlurRaster.GetScratchBytes(modelSize, radius) + VideoMotionBlurRaster.GetBufferBytes(modelSize))
            : 0;
        if (useOpticalFlow) {
            opticalFlow = new(modelSize);
        }
        else {
            model = new(modelSize);
        }
        originalReserve = measurements?.Reserve(generation, VideoPipelineStage.Original, readerBytes);
        modelReserve = measurements?.Reserve(generation, VideoPipelineStage.VideoSequence,
            checked(persistent + (this.background?.BufferBytes ?? 0)));
    }

    // берем растр подложки у того же физического кадра и рассчитываем поток по входной паре
    public ValueTask<VideoPipelineOutput?> ProcessAsync(TimedVideoInput input, VideoEditorFrame edited,
        long? startedAt, TimeSpan? editingElapsed, bool publish, CancellationToken token) {
        long enteredAt = startedAt ?? Stopwatch.GetTimestamp();
        using VideoEditorFrame? owned = PrepareEditing(input.Pixels, editingElapsed, publish, token);
        VideoEditorFrame prepared = owned ?? edited;
        using VideoEditorFrame? composition = upstream?.UsesAnalysis == true ? AnalyzeInput(upstream, input, prepared, token) : null;
        VideoEditorFrame pixels = composition ?? (upstream?.NeedsEditing == true ? prepared : input.Pixels);
        VideoForegroundInputPlan? selectedBackdrop = publish ? flowBackdrop : null;
        using VideoEditorFrame? backdropComposition = selectedBackdrop?.UsesAnalysis == true
            ? AnalyzeInput(selectedBackdrop, input, prepared, token) : null;
        VideoEditorFrame? backdropPixels = selectedBackdrop is null ? null :
            backdropComposition ?? (selectedBackdrop.NeedsEditing ? prepared : input.Pixels);
        VideoForegroundFrame? foreground;
        long workingBytes = checked(foregroundBytes + (publish ? motionBlurBytes : 0));
        using (var stage = measurements?.BeginStage(generation, VideoPipelineStage.VideoSequence, workingBytes)) {
            foreground = opticalFlow is { } flow
                ? flow.Process(input, pixels, publish, token, backdropPixels,
                    inputGeometry: backdropPixels is null ? null : inputGeometry,
                    backdropGeometry: backdropPixels is null ? null : backdropGeometry) :
                model!.Process(input, pixels, publish, token);
            if (foreground is not null && motionBlurRadius is { } radius) {
                foreground = foreground with { MotionBlur = VideoMotionBlurRaster.Create(foreground, radius, token) };
            }
            stage?.Complete();
        }
        if (foreground is null) {
            return ValueTask.FromResult<VideoPipelineOutput?>(null);
        }

        IDisposable? foregroundLease = measurements?.Reserve(generation, VideoPipelineStage.VideoSequence, foreground.BufferBytes);
        VideoEditorFrame? analysis = null;
        IDisposable? analysisLease = null;
        try {
            if (downstream is not null) {
                using var stage = measurements?.BeginStage(generation, VideoPipelineStage.Analysis, analysisBytes);
                using VideoEditorFrame temporal = VideoEditorFrame.FromForeground(foreground, maskOnly, token, background,
                    motionBlur: motionBlurRadius is not null);
                analysis = downstream.Transform(input, temporal, token, prepared);
                stage?.Complete();
                analysisLease = measurements?.Reserve(generation, VideoPipelineStage.Analysis,
                    VideoEditorFrame.GetBufferBytes(analysis.Size));
            }
            var ready = measurements?.RecordReady(generation, input.Ordinal, enteredAt);
            return ValueTask.FromResult<VideoPipelineOutput?>(new(foreground, analysis, ready, foregroundLease, analysisLease));
        }
        catch {
            analysis?.Dispose();
            analysisLease?.Dispose();
            foregroundLease?.Dispose();
            throw;
        }
    }

    // повторно используем редакторский буфер строгой очереди, в живой готовим его один раз
    private VideoEditorFrame? PrepareEditing(VideoEditorFrame original, TimeSpan? editingElapsed, bool publish,
        CancellationToken token) {
        if (editingElapsed is { } elapsed) {
            measurements?.RecordStage(generation, VideoPipelineStage.Editing, 0, elapsed);
            return null;
        }
        if (editingBytes == 0 || editor?.HasSteps != true || !publish && upstream?.NeedsEditing != true) {
            return null;
        }
        using var stage = measurements?.BeginStage(generation, VideoPipelineStage.Editing, editingBytes);
        VideoEditorFrame edited = original.Copy();
        try {
            editor.Apply(edited);
            token.ThrowIfCancellationRequested();
            stage?.Complete();
            return edited;
        }
        catch {
            edited.Dispose();
            throw;
        }
    }

    // вычисляем композицию анализа для входа или подложки на том же физическом кадре
    private VideoEditorFrame AnalyzeInput(VideoForegroundInputPlan plan, TimedVideoInput input, VideoEditorFrame edited,
        CancellationToken token) {
        using var stage = measurements?.BeginStage(generation, VideoPipelineStage.Analysis, analysisBytes);
        VideoEditorFrame composed = plan.Transform(input, token, edited);
        stage?.Complete();
        return composed;
    }

    // освобождаем модель и принадлежащие одному поколению рабочие резервы
    public ValueTask DisposeAsync() {
        opticalFlow?.Dispose();
        model?.Dispose();
        modelReserve?.Dispose();
        originalReserve?.Dispose();
        return ValueTask.CompletedTask;
    }
}
