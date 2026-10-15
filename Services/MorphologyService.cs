using System;
using System.Globalization;
using System.Text;
using ImageMagick;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

// алгоритм работает с изображением и проверенной моделью, без состояния окна или редактора
public static class MorphologyService {
    public static void Erode(MagickImage image, StructuralElement element) {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(element);

        // плоская эрозия: единицы задают соседство, значения матрицы не являются весами
        // S = {(column - AnchorColumn, row - AnchorRow) для всех активных клеток}
        // для канала c из R,G,B: result_c(x,y) = min image_c(x + dx, y + dy), (dx,dy) из S
        // опору совмещаем с текущим пикселем, ее клетка не обязана быть активной
        // например, соседи (220,40,180) и (70,190,90) дают (70,40,90)
        // минимум считаем по каждому каналу, поэтому итог не обязан совпадать с одним соседом
        // для ЧБ значения каналов равны: min(220,70,180) = 70, светлая область сужается
        // если опора неактивна, текущий пиксель может не входить в минимум: форма еще и смещается
        // Erode использует исходную ориентацию ядра, для его прямого вызова смещения не отражаем
        // описание ядра: https://usage.imagemagick.org/morphology/#user
        // реализация минимума: https://github.com/ImageMagick/ImageMagick/blob/7.1.2-31/MagickCore/morphology.c
        // в ImageMagick 7.1.2-31 Erode начинает минимум со значения текущего пикселя
        // при нулевой опоре это лишний сосед: например, вместо правого 200 остается текущий 90
        // для такой формы используем min(v) = 255 - max(255 - v) в установленной Q8-сборке
        // Dilate начинает максимум с 0 и отражает ядро, поэтому заранее отражаем форму и опору
        // две инверсии RGB возвращают требуемую эрозию, alpha в них не участвует
        bool useComplement = !element.IsActive(element.AnchorRow, element.AnchorColumn);
        string kernel = CreateUserKernel(element, useComplement);

        // приводим цвета к sRGB, чтобы R,G,B означали именно цветовые каналы, а не C,M,Y
        // это преобразование цветового пространства, не обязательный перевод в ЧБ
        image.ColorSpace = ColorSpace.sRGB;
        // Q8 хранит 8-битные каналы, даже если входной PNG был палитрой с глубиной 4
        image.Depth = 8;
        VirtualPixelMethod previousVirtualPixelMethod = image.VirtualPixelMethod;
        try {
            // за границей читаем ближайший крайний пиксель: X и Y ограничиваются размером изображения
            // для ширины 3 запрос x=-1 дает x=0, запрос x=3 дает x=2
            // искусственный черный фон не добавляем, размер изображения не меняем
            image.VirtualPixelMethod = VirtualPixelMethod.Edge;
            if (useComplement) {
                image.Negate(Channels.RGB);
            }
            image.Morphology(new MorphologySettings {
                Method = useComplement ? MorphologyMethod.Dilate : MorphologyMethod.Erode,
                UserKernel = kernel,
                Channels = Channels.RGB,
                Iterations = 1
            });
            if (useComplement) {
                image.Negate(Channels.RGB);
            }
            // RGB изменены, alpha текущего пикселя сохранена, маску прозрачности не сужаем
            // цвета полностью прозрачных соседей тоже входят в минимум, если их позиции активны
            // операция читает прежнее изображение и формирует результат отдельно
            // новое значение слева не становится исходным соседом для следующего пикселя
        }
        finally {
            // выбор границы относится к этой операции, не к последующим шагам очереди
            image.VirtualPixelMethod = previousVirtualPixelMethod;
        }
    }

    public static void Dilate(MagickImage image, StructuralElement element) {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(element);

        // плоская дилатация берет максимум отдельно по R,G,B, единицы задают соседство
        // S = {(column - AnchorColumn, row - AnchorRow) для активных клеток}
        // result_c(x,y) = max image_c(x - dx, y - dy), (dx,dy) из S
        // знак минус нужен для дилатации с отраженным соседством, у эрозии используется плюс
        // на симметричном кресте разница незаметна, на односторонней форме меняет направление
        // например, S={(+1,-1),(-1,0)}: для P(100,50) читаем (99,51) и (101,50)
        // соседи (220,40,180) и (70,190,90) дают (220,190,180)
        // итоговый цвет не обязан совпадать с одним соседом, максимум выбирается по каждому каналу
        // на ЧБ max(220,70,180) = 220, светлые детали расширяются при обычной активной опоре
        // для [0,0,1] с опорой на среднем нуле dx=+1: результат читает x-1
        // единственный сосед дает сдвиг содержимого вправо, без обязательного расширения
        // при неактивной опоре текущий пиксель не входит в максимум автоматически
        // native Dilate начинает максимум с 0, поэтому обход через Negate здесь не нужен
        // native сам отражает форму и опору, передаем исходное ядро с reflect=false
        // реализация: https://github.com/ImageMagick/ImageMagick/blob/7.1.2-31/MagickCore/morphology.c
        string kernel = CreateUserKernel(element, false);

        image.ColorSpace = ColorSpace.sRGB;
        image.Depth = 8;
        VirtualPixelMethod previousVirtualPixelMethod = image.VirtualPixelMethod;
        try {
            // за границей повторяем ближайший крайний пиксель, как в эрозии
            // результат строится из прежних значений, уже измененные соседи не читаются повторно
            image.VirtualPixelMethod = VirtualPixelMethod.Edge;
            image.Morphology(new MorphologySettings {
                Method = MorphologyMethod.Dilate,
                UserKernel = kernel,
                Channels = Channels.RGB,
                Iterations = 1
            });
            // размер и alpha текущего пикселя сохраняем, расширяем цвета, а не маску прозрачности
            // RGB полностью прозрачного соседа участвует, если его позиция входит в элемент
        }
        finally {
            image.VirtualPixelMethod = previousVirtualPixelMethod;
        }
    }

    public static void Open(MagickImage image, StructuralElement element) {
        // открытие: сначала минимум по соседству, затем максимум по результату первого прохода
        // для обычной симметричной формы убирает небольшие светлые детали на темном фоне
        // оба прохода используют один снимок, alpha и правило края сохраняют Erode/Dilate
        Erode(image, element);
        Dilate(image, element);
    }

    public static void Close(MagickImage image, StructuralElement element) {
        // закрытие: сначала максимум по соседству, затем минимум по результату первого прохода
        // для обычной симметричной формы заполняет небольшие темные разрывы на светлом фоне
        // первый проход не публикуется отдельно и не добавляет второй шаг в очередь
        Dilate(image, element);
        Erode(image, element);
    }

    public static void Gradient(MagickImage image, StructuralElement element) {
        ArgumentNullException.ThrowIfNull(image);
        ArgumentNullException.ThrowIfNull(element);

        VirtualPixelMethod previousVirtualPixelMethod = image.VirtualPixelMethod;
        try {
            // оба прохода начинаются с одного входа этого шага, а не с результата друг друга
            using MagickImage dilated = new MagickImage(image);
            using MagickImage eroded = new MagickImage(image);
            Dilate(dilated, element);
            Erode(eroded, element);

            byte[] dilatedRgba = dilated.ToByteArray(MagickFormat.Rgba);
            byte[] erodedRgba = eroded.ToByteArray(MagickFormat.Rgba);
            byte[] gradientRgb = new byte[dilatedRgba.Length / 4 * 3];
            for (int rgbaIndex = 0, rgbIndex = 0; rgbaIndex < dilatedRgba.Length; rgbaIndex += 4, rgbIndex += 3) {
                for (int channel = 0; channel < 3; channel++) {
                    // новый канал = max(0, дилатация - эрозия), например 200 - 70 = 130
                    // при неактивной опоре разность может быть отрицательной: 40 - 90 дает 0
                    gradientRgb[rgbIndex + channel] = (byte)Math.Max(
                        0, dilatedRgba[rgbaIndex + channel] - erodedRgba[rgbaIndex + channel]);
                }
            }

            image.ColorSpace = ColorSpace.sRGB;
            image.Depth = 8;
            // передаем только RGB, исходная alpha и метаданные текущего изображения сохраняются
            // https://github.com/dlemstra/Magick.NET/blob/298053457c16f413a9df6edaca668b12289bea2d/src/Magick.NET/MagickImage.cs#L3455-L3480
            image.ImportPixels(gradientRgb,
                new PixelImportSettings(image.Width, image.Height, StorageType.Char, PixelMapping.RGB));
        }
        finally {
            // проход на копии может сменить правило края исходника, возвращаем его и при ошибке
            image.VirtualPixelMethod = previousVirtualPixelMethod;
        }
    }

    private static string CreateUserKernel(StructuralElement element, bool reflect) {
        // Magick.NET ожидает width x height + originX + originY, затем строки слева направо
        // columns соответствуют width/X, rows соответствуют height/Y
        // модельный 0 означает исключенную позицию: в пользовательском ядре передаем NaN
        // это не требование искать черный пиксель и не нулевой вес свертки
        // для {0,0,1},{1,0,0} с опорой row=1,column=1 получаем
        // 3x2+1+1: NaN NaN 1 1 NaN NaN
        // для обхода через Dilate поворачиваем массив на 180 градусов вместе с опорой
        // его собственное отражение вернет выборку (x + dx, y + dy), снимок шага не меняем
        int anchorColumn = reflect ? element.Columns - 1 - element.AnchorColumn : element.AnchorColumn;
        int anchorRow = reflect ? element.Rows - 1 - element.AnchorRow : element.AnchorRow;
        StringBuilder kernel = new StringBuilder();
        kernel.Append(element.Columns.ToString(CultureInfo.InvariantCulture));
        kernel.Append('x');
        kernel.Append(element.Rows.ToString(CultureInfo.InvariantCulture));
        kernel.Append('+');
        kernel.Append(anchorColumn.ToString(CultureInfo.InvariantCulture));
        kernel.Append('+');
        kernel.Append(anchorRow.ToString(CultureInfo.InvariantCulture));
        kernel.Append(':');
        for (int row = 0; row < element.Rows; row++) {
            for (int column = 0; column < element.Columns; column++) {
                int sourceRow = reflect ? element.Rows - 1 - row : row;
                int sourceColumn = reflect ? element.Columns - 1 - column : column;
                kernel.Append(element.IsActive(sourceRow, sourceColumn) ? " 1" : " NaN");
            }
        }
        return kernel.ToString();
    }
}
