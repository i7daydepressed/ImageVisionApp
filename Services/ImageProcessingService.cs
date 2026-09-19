using System;
using ImageMagick;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

public class ImageProcessingService{

    public byte[] ApplyTransformations(
        byte[] originalImageData,
        ImageTransformationSettings settings
        ){
        // каждый раз берем базовое избрж
        using MagickImage processedImage = new MagickImage(originalImageData);

        //применяем параметры преобразования
        if (settings.BrightnessAdjustment != 0) {
            ChangeBrightness(
                processedImage,
                settings.BrightnessAdjustment);
        }

        if (settings.IsGrayscaleEnabled) {
            ConvertToGrayscale(processedImage);
        }
        //тут
        //тут и там
        //и тут ещо

        // возвращаем резулт
        byte[] processedImageData = processedImage.ToByteArray(MagickFormat.Png);

        return processedImageData;
    }


    private static void ChangeBrightness(//изменить яркость
        MagickImage processedImage,
        double brightnessAdjustment
        ){

        if (brightnessAdjustment < -100 ||
            brightnessAdjustment > 100) {

            throw new ArgumentOutOfRangeException(
                nameof(brightnessAdjustment),
                "яркость должна быть от -100 до 100");
        }

        processedImage.BrightnessContrast(//
            new Percentage(brightnessAdjustment),
            new Percentage(0));
    }



    [Obsolete]
    public byte[] ConvertToGrayscale(byte[] sourceImageData){
        // из исходных байтов создаём объект изображения magick
        using MagickImage magickImage =
            new MagickImage(sourceImageData);

        // метод изменяет объект
        // в серое вот этим https://github.com/ImageMagick/ImageMagick/blob/fb965f1b54a65ddb633f8c2eac4452c782c66d7f/MagickCore/enhance.c#L2487-L2666
        magickImage.Grayscale(PixelIntensityMethod.Rec709Luma);
            {
        // в нативном коде вариант Rec709Luma выбирает такую формулу чтобы rgb было умеренно серым в зависимости от текущ знач
        // intensity =
        //     0.212656 * red +
        //     0.715158 * green +
        //     0.072186 * blue;

        // мега примерно что происходит
        // for (int y = 0; y < imageHeight; y++){
        //     for (int x = 0; x < imageWidth; x++){
        //         double gray =
        //             0.212656 * red +
        //             0.715158 * green +
        //             0.072186 * blue;
        //         red = gray;
        //         green = gray;
        //         blue = gray;
        //     }
        // }
            }
        // назад в байты
        byte[] grayscaleImageData = magickImage.ToByteArray(MagickFormat.Png);

        return grayscaleImageData;
    }
    private static void ConvertToGrayscale(MagickImage processedImage){
        // из исходных байтов создаём объект изображения magick
        // метод изменяет объект
        // в серое вот этим https://github.com/ImageMagick/ImageMagick/blob/fb965f1b54a65ddb633f8c2eac4452c782c66d7f/MagickCore/enhance.c#L2487-L2666
        processedImage.Grayscale(PixelIntensityMethod.Rec709Luma);
            {
        // в нативном коде вариант Rec709Luma выбирает такую формулу чтобы rgb было умеренно серым в зависимости от текущ знач
        // intensity =
        //     0.212656 * red +
        //     0.715158 * green +
        //     0.072186 * blue;

        // мега примерно что происходит
        // for (int y = 0; y < imageHeight; y++){
        //     for (int x = 0; x < imageWidth; x++){
        //         double gray =
        //             0.212656 * red +
        //             0.715158 * green +
        //             0.072186 * blue;
        //         red = gray;
        //         green = gray;
        //         blue = gray;
        //     }
        // }
            }
    }


}
