using System;

namespace ImageVisionApp.Services;

internal sealed class VideoPreviewMeasurements(int windowMs = 2500) {
    internal readonly record struct Window(
        long DurationMs, int Input, int Output, int? IntervalP95Ms, int? LatencyP95Ms,
        long MaxInputGapMs, long ReplacedPending, long Superseded, long BudgetWaiting) {
        public double InputRate => 1000.0 * Input / DurationMs;
        public double OutputRate => 1000.0 * Output / DurationMs;
        public long Dropped => ReplacedPending + Superseded;
    }

    private const int MaxSamples = 192;
    private readonly int[] intervals = new int[MaxSamples];
    private readonly int[] latencies = new int[MaxSamples];
    private long start;
    private long lastInputAt = -1;
    private long lastOutputAt = -1;
    private long lastInputFrame = -1;
    private long lastOutputFrame = -1;
    private long maxInputGap;
    private int input;
    private int output;
    private int intervalCount;
    private int latencyCount;

    public long Id { get; private set; }
    public long InputGroup { get; private set; }
    public bool IsActive { get; private set; }
    public Window? Last { get; private set; }

    // начинаем новое измерение после короткого переходного интервала для старых работ
    public void Start(long now, int settleMs) {
        Stop(keepLast: false);
        start = now + settleMs;
        IsActive = true;
    }

    // закрываем поколение, чтобы поздний результат не попал в следующее окно
    public void Stop(bool keepLast) {
        Id++;
        InputGroup++;
        IsActive = false;
        if (!keepLast) {
            Last = null;
        }
        lastInputAt = lastOutputAt = lastInputFrame = lastOutputFrame = -1;
        ClearSamples();
    }

    // отмечаем один новый живой кадр после копирования его из плеера
    public bool RecordInput(long frame, long now) {
        if (!IsActive || now < start || frame <= lastInputFrame) {
            return false;
        }
        input++;
        maxInputGap = Math.Max(maxInputGap, now - (lastInputAt >= 0 ? lastInputAt : start));
        lastInputAt = now;
        lastInputFrame = frame;
        return true;
    }

    // измеряем задержку только для успешно опубликованного кадра текущего измерения
    public void RecordOutput(long observation, long frame, long offeredAt, long now) {
        if (!IsActive || observation != Id || frame <= lastOutputFrame || now < offeredAt) {
            return;
        }
        output++;
        if (lastOutputAt >= 0) {
            AddSample(intervals, ref intervalCount, now - lastOutputAt);
        }
        AddSample(latencies, ref latencyCount, now - offeredAt);
        lastOutputAt = now;
        lastOutputFrame = frame;
    }

    // проверяем границу до получения счетчиков группы под блокировкой исполнителя
    public bool IsDue(long now) => IsActive && now - start >= windowMs;

    // завершаем интервал по монотонному времени и оставляем только один снимок результатов
    public bool TryComplete(long now, VideoPreviewExecutorMetrics metrics, out Window window) {
        window = default;
        if (!IsDue(now)) {
            return false;
        }
        long gap = Math.Max(maxInputGap, now - Math.Max(start, lastInputAt));
        window = new Window(now - start, input, output, P95(intervals, intervalCount), P95(latencies, latencyCount),
            gap, metrics.ReplacedPending, metrics.Superseded, metrics.BudgetWaiting);
        Last = window;
        start = now;
        InputGroup++;
        ClearSamples();
        return true;
    }

    // освобождаем логические границы выборки, сами маленькие массивы переиспользуем
    private void ClearSamples() {
        input = output = intervalCount = latencyCount = 0;
        maxInputGap = 0;
    }

    // при неожиданно большой частоте не наращиваем массив и скрываем неполный процентиль
    private static void AddSample(int[] samples, ref int count, long value) {
        if (count < samples.Length) {
            samples[count] = (int)Math.Clamp(value, 0, int.MaxValue);
        }
        count++;
    }

    // берем ближайший верхний ранг из выборки фактически измеренных значений
    private static int? P95(int[] values, int count) {
        if (count <= 0 || count > values.Length) {
            return null;
        }
        Array.Sort(values, 0, count);
        return values[(int)Math.Ceiling(.95 * count) - 1];
    }
}
