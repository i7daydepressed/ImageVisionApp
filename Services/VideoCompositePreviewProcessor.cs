using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal sealed class VideoCompositePreviewProcessor : IVideoStrictPipelineProcessor {
    private readonly PixelSize size;
    private readonly VideoEditorGeometry editedGeometry;
    private readonly VideoEditorPlan editor;
    private readonly VideoForegroundInputPlan? upstreamAnalysis;
    private readonly VideoSequenceAnalysisPlan? downstream;
    private readonly VideoSequenceComposite composite;
    private readonly VideoPipelineMeasurements measurements;
    private readonly long generation;
    private readonly long editingScratchBytes;
    private readonly long upstreamAnalysisBytes;
    private readonly long downstreamAnalysisBytes;
    private readonly long sequenceBytes;
    private readonly IDisposable? originalReserve;
    private readonly IDisposable? sequenceReserve;
    private bool disposed;

    // фиксируем геометрию и учет рабочих буферов на весь последовательный проход
    public VideoCompositePreviewProcessor(PixelSize size, VideoSequenceCompositionPlan composition,
        VideoForegroundInputPlan? upstreamAnalysis, VideoSequenceAnalysisPlan? downstream, VideoEditorPlan editor,
        VideoPipelineMeasurements measurements, long generation, long readerBytes) {
        bool needsAnalysis = (composition.RequiredSources & VideoPipelineStage.Analysis) != 0;
        if (needsAnalysis && downstream is not null) {
            throw new ArgumentException("Связь анализа с видеорядом образует цикл", nameof(downstream));
        }
        if (needsAnalysis && upstreamAnalysis?.UsesAnalysis != true) {
            throw new ArgumentException("Не подготовлен источник анализа видеоряда", nameof(upstreamAnalysis));
        }

        this.size = size;
        this.editor = editor;
        this.upstreamAnalysis = needsAnalysis ? upstreamAnalysis : null;
        this.downstream = downstream;
        this.measurements = measurements;
        this.generation = generation;
        editedGeometry = editor.GetGeometry(size);
        editingScratchBytes = editor.GetScratchBytes(size);
        upstreamAnalysisBytes = this.upstreamAnalysis?.GetResources(size).AnalysisBytes ?? 0;
        var analysisResources = downstream?.GetResources(size) ?? default;
        downstreamAnalysisBytes = analysisResources.AnalysisBytes;
        if (downstream is not null && analysisResources.Workers == 0) {
            // без вычислительных методов оценка анализа включает только свой выход
            downstreamAnalysisBytes = checked(downstreamAnalysisBytes +
                VideoEditorFrame.GetBufferBytes(GetGeometry(composition.BaseInput).OutputSize));
        }

        var sequenceResources = VideoSequenceComposite.GetResources(composition, GetGeometry);
        sequenceBytes = checked(sequenceResources.ScratchBytes + sequenceResources.FrameBytes);
        composite = new(composition, GetGeometry);
        try {
            originalReserve = measurements.Reserve(generation, VideoPipelineStage.Original, readerBytes);
            sequenceReserve = measurements.Reserve(generation, VideoPipelineStage.VideoSequence, sequenceResources.PersistentBytes);
        }
        catch {
            originalReserve?.Dispose();
            composite.Dispose();
            throw;
        }
    }

    // подготавливаем каждый запрошенный растр один раз и передаем только готовые результаты
    public ValueTask<VideoPipelineOutput?> ProcessAsync(TimedVideoInput input, VideoEditorFrame edited,
        long? startedAt, TimeSpan? editingElapsed, bool publish, CancellationToken token) {
        ObjectDisposedException.ThrowIf(disposed, this);
        token.ThrowIfCancellationRequested();
        if (input.Pixels.Size != size) {
            throw new InvalidDataException("Размер исходного кадра не соответствует принятому плану видеоряда");
        }
        long enteredAt = startedAt ?? Stopwatch.GetTimestamp();
        if (editingElapsed is { } elapsed) {
            measurements.RecordStage(generation, VideoPipelineStage.Editing, 0, elapsed);
        }

        VideoEditorFrame? ownedEdited = null;
        VideoEditorFrame? upstreamPixels = null;
        IDisposable? ownedEditedLease = null;
        IDisposable? upstreamLease = null;
        long resolveTicks = 0;
        VideoEditorFrame? analysis = null;
        IDisposable? foregroundLease = null;
        IDisposable? analysisLease = null;
        try {
            VideoForegroundFrame? foreground;
            using (measurements.Reserve(generation, VideoPipelineStage.VideoSequence, sequenceBytes)) {
                long started = Stopwatch.GetTimestamp();
                foreground = composite.Process(input, ResolveSequence, publish, token);
                measurements.RecordStage(generation, VideoPipelineStage.VideoSequence, 0,
                    Stopwatch.GetElapsedTime(started + resolveTicks));
            }
            if (foreground is null) {
                return ValueTask.FromResult<VideoPipelineOutput?>(null);
            }

            token.ThrowIfCancellationRequested();
            foregroundLease = measurements.Reserve(generation, VideoPipelineStage.VideoSequence, foreground.BufferBytes);
            if (downstream is not null) {
                VideoEditorFrame? analysisEdited = downstream.NeedsEditing ? GetEdited() : null;
                using (var stage = measurements.BeginStage(generation, VideoPipelineStage.Analysis, downstreamAnalysisBytes)) {
                    using VideoEditorFrame temporal = VideoEditorFrame.FromForeground(foreground, maskOnly: false, token);
                    analysis = downstream.Transform(input, temporal, token, analysisEdited);
                    stage.Complete();
                }
                analysisLease = measurements.Reserve(generation, VideoPipelineStage.Analysis,
                    VideoEditorFrame.GetBufferBytes(analysis.Size));
            }

            token.ThrowIfCancellationRequested();
            var ready = measurements.RecordReady(generation, input.Ordinal, enteredAt);
            VideoPipelineOutput output = new(foreground, analysis, ready, foregroundLease, analysisLease);
            analysis = null;
            foregroundLease = analysisLease = null;
            return ValueTask.FromResult<VideoPipelineOutput?>(output);
        }
        finally {
            analysis?.Dispose();
            analysisLease?.Dispose();
            foregroundLease?.Dispose();
            upstreamPixels?.Dispose();
            upstreamLease?.Dispose();
            ownedEdited?.Dispose();
            ownedEditedLease?.Dispose();
        }

        // время подготовки источника относится к своему участку, даже если запрос возник при композиции
        VideoEditorFrame ResolveSequence(VideoPipelineStage stage) {
            if (stage == VideoPipelineStage.Original) {
                return input.Pixels;
            }
            long started = Stopwatch.GetTimestamp();
            try {
                return stage switch {
                    VideoPipelineStage.Editing => GetEdited(),
                    VideoPipelineStage.Analysis => GetAnalysis(),
                    _ => throw new InvalidDataException("Неизвестный источник композиции видеоряда")
                };
            }
            finally {
                resolveTicks += Stopwatch.GetTimestamp() - started;
            }
        }

        // используем переданный строгой очередью кадр либо один раз готовим свою копию
        VideoEditorFrame GetEdited() {
            if (!editor.HasSteps) {
                return input.Pixels;
            }
            if (editingElapsed is not null) {
                return edited;
            }
            if (ownedEdited is not null) {
                return ownedEdited;
            }

            using var stage = measurements.BeginStage(generation, VideoPipelineStage.Editing, editingScratchBytes);
            ownedEditedLease = measurements.Reserve(generation, VideoPipelineStage.Editing, VideoEditorFrame.GetBufferBytes(size));
            ownedEdited = input.Pixels.Copy();
            editor.Apply(ownedEdited);
            token.ThrowIfCancellationRequested();
            stage.Complete();
            return ownedEdited;
        }

        // все маски, подложка и поток одного кадра могут заимствовать одну композицию анализа
        VideoEditorFrame GetAnalysis() {
            if (upstreamPixels is not null) {
                return upstreamPixels;
            }

            VideoForegroundInputPlan plan = upstreamAnalysis ??
                throw new InvalidDataException("Не готов анализ для источника видеоряда");
            VideoEditorFrame? analysisEdited = plan.NeedsEditing ? GetEdited() : null;
            using (var stage = measurements.BeginStage(generation, VideoPipelineStage.Analysis, upstreamAnalysisBytes)) {
                upstreamPixels = plan.Transform(input, token, analysisEdited);
                stage.Complete();
            }
            upstreamLease = measurements.Reserve(generation, VideoPipelineStage.Analysis,
                VideoEditorFrame.GetBufferBytes(upstreamPixels.Size));
            return upstreamPixels;
        }
    }

    // используем ориентацию от подтвержденного плана, включая поворот квадратного кадра
    private VideoEditorGeometry GetGeometry(VideoPipelineStage stage) => stage switch {
        VideoPipelineStage.Original => new(size, 0),
        VideoPipelineStage.Editing => editedGeometry,
        VideoPipelineStage.Analysis when upstreamAnalysis is not null => upstreamAnalysis.GetGeometry(size),
        _ => throw new ArgumentException("Нет геометрии источника видеоряда", nameof(stage))
    };

    // закрываем модели после чтения и снимаем резервы этого поколения
    public ValueTask DisposeAsync() {
        if (!disposed) {
            disposed = true;
            try {
                composite.Dispose();
            }
            finally {
                sequenceReserve?.Dispose();
                originalReserve?.Dispose();
            }
        }
        return ValueTask.CompletedTask;
    }
}
