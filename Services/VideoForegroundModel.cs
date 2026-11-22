using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using OpenCvSharp;
using CvSize = OpenCvSharp.Size;

namespace ImageVisionApp.Services;

internal sealed class VideoForegroundModel : IDisposable {
    private const int History = 120;
    private const double VarianceThreshold = 16;
    private const int ModelLongSide = 640;
    private const int Mixtures = 5;
    private const int RgbChannels = 3;
    private const int PixelsPerModelStripe = 1 << 16;

    private readonly PixelSize size;
    private readonly CvSize trainingSize;
    private readonly BackgroundSubtractorMOG2 model;
    private readonly Mat reduced = new();
    private readonly Mat mask = new();
    private readonly Mat expanded = new();

    // одна модель принадлежит одному последовательному проходу общей очереди
    public VideoForegroundModel(PixelSize size) {
        this.size = size;
        trainingSize = GetTrainingSize(size);
        model = BackgroundSubtractorMOG2.Create(History, VarianceThreshold, detectShadows: false);
        model.NMixtures = Mixtures;
    }

    // оцениваем буферы декодирования, представлений в интерфейсе и модели
    public static long GetRequiredBufferBytes(PixelSize size, long readerBytes) {
        if (size.Width <= 0 || size.Height <= 0 || readerBytes < 0) {
            throw new ArgumentException("Неверный размер видеокадра или буферов чтения");
        }

        CvSize training = GetTrainingSize(size);
        long previewPixels = (long)size.Width * size.Height;
        long trainingPixels = (long)training.Width * training.Height;
        long bgraFrame = VideoEditorFrame.GetBufferBytes(size);
        long displayAndInput = checked(bgraFrame * 3 + size.Width * 4L);
        long snapshots = checked(3 * previewPixels * (RgbChannels + 1));
        long modelStripes = (trainingPixels + PixelsPerModelStripe - 1) / PixelsPerModelStripe;
        long modelRows = modelStripes * training.Width * RgbChannels * sizeof(float);
        long resizeBuffers = trainingPixels == previewPixels ? 0 : trainingPixels * RgbChannels + previewPixels;
        return checked(readerBytes + displayAndInput + snapshots + GetPersistentBytes(size) + modelRows + resizeBuffers);
    }

    // одна смесь хранит вес, дисперсию и среднее для каждого цветового канала
    public static long GetPersistentBytes(PixelSize size) {
        CvSize training = GetTrainingSize(size);
        return (long)training.Width * training.Height * (Mixtures * (2 + RgbChannels) * sizeof(float) + 2);
    }

    // прогрев обновляет модель, а выдача дополнительно сохраняет независимые пиксели и маску
    public VideoForegroundFrame? Process(TimedVideoInput input, VideoEditorFrame pixels, bool publish, CancellationToken token) {
        if (size != pixels.Size) {
            throw new InvalidDataException("Размер подготовленного кадра отличается от входа модели фона");
        }
        byte[] rgb = pixels.CopyRgb(token);
        using Mat image = Mat.FromPixelData(size.Height, size.Width, MatType.CV_8UC3, rgb);
        Mat sample = image;
        if (trainingSize.Width != size.Width || trainingSize.Height != size.Height) {
            Cv2.Resize(image, reduced, trainingSize, interpolation: InterpolationFlags.Area);
            sample = reduced;
        }
        token.ThrowIfCancellationRequested();
        model.Apply(sample, mask);
        token.ThrowIfCancellationRequested();
        if (!publish) {
            return null;
        }

        byte[] foreground = new byte[checked(size.Width * size.Height)];
        if (input.Ordinal != 0) {
            Mat output = mask;
            if (mask.Width != size.Width || mask.Height != size.Height) {
                Cv2.Resize(mask, expanded, new CvSize(size.Width, size.Height), interpolation: InterpolationFlags.Nearest);
                output = expanded;
            }
            for (int y = 0; y < size.Height; y++) {
                token.ThrowIfCancellationRequested();
                Marshal.Copy(output.Ptr(y), foreground, y * size.Width, size.Width);
            }
        }
        return new(input.Time, input.Until, input.Ordinal, size, rgb, foreground);
    }

    // уменьшаем только рабочую модель, сохраняя пропорции входного кадра
    private static CvSize GetTrainingSize(PixelSize input) {
        double scale = Math.Min(1, (double)ModelLongSide / Math.Max(input.Width, input.Height));
        return new(Math.Max(1, (int)Math.Round(input.Width * scale)),
            Math.Max(1, (int)Math.Round(input.Height * scale)));
    }

    // освобождаем нативные буферы модели при закрытии поколения
    public void Dispose() {
        model.Dispose();
        expanded.Dispose();
        mask.Dispose();
        reduced.Dispose();
    }
}
