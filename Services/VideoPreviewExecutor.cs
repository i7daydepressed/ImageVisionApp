using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Avalonia;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal readonly record struct VideoPreviewFrameKey(MediaDocument Document, long Source, long Plan, long Frame) {
    public long Observation { get; init; }
    public long OfferedAt { get; init; }
    public long PauseObservation { get; init; }
    public long LiveInput { get; init; }
    public long InputGroup { get; init; }
}
internal readonly record struct VideoPreviewExecutorMetrics(
    int Active, int Pending, int Ready, long ReservedBytes, int PeakActive, long PeakReservedBytes,
    long ReplacedPending, long Superseded, long BudgetWaiting) {
    public long ChainDropped { get; init; }
    public long BudgetPeakReservedBytes { get; init; }
}

internal enum VideoPreviewAdmission { Accepted, Deferred, TooLarge, CaptureFailed, Stopped }

// обрабатывает принятые кадры отдельно от декодера, UI получает один свежий результат
internal sealed class VideoPreviewExecutor(
    VideoPreviewSettings settings,
    Action<Action> postToUi,
    Action<VideoPreviewFrameKey, VideoEditorFrame?, VideoEditorGeometry, Exception?> publish,
    Action capacityAvailable) : IDisposable {
    private sealed record Work(
        long Generation, long ChainRevision, VideoPreviewFrameKey Key, VideoEditorFrame Frame, long Bytes, long Scratch,
        Func<VideoEditorFrame, VideoEditorGeometry> Process) {
        public bool OvertakenByOutput { get; set; }
    }

    private sealed record Result(
        VideoPreviewFrameKey Key, VideoEditorFrame Frame, long Bytes, VideoEditorGeometry Geometry);

    private readonly object gate = new();
    private readonly Dictionary<(long Generation, long Frame), Work> active = [];
    private VideoPreviewSettings limits = settings;
    private Work? pending;
    private VideoPreviewFrameKey? deferredInput;
    private Result? ready;
    private (VideoPreviewFrameKey Key, Exception Error)? failure;
    private long generation;
    private long lastDelivered = -1;
    private long lastReportedFailure = -1;
    private long reservedBytes;
    private int peakActive;
    private long peakReservedBytes;
    private long budgetPeakReservedBytes;
    private long liveInput;
    private long inputGroup;
    private long chainRevision;
    private long chainDropped;
    private long replacedPending;
    private long superseded;
    private long budgetWaiting;
    private long lastBudgetWaitFrame = -1;
    private bool capturing;
    private bool notificationPosted;
    private bool capacityChanged;
    private bool stopped;

    public VideoPreviewExecutorMetrics Metrics {
        get {
            lock (gate) {
                return ReadMetrics();
            }
        }
    }

    // резервируем вход до его копирования, заменяем только еще не начатую заявку
    public VideoPreviewAdmission TrySubmit(VideoPreviewFrameKey key, PixelSize size, long scratch,
        Func<VideoEditorFrame> capture, Func<VideoEditorFrame, VideoEditorGeometry> process) {
        long bytes = VideoEditorFrame.GetBufferBytes(size);
        long requestedGeneration;
        lock (gate) {
            if (stopped) {
                return VideoPreviewAdmission.Stopped;
            }
            requestedGeneration = generation;
            if (key.Frame <= lastDelivered || ready?.Key.Frame >= key.Frame || pending?.Key.Frame >= key.Frame ||
                active.ContainsKey((generation, key.Frame))) {
                return VideoPreviewAdmission.Accepted;
            }

            if (deferredInput is { } deferred && key.Frame >= deferred.Frame) {
                deferredInput = null;
                if (IsLiveReplacement(deferred, key)) {
                    CountLiveDrop(deferred, waiting: true);
                }
            }
            if (pending is { } previous && IsLiveReplacement(previous.Key, key)) {
                CountLiveDrop(previous.Key, waiting: true);
            }
            DropPending();
            if (bytes + scratch > limits.BudgetBytes) {
                return VideoPreviewAdmission.TooLarge;
            }
            if (reservedBytes + bytes > limits.BudgetBytes) {
                CountBudgetWait(key);
                return DeferInput(key);
            }
            if (capturing) {
                return DeferInput(key);
            }
            capturing = true;
            Reserve(bytes);
        }

        VideoEditorFrame? frame = null;
        try {
            frame = capture();
            if (frame.Size != size) {
                throw new InvalidOperationException("Размер снимка кадра изменился при захвате");
            }
        }
        catch (Exception) {
            frame?.Dispose();
            lock (gate) {
                capturing = false;
                reservedBytes -= bytes;
            }
            return VideoPreviewAdmission.CaptureFailed;
        }

        lock (gate) {
            capturing = false;
            bool tooLarge = bytes + scratch > limits.BudgetBytes;
            if (stopped || requestedGeneration != generation || tooLarge ||
                reservedBytes > limits.BudgetBytes) {
                Release(frame, bytes);
                if (stopped || requestedGeneration != generation) {
                    return VideoPreviewAdmission.Stopped;
                }
                if (tooLarge) {
                    return VideoPreviewAdmission.TooLarge;
                }
                CountBudgetWait(key);
                return DeferInput(key);
            }

            pending = new(generation, chainRevision, key, frame, bytes, scratch, process);
            StartPending();
            return VideoPreviewAdmission.Accepted;
        }
    }

    // начинаем учет нового показа, сохраняя зарезервированные буферы прежних работ
    public void BeginObservation(long group) {
        lock (gate) {
            ResetInputGroup(group);
            peakActive = active.Count;
            peakReservedBytes = reservedBytes;
        }
    }

    // возвращаем счетчики завершенной группы и сразу переключаем учет на новые входы
    public VideoPreviewExecutorMetrics AdvanceInputGroup(long group) {
        lock (gate) {
            VideoPreviewExecutorMetrics measured = ReadMetrics();
            ResetInputGroup(group);
            return measured;
        }
    }

    // отделяем живую смену кадров от команд паузы и перемотки
    public void SetLiveInput(long id) {
        lock (gate) {
            if (liveInput != id) {
                deferredInput = null;
            }
            liveInput = id;
        }
    }

    // начинаем общий учет заново при смене документа или цепочки
    public void ResetChainDrops() {
        lock (gate) {
            chainRevision++;
            chainDropped = 0;
        }
    }

    // начинаем учет пика нового файла с буферов, которые еще удерживаются
    public void ResetBudgetPeak() {
        lock (gate) {
            budgetPeakReservedBytes = reservedBytes;
        }
    }

    // применяем новые пределы, оставляя занятые старые буферы в учете до завершения
    public void Configure(VideoPreviewSettings next) {
        lock (gate) {
            if (stopped) {
                return;
            }
            bool budgetChanged = limits.BudgetBytes != next.BudgetBytes;
            limits = next;
            if (reservedBytes > limits.BudgetBytes || (pending is { } work && work.Bytes + work.Scratch > limits.BudgetBytes)) {
                DropPending();
            }
            if (reservedBytes > limits.BudgetBytes) {
                DropReady();
            }
            if (budgetChanged) {
                budgetPeakReservedBytes = reservedBytes;
            }
            StartPending();
        }
    }

    // меняем поколение источника или плана, активный расчет сам освободит свои пиксели
    public void Invalidate() {
        lock (gate) {
            generation++;
            lastDelivered = -1;
            deferredInput = null;
            lastReportedFailure = -1;
            failure = null;
            DropPending();
            DropReady();
        }
    }

    // после закрытия придется дождаться только возврата уже занятых рабочих буферов
    public void Dispose() {
        lock (gate) {
            stopped = true;
            deferredInput = null;
            DropPending();
            DropReady();
            failure = null;
        }
    }

    // отдаём последний вход первому свободному обработчику, если хватает памяти для расчета
    private void StartPending() {
        if (stopped || pending is not { } work || active.Count >= limits.EffectiveWorkers) {
            return;
        }
        if (reservedBytes + work.Scratch > limits.BudgetBytes) {
            CountBudgetWait(work.Key);
            return;
        }

        pending = null;
        active.Add((work.Generation, work.Key.Frame), work);
        peakActive = Math.Max(peakActive, active.Count);
        Reserve(work.Scratch);
        try {
            _ = Task.Run(() => Run(work));
        }
        catch (Exception exception) {
            Complete(work, default, exception);
        }
    }

    // измеряем выполнение плана для паузы, ошибка кадра не останавливает остальной пул
    private void Run(Work work) {
        VideoEditorGeometry geometry = default;
        Exception? error = null;
        try {
            long startedAt = work.Key.PauseObservation != 0 ? Stopwatch.GetTimestamp() : 0;
            geometry = work.Process(work.Frame);
            if (work.Key.PauseObservation != 0) {
                work.Frame.ProcessingTime = Stopwatch.GetElapsedTime(startedAt);
            }
        }
        catch (Exception exception) {
            error = exception;
        }
        Complete(work, geometry, error);
    }

    // освобождаем временный резерв и оставляем на выходе свежую картинку или уведомление
    private void Complete(Work work, VideoEditorGeometry geometry, Exception? error) {
        lock (gate) {
            active.Remove((work.Generation, work.Key.Frame));
            reservedBytes -= work.Scratch;
            if (!stopped && work.Generation == generation && work.Key.Frame > lastDelivered &&
                (ready is null || work.Key.Frame > ready.Key.Frame)) {
                if (error is null) {
                    if (ready is { } previous && IsLiveReplacement(previous.Key, work.Key)) {
                        CountLiveDrop(previous.Key, waiting: false);
                    }
                    DropReady();
                    ready = new(work.Key, work.Frame, work.Bytes, geometry);
                    if (failure?.Key.Frame <= work.Key.Frame) {
                        failure = null;
                    }
                }
                else {
                    Release(work.Frame, work.Bytes);
                    // сообщение об ошибке не занимает буфер и не вытесняет готовую картинку
                    if (work.Key.Frame > lastReportedFailure && (failure is null || work.Key.Frame > failure.Value.Key.Frame)) {
                        failure = (work.Key, error);
                    }
                }
            }
            else {
                bool generationMatches = work.Generation == generation;
                bool displacedByOutput = work.OvertakenByOutput && work.ChainRevision == chainRevision;
                if (!stopped && error is null &&
                    (displacedByOutput || generationMatches && IsLiveReplacement(work.Key, ready?.Key))) {
                    CountLiveDrop(work.Key, waiting: false, includeInGroup: generationMatches);
                }
                Release(work.Frame, work.Bytes);
            }

            StartPending();
            capacityChanged = true;
            NotifyUi();
        }
    }

    // копируем счетчики и объем резерва под общей блокировкой исполнителя
    private VideoPreviewExecutorMetrics ReadMetrics() =>
        new(active.Count, pending is null ? 0 : 1, ready is null ? 0 : 1,
            reservedBytes, peakActive, peakReservedBytes, replacedPending, superseded, budgetWaiting) {
            ChainDropped = chainDropped,
            BudgetPeakReservedBytes = budgetPeakReservedBytes
        };

    // закрытая группа больше не получает пропуски своих поздних кадров
    private void ResetInputGroup(long group) {
        inputGroup = group;
        replacedPending = superseded = budgetWaiting = 0;
        lastBudgetWaitFrame = -1;
    }

    // учитываем вытеснение только новым кадром той же непрерывной подачи
    private bool IsLiveReplacement(VideoPreviewFrameKey lost, VideoPreviewFrameKey? replacement) =>
        liveInput != 0 && lost.LiveInput == liveInput &&
        replacement is { } newer && newer.LiveInput == liveInput && newer.Frame > lost.Frame;

    // поздний пропуск попадает лишь в общий счетчик цепочки
    private void CountLiveDrop(VideoPreviewFrameKey key, bool waiting, bool includeInGroup = true) {
        chainDropped++;
        if (!includeInGroup || inputGroup == 0 || key.InputGroup != inputGroup) {
            return;
        }
        if (waiting) {
            replacedPending++;
        }
        else {
            superseded++;
        }
    }

    // сохраняем только номер живого кадра, который ожидал захвата в память
    private VideoPreviewAdmission DeferInput(VideoPreviewFrameKey key) {
        if (liveInput != 0 && key.LiveInput == liveInput &&
            (deferredInput is not { } previous || previous.Frame <= key.Frame)) {
            deferredInput = key;
        }
        return VideoPreviewAdmission.Deferred;
    }

    // повторная проверка того же кадра не добавляет новое ожидание бюджета
    private void CountBudgetWait(VideoPreviewFrameKey key) {
        if (inputGroup != 0 && key.InputGroup == inputGroup && key.Frame > lastBudgetWaitFrame) {
            lastBudgetWaitFrame = key.Frame;
            budgetWaiting++;
        }
    }

    // одна отложенная операция UI забирает результат и сообщает о возвращенной емкости
    private void NotifyUi() {
        if (notificationPosted || stopped) {
            return;
        }

        notificationPosted = true;
        for (int attempt = 0; attempt < 2; attempt++) {
            try {
                postToUi(Drain);
                return;
            }
            catch (Exception) {
                // одна неудачная отправка не теряет кадр, повтор ограничен
            }
        }
        notificationPosted = false;
        Dispose();
    }

    // удерживаем занятые пиксели до окончания копирования в bitmap, затем разрешаем повтор на паузе
    private void Drain() {
        Result? result;
        (VideoPreviewFrameKey Key, Exception Error)? failed;
        lock (gate) {
            result = ready;
            ready = null;
            failed = failure;
            failure = null;
            capacityChanged = false;
            if (result is not null) {
                lastDelivered = result.Key.Frame;
                foreach (Work work in active.Values) {
                    if (work.Generation == generation && IsLiveReplacement(work.Key, result.Key)) {
                        work.OvertakenByOutput = true;
                    }
                }
            }
            if (failed is { } report) {
                lastReportedFailure = report.Key.Frame;
            }
        }

        try {
            if (result is not null) {
                publish(result.Key, result.Frame, result.Geometry, null);
            }
            if (failed is { } report) {
                publish(report.Key, null, default, report.Error);
            }
        }
        finally {
            lock (gate) {
                if (result?.Frame is { } frame) {
                    Release(frame, result.Bytes);
                }
                StartPending();
            }

            try {
                capacityAvailable();
            }
            finally {
                lock (gate) {
                    notificationPosted = false;
                    if (ready is not null || failure is not null || capacityChanged) {
                        NotifyUi();
                    }
                }
            }
        }
    }

    // убираем единственный ожидающий кадр и возвращаем его входной резерв
    private void DropPending() {
        if (pending is { } work) {
            Release(work.Frame, work.Bytes);
            pending = null;
        }
    }

    // выбрасываем еще не взятый интерфейсом результат
    private void DropReady() {
        if (ready?.Frame is { } frame) {
            Release(frame, ready.Bytes);
        }
        ready = null;
    }

    // учитываем количество байтов, которое может быть занято до окончания владения
    private void Reserve(long bytes) {
        reservedBytes += bytes;
        peakReservedBytes = Math.Max(peakReservedBytes, reservedBytes);
        budgetPeakReservedBytes = Math.Max(budgetPeakReservedBytes, reservedBytes);
    }

    // кадр принадлежит рабочему месту или одной из двух очередей, возвращаем его ровно один раз
    private void Release(VideoEditorFrame frame, long bytes) {
        frame.Dispose();
        reservedBytes -= bytes;
    }
}
