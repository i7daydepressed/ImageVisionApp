using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Threading;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal readonly record struct FaceProfileAvatarPattern(uint Bits, int PaletteIndex) {
    // отражаем четыре исходные позиции каждой строки вокруг центрального столбца
    public bool HasCell(int column, int row) => (Bits & (1u << (4 * row + Math.Min(column, 6 - column)))) != 0;
}

internal static class FaceProfileAvatars {
    private const int KeySize = 32;
    private const int VectorSize = 128;
    private const UnixFileMode PrivateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode SharedMode = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    private static ReadOnlySpan<byte> Domain => "ImageVisionApp.FaceAvatar/v1\0"u8;

    // один ключ обслуживает весь снимок базы, недоступное хранилище дает нейтральные значки
    public static FaceProfileAvatarPattern?[] BuildCatalogue(IReadOnlyList<FaceProfile> profiles,
        CancellationToken token, string? testKeyPath = null) {

        var patterns = new FaceProfileAvatarPattern?[profiles.Count];
        if (patterns.Length == 0) {
            return patterns;
        }
        token.ThrowIfCancellationRequested();

        byte[] key;
        try {
            key = ReadOrCreateKey(testKeyPath ?? GetLocalKeyPath());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or SecurityException or InvalidDataException) {
            return patterns;
        }
        try {
            for (int i = 0; i < patterns.Length; i++) {
                token.ThrowIfCancellationRequested();
                FaceProfile profile = profiles[i];
                if (profile.Samples.Count != 0 && profile.Samples[0].Count == VectorSize) {
                    patterns[i] = BuildPattern(key, profile.Id, profile.Samples[0]);
                }
            }
            return patterns;
        }
        finally {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    // упаковываем Guid и IEEE 754 float32 в big endian, с отдельным доменом версии
    private static FaceProfileAvatarPattern BuildPattern(ReadOnlySpan<byte> key, Guid id, IReadOnlyList<float> vector) {
        int vectorOffset = Domain.Length + 16;
        Span<byte> input = stackalloc byte[vectorOffset + VectorSize * sizeof(float)];
        Domain.CopyTo(input);
        id.TryWriteBytes(input.Slice(Domain.Length, 16), bigEndian: true, out _);
        for (int i = 0; i < VectorSize; i++) {
            BinaryPrimitives.WriteInt32BigEndian(input[(vectorOffset + i * sizeof(float))..], BitConverter.SingleToInt32Bits(vector[i]));
        }

        Span<byte> hash = stackalloc byte[KeySize];
        HMACSHA256.HashData(key, input, hash);
        CryptographicOperations.ZeroMemory(input);
        uint bits = BinaryPrimitives.ReadUInt32BigEndian(hash) & 0x0fffffffu;
        return new FaceProfileAvatarPattern(bits == 0 ? 1u << 15 : bits, hash[4] & 7);
    }

    // берем тот же локальный каталог приложения, где хранятся матрицы
    private static string GetLocalKeyPath() {
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(local)) {
            throw new IOException("Локальная папка ImageVisionApp недоступна.");
        }
        return Path.Combine(local, "ImageVisionApp", "face-avatar-hmac.key");
    }

    // создаем ключ только при отсутствии файла, неверные данные оставляем на месте
    private static byte[] ReadOrCreateKey(string keyPath) {
        if (!Path.IsPathFullyQualified(keyPath) || Path.GetDirectoryName(keyPath) is not string directory) {
            throw new IOException("Для ключа аватара нужен абсолютный путь.");
        }
        CheckKeyDirectory(directory);
        try {
            return ReadKey(keyPath);
        }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException) {
            return CreateKey(keyPath, directory);
        }
    }

    // проверяем только папку ключа, ссылки и доступ на запись для других пользователей отклоняем
    private static void CheckKeyDirectory(string directory) {
        DirectoryInfo entry = new(directory);
        if (entry.LinkTarget is not null || (!OperatingSystem.IsWindows() && entry.Exists &&
            (File.GetUnixFileMode(directory) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)) {
            throw new InvalidDataException("Папка ключа аватара имеет неверный тип или режим доступа.");
        }
    }

    // читаем только файл ожидаемой длины с доступом для владельца на Unix
    private static byte[] ReadKey(string keyPath) {
        FileInfo entry = new(keyPath);
        if (entry.LinkTarget is not null || entry.Length != KeySize) {
            throw new InvalidDataException("Файл ключа аватара имеет неверный формат.");
        }
        using FileStream stream = File.Open(keyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        if ((!OperatingSystem.IsWindows() && (File.GetUnixFileMode(stream.SafeFileHandle) & SharedMode) != 0) ||
            stream.Length != KeySize) {
            throw new InvalidDataException("Файл ключа аватара имеет неверный размер или режим доступа.");
        }
        byte[] key = new byte[KeySize];
        stream.ReadExactly(key);
        return key;
    }

    // записываем приватный временный файл и публикуем его только под свободным именем
    private static byte[] CreateKey(string keyPath, string directory) {
        if (OperatingSystem.IsWindows()) {
            Directory.CreateDirectory(directory);
        }
        else {
            Directory.CreateDirectory(directory, PrivateMode | UnixFileMode.UserExecute);
        }
        CheckKeyDirectory(directory);
        string temporary = Path.Combine(directory, $".face-avatar-{Guid.NewGuid():N}.tmp");
        bool ownsTemporary = false;
        byte[] key = RandomNumberGenerator.GetBytes(KeySize);
        try {
            FileStreamOptions options = new() { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) {
                options.UnixCreateMode = PrivateMode;
            }
            using (FileStream stream = new(temporary, options)) {
                ownsTemporary = true;
                stream.Write(key);
                stream.Flush(flushToDisk: true);
            }
            PublishKey(temporary, keyPath);
            return key;
        }
        catch (IOException) when (File.Exists(keyPath)) {
            CryptographicOperations.ZeroMemory(key);
            return ReadKey(keyPath);
        }
        catch {
            CryptographicOperations.ZeroMemory(key);
            throw;
        }
        finally {
            if (ownsTemporary) {
                try {
                    File.Delete(temporary);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                    // уборка временного файла не меняет результат публикации
                }
            }
        }
    }

    // на Unix link публикует готовый inode и атомарно отклоняет занятое имя
    private static void PublishKey(string temporary, string keyPath) {
        if (OperatingSystem.IsWindows()) {
            File.Move(temporary, keyPath, overwrite: false);
            return;
        }
        int result = OperatingSystem.IsMacOS() ? LinkMacOS(temporary, keyPath) : LinkUnix(temporary, keyPath);
        if (result != 0) {
            throw new IOException($"Не удалось опубликовать ключ аватара, код {Marshal.GetLastPInvokeError()}.");
        }
    }

    // используем системный link macOS без подмены существующего имени
    [DllImport("libSystem.B.dylib", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkMacOS([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string target);

    // используем тот же контракт link на остальных Unix
    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int LinkUnix([MarshalAs(UnmanagedType.LPUTF8Str)] string source, [MarshalAs(UnmanagedType.LPUTF8Str)] string target);
}
