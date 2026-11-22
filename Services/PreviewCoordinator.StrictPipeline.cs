using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

// расчет ограничивает запас по бюджетам всех дополнительных участков до запуска декодера
internal sealed record StrictVideoPipeline(Func<IVideoStrictPipelineProcessor> Create, long PersistentBytes,
    long ScratchBytes, long SelectedFrameBytes, int Capacity, Func<VideoPipelineStage, long, IDisposable>? ReserveFrame = null);

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
            VideoFrameReadRequest history = new(0) { Anchor = new(new(0, 1), 0, false) };
            await using IAsyncEnumerator<TimedVideoInput> input =
                request.Source.ReadFramesAsync(history, token).GetAsyncEnumerator(token);
            long inputBytes = VideoEditorFrame.GetBufferBytes(request.Size);
            long editorScratch = request.Plan.GetScratchBytes(request.Size);
            TimedVideoInput? previous = null;
            bool reachedTarget = false;
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
                    if (!IsValidStrictInput(frame, request.Size, previous) ||
                        previous is null && (frame.Ordinal != 0 || frame.Time.Value != 0)) {
                        throw new InvalidDataException("Не удалось подтвердить порядок или начало предыстории видеоряда");
                    }
                    previous = frame;
                    reachedTarget = reachedTarget ||
                        VideoForegroundFrame.Covers(frame.Time, frame.Until, frame.Ordinal, request.Start);
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

                    long outputBytes = CheckPipelineOutput(output, frame, pipeline.SelectedFrameBytes);
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

    // сверяем метки итогового кадра и возвращаем резерв только тех пикселей, которые останутся в очереди
    private static long CheckPipelineOutput(VideoPipelineOutput? output, TimedVideoInput frame, long reservedBytes) {
        if (output is null || output.Foreground.Ordinal != frame.Ordinal || output.Foreground.Time != frame.Time ||
            output.Foreground.Until != frame.Until) {
            throw new InvalidDataException("Комбинированный результат относится к другому видеокадру");
        }
        VideoForegroundFrame foreground = output.Foreground;
        long pixels = (long)foreground.Size.Width * foreground.Size.Height;
        if (pixels <= 0 || foreground.Rgb.Length != checked(pixels * 3) || foreground.Mask.Length != pixels) {
            throw new InvalidDataException("Получен неполный результат видеоряда");
        }
        long bytes = checked((long)foreground.Rgb.Length + foreground.Mask.Length +
            (output.Analysis is { } analysis ? VideoEditorFrame.GetBufferBytes(analysis.Size) : 0));
        if (bytes > reservedBytes) {
            throw new InvalidOperationException("Комбинированный результат превышает предельный размер буфера");
        }
        return bytes;
    }
}
