using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

// вычисляет композицию анализа по подтвержденному кадру без временной модели
internal sealed class VideoAnalysisFrameProcessor : IVideoStrictPipelineProcessor {
    private readonly VideoSequenceAnalysisPlan plan;
    private readonly VideoPipelineMeasurements measurements;
    private readonly long generation;
    private readonly long analysisBytes;
    private readonly long editingScratch;
    private readonly IDisposable readerReserve;

    // проверяем план перед проходом, захватом исходного кадра владеет вызывающий код
    public VideoAnalysisFrameProcessor(VideoSequenceAnalysisPlan plan, PixelSize originalSize,
        VideoPipelineMeasurements measurements, long generation, long readerBytes) {
        if (plan.RequiresTemporal) {
            throw new ArgumentException("Для этого плана анализа требуется видеоряд", nameof(plan));
        }
        this.plan = plan;
        this.measurements = measurements;
        this.generation = generation;
        analysisBytes = plan.GetAnalysisWorkBytes(originalSize);
        editingScratch = plan.NeedsEditing ? plan.Editor.GetScratchBytes(originalSize) : 0;
        readerReserve = measurements.Reserve(generation, VideoPipelineStage.Original, readerBytes);
    }

    // в строгой очереди берем готовый отредактированный растр, в живой готовим его один раз при необходимости
    public ValueTask<VideoPipelineOutput?> ProcessAsync(TimedVideoInput input, VideoEditorFrame edited,
        long? startedAt, TimeSpan? editingElapsed, bool publish, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if (!publish) {
            return ValueTask.FromResult<VideoPipelineOutput?>(null);
        }
        if (startedAt is not null && plan.NeedsEditing && plan.Editor.HasSteps && ReferenceEquals(edited, input.Pixels)) {
            throw new InvalidOperationException("Не получен подготовленный растр редактора для анализа");
        }

        long enteredAt = startedAt ?? Stopwatch.GetTimestamp();
        VideoEditorFrame? ownedEdited = null;
        IDisposable? editedReserve = null;
        try {
            if (startedAt is null && plan.NeedsEditing && plan.Editor.HasSteps) {
                long bytes = VideoEditorFrame.GetBufferBytes(input.Pixels.Size);
                editedReserve = measurements.Reserve(generation, VideoPipelineStage.Editing, bytes);
                using var stage = measurements.BeginStage(generation, VideoPipelineStage.Editing, editingScratch);
                ownedEdited = input.Pixels.Copy();
                plan.Editor.Apply(ownedEdited);
                token.ThrowIfCancellationRequested();
                stage.Complete();
            }
            else if (plan.NeedsEditing && editingElapsed is { } elapsed) {
                measurements.RecordStage(generation, VideoPipelineStage.Editing, 0, elapsed);
            }

            return ValueTask.FromResult<VideoPipelineOutput?>(Compose(input, ownedEdited ?? edited, enteredAt, token));
        }
        finally {
            ownedEdited?.Dispose();
            editedReserve?.Dispose();
        }
    }

    // после окончания расчета удерживаем только итоговые пиксели с меткой того же кадра
    private VideoPipelineOutput Compose(TimedVideoInput input, VideoEditorFrame edited, long enteredAt,
        CancellationToken token) {
        VideoEditorFrame? result = null;
        IDisposable? analysisLease = null;
        try {
            using (var stage = measurements.BeginStage(generation, VideoPipelineStage.Analysis, analysisBytes)) {
                result = plan.Transform(input, temporal: null, token, preparedEdited: edited);
                token.ThrowIfCancellationRequested();
                stage.Complete();
            }
            analysisLease = measurements.Reserve(generation, VideoPipelineStage.Analysis,
                VideoEditorFrame.GetBufferBytes(result.Size));
            var ready = measurements.RecordReady(generation, input.Ordinal, enteredAt);
            return new(null, result, ready, null, analysisLease, new(input.Time, input.Ordinal, input.IsLast));
        }
        catch {
            result?.Dispose();
            analysisLease?.Dispose();
            throw;
        }
    }

    // освобождаем резерв исходного декодера после закрытия прохода
    public ValueTask DisposeAsync() {
        readerReserve.Dispose();
        return ValueTask.CompletedTask;
    }
}
