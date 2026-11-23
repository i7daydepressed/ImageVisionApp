using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal enum VideoPipelineStart {
    Beginning,
    PreviousFrame,
    Target
}

// расчет ограничивает запас по бюджетам всех дополнительных участков до запуска декодера
internal sealed record StrictVideoPipeline(Func<IVideoStrictPipelineProcessor> Create, long PersistentBytes,
    long ScratchBytes, long SelectedFrameBytes, int Capacity,
    Func<VideoPipelineStage, long, IDisposable>? ReserveFrame = null, VideoPipelineStart Start = VideoPipelineStart.Beginning);

internal interface IVideoStrictPipelineProcessor : IAsyncDisposable {
    // берем оба кадра взаймы, для предыстории обновляем состояние без выдачи готового результата
    ValueTask<VideoPipelineOutput?> ProcessAsync(TimedVideoInput input, VideoEditorFrame edited,
        long? startedAt, TimeSpan? editingElapsed, bool publish, CancellationToken token);
}

internal sealed partial class PreviewCoordinator {
    // считаем зависимые кадры одним проходом и добавляем в очередь только полностью готовый кадр
    private async Task PrepareStrictPipelineAsync(Reading work, StrictVideoPipeline pipeline) {
        RequestData request = work.Request;
        StrictStage stage = work.Strict!;
        CancellationToken token = work.Stop.Token;
        long frameBytes = checked(request.ResultBytes + request.ScratchBytes);
        if (request.WorkerLimit == 0) {
            throw new InvalidOperationException("Бюджета буферов недостаточно для комбинированного кадра");
        }

        await ReserveAsync(request, request.ReaderBytes, limitWindow: false, token, waitForPrevious: true).ConfigureAwait(false);
        try {
            await using IVideoStrictPipelineProcessor processor = pipeline.Create();
            // модель фона получает историю, а потоку достаточно непосредственного предшественника
            VideoFrameReadRequest start = pipeline.Start switch {
                VideoPipelineStart.Beginning => new VideoFrameReadRequest(0) { Anchor = new(new(0, 1), 0, false) },
                VideoPipelineStart.PreviousFrame => request.Start with { FrameOffset = -1, SingleFrame = false },
                _ => request.Start
            };
            await using IAsyncEnumerator<TimedVideoInput> input =
                request.Source.ReadFramesAsync(start, token).GetAsyncEnumerator(token);
            long inputBytes = VideoEditorFrame.GetBufferBytes(request.Size);
            long editorScratch = request.Plan.GetScratchBytes(request.Size);
            TimedVideoInput? previous = null;
            bool reachedTarget = pipeline.Start == VideoPipelineStart.Target;
            while (true) {
                await ReserveStrictSlotAsync(work, stage, frameBytes, token).ConfigureAwait(false);
                long remaining = frameBytes;
                TimedVideoInput frame = default;
                VideoEditorFrame? edited = null;
                VideoPipelineOutput? output = null;
                IDisposable? originalLease = null;
                IDisposable? editedLease = null;
                try {
                    if (!await input.MoveNextAsync().ConfigureAwait(false)) {
                        throw new InvalidDataException("Источник завершился без подтвержденного последнего кадра");
                    }
                    frame = input.Current;
                    token.ThrowIfCancellationRequested();
                    bool matchesTarget = VideoForegroundFrame.Covers(frame.Time, frame.Until, frame.Ordinal, request.Start);
                    bool invalidStart = pipeline.Start switch {
                        VideoPipelineStart.Beginning => previous is null && frame is not { Ordinal: 0, Time.Value: 0 },
                        VideoPipelineStart.PreviousFrame when !reachedTarget =>
                            previous is null ? matchesTarget && frame.Ordinal != 0 : !matchesTarget,
                        VideoPipelineStart.Target => previous is null && !matchesTarget,
                        _ => false
                    };
                    if (!IsValidStrictInput(frame, request.Size, previous) || invalidStart) {
                        throw new InvalidDataException("Не удалось подтвердить порядок или первый кадр строгой цепочки");
                    }
                    previous = frame;
                    reachedTarget |= matchesTarget;
                    if (!reachedTarget && (frame.IsLast || request.Start.Anchor?.Ordinal == frame.Ordinal)) {
                        throw new InvalidDataException("В предыстории не найден точный кадр для строгого показа");
                    }

                    long? acceptedAt = null;
                    lock (gate) {
                        if (!IsCurrent(request) || error is not null) {
                            return;
                        }
                        if (reachedTarget) {
                            acceptedAt = Environment.TickCount64;
                            RecordStrictAdmission(frame.Ordinal, acceptedAt.Value);
                        }
                    }

                    long startedAt = Stopwatch.GetTimestamp();
                    originalLease = pipeline.ReserveFrame?.Invoke(VideoPipelineStage.Original, inputBytes);
                    VideoEditorGeometry geometry;
                    using (IDisposable? scratch = editorScratch == 0 ? null :
                        pipeline.ReserveFrame?.Invoke(VideoPipelineStage.Editing, editorScratch)) {
                        if (request.Plan.HasSteps) {
                            editedLease = pipeline.ReserveFrame?.Invoke(VideoPipelineStage.Editing, inputBytes);
                        }
                        edited = request.Plan.HasSteps ? frame.Pixels.Copy() : frame.Pixels;
                        long started = Stopwatch.GetTimestamp();
                        geometry = strictProcessor?.Invoke(request.Plan, frame.Ordinal, edited) ?? request.Plan.Apply(edited);
                        if (request.Plan.HasSteps) {
                            edited.ProcessingTime = Stopwatch.GetElapsedTime(started);
                        }
                    }
                    output = await processor.ProcessAsync(frame, edited, startedAt, edited.ProcessingTime,
                        reachedTarget, token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                    if (!reachedTarget) {
                        continue;
                    }

                    long outputBytes = CheckPipelineOutput(output, frame, pipeline);
                    long retainedBytes = request.ResultBytes - pipeline.SelectedFrameBytes + outputBytes;
                    lock (gate) {
                        if (!IsCurrent(request) || error is not null) {
                            return;
                        }
                        IDisposable? readyOriginal = originalLease;
                        IDisposable? readyEdited = editedLease;
                        ready.Enqueue(new(frame, edited, geometry,
                            () => ReleaseMeasuredStrictPipelineFrame(retainedBytes, readyOriginal, readyEdited), acceptedAt, output));
                        last = frame.Time;
                        through = frame.Until;
                        ended = frame.IsLast;
                        remaining -= retainedBytes;
                        frame = default;
                        edited = null;
                        output = null;
                        originalLease = editedLease = null;
                        if (ended || request.Start.SingleFrame) {
                            return;
                        }
                    }
                }
                finally {
                    try {
                        output?.Dispose();
                    }
                    finally {
                        if (edited is not null && !ReferenceEquals(edited, frame.Pixels)) {
                            edited.Dispose();
                        }
                        frame.Pixels?.Dispose();
                        try {
                            DisposeStrictPipelineLeases(originalLease, editedLease);
                        }
                        finally {
                            FinishStrictSlot(stage, remaining);
                        }
                    }
                }
            }
        }
        catch (Exception failure) {
            FailStrict(work, stage, failure);
        }
        finally {
            Release(request.ReaderBytes);
        }
    }

    // кадры и их учтенные байты переходят получателю вместе и освобождаются после показа или сброса
    private void ReleaseMeasuredStrictPipelineFrame(long bytes, IDisposable? original, IDisposable? edited) {
        try {
            DisposeStrictPipelineLeases(original, edited);
        }
        finally {
            Release(bytes);
        }
    }

    // освобождаем оба измеренных ресурса даже при ошибке одного освобождения
    private static void DisposeStrictPipelineLeases(IDisposable? original, IDisposable? edited) {
        try {
            edited?.Dispose();
        }
        finally {
            original?.Dispose();
        }
    }

    // проверяем идентичность результата и считаем только пиксели, которые останутся в очереди
    private static long CheckPipelineOutput(VideoPipelineOutput? output, TimedVideoInput frame, StrictVideoPipeline pipeline) {
        if (output is null) {
            throw new InvalidDataException("Нет готового результата для показанного кадра");
        }
        long bytes = output.Analysis is { } analysis ? VideoEditorFrame.GetBufferBytes(analysis.Size) : 0;
        if (pipeline.Start != VideoPipelineStart.Target && output.Foreground is { } foreground &&
            foreground.Ordinal == frame.Ordinal && foreground.Time == frame.Time && foreground.Until == frame.Until) {
            long pixels = (long)foreground.Size.Width * foreground.Size.Height;
            if (pixels <= 0 || foreground.Rgb.Length != checked(pixels * 3) ||
                foreground.Mask.Length != (foreground.OpticalFlow is null ? pixels : 0)) {
                throw new InvalidDataException("Получен неполный результат видеоряда");
            }
            if (foreground.MotionBlur is { } blur &&
                (blur.Size != foreground.Size || !blur.Bgra.IsEmpty && blur.Bgra.Length != checked(pixels * 4))) {
                throw new InvalidDataException("Получен неполный результат размытия видеоряда");
            }
            if (foreground.OpticalFlow is { } flow) {
                bool hasValidGeometry = flow.Size == foreground.Size ||
                    flow.Size.Width == foreground.Size.Height && flow.Size.Height == foreground.Size.Width;
                if (!hasValidGeometry || (flow.Bgra.IsEmpty
                    ? flow.Size != foreground.Size || flow.ArrowCount != 0
                    : flow.BufferBytes != checked(pixels * 4))) {
                    throw new InvalidDataException("Получен неполный результат оптического потока");
                }
            }
            bytes = checked(bytes + foreground.BufferBytes);
        }
        else if (pipeline.Start != VideoPipelineStart.Target || output.Foreground is not null || output.Analysis is null ||
            output.AnalysisFrame is not { } cursor || cursor.Ordinal != frame.Ordinal || cursor.Time != frame.Time ||
            cursor.AtEnd != frame.IsLast) {
            throw new InvalidDataException("Комбинированный результат относится к другому видеокадру");
        }
        if (bytes <= 0 || bytes > pipeline.SelectedFrameBytes) {
            throw new InvalidOperationException("Результат строгой цепочки не помещается в кадровый резерв");
        }
        return bytes;
    }
}
