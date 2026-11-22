using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Platform;
using OpenCvSharp;
using CvSize = OpenCvSharp.Size;

namespace ImageVisionApp.Services;

// хранит готовый bgra того же кадра, где меняются только области маски
internal sealed class VideoMotionBlurRaster {
    public const int MinimumRadius = 1;
    public const int MaximumRadius = 12;
    private const int Channels = 3;
    private readonly byte[] bgra;

    public PixelSize Size { get; }
    public int Radius { get; }
    public ReadOnlyMemory<byte> Bgra => bgra;
    public long BufferBytes => bgra.LongLength;

    // пустой буфер означает, что вывод использует уже учтенный исходный rgb
    private VideoMotionBlurRaster(PixelSize size, int radius, byte[] bgra) {
        Size = size;
        Radius = radius;
        this.bgra = bgra;
    }

    // передаем в очередь плотный bgra только если маска требует обработки
    public static long GetBufferBytes(PixelSize size) {
        if (size.Width <= 0 || size.Height <= 0) {
            throw new ArgumentException("Неверный размер видеокадра для размытия", nameof(size));
        }
        return checked((long)size.Width * size.Height * 4);
    }

    // учитываем два временных rgb и рабочие строки cpu-реализации OpenCV 4.13
    public static long GetScratchBytes(PixelSize size, int radius) {
        int kernel = GetKernelSize(radius);
        long rgbBytes = GetBufferBytes(size) / 4 * Channels;
        long stripe = (long)kernel * (size.Width * (long)Channels * sizeof(ushort) + 2L * IntPtr.Size);
        int stripes = Math.Min(size.Height, Environment.ProcessorCount);
        return checked(2 * rgbBytes + stripe * stripes + 2L * kernel * sizeof(ushort));
    }

    // размываем rgb только по маске и сразу готовим непрозрачный bgra для показа
    public static VideoMotionBlurRaster Create(VideoForegroundFrame input, int radius, CancellationToken token = default) {
        token.ThrowIfCancellationRequested();
        PixelSize size = input.Size;
        int kernel = GetKernelSize(radius);
        int bgraLength = checked((int)GetBufferBytes(size));
        int length = bgraLength / 4 * Channels;
        ReadOnlySpan<byte> mask = input.Mask.Span;
        if (input.Rgb.Length != length || mask.Length != length / Channels) {
            throw new ArgumentException("Размер кадровых данных не соответствует маске размытия", nameof(input));
        }
        if (mask.IndexOfAnyExcept((byte)0) < 0) {
            return new(size, radius, []);
        }

        // матрица читает закрепленный исходный массив, отдельный rgb результата используется как выход OpenCV
        byte[] source = MemoryMarshal.TryGetArray(input.Rgb, out ArraySegment<byte> segment) &&
            segment.Array is { } array && segment.Offset == 0 && segment.Count == array.Length ? array : input.Rgb.ToArray();
        byte[] result = new byte[length];
        using Mat original = Mat.FromPixelData(size.Height, size.Width, MatType.CV_8UC3, source);
        using Mat blurred = Mat.FromPixelData(size.Height, size.Width, MatType.CV_8UC3, result);
        IntPtr address = blurred.Data;
        Cv2.GaussianBlur(original, blurred, new CvSize(kernel, kernel), 0, 0, BorderTypes.Reflect101);
        token.ThrowIfCancellationRequested();
        if (blurred.Data != address) {
            throw new InvalidOperationException("OpenCV изменил буфер результата размытия");
        }

        ReadOnlySpan<byte> rgb = input.Rgb.Span;
        for (int y = 0; y < size.Height; y++) {
            token.ThrowIfCancellationRequested();
            int firstPixel = y * size.Width;
            for (int x = 0; x < size.Width; x++) {
                int pixel = firstPixel + x;
                if (mask[pixel] == 0) {
                    int offset = pixel * Channels;
                    result[offset] = rgb[offset];
                    result[offset + 1] = rgb[offset + 1];
                    result[offset + 2] = rgb[offset + 2];
                }
            }
        }
        token.ThrowIfCancellationRequested();
        byte[] bgra = new byte[bgraLength];
        using Mat ready = Mat.FromPixelData(size.Height, size.Width, MatType.CV_8UC4, bgra);
        IntPtr readyAddress = ready.Data;
        Cv2.CvtColor(blurred, ready, ColorConversionCodes.RGB2BGRA);
        token.ThrowIfCancellationRequested();
        if (ready.Data != readyAddress) {
            throw new InvalidOperationException("OpenCV изменил буфер показа размытия");
        }
        return new(size, radius, bgra);
    }

    // копируем готовый кадр целиком или по строкам, если bitmap использует выравнивание
    public bool TryCopyTo(ILockedFramebuffer target) {
        if (bgra.Length == 0) {
            return false;
        }
        int stride = checked(Size.Width * 4);
        if (target.Size != Size || target.Format != PixelFormat.Bgra8888 ||
            target.RowBytes < stride || target.Address == IntPtr.Zero) {
            throw new ArgumentException("Буфер показа не соответствует кадру размытия", nameof(target));
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

    // переводим радиус в нечетный размер ядра с одним центральным пикселем
    private static int GetKernelSize(int radius) {
        if (radius is < MinimumRadius or > MaximumRadius) {
            throw new ArgumentOutOfRangeException(nameof(radius), "Радиус размытия должен быть от 1 до 12 пикселей");
        }
        return 2 * radius + 1;
    }
}
