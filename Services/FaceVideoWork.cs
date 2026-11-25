using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal sealed record FaceVideoResult(IReadOnlyList<RecognizedFace> Faces, bool EmptyCatalogue, string? Error, TimeSpan? Delay) {
    // ошибка одного слоя сохраняет возможность показать общий видеокадр
    public static FaceVideoResult Unavailable(string reason) => new([], false, reason, null);
}

internal readonly record struct FaceVideoLoad(int Active, int PeakWorkers, long Bytes, long PeakBytes,
    long PresentationBytes, TimeSpan? Delay, int Inference, int PeakInference, TimeSpan? PresentationDelay);

internal sealed class FaceVideoBudget {
    private readonly object gate = new();
    private TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long bytes;
    private long peak;
    private long presentation;
    private int active;
    private int peakActive;
    private int inference;
    private int peakInference;
    private TimeSpan? latestDelay;
    private TimeSpan? presentationDelay;

    // учитываем только резерв кадров, рабочих копий, метаданных и видимого результата
    public FaceVideoLoad Snapshot {
        get {
            lock (gate) {
                return new(active, peakActive, bytes + presentation, peak, presentation, latestDelay,
                    inference, peakInference, presentationDelay);
            }
        }
    }

    // считаем одновременно запущенные модели отдельно от зарезервированных мест
    public void ChangeInference(int change) {
        lock (gate) {
            inference += change;
            peakInference = Math.Max(peakInference, inference);
        }
    }

    // сохраняем задержку от допуска кадра до его реальной передачи экрану
    public void Presented(long acceptedAt) {
        lock (gate) {
            presentationDelay = TimeSpan.FromMilliseconds(Environment.TickCount64 - acceptedAt);
        }
    }

    // живой кадр получает резерв только если сразу доступны память и обработчик
    public FaceVideoLease? TryAcquire(long scratch, long retained, long display, long limit, int workers, out Task wait) {
        lock (gate) {
            wait = changed.Task;
            long displayReserve = Math.Max(presentation, display);
            if (active >= workers || scratch + retained > limit - bytes - displayReserve) {
                return null;
            }
            active++;
            bytes += scratch + retained;
            presentation = displayReserve;
            peak = Math.Max(peak, bytes + presentation);
            peakActive = Math.Max(peakActive, active);
            return new FaceVideoLease(this, scratch, retained);
        }
    }

    // возвращаем объем после окончания работы или освобождения готового кадра
    internal void Release(long released, bool worker, TimeSpan? delay = null) {
        lock (gate) {
            bytes -= released;
            if (worker) {
                active--;
            }
            latestDelay = delay ?? latestDelay;
            TaskCompletionSource previous = changed;
            changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
            previous.TrySetResult();
        }
    }

    // начинаем замер с еще принадлежащих прежним работам буферов
    public void ResetPeaks() {
        lock (gate) {
            presentation = 0;
            peak = bytes;
            peakActive = active;
            peakInference = inference;
            latestDelay = null;
            presentationDelay = null;
        }
    }
}

internal sealed class FaceVideoLease(FaceVideoBudget budget, long scratch, long retained) : IDisposable {
    private long activeBytes = scratch;
    private long readyBytes = retained;

    // после вывода модели оставляем в очереди только резерв готового результата
    public void Finish(TimeSpan? delay) {
        long released = Interlocked.Exchange(ref activeBytes, 0);
        if (released != 0) {
            budget.Release(released, worker: true, delay);
        }
    }

    // сброс и выдача очереди освобождают запись ровно один раз
    public void Dispose() {
        Finish(null);
        long released = Interlocked.Exchange(ref readyBytes, 0);
        if (released != 0) {
            budget.Release(released, worker: false);
        }
    }
}

internal sealed class FaceVideoWork : IDisposable {
    internal const string BudgetError = "Бюджета буферов «Лиц» недостаточно для видеокадра";
    // верхняя граница метаданных YuNet TopK, коробок, решений и ссылок на существующие профили
    private const long MetadataBytes = FacePipelineService.MaximumDetections * 160L + 4096;
    // управляемая копия BGR, входы YuNet 640², выравнивание и вход одного SFace 112²
    private const long ModelInputScratch = 3L * 640 * 640 * 2 + 4L * 3 * 112 * 112 * 2 + MetadataBytes;
    private readonly object gate = new();
    private readonly Stack<FaceRecognitionService> available = new();
    private readonly Lazy<(FaceWorkspacePaths Paths, IReadOnlyList<FaceProfile> Catalogue)> catalogue = new(ReadCatalogue);
    private readonly CancellationTokenSource ending = new();
    private readonly Task previousRetirement;
    private readonly TaskCompletionSource drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string? unavailable;
    private bool retired;
    private bool cancellationsComplete;
    private int executions;

    public FaceVideoBudget Budget { get; }
    public int Workers { get; }
    public long Limit { get; }
    public Task Drained => drained.Task;

    // каждому работнику принадлежит отдельная пара моделей, профили читаются один раз
    public FaceVideoWork(FaceVideoBudget budget, int workers, long limit, Task? previous = null) {
        Budget = budget;
        Workers = workers;
        Limit = limit;
        previousRetirement = previous ?? Task.CompletedTask;
    }

    // неподвижный выход может заменить предыдущую картинку, поэтому держим место для обеих
    internal static long PresentationReserve(PixelSize size) => checked(2 * VideoEditorFrame.GetBufferBytes(size) + MetadataBytes);

    // готовая последовательность учитывает один рабочий кадр перед заполнением очереди
    public int ReadyCapacity(PixelSize size) {
        long free = Limit - PresentationReserve(size) - Scratch(size, live: false);
        return (int)Math.Clamp(free / MetadataBytes, 0, 512);
    }

    // максимальное число одновременно допущенных работ при пустой очереди
    public int WorkerCapacity(PixelSize size, bool live) {
        long free = Limit - PresentationReserve(size);
        return (int)Math.Clamp(free / (Scratch(size, live) + MetadataBytes), 0, Workers);
    }

    // перед копированием BGR или BGRA занимаем память и один работник
    public FaceVideoLease? TryReserve(PixelSize size, bool live, out Task wait) {
        if (!previousRetirement.IsCompleted) {
            wait = previousRetirement;
            return null;
        }
        int capacity = WorkerCapacity(size, live);
        return Budget.TryAcquire(Scratch(size, live), MetadataBytes, PresentationReserve(size), Limit, capacity, out wait);
    }

    // бюджет рабочей части не включает память сетей и внутренние выделения OpenCV
    private static long Scratch(PixelSize size, bool live) => checked(ModelInputScratch +
        (long)size.Width * size.Height * 3 + (live ? VideoEditorFrame.GetBufferBytes(size) : 0));

    // сохраняем пиксели живого кадра только до его выдачи, подготовленному нужны одни метаданные
    public FaceVideoResult Process(VideoEditorFrame pixels, CancellationToken token, out FaceImage? image) {
        image = null;
        lock (gate) {
            if (retired) {
                return FaceVideoResult.Unavailable("Распознавание остановлено");
            }
            executions++;
        }
        try {
            return ProcessActive(pixels, token, out image);
        }
        finally {
            lock (gate) {
                executions--;
                TryFinishRetirement();
            }
        }
    }

    // объединяем отмену команды и каталога на время одного нативного вызова
    private FaceVideoResult ProcessActive(VideoEditorFrame pixels, CancellationToken token, out FaceImage? image) {
        image = null;
        long started = Stopwatch.GetTimestamp();
        using CancellationTokenSource operation = CancellationTokenSource.CreateLinkedTokenSource(token, ending.Token);
        CancellationToken current = operation.Token;
        FaceRecognitionService? engine = null;
        try {
            current.ThrowIfCancellationRequested();
            string? problem;
            lock (gate) {
                problem = unavailable;
            }
            if (problem is not null) {
                return FaceVideoResult.Unavailable(problem);
            }
            try {
                engine = Rent();
            }
            catch (Exception error) when (!current.IsCancellationRequested) {
                string reason = $"Не удалось распознать лица. {error.Message}";
                lock (gate) {
                    unavailable ??= reason;
                }
                return FaceVideoResult.Unavailable(reason);
            }
            current.ThrowIfCancellationRequested();
            image = FaceRecognitionService.CopyVideoImage(pixels, current);
            FaceRecognitionFrame frame;
            Budget.ChangeInference(1);
            try {
                frame = engine.Recognize(image, current);
            }
            finally {
                Budget.ChangeInference(-1);
            }
            return new(frame.Faces, frame.EmptyCatalogue, null, Stopwatch.GetElapsedTime(started));
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested && ending.IsCancellationRequested) {
            return FaceVideoResult.Unavailable("Распознавание остановлено");
        }
        catch (Exception error) when (!current.IsCancellationRequested) {
            return FaceVideoResult.Unavailable($"Не удалось обработать кадр. {error.Message}");
        }
        finally {
            if (engine is not null) {
                Return(engine);
            }
        }
    }

    // повторно используем свободную модель, для параллельной работы создаем независимую
    private FaceRecognitionService Rent() {
        lock (gate) {
            if (retired) {
                throw new OperationCanceledException(ending.Token);
            }
            if (available.TryPop(out FaceRecognitionService? engine)) {
                return engine;
            }
        }
        var source = catalogue.Value;
        return new FaceRecognitionService(source.Paths.CreatePipeline(), source.Catalogue);
    }

    // модель в выполняющемся кадре освобождает только его работник
    private void Return(FaceRecognitionService engine) {
        lock (gate) {
            if (!retired) {
                available.Push(engine);
                return;
            }
        }
        ReleaseEngine(engine);
    }

    // снимаем постоянные ссылки и дожидаемся отмены внутри вызовов модели на их потоках
    public void Dispose() {
        FaceRecognitionService[] idle;
        lock (gate) {
            if (retired) {
                return;
            }
            retired = true;
            idle = available.ToArray();
            available.Clear();
        }
        _ = ReleaseIdleAsync(idle);
    }

    // возвращаем также простаивающие экземпляры и завершаем отмену вне потока интерфейса
    private async Task ReleaseIdleAsync(FaceRecognitionService[] idle) {
        try {
            await ending.CancelAsync().ConfigureAwait(false);
        }
        catch (Exception error) {
            Trace.TraceWarning($"Ошибка отмены распознавания: {error.Message}");
        }
        try {
            await Task.Run(() => Array.ForEach(idle, ReleaseEngine)).ConfigureAwait(false);
        }
        finally {
            lock (gate) {
                cancellationsComplete = true;
                TryFinishRetirement();
            }
        }
    }

    // токен нужен до последнего возврата обрабатывающей модели
    private void TryFinishRetirement() {
        if (retired && cancellationsComplete && executions == 0) {
            ending.Dispose();
            drained.TrySetResult();
        }
    }

    // не передаем ошибку закрытия сети в общую транспортную очередь
    private static void ReleaseEngine(FaceRecognitionService engine) {
        try { engine.Dispose(); }
        catch (Exception error) { Trace.TraceWarning($"Ошибка освобождения распознавания: {error.Message}"); }
    }

    // удерживаем проверенный каталог исключительно на время одного посещения или набора настроек
    private static (FaceWorkspacePaths Paths, IReadOnlyList<FaceProfile> Catalogue) ReadCatalogue() {
        FaceWorkspacePaths paths = FaceWorkspacePaths.Resolve(AppContext.BaseDirectory,
            Environment.GetEnvironmentVariable(FaceWorkspacePaths.RootVariable));
        return (paths, new FaceProfileStore(paths.CheckoutRoot).Load());
    }
}
