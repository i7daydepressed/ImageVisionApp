using ImageMagick;

namespace ImageVisionApp.Services;

public class ImageProcessingService{
    public byte[] ConvertToGrayscale(byte[] sourceImageData)
    {
        // из исходных байтов создаём объект изображения magick
        using MagickImage magickImage =
            new MagickImage(sourceImageData);

        // метод изменяет объект
        // в серое вот этим https://github.com/ImageMagick/ImageMagick/blob/fb965f1b54a65ddb633f8c2eac4452c782c66d7f/MagickCore/enhance.c#L2487-L2666
        magickImage.Grayscale(PixelIntensityMethod.Rec709Luma);

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

        // назад в байты
        byte[] grayscaleImageData = magickImage.ToByteArray(MagickFormat.Png);

        return grayscaleImageData;
    }
}
