using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Platform;
using OpenCvSharp;
using CvPoint = OpenCvSharp.Point;
using CvSize = OpenCvSharp.Size;

namespace ImageVisionApp.Services;

internal sealed class VideoOpticalFlowModel : IDisposable {
    private const int WorkingLongSide = 320;
    private const int GridStep = 16;
    private const int WindowSize = 15;
    private const int PolynomialSize = 5;
    private const double NoiseThreshold = 0.75;
    private const double ArrowGain = 2;
    private const double MaximumArrowLength = 12;
    private static readonly Scalar OutlineColor = new(0, 0, 0, 255);
    private static readonly Scalar ArrowColor = new(48, 244, 250, 255);

    private readonly PixelSize size;
    private readonly CvSize workingSize;
    private readonly Mat reduced = new();
    private readonly Mat flow = new();
    private Mat previousGray = new();
    private Mat currentGray = new();
    private long? previousOrdinal;
    private VideoPresentationTime? previousUntil;

    // одна пара рабочих матриц яркости принадлежит одному последовательному проходу
    public VideoOpticalFlowModel(PixelSize size) {
        this.size = size;
        workingSize = GetWorkingSize(size);
    }

    // готовый кадр держит исходный rgb и при наличии стрелок полный bgra для показа
    public static long GetFrameBytes(PixelSize size) =>
        checked(7 * GetPixelCount(size));

    // сохраняем две яркости, поле векторов и уменьшенный bgra, если нужен resize
    public static long GetPersistentBytes(PixelSize size) {
        CvSize working = GetWorkingSize(size);
        long workPixels = (long)working.Width * working.Height;
        int bytesPerPixel = 2 + 2 * sizeof(float) + (workPixels == GetPixelCount(size) ? 0 : 4);
        return checked(workPixels * bytesPerPixel);
    }

    // учитываем две выдачи, три буфера входа и показа, рабочие матрицы и строки OpenCV
    public static long GetRequiredBufferBytes(PixelSize size, long readerBytes) {
        ArgumentOutOfRangeException.ThrowIfNegative(readerBytes);
        CvSize working = GetWorkingSize(size);
        long workPixels = (long)working.Width * working.Height;
        // оцениваем cpu-путь Farneback: две полиномиальные матрицы, M, яркости и уровни пирамиды
        long flowScratch = checked(20 * sizeof(float) * workPixels + 64L * sizeof(float) * working.Width);
        long resizeRows = checked(16L * size.Width * sizeof(float));
        return checked(readerBytes + 3 * VideoEditorFrame.GetBufferBytes(size) + 2 * GetFrameBytes(size) +
            size.Width * 4L + GetPersistentBytes(size) + flowScratch + resizeRows);
    }

    // используем предыдущий физический кадр, даже когда его pts равен текущему
    public VideoForegroundFrame? Process(TimedVideoInput input, VideoEditorFrame pixels, bool publish, CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if (pixels.Size != size || input.Ordinal < 0 || !input.Time.IsKnown ||
            input.Until is { } until && (!until.IsKnown || until.CompareTo(input.Time) < 0) ||
            previousOrdinal is { } ordinal && (input.Ordinal != checked(ordinal + 1) ||
                previousUntil is not { } next || next.CompareTo(input.Time) != 0)) {
            throw new InvalidDataException("Нарушены порядок, время или геометрия кадров оптического потока");
        }

        using Mat image = pixels.BorrowBgra();
        Mat working = image;
        if (workingSize.Width != size.Width || workingSize.Height != size.Height) {
            Cv2.Resize(image, reduced, workingSize, interpolation: InterpolationFlags.Area);
            working = reduced;
        }
        Cv2.CvtColor(working, currentGray, ColorConversionCodes.BGRA2GRAY);
        token.ThrowIfCancellationRequested();

        VideoForegroundFrame? result = null;
        if (publish) {
            bool hasPair = previousOrdinal.HasValue;
            byte[] rgb = GC.AllocateUninitializedArray<byte>(checked((int)(GetPixelCount(size) * 3)));
            using Mat snapshot = Mat.FromPixelData(size.Height, size.Width, MatType.CV_8UC3, rgb);
            IntPtr address = snapshot.Data;
            Cv2.CvtColor(image, snapshot, ColorConversionCodes.BGRA2RGB);
            if (snapshot.Data != address) {
                throw new InvalidOperationException("OpenCV изменил буфер входа оптического потока");
            }

            byte[] bgra = [];
            int arrows = 0;
            if (hasPair) {
                token.ThrowIfCancellationRequested();
                Cv2.CalcOpticalFlowFarneback(previousGray, currentGray, flow, 0.5, 3, WindowSize, 3,
                    PolynomialSize, 1.2, OpticalFlowFlags.None);
                token.ThrowIfCancellationRequested();
                (bgra, arrows) = DrawArrows(snapshot, token);
            }

            result = new(input.Time, input.Until, input.Ordinal, size, rgb, ReadOnlyMemory<byte>.Empty) {
                OpticalFlow = new(size, hasPair, arrows, bgra)
            };
        }

        token.ThrowIfCancellationRequested();
        (previousGray, currentGray) = (currentGray, previousGray);
        previousOrdinal = input.Ordinal;
        previousUntil = input.Until;
        return result;
    }

    // переносим точки и смещения из рабочей сетки в геометрию кадра и выделяем буфер только для видимых стрелок
    private (byte[] Bgra, int Arrows) DrawArrows(Mat rgb, CancellationToken token) {
        double scaleX = size.Width / (double)workingSize.Width;
        double scaleY = size.Height / (double)workingSize.Height;
        int thickness = Math.Max(1, (int)Math.Round(Math.Min(scaleX, scaleY) / 2));
        CvSize bounds = new(size.Width, size.Height);
        Mat? overlay = null;
        byte[] bgra = [];
        int arrows = 0;
        try {
            for (int y = Math.Min(GridStep / 2, workingSize.Height / 2); y < workingSize.Height; y += GridStep) {
                token.ThrowIfCancellationRequested();
                for (int x = Math.Min(GridStep / 2, workingSize.Width / 2); x < workingSize.Width; x += GridStep) {
                    Vec2f shift = flow.At<Vec2f>(y, x);
                    double dx = shift.Item0;
                    double dy = shift.Item1;
                    double length = Math.Sqrt(dx * dx + dy * dy);
                    if (!double.IsFinite(length) || length < NoiseThreshold) {
                        continue;
                    }
                    double gain = Math.Min(ArrowGain, MaximumArrowLength / length);
                    double centerX = (x + 0.5) * scaleX - 0.5;
                    double centerY = (y + 0.5) * scaleY - 0.5;
                    CvPoint start = new((int)Math.Round(centerX), (int)Math.Round(centerY));
                    CvPoint end = new((int)Math.Round(centerX + dx * scaleX * gain),
                        (int)Math.Round(centerY + dy * scaleY * gain));
                    if (!Cv2.ClipLine(bounds, ref start, ref end) || start == end) {
                        continue;
                    }

                    if (overlay is null) {
                        bgra = GC.AllocateUninitializedArray<byte>(checked((int)VideoEditorFrame.GetBufferBytes(size)));
                        overlay = Mat.FromPixelData(size.Height, size.Width, MatType.CV_8UC4, bgra);
                        IntPtr address = overlay.Data;
                        Cv2.CvtColor(rgb, overlay, ColorConversionCodes.RGB2BGRA);
                        if (overlay.Data != address) {
                            throw new InvalidOperationException("OpenCV изменил буфер показа оптического потока");
                        }
                    }

                    Cv2.ArrowedLine(overlay, start, end, OutlineColor, thickness + 2, LineTypes.AntiAlias, tipLength: 0.4);
                    Cv2.ArrowedLine(overlay, start, end, ArrowColor, thickness, LineTypes.AntiAlias, tipLength: 0.4);
                    arrows++;
                }
            }
            return (bgra, arrows);
        }
        finally {
            overlay?.Dispose();
        }
    }

    // уменьшаем длинную сторону рабочего кадра с сохранением пропорций
    private static CvSize GetWorkingSize(PixelSize size) {
        _ = GetPixelCount(size);
        double scale = Math.Min(1, WorkingLongSide / (double)Math.Max(size.Width, size.Height));
        return new(Math.Max(1, (int)Math.Round(size.Width * scale)),
            Math.Max(1, (int)Math.Round(size.Height * scale)));
    }

    private static long GetPixelCount(PixelSize size) {
        if (size.Width <= 0 || size.Height <= 0) {
            throw new ArgumentException("Неверный размер видеокадра для оптического потока", nameof(size));
        }
        return (long)size.Width * size.Height;
    }

    // освобождаем рабочие матрицы при завершении или замене прохода
    public void Dispose() {
        previousGray.Dispose();
        currentGray.Dispose();
        reduced.Dispose();
        flow.Dispose();
    }
}

// хранит готовые стрелки и отличает отсутствие пары от пары без заметного смещения
internal sealed class VideoOpticalFlowRaster(PixelSize size, bool hasPair, int arrowCount, byte[] bgra) {
    public PixelSize Size { get; } = size;
    public bool HasPair { get; } = hasPair;
    public int ArrowCount { get; } = arrowCount;
    public ReadOnlyMemory<byte> Bgra => bgra;
    public long BufferBytes => bgra.LongLength;

    // уже готовые bgra передаем в bitmap целиком или по строкам при выравнивании
    public bool TryCopyTo(ILockedFramebuffer target) {
        if (bgra.Length == 0) {
            return false;
        }
        int stride = checked(Size.Width * 4);
        if (target.Size != Size || target.Format != PixelFormat.Bgra8888 ||
            target.RowBytes < stride || target.Address == IntPtr.Zero) {
            throw new ArgumentException("Буфер показа не соответствует кадру оптического потока", nameof(target));
        }
        if (target.RowBytes == stride) {
            Marshal.Copy(bgra, 0, target.Address, bgra.Length);
        }
        else {
            for (int y = 0; y < Size.Height; y++) {
                Marshal.Copy(bgra, y * stride, target.Address + y * target.RowBytes, stride);
            }
        }
        return true;
    }
}
