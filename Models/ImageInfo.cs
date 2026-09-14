using System;
using System.Collections.Generic;

namespace ImageVisionApp.Models;

public sealed class ImageInfo
{
    public string FileName { get; init; } = "—";
    public long FileSizeBytes { get; init; }
    public uint Width { get; init; }
    public uint Height { get; init; }
    public uint BitsPerChannel { get; init; }
    public uint ChannelCount { get; init; }
    public string Format { get; init; } = "—";
    public string ColorModel { get; init; } = "—";
    public string ColorType { get; init; } = "—";
    public bool HasAlpha { get; init; }
    public string Compression { get; init; } = "—";
    public uint Quality { get; init; }
    public string Orientation { get; init; } = "—";
    public double Gamma { get; init; }
    public string Interlace { get; init; } = "—";
    public uint UniqueColorCount { get; init; }
    public string Profiles { get; init; } = "Нет";

    public IReadOnlyList<MetadataItem> ExifData { get; init; }
        = Array.Empty<MetadataItem>();

    public ulong PixelCount => (ulong)Width * Height;

    public uint BitsPerPixel => BitsPerChannel * ChannelCount;

    public string Resolution =>
        $"{Width} × {Height} пикселей";

    public string Megapixels{
        get {
            return $"{PixelCount / 1_000_000.0:0.##} Мп";
        }
    }

    public string AlphaDescription =>
        HasAlpha ? "Есть" : "Нет";

    public string GammaDescription {
        get {
            return Gamma.ToString("0.###");
        }
    }

    public string AspectRatio{
        get{
            var divisor = GreatestCommonDivisor(Width, Height);

            return divisor == 0
                ? "—"
                : $"{Width / divisor}:{Height / divisor}";
        }
    }

    public string FormattedFileSize{
        get{
            string[] units = ["байт", "КБ", "МБ", "ГБ"];

            var size = (double)FileSizeBytes;
            var unitIndex = 0;

            while (size >= 1024 && unitIndex < units.Length - 1)
            {
                size /= 1024;
                unitIndex++;
            }

            return $"{size:0.##} {units[unitIndex]}";
        }
    }

    public string ExifStatus =>
        ExifData.Count == 0
            ? "EXIF отсутствует"
            : $"Найдено параметров: {ExifData.Count}";

    private static uint GreatestCommonDivisor(uint first, uint second){
        while (second != 0){
            var remainder = first % second;
            first = second;
            second = remainder;
        }

        return first;
    }
}