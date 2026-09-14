using System;
using System.Collections.Generic;
using ImageMagick;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

public sealed class ImageStatsFileService {
    public ImageInfo readImageInfo(byte[] imageData, string fileName) {
        ArgumentNullException.ThrowIfNull(imageData);

        if (imageData.Length == 0) {
            throw new ArgumentException(
                "Файл изображения пуст",
                nameof(imageData));
        }

        using var magickImage = new MagickImage(imageData);

        var profileNames = string.Join(
            ", ",
            magickImage.ProfileNames);

        return new ImageInfo {
            FileName = fileName,
            FileSizeBytes = imageData.LongLength,
            Width = magickImage.Width,
            Height = magickImage.Height,
            //  magickImage.
            BitsPerChannel = magickImage.Depth,
            ChannelCount = magickImage.ChannelCount,
            Format = magickImage.Format.ToString(),
            ColorModel = magickImage.ColorSpace.ToString(),
            ColorType = magickImage.ColorType.ToString(),
            HasAlpha = magickImage.HasAlpha,
            Compression = magickImage.Compression.ToString(),
            Quality = magickImage.Quality,
            Orientation = magickImage.Orientation.ToString(),
            Gamma = magickImage.Gamma,
            Interlace = magickImage.Interlace.ToString(),
            UniqueColorCount = magickImage.TotalColors,
            Profiles = string.IsNullOrWhiteSpace(profileNames)
                ? "Нет"
                : profileNames,
            ExifData = readExifData(magickImage)
        };
    }


    private static IReadOnlyList<MetadataItem> readExifData(
        MagickImage magickImage){

        var exifItems = new List<MetadataItem>();
        var exifProfile = magickImage.GetExifProfile();

        if (exifProfile is null) {
            return exifItems;
        }

        {
        // foreach (var exifValue in exifProfile.Values) {
        //      

        //     if (string.IsNullOrWhiteSpace(value)) {
        //         continue;
        //     }

        //     if (value.Length > 250) {
        //         value = value[..250] + "…";
        //     }

        //     exifItems.Add(
        //         new MetadataItem {
        //             Name = translateExifName(
        //                 exifValue.Tag.ToString()),
        //             Value = value
        //         });
        // }
        }

        foreach (IExifValue exifItem in exifProfile.Values) {
            // exifItem — одна запись EXIF из Magick.NET. - те ехиф которые он вытащил из картинки и криво себе упаковал(я его ненавижу)
            // Внутри неё:
            // exifItem.Tag        — какой это параметр;
            // exifItem.GetValue() — исходное значение параметра. НО ОНИ СИЛЬНО УМНЫЕ и нигде не написав молча сказали себе там поднос ЧТО exifItem.ToString() ЭТО безопасный exifItem.GetValue() СРАЗУ В СТРОЧНОЕ ЗНАЧЕНИЕ (я их ненавижу)
                // exifItem.ToString() - безопасный exifItem.GetValue()
            ExifTag exifTag = exifItem.Tag;
            // object exifValue = exifItem.GetValue();
            var exifValue = exifItem.ToString();//string?

            // Название тега Magick.NET переводим в строку:
            // ExifTag.Model → "Model".
            string exifTagName = exifTag.ToString();

            // // Исходное значение object переводим в строку,
            // // потому что в интерфейсе приложения оно выводится как текст
            // string exifValueText = rawExifValue.ToString() ?? string.Empty;

            // Пустые значения в список не добавляем.
            if (string.IsNullOrWhiteSpace(exifValue)) {
                continue;
            }

            // Слишком длинное значение сокращаем до 250 символов
            if (exifValue.Length > 250) {
                exifValue = exifValue[..250] + "…";
            }

            // Переводим техническое название EXIF-тега:
            // "Model" → "Модель камеры".
            string translatedExifTagName =
                translateExifName(exifTagName);

            // Создаём уже наш ЧЕЛОВЕЧЕСКИЙ объект, предназначенный для интерфейса
            MetadataItem metadataItem = new MetadataItem {
                Name = translatedExifTagName,
                Value = exifValue
            };

            exifItems.Add(metadataItem);
        }

        return exifItems;
    }

    private static string translateExifName(string name) {
        return name switch {
            "ImageDescription" => "Описание",
            "Make" => "Производитель камеры",
            "Model" => "Модель камеры",
            "Software" => "Программа обработки",
            "DateTime" => "Дата изменения",
            "DateTimeOriginal" => "Дата съёмки",
            "DateTimeDigitized" => "Дата оцифровки",
            "ExposureTime" => "Выдержка",
            "FNumber" => "Диафрагма",
            "ISOSpeedRatings" => "ISO",
            "PhotographicSensitivity" => "Чувствительность ISO",
            "FocalLength" => "Фокусное расстояние",
            "Flash" => "Вспышка",
            "LensMake" => "Производитель объектива",
            "LensModel" => "Модель объектива",
            "Artist" => "Автор",
            "Copyright" => "Авторские права",
            "GPSLatitude" => "Широта GPS",
            "GPSLongitude" => "Долгота GPS",
            "GPSAltitude" => "Высота GPS",
            "Orientation" => "Ориентация EXIF",
            "WhiteBalance" => "Баланс белого",
            "ExposureProgram" => "Программа экспозиции",
            "MeteringMode" => "Режим замера",
            _ => name
        };
    }
}