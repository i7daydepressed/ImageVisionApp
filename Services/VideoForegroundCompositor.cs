using System;

namespace ImageVisionApp.Services;

internal static class VideoForegroundCompositor {
    // один и тот же выбор пикселя используется для предпросмотра и входа анализа
    public static void CopyRow(VideoForegroundFrame input, int row, Span<byte> destination, bool maskOnly,
        VideoBackgroundRaster? background = null, bool rawInput = false, bool motionBlur = false) {
        bool readsEffect = !rawInput && !maskOnly;
        var size = readsEffect ? input.ResultSize : input.Size;
        int width = size.Width;
        if ((uint)row >= (uint)size.Height || destination.Length != checked(width * 4)) {
            throw new ArgumentException("Размер строки не соответствует видеокадру", nameof(destination));
        }
        VideoOpticalFlowRaster? opticalFlow = readsEffect ? input.OpticalFlow : null;
        if (opticalFlow is not null) {
            if (!opticalFlow.Bgra.IsEmpty) {
                opticalFlow.Bgra.Span.Slice(row * destination.Length, destination.Length).CopyTo(destination);
                return;
            }
            if (size != input.Size) {
                throw new InvalidOperationException("Не подготовлены пиксели подложки оптического потока");
            }
        }
        bool usesBlur = readsEffect && motionBlur && opticalFlow is null;
        if (usesBlur) {
            VideoMotionBlurRaster blur = input.MotionBlur ??
                throw new InvalidOperationException("Размытие для выбранного видеокадра еще не подготовлено");
            if (blur.Size != input.Size) {
                throw new InvalidOperationException("Размер размытия не соответствует выбранному видеокадру");
            }
            if (!blur.Bgra.IsEmpty) {
                blur.Bgra.Span.Slice(row * destination.Length, destination.Length).CopyTo(destination);
                return;
            }
        }
        bool copyInput = rawInput || usesBlur || opticalFlow is not null;
        bool usesBackground = !copyInput && !maskOnly && background is not null;
        if (usesBackground && background!.Size != input.Size) {
            throw new ArgumentException("Размер фона не соответствует видеокадру", nameof(background));
        }

        int pixelStart = row * width;
        int colorStart = pixelStart * 3;
        int colorLength = width * 3;
        ReadOnlySpan<byte> rgb = input.Rgb.Span.Slice(colorStart, colorLength);
        ReadOnlySpan<byte> mask = copyInput ? default : input.Mask.Span.Slice(pixelStart, width);
        ReadOnlySpan<byte> backdrop = usesBackground ? background!.Rgb.Span.Slice(colorStart, colorLength) : default;
        for (int x = 0; x < width; x++) {
            int pixel = x * 4;
            if (!rawInput && maskOnly) {
                destination[pixel] = destination[pixel + 1] = destination[pixel + 2] = mask[x];
            }
            else {
                ReadOnlySpan<byte> colors = copyInput || mask[x] != 0 ? rgb : backdrop;
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
