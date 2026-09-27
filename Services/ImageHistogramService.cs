using System;
using ImageMagick;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

public class ImageHistogramService {

    public ImageHistogramData CalculateHistogram(
        byte[] imageData
        ) {

        using MagickImage image =
            new MagickImage(imageData);

        using IPixelCollection<byte> pixels =
            image.GetPixels();

        byte[] rgbPixelData = pixels.ToByteArray(PixelMapping.RGB)
            ?? throw new InvalidOperationException(
                "не удалось получить пиксели изображения");
        // Индекс:    0    1   2    3    4   5    6   7   8
        // Значение: 255   0   0   255  10   0   20  10   0
        //         └ пиксель 1 ┘ └ пиксель 2 ┘ └ пиксель 3 ┘

        if (rgbPixelData.Length % 3 != 0) {throw new InvalidOperationException(
                "получен некорректный набор RGB-пикселей");
        }

        long[] redValues = new long[256];
        long[] greenValues = new long[256];
        long[] blueValues = new long[256];

        for (int i = 0; i < rgbPixelData.Length; i += 3) {//?

            byte red = rgbPixelData[i];
            byte green = rgbPixelData[i + 1];
            byte blue = rgbPixelData[i + 2];

            redValues[red]++;
            greenValues[green]++;
            blueValues[blue]++;
        }
        // каждый пиксель 3 значения в трех каналах
        // регаем каждое значение каждого канала и составляем стату в каком канале сколько значений

        return new ImageHistogramData(redValues,greenValues,blueValues);//передаем стату
    }
}