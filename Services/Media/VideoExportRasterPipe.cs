using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;

namespace ImageVisionApp.Services;

// потоковый Matroska передает FFmpeg плотный BGRA вместе с временными метками в наносекундах
internal static class VideoExportRasterPipe {
    private static readonly byte[] UnknownSegment = [0x18, 0x53, 0x80, 0x67, 0x01, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff, 0xff];
    private static readonly byte[] BlockTrackAndFlags = [0x81, 0, 0, 0];

    // отрицательная высота в BITMAPINFOHEADER задает порядок строк сверху вниз
    public static ValueTask WriteHeaderAsync(Stream output, PixelSize size, long nominalFrameDuration,
        CancellationToken token) {
        byte[] bitmap = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(bitmap, bitmap.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(4), size.Width);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(8), -size.Height);
        BinaryPrimitives.WriteUInt16LittleEndian(bitmap.AsSpan(12), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(bitmap.AsSpan(14), 32);
        BinaryPrimitives.WriteInt32LittleEndian(bitmap.AsSpan(20), checked((int)VideoEditorFrame.GetBufferBytes(size)));

        return output.WriteAsync(Join(
            Element(0x1a45dfa3,
                NumberElement(0x4286, 1), NumberElement(0x42f7, 1),
                NumberElement(0x42f2, 4), NumberElement(0x42f3, 8),
                TextElement(0x4282, "matroska"), NumberElement(0x4287, 4), NumberElement(0x4285, 2)),
            UnknownSegment,
            Element(0x1549a966, NumberElement(0x2ad7b1, 1),
                TextElement(0x4d80, "ImageVisionApp"), TextElement(0x5741, "ImageVisionApp")),
            Element(0x1654ae6b, Element(0xae,
                NumberElement(0xd7, 1), NumberElement(0x73c5, 1), NumberElement(0x83, 1), NumberElement(0x9c, 0),
                NumberElement(0x23e383, nominalFrameDuration),
                TextElement(0x86, "V_MS/VFW/FOURCC"), Element(0x63a2, bitmap),
                Element(0xe0, NumberElement(0xb0, size.Width), NumberElement(0xba, size.Height))))), token);
    }

    // каждый кадр начинает кластер с собственным pts, поэтому блочный сдвиг всегда равен нулю
    public static async ValueTask WriteFrameAsync(Stream output, ReadOnlyMemory<byte> bgra, long time, long duration,
        CancellationToken token) {
        byte[] stamp = NumberElement(0xe7, time);
        byte[] length = NumberElement(0x9b, duration);
        byte[] blockHeader = Join(Unsigned(0xa1), VariableSize(checked(bgra.Length + BlockTrackAndFlags.Length)),
            BlockTrackAndFlags);
        byte[] groupHeader = Join(Unsigned(0xa0), VariableSize(checked(blockHeader.Length + bgra.Length + length.Length)));
        byte[] cluster = Join(Unsigned(0x1f43b675),
            VariableSize(checked(stamp.Length + groupHeader.Length + blockHeader.Length + bgra.Length + length.Length)),
            stamp, groupHeader, blockHeader);
        await output.WriteAsync(cluster, token).ConfigureAwait(false);
        await output.WriteAsync(bgra, token).ConfigureAwait(false);
        await output.WriteAsync(length, token).ConfigureAwait(false);
    }

    private static byte[] NumberElement(uint id, long value) => Element(id, Unsigned(value));
    private static byte[] TextElement(uint id, string value) => Element(id, Encoding.ASCII.GetBytes(value));
    private static byte[] Element(uint id, params byte[][] pieces) =>
        Join(Unsigned(id), VariableSize(pieces.Sum(static p => p.Length)), Join(pieces));

    private static byte[] Join(params byte[][] pieces) {
        byte[] result = new byte[pieces.Sum(static p => p.Length)];
        int offset = 0;
        foreach (byte[] piece in pieces) {
            piece.CopyTo(result, offset);
            offset += piece.Length;
        }
        return result;
    }

    // длина EBML хранит номер старшего бита отдельно от значения и резервирует все единицы
    private static byte[] VariableSize(int value) {
        int width = 1;
        while (value >= (1L << (7 * width)) - 1) {
            width++;
        }
        return Unsigned((uint)value | (1L << (7 * width)));
    }

    private static byte[] Unsigned(long value) {
        Span<byte> bytes = stackalloc byte[sizeof(long)];
        BinaryPrimitives.WriteInt64BigEndian(bytes, value);
        int offset = 0;
        while (offset < bytes.Length - 1 && bytes[offset] == 0) {
            offset++;
        }
        return bytes[offset..].ToArray();
    }
}
