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

        if (settings.SaturationPercentage != 100) {//насыщенность
            ChangeSaturation(
                processedImage,
                settings.SaturationPercentage);
        }
        if (settings.ContrastAdjustment != 0) {//контраст
            ChangeContrast(
                processedImage,
                settings.ContrastAdjustment);
        }

        if (settings.IsGrayscaleEnabled) {//серое
            ConvertToGrayscale(processedImage);

            // линейная и нелинчб коррекция
            switch (settings.CorrectionMode) {
                case GrayscaleCorrectionMode.Linear:
                    ApplyLinearCorrection(processedImage);
                    break;

                case GrayscaleCorrectionMode.Nonlinear:
                    ApplyLogarithmicCorrection(processedImage);
                    break;
            }
        }

        if (settings.RotationDegrees != 0) {//поворот
            RotateImage(
                processedImage,
                settings.RotationDegrees);
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

        processedImage.BrightnessContrast(//https://github.com/ImageMagick/ImageMagick/blob/7.1.2-31/MagickCore/enhance.c#L225-L252
            new Percentage(brightnessAdjustment),
            new Percentage(0));
        {//новое r g b пикселя = старое + (( ( текущий % яркости - исход процент) / 100) × 255)
            // яркость и контраст преобразуются в лин функцию
            //
            // result = slope * source + intercept;
            //
            // так как contrast = 0:
            // slope = 1;
            // intercept = brightnessAdjustment / 100;
            //
            // поэтому для каждого цветового канала:
            // result = Clamp(
            //     source + brightnessAdjustment / 100,
            //     0,
            //     1);
            //
            // Clamp не позволяет значению выйти за допустимые границы
            // Clamp(-0.2, 0, 1) → 0
            // Clamp(0.6, 0, 1)  → 0.6
            // Clamp(1.3, 0, 1)  → 1

            // в нашей сборке Magick.NET Q8 значения каналов можно представить диапазоном от 0 до 255
            //
            // double offset = brightnessAdjustment / 100.0 * 255.0;
            //
            // примерно это работает так:
            //
            // for (int y = 0; y < imageHeight; y++) {
            //     for (int x = 0; x < imageWidth; x++) {
            //
            //         red = Clamp(red + offset, 0, 255);
            //         green = Clamp(green + offset, 0, 255);
            //         blue = Clamp(blue + offset, 0, 255);
            //     }
            // }
            //
            // например, brightnessAdjustment = 20:
            // offset = 20 / 100 * 255 = 51
            //
            // RGB(100, 150, 240)
            // превращается примерно в RGB(151, 201, 255)
            // синий ограничивается значением 255
        }
    }

    private static void ChangeSaturation(//насыщенность
        MagickImage processedImage,
        double saturationPercentage
        ){
// при уменьшении насыщенности каналы приближаются к некоторому серому(середин) значению
// то есть идут на сближение к друг другу, при насыщенности 0 - все 3 канала становяся одинаковыми
        
        // середина = (max(R, G, B) + min(R, G, B)) / 2
        // Rновый = середина + (R - середина) × коэффициент
        // Gновый = середина + (G - середина) × коэффициент
        // Bновый = середина + (B - середина) × коэффициент

        // насыщ 150% 150 / 100 = 1,5
        // R = 125 + (200 - 125) × 1,5 = 237,5
        // G = 125 + (100 - 125) × 1,5 = 87,5
        // B = 125 + (50 - 125) × 1,5 = 12,5
        
        if (saturationPercentage < 0 ||
            saturationPercentage > 200) {//https://github.com/dlemstra/Magick.NET/blob/0491ab4a43cac00f0326319a6daa8a59136b0901/src/Magick.NET/MagickImage.cs#L4180-L4211
                                        //https://github.com/dlemstra/Magick.Native/blob/f36d33b6028f7799e2db39a4ae6e9c6de2aa3c0d/src/Magick.Native/MagickImage.c#L1813-L1818
            throw new ArgumentOutOfRangeException(//https://github.com/ImageMagick/ImageMagick/blob/d956bba0bacd70dc1fbe0d8dc3b87dca14086496/MagickCore/enhance.c#L3550-L3567
                nameof(saturationPercentage),
                "насыщенность должна быть от 0 до 200");
        }

        processedImage.Modulate(
            new Percentage(100),
            new Percentage(saturationPercentage));
    }

    private static void ChangeContrast(//контрастность
        MagickImage processedImage,
        double contrastAdjustment
        ) {

        if (contrastAdjustment < -100 ||
            contrastAdjustment > 100) {

            throw new ArgumentOutOfRangeException(
                nameof(contrastAdjustment),
                "контрастность должна быть от -100 до 100");
        }

        processedImage.BrightnessContrast(//https://github.com/dlemstra/Magick.NET/blob/0491ab4a43cac00f0326319a6daa8a59136b0901/src/Magick.NET/MagickImage.cs#L1415-L1436
            new Percentage(0),//https://github.com/dlemstra/Magick.Native/blob/f36d33b6028f7799e2db39a4ae6e9c6de2aa3c0d/src/Magick.Native/MagickImage.c#L865-L872
            new Percentage(contrastAdjustment));//https://github.com/ImageMagick/ImageMagick/blob/d956bba0bacd70dc1fbe0d8dc3b87dca14086496/MagickCore/enhance.c#L225-L252
        {
            // контрастность определяет разницу между светлыми и темными областями изображения
            // положительное значение усиливает различия
            // отрицательное значение уменьшает различия

            // яркость каждого цветового канала сравнивается со средней точкой диапазона 127+-
            
            // При увеличении контрастности
            // значения меньше середины уменьшаются
            // значения больше середины увеличиваются
            // До обработки:    50, 100, 150, 200
            // После усиления:  20,  80, 170, 230

            // При уменьшении контрастности значения, наоборот, приближаются к середине
            // До обработки:       50, 100, 150, 200
            // После уменьшения:   90, 115, 140, 165

            // Rновый = 127.5 + (R - 127.5) * coefficient
            // Gновый = 127.5 + (G - 127.5) * coefficient
            // Bновый = 127.5 + (B - 127.5) * coefficient

            // RGB(50, 100, 200), контраст +20
            //
            // coefficient = 100 / (100 - 20) = 1.25
            //
            // R = 127.5 + (50 - 127.5) * 1.25 = 30.625
            // G = 127.5 + (100 - 127.5) * 1.25 = 93.125
            // B = 127.5 + (200 - 127.5) * 1.25 = 218.125
        }
    }

    private static void ApplyLinearCorrection(//линейная чб коррекция
        MagickImage processedImage
        ){

        processedImage.AutoLevel();//https://github.com/dlemstra/Magick.NET/blob/main/src/Magick.NET/MagickImage.cs#L1229-L1244
        //https://github.com/ImageMagick/ImageMagick/blob/main/MagickCore/enhance.c#L174-L192
        //AutoLevel() находит минимальную и максимальную яркость серого изображения и растягивает этот диапазон до 0–255
        //I' = 255 * (I - Imin) / (Imax - Imin)
        {
            // после перевода в градации серого:
            // R = G = B = I
            //
            // AutoLevel находит Imin и Imax
            // среди всех пикселей изображения
            //
            // новое значение серого пикселя:
            // Iновый = (I - Imin) / (Imax - Imin) * 255
            //
            // затем:
            // Rновый = Iновый
            // Gновый = Iновый
            // Bновый = Iновый
            //
            // минимальная яркость изображения становится 0
            // максимальная яркость изображения становится 255
            // остальные значения растягиваются между ними
            //
            // пример:
            // Imin = 50
            // Imax = 200
            // текущий пиксель RGB(100, 100, 100)
            //
            // Iновый = (100 - 50) / (200 - 50) * 255
            // Iновый = 50 / 150 * 255
            // Iновый = 85
            //
            // RGB(100, 100, 100) -> RGB(85, 85, 85)
            //
            // для всего изображения:
            // 50  -> 0
            // 100 -> 85
            // 150 -> 170
            // 200 -> 255
        }

    }

    private static void ApplyLogarithmicCorrection(// нелин логариф чб коррекц
        MagickImage processedImage
        ) {
        const double logarithmicScale = 255.0;
        processedImage.Evaluate(
            Channels.Gray,
            EvaluateOperator.Log,
            logarithmicScale);
        // применяет к яркости каждого пикселя логарифмическое преобразование
        // Оно сильнее осветляет тёмные участки и слабее изменяет светлые, благодаря чему проявляются детали в тенях
        // https://github.com/ImageMagick/ImageMagick/blob/main/MagickCore/statistic.c#L330-L335
        //I' = 255 * ln(I + 1) / ln(256)
        {
            // после перевода в градации серого:
            // R = G = B = I
            //
            // к яркости каждого пикселя применяется логарифм:
            //
            // Iновый = 255 * ln(I + 1) / ln(256)
            //
            // затем:
            // Rновый = Iновый
            // Gновый = Iновый
            // Bновый = Iновый
            //
            // пример для пикселя RGB(50, 50, 50):
            //
            // Iновый = 255 * ln(50 + 1) / ln(256)
            // Iновый = 255 * ln(51) / ln(256)
            // Iновый примерно равен 181
            //
            // RGB(50, 50, 50) -> RGB(181, 181, 181)
            //
            // другие примеры:
            // 0   -> 0
            // 10  -> 110
            // 50  -> 181
            // 100 -> 212
            // 128 -> 223
            // 200 -> 244
            // 255 -> 255
            //
            // поэтому тёмные пиксели осветляются очень сильно,
            // а уже светлые пиксели изменяются слабее
        }
    }

    private static void RotateImage(//поворот изображения
        MagickImage processedImage,
        int rotationDegrees
        ){

        if (rotationDegrees < 0 ||
            rotationDegrees >= 360 ||
            rotationDegrees % 90 != 0) {

            throw new ArgumentOutOfRangeException(
                nameof(rotationDegrees),
                "угол поворота должен быть равен 0, 90, 180 или 270");
        }

        processedImage.Rotate(rotationDegrees);//https://github.com/dlemstra/Magick.NET/blob/main/src/Magick.NET/MagickImage.cs#L5743-L5753
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
