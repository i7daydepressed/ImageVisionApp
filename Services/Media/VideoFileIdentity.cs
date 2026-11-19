using System;
using System.IO;
using System.Runtime.InteropServices;

namespace ImageVisionApp.Services;

internal sealed class VideoFileChangedException() : IOException(VideoFileIdentity.ChangedMessage) { }

internal readonly record struct VideoFileIdentity(int Device, ulong FileId, long Length,
    long ModifiedSeconds, long ModifiedNanoseconds, long ChangedSeconds, long ChangedNanoseconds) {
    internal const string ChangedMessage = "Исходный файл изменился. Откройте видео заново.";

    // получаем номер файла и версию по метаданным Darwin, без чтения содержимого
    public static VideoFileIdentity? TryCapture(string path) {
        if (!OperatingSystem.IsMacOS() || RuntimeInformation.ProcessArchitecture != Architecture.Arm64) {
            return null;
        }

        const uint device = 0x00000002, modificationTime = 0x00000400, changeTime = 0x00000800, fileId = 0x02000000;
        const uint dataLength = 0x00000200;
        Attributes request = new() { BitmapCount = 5, Common = device | modificationTime | changeTime | fileId, File = dataLength };
        uint size = (uint)Marshal.SizeOf<Values>();
        if (GetAttributes(path, ref request, out Values values, size, 0) != 0 || values.Size != size || values.FileId == 0) {
            return null;
        }
        return new(values.Device, values.FileId, values.Length, values.ModifiedSeconds,
            values.ModifiedNanoseconds, values.ChangedSeconds, values.ChangedNanoseconds);
    }

    // исключаем чтение другого файла, даже если путь и временные метки его кадров совпали
    public void EnsureCurrent(string path) {
        if (TryCapture(path) != this) {
            throw new VideoFileChangedException();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Attributes {
        public ushort BitmapCount;
        public ushort Reserved;
        public uint Common;
        public uint Volume;
        public uint Directory;
        public uint File;
        public uint Fork;
    }

    // getattrlist возвращает поля в порядке битов с выравниванием 4 байта
    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct Values {
        public uint Size;
        public int Device;
        public long ModifiedSeconds;
        public long ModifiedNanoseconds;
        public long ChangedSeconds;
        public long ChangedNanoseconds;
        public ulong FileId;
        public long Length;
    }

    [DllImport("libSystem.B.dylib", EntryPoint = "getattrlist")]
    private static extern int GetAttributes([MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        ref Attributes request, out Values values, nuint size, nuint options);
}
