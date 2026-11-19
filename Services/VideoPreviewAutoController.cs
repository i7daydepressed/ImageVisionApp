using System;

namespace ImageVisionApp.Services;

internal sealed class VideoPreviewAutoController(int logicalCpus) {
    private const int CooldownMs = 5000;
    private int accepted = Math.Clamp(logicalCpus, 1, 2);
    private int rejected;
    private long nextTrialAt;
    private double measuredInputRate;
    private VideoPreviewMeasurements.Window? previous;
    private VideoPreviewMeasurements.Window? baseline;
    private int trialWindows;
    private bool trialPassed;

    public int Workers { get; private set; } = Math.Clamp(logicalCpus, 1, 2);
    public bool IsTrial => baseline is not null;

    // новый документ, план или настройка получают исходный рабочий предел
    public void Reset() {
        accepted = Math.Clamp(logicalCpus, 1, 2);
        rejected = 0;
        measuredInputRate = 0;
        nextTrialAt = 0;
        CancelTrial();
    }

    // пауза и перемотка прерывают сравнение и возвращают последний принятый предел
    public void CancelTrial() {
        Workers = accepted;
        previous = baseline = null;
    }

    // ограничиваем рабочий предел вместимостью кадра, не меняя пользовательский бюджет
    public void FitCapacity(int capacity) {
        int limit = Math.Clamp(capacity, 1, Math.Clamp(logicalCpus, 1, 4));
        if (Workers > limit || accepted > limit) {
            accepted = Math.Min(accepted, limit);
            CancelTrial();
        }
    }

    // выбираем пробу после двух сопоставимых окон и оцениваем ее еще в двух окнах
    public void Observe(VideoPreviewMeasurements.Window window, int capacity, long now) {
        FitCapacity(capacity);
        if (!IsUsable(window)) {
            if (baseline is { } activeReference) {
                if (HasStableInput(window) && Comparable(activeReference.InputRate, window.InputRate)) {
                    rejected |= 1 << Workers;
                }
                nextTrialAt = now + CooldownMs;
            }
            CancelTrial();
            return;
        }
        if (baseline is { } reference) {
            EvaluateTrial(reference, window, now);
            return;
        }
        if (previous is not { } earlier || !Comparable(earlier.InputRate, window.InputRate)) {
            previous = window;
            return;
        }
        previous = window;
        if (measuredInputRate > 0 && !Comparable(measuredInputRate, window.InputRate)) {
            rejected = 0;
        }
        measuredInputRate = window.InputRate;
        if (now < nextTrialAt) {
            return;
        }

        bool enoughEarlier = earlier.OutputRate >= .9 * earlier.InputRate;
        bool enoughNow = window.OutputRate >= .9 * window.InputRate;
        int candidate = enoughEarlier && enoughNow ? Workers - 1 :
            !enoughEarlier && !enoughNow ? Workers + 1 : Workers;
        if (candidate == Workers || candidate < 1 || candidate > Math.Min(capacity, Math.Min(4, logicalCpus)) ||
            (rejected & (1 << candidate)) != 0) {
            return;
        }

        baseline = window;
        trialWindows = 0;
        trialPassed = true;
        Workers = candidate;
    }

    // откатываем несопоставимый вход, а принятый предел меняем только после двух успешных окон
    private void EvaluateTrial(VideoPreviewMeasurements.Window reference, VideoPreviewMeasurements.Window sample, long now) {
        if (!Comparable(reference.InputRate, sample.InputRate)) {
            CancelTrial();
            nextTrialAt = now + CooldownMs;
            previous = sample;
            return;
        }

        double delay = reference.LatencyP95Ms!.Value;
        double interval = reference.IntervalP95Ms!.Value;
        double output = reference.OutputRate;
        bool acceptableDelay = sample.LatencyP95Ms <= Math.Max(delay + 35, 1.2 * delay);
        double coverage = (double)sample.Output / sample.Input;
        double referenceCoverage = (double)reference.Output / reference.Input;
        bool enough = Workers < accepted
            ? coverage >= .9 && sample.OutputRate >= .95 * output &&
              sample.IntervalP95Ms <= Math.Max(interval + 8, 1.15 * interval)
            : (coverage > referenceCoverage && sample.OutputRate >= Math.Max(output + 1, 1.10 * output)) ||
              (coverage >= referenceCoverage && sample.OutputRate >= .96 * output &&
               sample.IntervalP95Ms <= .85 * interval);
        trialPassed &= acceptableDelay && enough;
        if (++trialWindows != 2) {
            return;
        }

        if (trialPassed) {
            accepted = Workers;
        }
        else {
            rejected |= 1 << Workers;
        }
        nextTrialAt = now + CooldownMs;
        CancelTrial();
    }

    // отсутствие входа, долгая пауза между кадрами и неполные процентили не дают сравнивать окна
    private static bool IsUsable(VideoPreviewMeasurements.Window window) =>
        HasStableInput(window) && window.Output >= 4 &&
        window.IntervalP95Ms.HasValue && window.LatencyP95Ms.HasValue;

    // отдельно проверяем вход, чтобы отличить неудачную пробу от прерывания потока
    private static bool HasStableInput(VideoPreviewMeasurements.Window window) =>
        window.Input >= 25 && window.MaxInputGapMs <= 150;

    // заметно изменившаяся подача означает другие условия для сравнения
    private static bool Comparable(double first, double second) =>
        Math.Abs(first - second) <= .15 * Math.Max(first, second);
}
