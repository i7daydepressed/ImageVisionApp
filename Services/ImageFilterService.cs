using System;
using ImageMagick;

namespace ImageVisionApp.Services;

// фильтры работают с текущим изображением, состояние очереди и интерфейса сюда не передаем
public static class ImageFilterService {
    public static void Sharpen(MagickImage image) {
        ArgumentNullException.ThrowIfNull(image);
        image.ColorSpace = ColorSpace.sRGB;
        image.Depth = 8;

        // читаем отдельную копию: экспорт RGBA не меняет наличие alpha у рабочего изображения
        using MagickImage source = new MagickImage(image);
        byte[] sourceRgba = source.ToByteArray(MagickFormat.Rgba);
        byte[] sharpenedRgb = new byte[sourceRgba.Length / 4 * 3];
        int width = checked((int)image.Width);
        int height = checked((int)image.Height);

        // матрица 3x3: {0,-1,0}, {-1,5,-1}, {0,-1,0}, опора в центре
        // новый канал = Clamp(5 * центр - слева - справа - сверху - снизу, 0, 255)
        // центр 100 и четыре соседа по 90 дают 5 * 100 - 4 * 90 = 140
        // https://docs.opencv.org/4.12.0/d7/d37/tutorial_mat_mask_operations.html
        for (int y = 0; y < height; y++) {
            int topY = Math.Max(0, y - 1);
            int bottomY = Math.Min(height - 1, y + 1);
            for (int x = 0; x < width; x++) {
                // за краем повторяем ближайший пиксель, как Edge в морфологии
                int leftX = Math.Max(0, x - 1);
                int rightX = Math.Min(width - 1, x + 1);
                int center = (y * width + x) * 4;
                int left = (y * width + leftX) * 4;
                int right = (y * width + rightX) * 4;
                int top = (topY * width + x) * 4;
                int bottom = (bottomY * width + x) * 4;
                int target = (y * width + x) * 3;
                for (int channel = 0; channel < 3; channel++) {
                    // все соседи читаются из входа шага, готовый пиксель не влияет на следующий
                    int value = 5 * sourceRgba[center + channel]
                        - sourceRgba[left + channel] - sourceRgba[right + channel]
                        - sourceRgba[top + channel] - sourceRgba[bottom + channel];
                    sharpenedRgb[target + channel] = (byte)Math.Clamp(value, 0, 255);
                }
            }
        }

        // записываем только RGB, исходная alpha и метаданные остаются у рабочего объекта
        // https://github.com/dlemstra/Magick.NET/blob/298053457c16f413a9df6edaca668b12289bea2d/src/Magick.NET/MagickImage.cs#L3455-L3480
        image.ImportPixels(sharpenedRgb,
            new PixelImportSettings(image.Width, image.Height, StorageType.Char, PixelMapping.RGB));
    }
}
