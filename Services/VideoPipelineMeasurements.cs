using System;
using System.Diagnostics;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal sealed class VideoPipelineMeasurements {
    internal readonly record struct ReadyStamp(long Generation, long Ordinal, long StartedAt, long ReadyAt);
    internal readonly record struct StageSnapshot(long CompletedFrames, TimeSpan? LastProcessing,
        TimeSpan? MeanProcessing, long ReservedBytes, long PeakReservedBytes);
    internal readonly record struct OutputSnapshot(long ReadyFrames, long PublishedFrames, long? LastReadyOrdinal,
        long? LastPublishedOrdinal, TimeSpan? LastReadyLatency, TimeSpan? LastPresentationWait,
        TimeSpan? LastPresentationLatency, TimeSpan AutomaticWait);
    internal readonly record struct Snapshot(StageSnapshot Editing, StageSnapshot Analysis, StageSnapshot VideoSequence,
        OutputSnapshot Output, long ReservedBytes, long PeakReservedBytes);

    private readonly object gate = new();
    private readonly StageCounter[] stages = [new(), new(), new(), new()];
    private long generation;
    private bool active;
    private long reservedBytes;
    private long peakReservedBytes;
    private OutputSnapshot output;

    // Сбрасываем показания при изменении документа, маршрута или позиции нового прохода.
    public long StartGeneration() {
        lock (gate) {
            Reset(active: true);
            return generation;
        }
    }

    // Запрещаем поздним работам писать показания после ухода из измеряемой цепочки.
    public void Clear() {
        lock (gate) {
            Reset(active: false);
        }
    }

    // Резервируем рабочие буферы и запускаем часы обработки одного кадра.
    public StageWork BeginStage(long expectedGeneration, VideoPipelineStage stage, long bytes) =>
        OpenReservation(expectedGeneration, stage, bytes, measureProcessing: true);

    // Отдельные резервы пригодны для буферов, которые живут дольше обработки кадра.
    public IDisposable Reserve(long expectedGeneration, VideoPipelineStage stage, long bytes) =>
        OpenReservation(expectedGeneration, stage, bytes, measureProcessing: false);

    // Принимаем длительность от исполнителя. Указанные буферы должны ещё принадлежать этому участку.
    public void RecordStage(long expectedGeneration, VideoPipelineStage stage, long accountedBytes, TimeSpan elapsed) {
        if (elapsed < TimeSpan.Zero) {
            return;
        }
        using StageWork reservation = OpenReservation(expectedGeneration, stage, accountedBytes, measureProcessing: false);
        Complete(reservation, elapsed);
    }

    // Передаем отметку вместе с готовым кадром, чтобы очередь могла измерить именно его выдачу.
    public ReadyStamp? RecordReady(long expectedGeneration, long ordinal, long startedAt) {
        long now = Stopwatch.GetTimestamp();
        lock (gate) {
            if (!IsCurrent(expectedGeneration) || ordinal < 0 || startedAt < 0 || now < startedAt) {
                return null;
            }
            output = output with {
                ReadyFrames = output.ReadyFrames + 1,
                LastReadyOrdinal = ordinal,
                LastReadyLatency = Stopwatch.GetElapsedTime(startedAt, now)
            };
            return new(expectedGeneration, ordinal, startedAt, now);
        }
    }

    // Считаем предъявление после фактической публикации и оставляем задержку готового кадра отдельно.
    public void RecordPublished(ReadyStamp sample) {
        long now = Stopwatch.GetTimestamp();
        lock (gate) {
            if (!IsCurrent(sample.Generation) || sample.ReadyAt > now || sample.ReadyAt < sample.StartedAt ||
                output.LastPublishedOrdinal == sample.Ordinal) {
                return;
            }
            output = output with {
                PublishedFrames = output.PublishedFrames + 1,
                LastPublishedOrdinal = sample.Ordinal,
                LastPresentationWait = Stopwatch.GetElapsedTime(sample.ReadyAt, now),
                LastPresentationLatency = Stopwatch.GetElapsedTime(sample.StartedAt, now)
            };
        }
    }

    // Суммируем только интервалы, в которых координатор удерживал воспроизведение.
    public void RecordAutomaticWait(long expectedGeneration, TimeSpan elapsed) {
        lock (gate) {
            if (IsCurrent(expectedGeneration) && elapsed > TimeSpan.Zero) {
                output = output with { AutomaticWait = output.AutomaticWait + elapsed };
            }
        }
    }

    // Считываем все участки и общий резерв под одной блокировкой.
    public Snapshot? Read() {
        lock (gate) {
            return active ? new(stages[1].Read(), stages[2].Read(), stages[3].Read(),
                output, reservedBytes, peakReservedBytes) : null;
        }
    }

    private StageWork OpenReservation(long expectedGeneration, VideoPipelineStage stage, long bytes, bool measureProcessing) {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);
        int index = stage switch {
            VideoPipelineStage.Original => 0,
            VideoPipelineStage.Editing => 1,
            VideoPipelineStage.Analysis => 2,
            VideoPipelineStage.VideoSequence => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(stage))
        };
        lock (gate) {
            if (!IsCurrent(expectedGeneration)) {
                return StageWork.Inactive;
            }
            var counter = stages[index];
            long reserved = checked(reservedBytes + bytes);
            counter.ReservedBytes += bytes;
            counter.PeakReservedBytes = Math.Max(counter.PeakReservedBytes, counter.ReservedBytes);
            reservedBytes = reserved;
            peakReservedBytes = Math.Max(peakReservedBytes, reserved);
            return new(this, expectedGeneration, index, bytes, measureProcessing ? Stopwatch.GetTimestamp() : null);
        }
    }

    private void Complete(StageWork work, TimeSpan elapsed) {
        lock (gate) {
            if (!work.Closed && !work.Completed && IsCurrent(work.Generation)) {
                work.Completed = true;
                var counter = stages[work.StageIndex];
                counter.CompletedFrames++;
                counter.LastProcessing = elapsed;
                counter.TotalProcessing += elapsed;
            }
        }
    }

    private void Release(StageWork work) {
        lock (gate) {
            if (work.Closed) {
                return;
            }
            work.Closed = true;
            if (IsCurrent(work.Generation)) {
                stages[work.StageIndex].ReservedBytes -= work.Bytes;
                reservedBytes -= work.Bytes;
            }
        }
    }

    private bool IsCurrent(long expectedGeneration) => active && expectedGeneration == generation;

    private void Reset(bool active) {
        generation++;
        this.active = active;
        reservedBytes = peakReservedBytes = 0;
        output = default;
        foreach (StageCounter counter in stages) {
            counter.Reset();
        }
    }

    private sealed class StageCounter {
        public long CompletedFrames;
        public TimeSpan? LastProcessing;
        public TimeSpan TotalProcessing;
        public long ReservedBytes;
        public long PeakReservedBytes;

        public StageSnapshot Read() => new(CompletedFrames, LastProcessing,
            CompletedFrames == 0 ? null : TimeSpan.FromTicks(TotalProcessing.Ticks / CompletedFrames),
            ReservedBytes, PeakReservedBytes);

        public void Reset() {
            CompletedFrames = ReservedBytes = PeakReservedBytes = 0;
            LastProcessing = null;
            TotalProcessing = TimeSpan.Zero;
        }
    }

    internal sealed class StageWork(VideoPipelineMeasurements? owner, long generation, int stageIndex, long bytes,
        long? startedAt) : IDisposable {
        internal static readonly StageWork Inactive = new(null, 0, 0, 0, null);
        internal long Generation { get; } = generation;
        internal int StageIndex { get; } = stageIndex;
        internal long Bytes { get; } = bytes;
        internal long? StartedAt { get; } = startedAt;
        internal bool Closed { get; set; }
        internal bool Completed { get; set; }

        public void Complete() {
            if (owner is not null && StartedAt is long startedAt) {
                owner.Complete(this, Stopwatch.GetElapsedTime(startedAt));
            }
        }
        public void Dispose() => owner?.Release(this);
    }
}
