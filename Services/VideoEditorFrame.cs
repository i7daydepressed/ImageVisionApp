using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal readonly record struct VideoEditorStep(ImageOperationType Type, double Parameter);

internal sealed class VideoEditorPlan {
    private readonly VideoEditorStep[] adjustmentSteps;
    private readonly int clockwiseQuarterTurns;
    private byte[]? channelLookup;

    public long Revision { get; }
    public bool HasSteps { get; }

    // яркость и контраст меняют значение пикселя независимо от его позиции
    // сохраняем их взаимный порядок, четверть-повороты складываем отдельно
    public VideoEditorPlan(long revision, VideoEditorStep[] steps) {
        Revision = revision;
        HasSteps = steps.Length != 0;
        List<VideoEditorStep> adjustments = new(steps.Length);
        foreach (VideoEditorStep step in steps) {
            if (step.Type == ImageOperationType.Rotation) {
                clockwiseQuarterTurns = (clockwiseQuarterTurns + (int)step.Parameter / 90) % 4;
            }
            else {
                adjustments.Add(step);
            }
        }
        adjustmentSteps = adjustments.ToArray();
    }

    // строим таблицу значений один раз на снимок очереди и применяем ее к кадру
    public VideoEditorGeometry Apply(VideoEditorFrame frame) {
        if (adjustmentSteps.Length != 0) {
            frame.Apply(channelLookup ??= ImageProcessingService.CreateVideoAdjustmentLookup(adjustmentSteps));
        }
        VideoEditorGeometry geometry = new(frame.Size, clockwiseQuarterTurns);
        frame.Rotate(geometry);
        return geometry;
    }
}

internal sealed class VideoEditorFrame : IDisposable {
    private byte[] pixels;
    private int stride;
    private readonly int length;

    public PixelSize Size { get; private set; }

    // берем буфер только на время обработки и вывода кадра
    private VideoEditorFrame(PixelSize size) {
        Size = size;
        stride = checked(size.Width * 4);
        length = checked(stride * size.Height);
        pixels = ArrayPool<byte>.Shared.Rent(length);
    }

    // копируем исходные пиксели до того, как плеер перепишет свой bitmap
    public static VideoEditorFrame Capture(Bitmap source) {
        VideoEditorFrame frame = new(source.PixelSize);
        try {
            using CaptureBuffer target = new(frame);
            source.CopyPixels(target);
            return frame;
        }
        catch {
            frame.Dispose();
            throw;
        }
    }

    // заменяем три цветовых канала по таблице, четвертый байт оставляем прежним
    public void Apply(byte[] channelLookup) {
        for (int index = 0; index < length; index += 4) {
            pixels[index] = channelLookup[pixels[index]];
            pixels[index + 1] = channelLookup[pixels[index + 1]];
            pixels[index + 2] = channelLookup[pixels[index + 2]];
        }
    }

    // переносим целые BGRA-пиксели на новые позиции и заменяем буфер кадра только после поворота
    public void Rotate(VideoEditorGeometry geometry) {
        if (geometry.ClockwiseQuarterTurns == 0) {
            return;
        }

        PixelSize outputSize = geometry.OutputSize;
        int outputStride = checked(outputSize.Width * 4);
        byte[] rotated = ArrayPool<byte>.Shared.Rent(length);
        try {
            Span<uint> input = MemoryMarshal.Cast<byte, uint>(pixels.AsSpan(0, length));
            Span<uint> output = MemoryMarshal.Cast<byte, uint>(rotated.AsSpan(0, length));
            // следующий пиксель исходной строки идет вдоль столбца или обратно по строке результата
            int nextPixel = geometry.ClockwiseQuarterTurns switch {
                1 => outputSize.Width,
                2 => -1,
                _ => -outputSize.Width
            };
            int inputIndex = 0;
            for (int y = 0; y < Size.Height; y++) {
                PixelPoint start = geometry.MapPixel(new PixelPoint(0, y));
                int outputIndex = start.Y * outputSize.Width + start.X;
                for (int x = 0; x < Size.Width; x++) {
                    output[outputIndex] = input[inputIndex++];
                    outputIndex += nextPixel;
                }
            }
        }
        catch {
            ArrayPool<byte>.Shared.Return(rotated);
            throw;
        }

        ArrayPool<byte>.Shared.Return(pixels);
        pixels = rotated;
        Size = outputSize;
        stride = outputStride;
    }

    // передаем готовые строки в bitmap редактора с учетом шага целевого буфера
    public void CopyTo(ILockedFramebuffer target) {
        if (target.RowBytes == stride) {
            Marshal.Copy(pixels, 0, target.Address, length);
        }
        else {
            for (int row = 0; row < Size.Height; row++) {
                Marshal.Copy(pixels, row * stride, target.Address + row * target.RowBytes, stride);
            }
        }
    }

    // освобождаем буфер после публикации результата или отмены запроса
    public void Dispose() => ArrayPool<byte>.Shared.Return(pixels);

    private sealed class CaptureBuffer : ILockedFramebuffer {
        private GCHandle handle;

        public IntPtr Address => handle.AddrOfPinnedObject();
        public PixelSize Size { get; }
        public int RowBytes { get; }
        public Vector Dpi => new(96, 96);
        public PixelFormat Format => PixelFormat.Bgra8888;
        public AlphaFormat AlphaFormat => AlphaFormat.Opaque;

        // фиксируем адрес массива на время копирования из bitmap
        public CaptureBuffer(VideoEditorFrame frame) {
            handle = GCHandle.Alloc(frame.pixels, GCHandleType.Pinned);
            Size = frame.Size;
            RowBytes = frame.stride;
        }

        // после копирования адрес массива больше не нужен
        public void Dispose() => handle.Free();
    }
}
