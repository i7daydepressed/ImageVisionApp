using System;

namespace ImageVisionApp.Services;

internal static class VideoForegroundCompositor {
    // один и тот же выбор пикселя используется для предпросмотра и входа анализа
    public static void CopyRow(VideoForegroundFrame input, int row, Span<byte> destination, bool maskOnly,
        VideoBackgroundRaster? background = null, bool rawInput = false) {
        int width = input.Size.Width;
        if ((uint)row >= (uint)input.Size.Height || destination.Length != checked(width * 4)) {
            throw new ArgumentException("Размер строки не соответствует видеокадру", nameof(destination));
        }
        bool usesBackground = !rawInput && !maskOnly && background is not null;
        if (usesBackground && background!.Size != input.Size) {
            throw new ArgumentException("Размер фона не соответствует видеокадру", nameof(background));
        }

        int pixelStart = row * width;
        int colorStart = pixelStart * 3;
        int colorLength = width * 3;
        ReadOnlySpan<byte> rgb = input.Rgb.Span.Slice(colorStart, colorLength);
        ReadOnlySpan<byte> mask = rawInput ? default : input.Mask.Span.Slice(pixelStart, width);
        ReadOnlySpan<byte> backdrop = usesBackground ? background!.Rgb.Span.Slice(colorStart, colorLength) : default;
        for (int x = 0; x < width; x++) {
            int pixel = x * 4;
            if (!rawInput && maskOnly) {
                destination[pixel] = destination[pixel + 1] = destination[pixel + 2] = mask[x];
            }
            else {
                ReadOnlySpan<byte> colors = rawInput || mask[x] != 0 ? rgb : backdrop;
                if (colors.IsEmpty) {
                    destination[pixel] = destination[pixel + 1] = destination[pixel + 2] = 0;
                }
                else {
                    int color = x * 3;
                    destination[pixel] = colors[color + 2];
                    destination[pixel + 1] = colors[color + 1];
                    destination[pixel + 2] = colors[color];
                }
            }
            destination[pixel + 3] = 255;
        }
    }
}
