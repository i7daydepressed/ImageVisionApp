using System;
using System.Buffers;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal readonly record struct VideoEditorStep(ImageOperationType Type, double Parameter);

internal sealed class VideoEditorPlan(long revision, VideoEditorStep[] steps) {
    private byte[]? channelLookup;

    public long Revision { get; } = revision;
    public bool HasSteps => steps.Length != 0;

    // строим таблицу значений один раз на снимок очереди и применяем ее к кадру
    public void Apply(VideoEditorFrame frame) =>
        frame.Apply(channelLookup ??= ImageProcessingService.CreateVideoAdjustmentLookup(steps));
}

internal sealed class VideoEditorFrame : IDisposable {
    private readonly byte[] pixels;
    private readonly int stride;
    private readonly int length;

    public PixelSize Size { get; }

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
