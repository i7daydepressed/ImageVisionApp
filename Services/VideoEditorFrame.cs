using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageMagick;
using ImageVisionApp.Models;
using OpenCvSharp;

namespace ImageVisionApp.Services;

internal readonly record struct VideoEditorStep(
    ImageOperationType Type, double Parameter, StructuralElement? ElementSnapshot, FilterKernel? KernelSnapshot);

internal sealed class VideoEditorPlan {
    private readonly VideoEditorStep[] steps;
    private readonly int clockwiseQuarterTurns;
    private readonly bool useLookup;
    private readonly object lookupGate = new();
    private readonly VideoEditorStep[]? adjustmentSteps;
    private byte[]? channelLookup;
    private readonly int scratchBytesPerPixel;
    private readonly int scratchExtraBytes;

    public long Revision { get; }
    public bool HasSteps { get; }

    // яркость и контраст независимы от позиции пикселя
    // складываем и переносим повороты только в очереди из этих трех операций
    public VideoEditorPlan(long revision, VideoEditorStep[] steps) {
        Revision = revision;
        HasSteps = steps.Length != 0;
        this.steps = (VideoEditorStep[])steps.Clone();
        List<VideoEditorStep> adjustments = new(steps.Length);
        useLookup = true;
        foreach (VideoEditorStep step in steps) {
            if (step.Type == ImageOperationType.Rotation) {
                clockwiseQuarterTurns = (clockwiseQuarterTurns + (int)step.Parameter / 90) % 4;
            }
            else if (step.Type is ImageOperationType.Brightness or ImageOperationType.Contrast) {
                adjustments.Add(step);
            }
            else {
                useLookup = false;
            }
            scratchBytesPerPixel = Math.Max(scratchBytesPerPixel, step.Type switch {
                // градиент и шляпы одновременно держат два RGBA-входа и один RGB-выход
                ImageOperationType.MorphologicalGradient or ImageOperationType.BlackHat or ImageOperationType.TopHat => 11,
                ImageOperationType.Sharpen or ImageOperationType.MotionBlur or ImageOperationType.Emboss or
                    ImageOperationType.Median or ImageOperationType.CustomFilter => 7,
                _ => 0
            });
            if (step.Type == ImageOperationType.Median) {
                scratchExtraBytes = 9;
            }
        }
        if (useLookup && adjustments.Count != 0) {
            adjustmentSteps = adjustments.ToArray();
        }
    }

    // оцениваем одновременно живые временные пиксели отдельно от входного кадра
    public long GetScratchBytes(PixelSize size) {
        long buffer = VideoEditorFrame.GetBufferBytes(size);
        return useLookup
            ? clockwiseQuarterTurns == 0 ? 0 : buffer
            : buffer / 4 * Math.Max(4, scratchBytesPerPixel) + scratchExtraBytes;
    }

    // определяем размеры и поворот результата без чтения пикселей
    public VideoEditorGeometry GetGeometry(PixelSize inputSize) => new(inputSize, clockwiseQuarterTurns);

    // сохраняем быстрый путь трех операций, в смешанной очереди выполняем каждый шаг на своем месте
    public VideoEditorGeometry Apply(VideoEditorFrame frame) {
        VideoEditorGeometry geometry = GetGeometry(frame.Size);
        if (!useLookup) {
            frame.ApplyOperations(steps, geometry.OutputSize);
            return geometry;
        }

        if (adjustmentSteps is not null) {
            frame.Apply(GetAdjustmentLookup(adjustmentSteps));
        }
        frame.Rotate(geometry);
        return geometry;
    }

    // один обработчик строит таблицу, при сбое следующий кадр может повторить попытку
    private byte[] GetAdjustmentLookup(VideoEditorStep[] steps) {
        lock (lookupGate) {
            return channelLookup ??= ImageProcessingService.CreateVideoAdjustmentLookup(steps);
        }
    }
}

internal sealed class VideoEditorFrame : IDisposable {
    private byte[] pixels;
    private int stride;
    private readonly int length;

    public PixelSize Size { get; private set; }
    public TimeSpan? ProcessingTime { get; set; }
    internal ReadOnlyMemory<byte> Bgra => pixels.AsMemory(0, length);

    // берем буфер только на время обработки и вывода кадра
    private VideoEditorFrame(PixelSize size, byte[]? ownedBgra = null) {
        Size = size;
        stride = checked(size.Width * 4);
        length = checked(stride * size.Height);
        pixels = ownedBgra ?? GC.AllocateUninitializedArray<byte>(length);
    }

    // размер плотного BGRA-кадра известен до выделения массива
    public static long GetBufferBytes(PixelSize size) => (long)size.Width * size.Height * 4;

    // используем готовый плотный буфер композиции без дополнительной копии
    internal static VideoEditorFrame TakeBgra(PixelSize size, byte[] pixels) {
        if (size.Width <= 0 || size.Height <= 0 || pixels.LongLength != GetBufferBytes(size)) {
            throw new ArgumentException("Размер BGRA не соответствует композиции видеокадра", nameof(pixels));
        }
        return new(size, pixels);
    }

    // закрепляем bgra без копии на время чтения пикселей в OpenCV
    public Mat BorrowBgra() => Mat.FromPixelData(Size.Height, Size.Width, MatType.CV_8UC4, pixels, stride);

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

    // оставляем неизмененный кадр и даем редактору отдельный буфер того же размера
    public VideoEditorFrame Copy() {
        VideoEditorFrame copy = new(Size);
        pixels.AsSpan().CopyTo(copy.pixels);
        return copy;
    }

    // переносим пиксели независимого источника в принадлежащий редактору кадр
    public static VideoEditorFrame CopyBgra(IntPtr source, int rowBytes, PixelSize size) {
        int stride = checked(size.Width * 4);
        if (source == IntPtr.Zero || rowBytes < stride || size.Width <= 0 || size.Height <= 0) {
            throw new ArgumentException("Неверный буфер BGRA видеокадра", nameof(source));
        }
        VideoEditorFrame frame = new(size);
        for (int row = 0; row < size.Height; row++) {
            Marshal.Copy(IntPtr.Add(source, checked(row * rowBytes)), frame.pixels, row * stride, stride);
        }
        return frame;
    }

    // заменяем три цветовых канала по таблице, четвертый байт оставляем прежним
    public void Apply(byte[] channelLookup) {
        for (int index = 0; index < length; index += 4) {
            pixels[index] = channelLookup[pixels[index]];
            pixels[index + 1] = channelLookup[pixels[index + 1]];
            pixels[index + 2] = channelLookup[pixels[index + 2]];
        }
    }

    // меняем порядок каналов BGRA на RGB, не включая четвертый байт в анализ
    public byte[] CopyRgb(CancellationToken token) {
        byte[] rgb = new byte[checked(Size.Width * Size.Height * 3)];
        int rowLength = Size.Width * 3;
        for (int y = 0; y < Size.Height; y++) {
            token.ThrowIfCancellationRequested();
            int output = y * rowLength;
            int input = y * stride;
            for (int x = 0; x < Size.Width; x++, input += 4, output += 3) {
                rgb[output] = pixels[input + 2];
                rgb[output + 1] = pixels[input + 1];
                rgb[output + 2] = pixels[input];
            }
        }
        return rgb;
    }

    // собираем непрозрачный BGRA-кадр предпросмотра из рассчитанного RGB
    public static VideoEditorFrame FromRgb(PixelSize size, ReadOnlySpan<byte> rgb, CancellationToken token) {
        if (rgb.Length != checked(size.Width * size.Height * 3)) {
            throw new ArgumentException("Размер кадра не соответствует RGB-данным", nameof(rgb));
        }
        VideoEditorFrame frame = new(size);
        try {
            int input = 0;
            int output = 0;
            for (int y = 0; y < size.Height; y++) {
                token.ThrowIfCancellationRequested();
                for (int x = 0; x < size.Width; x++, input += 3, output += 4) {
                    frame.pixels[output] = rgb[input + 2];
                    frame.pixels[output + 1] = rgb[input + 1];
                    frame.pixels[output + 2] = rgb[input];
                    frame.pixels[output + 3] = 255;
                }
            }
            return frame;
        }
        catch {
            frame.Dispose();
            throw;
        }
    }

    // передаем анализу готовые пиксели того же вида видеоряда, который виден в интерфейсе
    public static VideoEditorFrame FromForeground(VideoForegroundFrame input, bool maskOnly, CancellationToken token,
        VideoBackgroundRaster? background = null, bool motionBlur = false) {
        token.ThrowIfCancellationRequested();
        PixelSize size = maskOnly ? input.Size : input.ResultSize;
        VideoEditorFrame frame = new(size);
        try {
            if (!maskOnly && !input.Composition.IsEmpty) {
                input.Composition.Span.CopyTo(frame.pixels);
                return frame;
            }
            if (!maskOnly && input.OpticalFlow is { } opticalFlow && !opticalFlow.Bgra.IsEmpty) {
                opticalFlow.Bgra.Span.CopyTo(frame.pixels);
                return frame;
            }
            int rowBytes = checked(size.Width * 4);
            for (int y = 0; y < size.Height; y++) {
                token.ThrowIfCancellationRequested();
                VideoForegroundCompositor.CopyRow(input, y, frame.pixels.AsSpan(y * rowBytes, rowBytes), maskOnly, background,
                    motionBlur: motionBlur);
            }
            return frame;
        }
        catch {
            frame.Dispose();
            throw;
        }
    }

    // переносим цветовые байты в рабочий MagickImage, выполняем очередь и возвращаем пиксели без кодирования
    public void ApplyOperations(ReadOnlySpan<VideoEditorStep> steps, PixelSize outputSize) {
        using MagickImage image = new();
        // четвертый байт RV32 служебный, BGRP читает его как заполнение, а не как альфа-канал
        image.ReadPixels(pixels.AsSpan(0, length),
            new PixelReadSettings((uint)Size.Width, (uint)Size.Height, StorageType.Char, "BGRP"));
        ImageProcessingService.ApplyVideoSteps(image, steps);
        if (image.Width != outputSize.Width || image.Height != outputSize.Height) {
            throw new InvalidOperationException("Размер результата не соответствует повороту видеокадра");
        }

        using IPixelCollection<byte> output = image.GetPixels();
        byte[] result = output.ToByteArray(PixelMapping.BGRA)
            ?? throw new InvalidOperationException("Не удалось получить пиксели видеокадра");
        if (result.Length != length) {
            throw new InvalidOperationException("Получен неполный буфер видеокадра");
        }
        pixels = result;
        Size = outputSize;
        stride = checked(outputSize.Width * 4);
    }

    // переносим целые BGRA-пиксели на новые позиции и заменяем буфер кадра только после поворота
    public void Rotate(VideoEditorGeometry geometry) {
        if (geometry.ClockwiseQuarterTurns == 0) {
            return;
        }

        PixelSize outputSize = geometry.OutputSize;
        int outputStride = checked(outputSize.Width * 4);
        byte[] rotated = GC.AllocateUninitializedArray<byte>(length);
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
    public void Dispose() => pixels = [];

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
