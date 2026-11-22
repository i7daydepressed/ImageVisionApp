using System;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace ImageVisionApp.Services;

// хранит самостоятельные пиксели картинки без ссылки на bitmap или открытый файл
internal sealed class VideoBackgroundImage {
    private readonly byte[] rgb;

    public PixelSize Size { get; }
    public long BufferBytes => rgb.LongLength;

    // используем массив, который заполнил только захват фона
    private VideoBackgroundImage(PixelSize size, byte[] rgb) {
        Size = size;
        this.rgb = rgb;
    }

    // считаем временный bgra, целевой rgb и одну строку захвата без исходного bitmap
    public static long GetCaptureBufferBytes(PixelSize size) {
        if (size.Width <= 0 || size.Height <= 0) {
            throw new ArgumentException("Неверный размер фонового изображения", nameof(size));
        }
        return checked((long)size.Width * size.Height * 7 + size.Width * 4L);
    }

    // забираем видимые цвета, прозрачные пиксели получают вклад на черном фоне
    public static VideoBackgroundImage Capture(Bitmap bitmap, CancellationToken token = default) {
        ArgumentNullException.ThrowIfNull(bitmap);
        token.ThrowIfCancellationRequested();
        PixelSize size = bitmap.PixelSize;
        byte[] rgb = new byte[checked(size.Width * size.Height * 3)];
        byte[] row = new byte[checked(size.Width * 4)];
        using WriteableBitmap readback = new(size, new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using ILockedFramebuffer buffer = readback.Lock();
        bitmap.CopyPixels(buffer);
        for (int y = 0; y < size.Height; y++) {
            token.ThrowIfCancellationRequested();
            Marshal.Copy(buffer.Address + y * buffer.RowBytes, row, 0, row.Length);
            for (int x = 0; x < size.Width; x++) {
                int source = x * 4;
                int target = (y * size.Width + x) * 3;
                rgb[target] = row[source + 2];
                rgb[target + 1] = row[source + 1];
                rgb[target + 2] = row[source];
            }
        }
        token.ThrowIfCancellationRequested();
        return new(size, rgb);
    }

    // подготавливаем постоянный фон в размере выбранного видеокадра
    public VideoBackgroundRaster PrepareCover(PixelSize size, CancellationToken token = default) =>
        VideoBackgroundRaster.CreateCover(Size, rgb, size, token);
}

// неизменяемый растр одной геометрии можно использовать в разных кадрах очереди
internal sealed class VideoBackgroundRaster {
    private readonly byte[] rgb;

    public PixelSize Size { get; }
    public ReadOnlyMemory<byte> Rgb => rgb;
    public long BufferBytes => rgb.LongLength;

    // берем подготовленный массив в собственность одного растра
    private VideoBackgroundRaster(PixelSize size, byte[] rgb) {
        Size = size;
        this.rgb = rgb;
    }

    // берем центральную область в одинаковом масштабе по обеим осям без промежуточного растра
    internal static VideoBackgroundRaster CreateCover(PixelSize source, ReadOnlySpan<byte> image, PixelSize size,
        CancellationToken token) {
        token.ThrowIfCancellationRequested();
        if (size.Width <= 0 || size.Height <= 0) {
            throw new ArgumentException("Неверный размер видеокадра для фонового изображения", nameof(size));
        }
        if (source.Width <= 0 || source.Height <= 0 || image.Length != checked(source.Width * source.Height * 3)) {
            throw new ArgumentException("Размер фонового изображения не соответствует пикселям", nameof(image));
        }

        byte[] fitted = new byte[checked(size.Width * size.Height * 3)];
        if (size == source) {
            image.CopyTo(fitted);
            token.ThrowIfCancellationRequested();
            return new(size, fitted);
        }

        double step = Math.Min(source.Width / (double)size.Width, source.Height / (double)size.Height);
        double left = (source.Width - 1 - (size.Width - 1) * step) / 2;
        double top = (source.Height - 1 - (size.Height - 1) * step) / 2;
        for (int y = 0; y < size.Height; y++) {
            token.ThrowIfCancellationRequested();
            double sy = Math.Clamp(top + y * step, 0, source.Height - 1);
            int firstRow = (int)sy * source.Width * 3;
            int nextRow = Math.Min((int)sy + 1, source.Height - 1) * source.Width * 3;
            double dy = sy - (int)sy;
            for (int x = 0; x < size.Width; x++) {
                double sx = Math.Clamp(left + x * step, 0, source.Width - 1);
                int firstColumn = (int)sx * 3;
                int nextColumn = Math.Min((int)sx + 1, source.Width - 1) * 3;
                double dx = sx - (int)sx;
                int target = (y * size.Width + x) * 3;
                for (int channel = 0; channel < 3; channel++) {
                    double upper = image[firstRow + firstColumn + channel] * (1 - dx) +
                        image[firstRow + nextColumn + channel] * dx;
                    double lower = image[nextRow + firstColumn + channel] * (1 - dx) +
                        image[nextRow + nextColumn + channel] * dx;
                    fitted[target + channel] = (byte)Math.Round(upper * (1 - dy) + lower * dy, MidpointRounding.AwayFromZero);
                }
            }
        }
        token.ThrowIfCancellationRequested();
        return new(size, fitted);
    }
}
