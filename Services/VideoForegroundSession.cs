using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;

namespace ImageVisionApp.Services;

internal sealed record VideoForegroundFrame(
    VideoPresentationTime Time, VideoPresentationTime? Until, long Ordinal, PixelSize Size,
    ReadOnlyMemory<byte> Rgb, ReadOnlyMemory<byte> Mask) {
    public VideoMotionBlurRaster? MotionBlur { get; init; }
    public VideoOpticalFlowRaster? OpticalFlow { get; init; }
    public ReadOnlyMemory<byte> Composition { get; init; }
    public PixelSize ResultSize => OpticalFlow?.Size ?? Size;
    public long BufferBytes => Rgb.Length + (long)Mask.Length + (MotionBlur?.BufferBytes ?? 0) +
        (OpticalFlow?.BufferBytes ?? 0) + Composition.Length;

    // проверяем точный кадр или временной интервал даже при одинаковых метках
    public bool Covers(VideoFrameReadRequest request) => Covers(Time, Until, Ordinal, request);

    // промежуточный кадр с нулевым интервалом доступен по номеру и участвует в обучении
    internal static bool Covers(VideoPresentationTime time, VideoPresentationTime? until, long ordinal,
        VideoFrameReadRequest request) {
        if (request.AtEnd && until is not null) {
            return false;
        }
        if (request.Anchor is { } anchor) {
            return ordinal == anchor.Ordinal && time.CompareTo(anchor.Time) == 0;
        }
        VideoPresentationTime wanted = new(request.PositionMilliseconds, 1000);
        return request.AtEnd || time.CompareTo(wanted) <= 0 &&
            (until is not { } boundary || wanted.CompareTo(boundary) < 0);
    }
}

// один фоновый потребитель читает кадры по порядку и закрывает поток после остановки чтения
internal sealed class VideoForegroundSession : IAsyncDisposable {
    private readonly IVideoMediaSource source;
    private readonly CancellationToken token;
    private readonly Func<IVideoStrictPipelineProcessor> factory;
    private readonly bool needsPreviousFrame;
    private IAsyncEnumerator<TimedVideoInput>? frames;
    private IVideoStrictPipelineProcessor? processor;
    private VideoPipelineOutput? current;
    private bool disposed;

    // сохраняем единый источник и создаем заново модель при движении назад
    public VideoForegroundSession(IVideoMediaSource source, CancellationToken token)
        : this(source, token, () => new VideoCombinedFrameProcessor(source.FrameSize)) { }

    public VideoForegroundSession(IVideoMediaSource source, CancellationToken token,
        Func<IVideoStrictPipelineProcessor> factory, bool needsPreviousFrame = false) {
        this.source = source;
        this.token = token;
        this.factory = factory;
        this.needsPreviousFrame = needsPreviousFrame;
    }

    // сохраняем прежнюю оценку для прохода по оригиналу
    internal static long GetRequiredBufferBytes(IVideoMediaSource source) =>
        VideoForegroundModel.GetRequiredBufferBytes(source.FrameSize, source.ReadScratchBytes);

    // возвращаем маску одиночного сеанса без зависимости от представления анализа
    public async Task<VideoForegroundFrame> ReadAsync(VideoFrameReadRequest request) =>
        (await ReadPipelineAsync(request).ConfigureAwait(false)).Foreground
        ?? throw new InvalidDataException("Нет результата выделения переднего плана");

    // обучаем модель на всех кадрах от начала, а сохраняем только нужный RGB и маску
    public async Task<VideoPipelineOutput> ReadPipelineAsync(VideoFrameReadRequest request) {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (request.PositionMilliseconds < 0 || request.FrameOffset != 0 || request.DecodedAnchor is not null ||
            request.Anchor is { } anchor && (anchor.Ordinal < 0 || !anchor.Time.IsKnown)) {
            throw new ArgumentException("Для выделения переднего плана нужна позиция или подтвержденный кадр", nameof(request));
        }
        try {
            token.ThrowIfCancellationRequested();
            if (current?.Foreground is { } last) {
                if (last.Covers(request)) {
                    return current;
                }
                if (IsBefore(request, last)) {
                    await CloseInputAsync().ConfigureAwait(false);
                    ClearCurrent();
                }
                else if (last.Until is null || request.Anchor?.Ordinal == last.Ordinal) {
                    throw new InvalidDataException("Запрошенный кадр не совпадает с видеопотоком");
                }
            }
            if (frames is null) {
                OpenInput(request);
            }
            return await ProcessToAsync(request).ConfigureAwait(false);
        }
        catch {
            ClearCurrent();
            await CloseInputAsync().ConfigureAwait(false);
            throw;
        }
    }

    // при равных pts физическое направление определяет номер кадра
    private static bool IsBefore(VideoFrameReadRequest request, VideoForegroundFrame last) =>
        request.Anchor is { } anchor ? anchor.Ordinal < last.Ordinal :
        !request.AtEnd && last.Time.CompareTo(new VideoPresentationTime(request.PositionMilliseconds, 1000)) > 0;

    // модель фона зависит от начала потока, оптический поток — только от предыдущего кадра
    private void OpenInput(VideoFrameReadRequest request) {
        token.ThrowIfCancellationRequested();
        VideoFrameReadRequest start = needsPreviousFrame
            ? request with { FrameOffset = -1, SingleFrame = false }
            : new(0) { Anchor = new VideoFrameCursor(new(0, 1), 0, false) };
        frames = source.ReadFramesAsync(start, token).GetAsyncEnumerator(token);
        processor = factory();
    }

    // передаем процессору каждый физический кадр, а для публикации сохраняем только нужный
    private async Task<VideoPipelineOutput> ProcessToAsync(VideoFrameReadRequest request) {
        IAsyncEnumerator<TimedVideoInput> inputFrames = frames!;
        long? expectedOrdinal = current?.Foreground is { } last ? last.Ordinal + 1 : needsPreviousFrame ? null : 0;
        VideoPresentationTime expectedTime = current?.Foreground?.Until ?? new(0, 1);
        while (await inputFrames.MoveNextAsync().ConfigureAwait(false)) {
            TimedVideoInput input = inputFrames.Current;
            using VideoEditorFrame pixels = input.Pixels;
            token.ThrowIfCancellationRequested();
            if (expectedOrdinal is { } next && (input.Ordinal != next || input.Time.CompareTo(expectedTime) != 0) ||
                !input.Time.IsKnown ||
                pixels.Size != source.FrameSize ||
                input.Until is { } boundary && (!boundary.IsKnown || boundary.CompareTo(input.Time) < 0)) {
                throw new InvalidDataException("Нарушен порядок, время или размер кадров видеопотока");
            }

            bool publish = VideoForegroundFrame.Covers(input.Time, input.Until, input.Ordinal, request);
            if (needsPreviousFrame && current is null &&
                (expectedOrdinal is null ? publish && input.Ordinal != 0 : !publish)) {
                throw new InvalidDataException("Не удалось подтвердить соседние кадры оптического потока");
            }
            VideoPipelineOutput? output = await processor!.ProcessAsync(input, pixels, null, null, publish, token)
                .ConfigureAwait(false);
            if (publish) {
                if (output?.Foreground is null) {
                    output?.Dispose();
                    throw new InvalidDataException("Нет результата выделения переднего плана");
                }
                ClearCurrent();
                current = output;
                if (input.IsLast) {
                    await CloseInputAsync().ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                return current;
            }
            output?.Dispose();
            if (input.IsLast || request.Anchor?.Ordinal == input.Ordinal) {
                break;
            }
            expectedOrdinal = input.Ordinal + 1;
            expectedTime = input.Until!.Value;
        }
        throw new InvalidDataException("В видеопотоке нет кадра с запрошенной позицией");
    }

    // освобождаем обе нативные части, оставляя последний готовый снимок доступным после eof
    private async ValueTask CloseInputAsync() {
        IAsyncEnumerator<TimedVideoInput>? input = frames;
        IVideoStrictPipelineProcessor? background = processor;
        frames = null;
        processor = null;
        try {
            if (background is not null) {
                await background.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally {
            if (input is not null) {
                await input.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    // закрываем сеанс после отмены или ухода страницы и отпускаем последний снимок
    public async ValueTask DisposeAsync() {
        disposed = true;
        ClearCurrent();
        await CloseInputAsync().ConfigureAwait(false);
    }

    // срок жизни выходного кадра заканчивается при следующем результате или закрытии сеанса
    private void ClearCurrent() {
        current?.Dispose();
        current = null;
    }
}
