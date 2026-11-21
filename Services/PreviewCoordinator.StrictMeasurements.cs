using System;

namespace ImageVisionApp.Services;

internal sealed partial class PreviewCoordinator {
    public const int MinimumStrictRateWindowMs = 100;

    private readonly VideoPreviewMeasurements strictMeasurements = new(sampleCapacity: MaximumReadyFrames);
    private bool strictRequest;
    private bool strictMeasuring;
    private VideoPreviewMeasurements.Window? strictLastOutputWindow;
    private long strictInputTotal;
    private long strictOutputTotal;
    private long strictLastPublished = -1;
    private long? strictWaitStart;
    private long strictWaitMilliseconds;

    // начинаем окно воспроизведения без промежутка между ручной паузой и очередной выдачей
    public void StartStrictMeasurement(long expectedGeneration) {
        lock (gate) {
            if (IsStrictMeasurementCurrent(expectedGeneration)) {
                long now = Environment.TickCount64;
                CompleteStrictWindow(now, ending: true);
                strictMeasurements.Start(now, settleMs: 0, keepLast: true);
                strictMeasuring = true;
            }
        }
    }

    // сохраняем последнюю выборку и останавливаем отсчет при пользовательской паузе или конце
    public void StopStrictMeasurement(long expectedGeneration) {
        lock (gate) {
            if (IsStrictMeasurementCurrent(expectedGeneration)) {
                long now = Environment.TickCount64;
                CompleteStrictWindow(now, ending: true);
                strictMeasurements.Stop(keepLast: true);
                strictMeasuring = false;
                CloseStrictWait(now);
            }
        }
    }

    // докладываем прием кадра после проверки источника и до старта обработчика под той же блокировкой
    private void RecordStrictAdmission(long ordinal, long now) {
        strictInputTotal++;
        if (strictMeasuring) {
            if (!strictMeasurements.IsActive) {
                strictMeasurements.Start(now, settleMs: 0, keepLast: true);
            }
            CompleteStrictWindow(now);
            strictMeasurements.RecordInput(ordinal, now);
        }
    }

    // только после копирования обеих картинок подставляем настоящее время выдачи
    public void RecordStrictPublication(long expectedGeneration, PreparedVideoFrame frame) {
        lock (gate) {
            if (!IsStrictMeasurementCurrent(expectedGeneration) || frame.Ordinal <= strictLastPublished ||
                frame.AcceptedAt is not { } acceptedAt) {
                return;
            }
            strictLastPublished = frame.Ordinal;
            strictOutputTotal++;
            if (strictMeasuring) {
                long now = Environment.TickCount64;
                CompleteStrictWindow(now);
                strictMeasurements.RecordOutput(strictMeasurements.Id, frame.Ordinal, acceptedAt, now);
            }
        }
    }

    // измеряем время остановки строгой очереди до готовности запаса или команды пользователя
    public void SetStrictWaiting(long expectedGeneration, bool waiting) {
        lock (gate) {
            if (!IsStrictMeasurementCurrent(expectedGeneration)) {
                return;
            }
            long now = Environment.TickCount64;
            if (waiting && strictMeasuring) {
                strictWaitStart ??= now;
            }
            else {
                CloseStrictWait(now);
            }
        }
    }

    // возвращаем законченное окно и итоги подтвержденного запроса без смешивания поколений
    public (VideoPreviewMeasurements.Window? Window, long InputTotal, long OutputTotal, long AutomaticWaitMs)
        ReadStrictMeasurements(long expectedGeneration) {
        lock (gate) {
            if (!IsStrictMeasurementCurrent(expectedGeneration)) {
                return default;
            }
            long now = Environment.TickCount64;
            CompleteStrictWindow(now);
            VideoPreviewMeasurements.Window? window = strictMeasuring ? strictMeasurements.Last
                : strictLastOutputWindow ?? strictMeasurements.Last;
            return (window, strictInputTotal, strictOutputTotal,
                strictWaitMilliseconds + (strictWaitStart is { } start ? now - start : 0));
        }
    }

    // короткий хвост оставляет предыдущее пригодное окно, а первый короткий ролик сохраняет свои счетчики
    private void CompleteStrictWindow(long now, bool ending = false) {
        if (ending) {
            strictMeasurements.CompletePartial(now);
        }
        else if (!strictMeasurements.TryComplete(now, default, out _)) {
            return;
        }
        if (strictMeasurements.Last is { Output: > 0 } completed &&
            (completed is { Output: >= 2, DurationMs: >= MinimumStrictRateWindowMs } ||
             strictLastOutputWindow is not { Output: >= 2, DurationMs: >= MinimumStrictRateWindowMs })) {
            strictLastOutputWindow = completed;
        }
    }

    // отмечаем очередной завершившийся период автоматического ожидания
    private void CloseStrictWait(long now) {
        if (strictWaitStart is { } start) {
            strictWaitMilliseconds += now - start;
            strictWaitStart = null;
        }
    }

    // свой запрос, включая паузу и EOF, отличается от отмененного запроса и других режимов
    private bool IsStrictMeasurementCurrent(long expectedGeneration) =>
        !disposed && strictRequest && generation == expectedGeneration;

    // сбрасываем счетчики до перехода к новому документу, способу показа или позиции
    private void ResetStrictMeasurements() {
        strictMeasuring = strictRequest = false;
        strictMeasurements.Stop(keepLast: false);
        strictLastOutputWindow = null;
        strictInputTotal = strictOutputTotal = strictWaitMilliseconds = 0;
        strictLastPublished = -1;
        strictWaitStart = null;
    }
}
