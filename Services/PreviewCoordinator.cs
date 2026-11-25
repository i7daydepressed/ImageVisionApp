using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal readonly record struct PreparedVideoWindow(
    long Generation, int ReadyFrames, VideoPresentationTime? First, VideoPresentationTime? Last, VideoPresentationTime? Through,
    bool Ended, Exception? Error, long ReservedBytes, long BudgetBytes, long PeakReservedBytes) {
    public long? FirstOrdinal { get; init; }
    public int ActiveWorkers { get; init; }
    public int PeakActiveWorkers { get; init; }
    public int WorkerLimit { get; init; }
    public int ResumeCapacity { get; init; }
}

// все картинки принадлежат вызывающей стороне до окончания копирования в интерфейс
internal sealed class PreparedVideoFrame(
    TimedVideoInput input, VideoEditorFrame edited, VideoEditorGeometry geometry, Action release,
    long? acceptedAt = null, VideoPipelineOutput? pipelineOutput = null,
    FaceVideoResult? faceResult = null, FaceVideoLease? faceLease = null, FaceVideoWork? faceOwner = null) : IDisposable {
    private Action? releaseReservation = release;
    private VideoPipelineOutput? ownedOutput = pipelineOutput;

    public VideoPresentationTime Time => input.Time;
    public VideoPresentationTime? Until => input.Until;
    public long Ordinal => input.Ordinal;
    public VideoEditorFrame Original => input.Pixels;
    public VideoEditorFrame Edited => edited;
    public VideoEditorGeometry Geometry => geometry;
    public long? AcceptedAt => acceptedAt;
    public VideoPipelineOutput? PipelineOutput => ownedOutput;
    public FaceVideoResult? FaceResult { get; } = faceResult;
    public FaceVideoWork? FaceOwner { get; } = faceOwner;

    // кадр действует на полуоткрытом интервале до следующей подтвержденной метки
    public bool Contains(VideoPresentationTime time) =>
        Time.CompareTo(time) <= 0 && (Until is not { } boundary || time.CompareTo(boundary) < 0);

    // возвращаем резерв один раз, даже если исходник и результат используют один буфер
    public void Dispose() {
        if (Interlocked.Exchange(ref releaseReservation, null) is { } free) {
            try {
                Interlocked.Exchange(ref ownedOutput, null)?.Dispose();
            }
            finally {
                if (!ReferenceEquals(Original, Edited)) {
                    Edited.Dispose();
                }
                Original.Dispose();
                faceLease?.Dispose();
                free();
            }
        }
    }
}

// читает ближайший участок фоновым циклом, удерживая кадровый резерв до освобождения
internal sealed partial class PreviewCoordinator : IAsyncDisposable {
    private sealed record RequestData(long Generation, IVideoMediaSource Source, VideoFrameReadRequest Start,
        VideoEditorPlan Plan, long Budget, PixelSize Size, long ReaderBytes, long ResultBytes, long ScratchBytes,
        bool Strict, int WorkerLimit, int ResumeCapacity, StrictVideoPipeline? Pipeline, FaceVideoWork? Faces, int FaceCapacity);

    private sealed class Reading(RequestData request) {
        public RequestData Request { get; } = request;
        public CancellationTokenSource Stop { get; } = new();
        public Task? Cancellation { get; set; }
        public StrictStage? Strict { get; } = request.Strict ? new() : null;
    }

    private const int AheadMilliseconds = 7000;
    private const int MaximumReadyFrames = 512;
    private readonly object gate = new();
    private readonly Queue<PreparedVideoFrame> ready = new();
    private readonly SemaphoreSlim changed = new(0, 1);
    private RequestData? pending;
    private Reading? reading;
    private Task? producer;
    private VideoPresentationTime? last;
    private VideoPresentationTime? through;
    private long generation;
    private long position;
    private long reserved;
    private long peakReserved;
    private long budget;
    private bool ended;
    private bool atEnd;
    private int workerLimit;
    private int peakActiveWorkers;
    private int resumeCapacity;
    private bool disposed;
    private Exception? error;

    public PreparedVideoWindow Snapshot {
        get {
            lock (gate) {
                ready.TryPeek(out PreparedVideoFrame? first);
                return new(generation, ready.Count, first?.Time, last, through, ended, error, reserved, budget, peakReserved) {
                    FirstOrdinal = first?.Ordinal,
                    ActiveWorkers = reading?.Strict?.Active ?? 0,
                    PeakActiveWorkers = peakActiveWorkers,
                    WorkerLimit = workerLimit,
                    ResumeCapacity = resumeCapacity
                };
            }
        }
    }

    // оставляем только последний запрос, старый читатель освобождает собственные буферы при выходе
    public long Request(IVideoMediaSource source, VideoFrameReadRequest start, VideoEditorPlan plan, long budgetBytes,
        FaceVideoWork? faces = null) =>
        RequestCore(source, start, plan, budgetBytes, strict: false, workers: 1, faces: faces);

    // используем тот же бюджет и поколение, сохраняя особый порядок потребления строгого запроса
    private long RequestCore(IVideoMediaSource source, VideoFrameReadRequest start, VideoEditorPlan plan, long budgetBytes,
        bool strict, int workers, StrictVideoPipeline? pipeline = null, FaceVideoWork? faces = null) {
        PixelSize size = source.FrameSize;
        long readerBytes = source.ReadScratchBytes;
        if (size.Width <= 0 || size.Height <= 0 || readerBytes < 0 || budgetBytes <= 0 ||
            workers <= 0 || start.PositionMilliseconds < 0 || start.PositionMilliseconds > long.MaxValue - AheadMilliseconds) {
            throw new ArgumentOutOfRangeException(nameof(start), "Неверные параметры подготовленного участка");
        }
        long resultBytes = checked((long)size.Width * size.Height * (plan.HasSteps ? 8 : 4));
        long scratchBytes = plan.GetScratchBytes(size);
        long frameBytes = checked(resultBytes + scratchBytes);
        long frameBudget = Math.Max(0, budgetBytes - readerBytes);
        int limit = strict ? (int)Math.Min(plan.HasSteps ? workers : 1, frameBudget / frameBytes) : 1;
        int capacity = strict && limit > 0
            ? (int)Math.Min(MaximumReadyFrames, 1 + (frameBudget - frameBytes) / resultBytes) : 0;
        int faceCapacity = faces?.ReadyCapacity(size) ?? 0;
        if (strict && pipeline is null && faceCapacity > 0) {
            int faceWorkers = faces!.WorkerCapacity(size, live: false);
            limit = (int)Math.Min(plan.HasSteps ? Math.Min(workers, faceWorkers) : faceWorkers, frameBudget / frameBytes);
            capacity = Math.Min(capacity, faceCapacity);
        }
        if (pipeline is not null) {
            capacity = Math.Min(capacity, pipeline.Capacity);
            limit = capacity > 0 ? 1 : 0;
            readerBytes = checked(readerBytes + pipeline.PersistentBytes);
            resultBytes = checked(resultBytes + pipeline.SelectedFrameBytes);
            scratchBytes = checked(scratchBytes + pipeline.ScratchBytes);
            // лимит очереди определяет самый тесный участок, остальные буферы учитываются по факту владения
            budgetBytes = capacity > 0 ? checked(readerBytes + capacity * resultBytes + scratchBytes) : 0;
        }
        lock (gate) {
            ObjectDisposedException.ThrowIf(disposed, this);
            Invalidate();
            position = start.PositionMilliseconds;
            atEnd = start.AtEnd;
            budget = budgetBytes;
            workerLimit = limit;
            resumeCapacity = capacity;
            strictMeasuring = strictRequest = strict;
            pending = new(generation, source, start, plan, budgetBytes, size, readerBytes, resultBytes, scratchBytes,
                strict, limit, capacity, pipeline, pipeline is null ? faces : null, faceCapacity);
            producer ??= Task.Run(RunAsync);
            return generation;
        }
    }

    // пропускаем уже пройденные кадры и отдаём последний кадр на запрошенное время
    public PreparedVideoFrame? TakeAt(long expectedGeneration, long positionMilliseconds) {
        lock (gate) {
            if (disposed || generation != expectedGeneration) {
                return null;
            }
            position = Math.Max(position, Math.Min(positionMilliseconds, long.MaxValue - AheadMilliseconds));
            VideoPresentationTime time = new(positionMilliseconds, 1000);
            PreparedVideoFrame? current = null;
            while (ready.TryPeek(out PreparedVideoFrame? next) && (atEnd || next.Time.CompareTo(time) <= 0)) {
                ready.Dequeue();
                if (atEnd || next.Contains(time)) {
                    current = next;
                    break;
                }
                next.Dispose();
            }
            if (ready.Count == 0) {
                last = null;
                through = null;
            }
            SignalProducer();
            return current;
        }
    }

    // забираем первую точную пару запроса, включая кадр с нулевым интервалом до дубликата
    public PreparedVideoFrame? TakeFirst(long expectedGeneration) {
        lock (gate) {
            if (disposed || generation != expectedGeneration || !ready.TryDequeue(out PreparedVideoFrame? frame)) {
                return null;
            }
            if (ready.Count == 0) {
                last = null;
                through = null;
            }
            SignalProducer();
            return frame;
        }
    }

    // снимаем готовый участок и запрещаем позднюю выдачу старого поколения
    public void Cancel() {
        lock (gate) {
            if (!disposed) {
                Invalidate();
            }
        }
    }

    // ждём выхода читателя и обработки, выданные кадры возвращает их владелец
    public async ValueTask DisposeAsync() {
        Task? completion;
        lock (gate) {
            if (!disposed) {
                Invalidate();
                disposed = true;
            }
            completion = producer;
        }
        if (completion is not null) {
            await completion.ConfigureAwait(false);
        }
    }

    // выполняем смену поколения под блокировкой, не вызывая отмену читателя на потоке UI
    private void Invalidate() {
        generation++;
        pending = null;
        ended = false;
        error = null;
        if (reading is { } active) {
            active.Cancellation ??= active.Stop.CancelAsync();
            ClearStrictCompleted(active.Strict);
        }
        workerLimit = resumeCapacity = peakActiveWorkers = 0;
        ResetStrictMeasurements();
        ClearReady();
        peakReserved = reserved;
        SignalProducer();
    }

    // освобождаем готовые пары, не затрагивая выполняющийся кадр и кадры у потребителя
    private void ClearReady() {
        while (ready.TryDequeue(out PreparedVideoFrame? frame)) {
            frame.Dispose();
        }
        last = null;
        through = null;
    }

    // последовательно закрываем старый источник перед следующим запросом
    private async Task RunAsync() {
        while (true) {
            Reading work;
            lock (gate) {
                if (pending is not { } request) {
                    producer = null;
                    return;
                }
                pending = null;
                reading = work = new(request);
            }
            try {
                if (work.Request.Pipeline is { } pipeline) {
                    await PrepareStrictPipelineAsync(work, pipeline).ConfigureAwait(false);
                }
                else if (work.Request.Strict) {
                    await PrepareStrictAsync(work).ConfigureAwait(false);
                }
                else {
                    await PrepareAsync(work).ConfigureAwait(false);
                }
            }
            catch (Exception failure) {
                lock (gate) {
                    if (IsCurrent(work.Request)) {
                        ClearReady();
                        ended = false;
                        error = failure;
                    }
                }
            }
            finally {
                Task? cancellation;
                lock (gate) {
                    reading = null;
                    cancellation = work.Cancellation;
                }
                if (cancellation is not null) {
                    try {
                        await cancellation.ConfigureAwait(false);
                    }
                    catch (Exception) {
                        // ошибка обработчика отмены не относится к следующему запросу
                    }
                }
                work.Stop.Dispose();
            }
        }
    }

    // держим рабочий резерв источника до закрытия его перечислителя
    private async Task PrepareAsync(Reading work) {
        RequestData request = work.Request;
        CancellationToken token = work.Stop.Token;
        long frameBytes = checked(request.ResultBytes + request.ScratchBytes);
        if (checked(frameBytes + request.ReaderBytes) > request.Budget) {
            throw new InvalidOperationException("Бюджета буферов недостаточно для подготовки видеокадра");
        }
        await ReserveAsync(request, request.ReaderBytes, limitWindow: false, token).ConfigureAwait(false);
        try {
            await using IAsyncEnumerator<TimedVideoInput> input =
                request.Source.ReadFramesAsync(request.Start, token).GetAsyncEnumerator(token);
            VideoPresentationTime? previous = null;
            VideoPresentationTime? expected = null;
            long previousOrdinal = 0;
            while (true) {
                await ReserveAsync(request, frameBytes, limitWindow: true, token).ConfigureAwait(false);
                long remaining = frameBytes;
                TimedVideoInput frame = default;
                VideoEditorFrame? edited = null;
                FaceVideoLease? faceLease = null;
                try {
                    if (!await input.MoveNextAsync().ConfigureAwait(false)) {
                        if (previous is not null) {
                            throw new InvalidOperationException("Источник завершился до подтвержденной правой границы");
                        }
                        lock (gate) {
                            if (IsCurrent(request)) {
                                ended = true;
                            }
                        }
                        break;
                    }
                    frame = input.Current;
                    token.ThrowIfCancellationRequested();
                    if (frame.Pixels is null || frame.Pixels.Size != request.Size || !frame.Time.IsKnown ||
                        (frame.Until is { } boundary && (!boundary.IsKnown || boundary.CompareTo(frame.Time) < 0)) ||
                        (previous is not null && (frame.Time.CompareTo(expected!.Value) != 0 || frame.Ordinal <= previousOrdinal)) ||
                        (request.Start is { AtEnd: true, SingleFrame: false } && !frame.IsLast)) {
                        throw new InvalidOperationException("Источник вернул кадр с неверным размером, меткой или порядком");
                    }
                    previous = frame.Time;
                    expected = frame.Until;
                    previousOrdinal = frame.Ordinal;
                    edited = request.Plan.HasSteps ? frame.Pixels.Copy() : frame.Pixels;
                    long started = Stopwatch.GetTimestamp();
                    VideoEditorGeometry geometry = request.Plan.Apply(edited);
                    if (request.Plan.HasSteps) {
                        edited.ProcessingTime = Stopwatch.GetElapsedTime(started);
                    }
                    faceLease = await ReservePreparedFaceAsync(request, token).ConfigureAwait(false);
                    long? acceptedAt = faceLease is not null ? Environment.TickCount64 : null;
                    FaceVideoResult? face = ProcessFace(request, frame.Pixels, faceLease, token);
                    lock (gate) {
                        if (!IsCurrent(request)) {
                            return;
                        }
                        ready.Enqueue(new(frame, edited, geometry, () => Release(request.ResultBytes), acceptedAt,
                            faceResult: face, faceLease: faceLease, faceOwner: request.Faces));
                        faceLease = null;
                        last = frame.Time;
                        through = frame.Until;
                        ended = frame.IsLast;
                        remaining -= request.ResultBytes;
                        frame = default;
                        edited = null;
                        if (ended || request.Start.SingleFrame) {
                            return;
                        }
                    }
                }
                finally {
                    faceLease?.Dispose();
                    if (edited is not null && !ReferenceEquals(edited, frame.Pixels)) {
                        edited.Dispose();
                    }
                    frame.Pixels?.Dispose();
                    Release(remaining);
                }
            }
        }
        finally {
            Release(request.ReaderBytes);
        }
    }

    // последовательный участок ждет память только в фоновом подготовителе
    private static async Task<FaceVideoLease?> ReservePreparedFaceAsync(RequestData request, CancellationToken token) {
        if (request.Faces is not { } faces || request.FaceCapacity == 0) {
            return null;
        }
        while (true) {
            token.ThrowIfCancellationRequested();
            if (faces.TryReserve(request.Size, live: false, out Task wait) is { } lease) {
                return lease;
            }
            await wait.WaitAsync(token).ConfigureAwait(false);
        }
    }

    // готовая пара хранит коробки и решения, а временный BGR исходника ей больше не нужен
    private static FaceVideoResult? ProcessFace(RequestData request, VideoEditorFrame original,
        FaceVideoLease? lease, CancellationToken token) {
        if (request.Faces is not { } faces) {
            return null;
        }
        if (lease is null) {
            return FaceVideoResult.Unavailable(FaceVideoWork.BudgetError);
        }
        FaceVideoResult result = faces.Process(original, token, out _);
        lease.Finish(result.Delay);
        return result;
    }

    // ждём свободной памяти и места в ближайшем участке до следующего чтения или копирования
    private async Task ReserveAsync(RequestData request, long bytes, bool limitWindow, CancellationToken token,
        bool waitForPrevious = false) {
        while (true) {
            token.ThrowIfCancellationRequested();
            lock (gate) {
                if (!IsCurrent(request)) {
                    throw new OperationCanceledException(token);
                }
                VideoPresentationTime horizon = new(position + AheadMilliseconds, 1000);
                bool windowHasRoom = ready.Count < MaximumReadyFrames &&
                    (through is null || through.Value.CompareTo(horizon) < 0);
                if ((!limitWindow || windowHasRoom) && (!waitForPrevious || reserved == 0) &&
                    bytes <= request.Budget - reserved) {
                    reserved += bytes;
                    peakReserved = Math.Max(peakReserved, reserved);
                    return;
                }
            }
            await changed.WaitAsync(token).ConfigureAwait(false);
        }
    }

    // сравниваем поколение запроса и владельца контроллера под блокировкой
    private bool IsCurrent(RequestData request) => !disposed && generation == request.Generation;

    // уменьшаем фактический резерв после освобождения буфера
    private void Release(long bytes) {
        lock (gate) {
            reserved -= bytes;
            SignalProducer();
        }
    }

    // одна отметка достаточна, чтобы фоновый цикл повторно проверил условия
    private void SignalProducer() {
        if (changed.CurrentCount == 0) {
            changed.Release();
        }
    }
}
