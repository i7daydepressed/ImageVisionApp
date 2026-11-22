using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal sealed partial class PreviewCoordinator {
    private sealed class StrictStage {
        public Dictionary<long, PreparedVideoFrame> Completed { get; } = [];
        public TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Active { get; set; }
        public bool ReadingFinished { get; set; }
        public long? NextOrdinal { get; set; }
    }

    private readonly Func<VideoEditorPlan, long, VideoEditorFrame, VideoEditorGeometry>? strictProcessor;

    // в приложении используем текущий план, в проверке можем задержать один заданный номер
    public PreviewCoordinator() { }

    // позволяем передать обработчик, сохранив тот же порядок захвата, владения и выдачи
    internal PreviewCoordinator(Func<VideoEditorPlan, long, VideoEditorFrame, VideoEditorGeometry> processor) =>
        strictProcessor = processor;

    // от нуля читаем физический первый кадр, даже если следующая метка совпадает с его временем
    public long RequestStrict(IVideoMediaSource source, VideoFrameReadRequest start,
        VideoEditorPlan plan, long budgetBytes, int workers, StrictVideoPipeline? pipeline = null) {
        if (pipeline is { } combined && (combined.PersistentBytes < 0 || combined.ScratchBytes < 0 ||
            combined.SelectedFrameBytes <= 0 || combined.Capacity < 0 || start.FrameOffset != 0 ||
            start.DecodedAnchor is not null || start.Anchor is { } anchor && (anchor.Ordinal < 0 || !anchor.Time.IsKnown))) {
            throw new ArgumentException("Неверные параметры строгой комбинированной очереди", nameof(pipeline));
        }
        if (!start.AtEnd && start.PositionMilliseconds == 0 && start.Anchor is null) {
            start = start with { Anchor = new(new(0, 1000), 0, false) };
        }
        return RequestCore(source, start, plan, budgetBytes, strict: true, workers, pipeline);
    }

    // выдаем ровно голову непрерывной очереди, включая разные кадры одного времени
    public PreparedVideoFrame? TakeStrict(long expectedGeneration, long positionMilliseconds) {
        lock (gate) {
            PreparedVideoFrame? frame = TakeFirst(expectedGeneration);
            if (frame is not null) {
                position = Math.Max(position, Math.Min(positionMilliseconds, long.MaxValue - AheadMilliseconds));
            }
            return frame;
        }
    }

    // один декодер читает по порядку и отдает независимые кадры работникам с заранее занятыми буферами
    private async Task PrepareStrictAsync(Reading work) {
        RequestData request = work.Request;
        StrictStage stage = work.Strict!;
        CancellationToken token = work.Stop.Token;
        long frameBytes = checked(request.ResultBytes + request.ScratchBytes);
        if (request.WorkerLimit == 0) {
            throw new InvalidOperationException("Бюджета буферов недостаточно для строгого предпросмотра");
        }
        await ReserveAsync(request, request.ReaderBytes, limitWindow: false, token).ConfigureAwait(false);
        try {
            await using IAsyncEnumerator<TimedVideoInput> input =
                request.Source.ReadFramesAsync(request.Start, token).GetAsyncEnumerator(token);
            TimedVideoInput? previous = null;
            while (true) {
                await ReserveStrictSlotAsync(work, stage, frameBytes, token).ConfigureAwait(false);
                TimedVideoInput frame = default;
                bool handedOff = false;
                try {
                    if (!await input.MoveNextAsync().ConfigureAwait(false)) {
                        throw new InvalidDataException("Источник завершился без подтвержденного последнего кадра");
                    }
                    frame = input.Current;
                    token.ThrowIfCancellationRequested();
                    if (!IsValidStrictInput(frame, request.Size, previous) ||
                        (request.Start is { AtEnd: true, SingleFrame: false } && !frame.IsLast) ||
                        (previous is null && request.Start.Anchor is { } anchor &&
                            (frame.Ordinal != anchor.Ordinal || frame.Time.CompareTo(anchor.Time) != 0))) {
                        throw new InvalidDataException("Источник вернул строгой очереди кадр с неверной меткой или номером");
                    }
                    previous = frame;
                    long acceptedAt;
                    lock (gate) {
                        if (!IsCurrent(request) || error is not null) {
                            return;
                        }
                        stage.NextOrdinal ??= frame.Ordinal;
                        acceptedAt = Environment.TickCount64;
                        RecordStrictAdmission(frame.Ordinal, acceptedAt);
                    }

                    // выполняющийся кадр сам освобождает свой резерв, в том числе после отмены
                    TimedVideoInput owned = frame;
                    _ = Task.Run(() => ProcessStrictFrame(work, stage, owned, frameBytes, acceptedAt));
                    handedOff = true;
                    if (frame.IsLast || request.Start.SingleFrame) {
                        return;
                    }
                }
                finally {
                    if (!handedOff) {
                        frame.Pixels?.Dispose();
                        FinishStrictSlot(stage, frameBytes);
                    }
                }
            }
        }
        catch (Exception failure) {
            FailStrict(work, stage, failure);
        }
        finally {
            Release(request.ReaderBytes);
            lock (gate) {
                stage.ReadingFinished = true;
                if (stage.Active == 0) {
                    stage.Drained.TrySetResult();
                }
            }
            await stage.Drained.Task.ConfigureAwait(false);
        }
    }

    // очередь не читает следующие пиксели, пока одновременно помещаются вход, копия и рабочие буферы
    private async Task ReserveStrictSlotAsync(Reading work, StrictStage stage, long bytes, CancellationToken token) {
        RequestData request = work.Request;
        while (true) {
            token.ThrowIfCancellationRequested();
            lock (gate) {
                if (!IsCurrent(request) || error is not null) {
                    throw new OperationCanceledException(token);
                }
                VideoPresentationTime horizon = new(position + AheadMilliseconds, 1000);
                int capacity = request.Pipeline is null ? MaximumReadyFrames : request.ResumeCapacity;
                if (stage.Active < request.WorkerLimit && bytes <= request.Budget - reserved &&
                    ready.Count + stage.Completed.Count + stage.Active < capacity &&
                    (through is null || through.Value.CompareTo(horizon) < 0)) {
                    stage.Active++;
                    peakActiveWorkers = Math.Max(peakActiveWorkers, stage.Active);
                    reserved += bytes;
                    peakReserved = Math.Max(peakReserved, reserved);
                    return;
                }
            }
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }

    // переход по порядку сверяет правую границу предыдущего кадра, включая одинаковые метки
    private static bool IsValidStrictInput(TimedVideoInput frame, Avalonia.PixelSize size, TimedVideoInput? previous) =>
        frame.Pixels is not null && frame.Pixels.Size == size && frame.Ordinal >= 0 && frame.Time.IsKnown &&
        (frame.Until is not { } boundary || boundary.IsKnown && boundary.CompareTo(frame.Time) >= 0) &&
        (previous is not { } prior || frame.Ordinal == checked(prior.Ordinal + 1) &&
            prior.Until is { } next && frame.Time.CompareTo(next) == 0);

    // готовый поздний кадр занимает свой резерв, пока не заполнится пропуск перед ним
    private void ProcessStrictFrame(Reading work, StrictStage stage, TimedVideoInput frame, long remaining, long acceptedAt) {
        RequestData request = work.Request;
        VideoEditorFrame? edited = null;
        try {
            work.Stop.Token.ThrowIfCancellationRequested();
            edited = request.Plan.HasSteps ? frame.Pixels.Copy() : frame.Pixels;
            long started = Stopwatch.GetTimestamp();
            VideoEditorGeometry geometry = strictProcessor?.Invoke(request.Plan, frame.Ordinal, edited) ?? request.Plan.Apply(edited);
            if (request.Plan.HasSteps) {
                edited.ProcessingTime = Stopwatch.GetElapsedTime(started);
            }
            lock (gate) {
                if (!IsCurrent(request) || error is not null) {
                    return;
                }
                stage.Completed.Add(frame.Ordinal, new(frame, edited, geometry, () => Release(request.ResultBytes), acceptedAt));
                remaining -= request.ResultBytes;
                frame = default;
                edited = null;
                while (stage.NextOrdinal is { } number && stage.Completed.Remove(number, out PreparedVideoFrame? completed)) {
                    ready.Enqueue(completed);
                    last = completed.Time;
                    through = completed.Until;
                    ended = completed.Until is null;
                    stage.NextOrdinal = ended ? null : checked(number + 1);
                }
            }
        }
        catch (Exception failure) {
            FailStrict(work, stage, failure);
        }
        finally {
            if (edited is not null && !ReferenceEquals(edited, frame.Pixels)) {
                edited.Dispose();
            }
            frame.Pixels?.Dispose();
            FinishStrictSlot(stage, remaining);
        }
    }

    // удерживаем память активной работы до фактического освобождения ее пикселей
    private void FinishStrictSlot(StrictStage stage, long bytes) {
        lock (gate) {
            stage.Active--;
            Release(bytes);
            if (stage.ReadingFinished && stage.Active == 0) {
                stage.Drained.TrySetResult();
            }
        }
    }

    // остановившийся источник или фильтр закрывает текущее поколение без поздних кадров
    private void FailStrict(Reading work, StrictStage stage, Exception failure) {
        lock (gate) {
            if (!IsCurrent(work.Request) || error is not null) {
                return;
            }
            error = failure;
            ended = false;
            ClearReady();
            ClearStrictCompleted(stage);
            work.Cancellation ??= work.Stop.CancelAsync();
        }
    }

    // сбрасываем отложенные готовые пары и возвращаем только принадлежащие им резервы
    private static void ClearStrictCompleted(StrictStage? stage) {
        if (stage is null) {
            return;
        }
        foreach (PreparedVideoFrame frame in stage.Completed.Values) {
            frame.Dispose();
        }
        stage.Completed.Clear();
    }
}
