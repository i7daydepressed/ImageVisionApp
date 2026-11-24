using System;
using OpenCvSharp;

namespace ImageVisionApp.Models;

internal sealed class FaceImage {
    private const int BytesPerPixel = 3;
    private readonly byte[] pixels;

    public int Width { get; }
    public int Height { get; }
    public ReadOnlySpan<byte> Bgr8 => pixels;

    // сохраняем плотные пиксели и размеры уже ориентированного снимка
    private FaceImage(int width, int height, byte[] ownedPixels) {
        Width = width;
        Height = height;
        pixels = ownedPixels;
    }

    // копируем пиксели кадра, пока источник еще держит свой буфер
    public static FaceImage FromBgr8(int width, int height, ReadOnlySpan<byte> pixels) {
        if (pixels.Length != GetBgr8Length(width, height)) {
            throw new ArgumentException("Длина BGR8-буфера не совпадает с размерами кадра.", nameof(pixels));
        }
        return new FaceImage(width, height, pixels.ToArray());
    }

    // забираем новый буфер фото или выровненного лица без повторного копирования
    internal static FaceImage TakeBgr8(int width, int height, byte[] pixels) {
        if (pixels.Length != GetBgr8Length(width, height)) {
            throw new ArgumentException("Длина BGR8-буфера не совпадает с размерами кадра.", nameof(pixels));
        }
        return new FaceImage(width, height, pixels);
    }

    // проверяем обе стороны и число пикселей до умножения на три байта
    internal static int GetBgr8Length(int width, int height) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);
        if ((long)width * height > Array.MaxLength / BytesPerPixel) {
            throw new ArgumentOutOfRangeException(nameof(width), "Размеры кадра превышают допустимую длину BGR8-буфера.");
        }
        return width * height * BytesPerPixel;
    }

    // закрепляем собственный массив для чтения до освобождения Mat
    internal Mat BorrowBgr8() => Mat.FromPixelData(Height, Width, MatType.CV_8UC3, pixels);
}
