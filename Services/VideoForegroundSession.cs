using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using OpenCvSharp;
using CvSize = OpenCvSharp.Size;

namespace ImageVisionApp.Services;

internal sealed record VideoForegroundFrame(
    VideoPresentationTime Time, VideoPresentationTime? Until, long Ordinal, PixelSize Size,
    ReadOnlyMemory<byte> Rgb, ReadOnlyMemory<byte> Mask) {

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
    private const int History = 120;
    private const double VarianceThreshold = 16;
    private const int ModelLongSide = 640;
    private const int Mixtures = 5;
    private const int RgbChannels = 3;
    private const int PixelsPerModelStripe = 1 << 16;

    private readonly IVideoMediaSource source;
    private readonly CancellationToken token;
    private IAsyncEnumerator<TimedVideoInput>? frames;
    private BackgroundSubtractorMOG2? model;
    private VideoForegroundFrame? current;
    private bool disposed;

    // сохраняем источник подтвержденных кадров и отмену на весь сеанс обучения
    public VideoForegroundSession(IVideoMediaSource source, CancellationToken token) {
        this.source = source;
        this.token = token;
    }

    // оцениваем одновременные буферы одного прохода, включая показ и фон, но без памяти всего процесса
    internal static long GetRequiredBufferBytes(IVideoMediaSource source) {
        PixelSize size = source.FrameSize;
        long readerBytes = source.ReadScratchBytes;
        if (size.Width <= 0 || size.Height <= 0 || readerBytes < 0) {
            throw new ArgumentException("Неверный размер видеокадра или буферов чтения", nameof(source));
        }

        CvSize training = GetTrainingSize(size);
        long previewPixels = (long)size.Width * size.Height;
        long trainingPixels = (long)training.Width * training.Height;
        long bgraFrame = VideoEditorFrame.GetBufferBytes(size);
        // вход BGRA, два bitmap показа и одна строка переноса в bitmap
        long displayAndInput = checked(bgraFrame * 3 + size.Width * 4L);
        // снимки на экране и в сеансе могут различаться, пока готовится следующий
        long snapshots = checked(3 * previewPixels * (RgbChannels + 1));
        // cpu-модель OpenCV: вес, дисперсия и средние RGB каждой смеси, число смесей и маска на пиксель
        long modelAndMask = trainingPixels * (Mixtures * (2 + RgbChannels) * sizeof(float) + 2);
        long modelStripes = (trainingPixels + PixelsPerModelStripe - 1) / PixelsPerModelStripe;
        long modelRows = modelStripes * training.Width * RgbChannels * sizeof(float);
        long resizeBuffers = trainingPixels == previewPixels ? 0 : trainingPixels * RgbChannels + previewPixels;
        return checked(readerBytes + displayAndInput + snapshots + modelAndMask + modelRows + resizeBuffers);
    }

    // обучаем модель на всех кадрах от начала, а сохраняем только нужный RGB и маску
    public async Task<VideoForegroundFrame> ReadAsync(VideoFrameReadRequest request) {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (request.PositionMilliseconds < 0 || request.FrameOffset != 0 || request.DecodedAnchor is not null ||
            request.Anchor is { } anchor && (anchor.Ordinal < 0 || !anchor.Time.IsKnown)) {
            throw new ArgumentException("Для выделения переднего плана нужна позиция или подтвержденный кадр", nameof(request));
        }
        try {
            token.ThrowIfCancellationRequested();
            if (current is { } last) {
                if (last.Covers(request)) {
                    return last;
                }
                if (IsBefore(request, last)) {
                    await CloseInputAsync().ConfigureAwait(false);
                    current = null;
                }
                else if (last.Until is null || request.Anchor?.Ordinal == last.Ordinal) {
                    throw new InvalidDataException("Запрошенный кадр не совпадает с видеопотоком");
                }
            }
            if (frames is null) {
                OpenFromStart();
            }
            return await ProcessToAsync(request).ConfigureAwait(false);
        }
        catch {
            current = null;
            await CloseInputAsync().ConfigureAwait(false);
            throw;
        }
    }

    // при равных pts физическое направление определяет номер кадра
    private static bool IsBefore(VideoFrameReadRequest request, VideoForegroundFrame last) =>
        request.Anchor is { } anchor ? anchor.Ordinal < last.Ordinal :
        !request.AtEnd && last.Time.CompareTo(new VideoPresentationTime(request.PositionMilliseconds, 1000)) > 0;

    // привязка к нулевому номеру включает самый ранний кадр даже при нескольких нулевых pts
    private void OpenFromStart() {
        token.ThrowIfCancellationRequested();
        VideoFrameReadRequest start = new(0) { Anchor = new VideoFrameCursor(new(0, 1), 0, false) };
        frames = source.ReadFramesAsync(start, token).GetAsyncEnumerator(token);
        model = BackgroundSubtractorMOG2.Create(History, VarianceThreshold, detectShadows: false);
        model.NMixtures = Mixtures;
    }

    // проверяем порядок и обучаемся на каждом кадре, включая те, которые не будут показаны
    private async Task<VideoForegroundFrame> ProcessToAsync(VideoFrameReadRequest request) {
        IAsyncEnumerator<TimedVideoInput> inputFrames = frames!;
        PixelSize previewSize = source.FrameSize;
        CvSize trainingSize = GetTrainingSize(previewSize);
        long expectedOrdinal = current is { } last ? last.Ordinal + 1 : 0;
        VideoPresentationTime expectedTime = current?.Until ?? new(0, 1);
        using Mat reduced = new();
        using Mat mask = new();

        while (await inputFrames.MoveNextAsync().ConfigureAwait(false)) {
            TimedVideoInput input = inputFrames.Current;
            using VideoEditorFrame pixels = input.Pixels;
            token.ThrowIfCancellationRequested();
            if (input.Ordinal != expectedOrdinal || !input.Time.IsKnown || input.Time.CompareTo(expectedTime) != 0 ||
                pixels.Size != previewSize ||
                input.Until is { } boundary && (!boundary.IsKnown || boundary.CompareTo(input.Time) < 0)) {
                throw new InvalidDataException("Нарушен порядок, время или размер кадров видеопотока");
            }

            byte[] rgb = pixels.CopyRgb(token);
            using Mat image = Mat.FromPixelData(previewSize.Height, previewSize.Width, MatType.CV_8UC3, rgb);
            Mat sample = image;
            if (trainingSize.Width != previewSize.Width || trainingSize.Height != previewSize.Height) {
                Cv2.Resize(image, reduced, trainingSize, interpolation: InterpolationFlags.Area);
                sample = reduced;
            }
            token.ThrowIfCancellationRequested();
            model!.Apply(sample, mask);
            token.ThrowIfCancellationRequested();

            if (VideoForegroundFrame.Covers(input.Time, input.Until, input.Ordinal, request)) {
                // первый кадр создает модель, для его показа отдаваемая маска пуста
                byte[] foreground = input.Ordinal == 0
                    ? new byte[checked(previewSize.Width * previewSize.Height)]
                    : CopyMask(mask, previewSize);
                if (input.IsLast) {
                    await CloseInputAsync().ConfigureAwait(false);
                }
                token.ThrowIfCancellationRequested();
                current = new(input.Time, input.Until, input.Ordinal, previewSize, rgb, foreground);
                return current;
            }
            if (input.IsLast || request.Anchor?.Ordinal == input.Ordinal) {
                break;
            }
            expectedOrdinal++;
            expectedTime = input.Until!.Value;
        }
        throw new InvalidDataException("В видеопотоке нет кадра с запрошенной позицией");
    }

    // уменьшаем только модель фона, сохраняя пропорции и небольшие кадры в исходном размере
    private static CvSize GetTrainingSize(PixelSize input) {
        double scale = Math.Min(1, (double)ModelLongSide / Math.Max(input.Width, input.Height));
        return new(Math.Max(1, (int)Math.Round(input.Width * scale)),
            Math.Max(1, (int)Math.Round(input.Height * scale)));
    }

    // возвращаем двоичные значения в координатах предпросмотра без смешивания соседних пикселей
    private byte[] CopyMask(Mat mask, PixelSize size) {
        using Mat expanded = new();
        if (mask.Width != size.Width || mask.Height != size.Height) {
            Cv2.Resize(mask, expanded, new CvSize(size.Width, size.Height), interpolation: InterpolationFlags.Nearest);
            mask = expanded;
        }
        byte[] output = new byte[checked(size.Width * size.Height)];
        for (int y = 0; y < size.Height; y++) {
            token.ThrowIfCancellationRequested();
            Marshal.Copy(mask.Ptr(y), output, y * size.Width, size.Width);
        }
        return output;
    }

    // освобождаем обе нативные части, оставляя последний готовый снимок доступным после eof
    private async ValueTask CloseInputAsync() {
        IAsyncEnumerator<TimedVideoInput>? input = frames;
        BackgroundSubtractorMOG2? background = model;
        frames = null;
        model = null;
        try {
            background?.Dispose();
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
        current = null;
        await CloseInputAsync().ConfigureAwait(false);
    }
}
