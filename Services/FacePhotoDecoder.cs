using System;
using System.IO;
using System.Threading;
using ImageMagick;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal static class FacePhotoDecoder {
    // применяем ориентацию фото до чтения размеров и выгрузки плотных пикселей BGR8
    public static FaceImage Decode(Stream source, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(source);
        cancellationToken.ThrowIfCancellationRequested();
        if (!source.CanRead || (source.CanSeek && source.Position >= source.Length)) {
            throw new InvalidDataException("Фото пустое или недоступно для чтения.");
        }
        try {
            using MagickImage image = new(source);
            cancellationToken.ThrowIfCancellationRequested();
            image.AutoOrient();
            int width = checked((int)image.Width);
            int height = checked((int)image.Height);
            int length = FaceImage.GetBgr8Length(width, height);
            image.ColorSpace = ColorSpace.sRGB;
            image.Depth = 8;
            // черный фон дает видимые пиксели фото без скрытого цвета под прозрачностью
            if (image.HasAlpha) {
                image.BackgroundColor = MagickColors.Black;
                image.Alpha(AlphaOption.Remove);
            }
            cancellationToken.ThrowIfCancellationRequested();

            using IPixelCollection<byte> pixels = image.GetPixels();
            byte[]? bgr = pixels.ToByteArray(PixelMapping.BGR);
            if (bgr is null || bgr.Length != length) {
                throw new InvalidDataException("Не удалось получить полный BGR8-буфер фото.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            return FaceImage.TakeBgr8(width, height, bgr);
        }
        catch (MagickException error) {
            throw new InvalidDataException("Не удалось прочитать фото для обработки лица.", error);
        }
        catch (Exception error) when (error is OverflowException or ArgumentOutOfRangeException) {
            throw new InvalidDataException("Размеры фото превышают допустимые для обработки лица.", error);
        }
    }
}
