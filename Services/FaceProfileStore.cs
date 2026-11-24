using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using ImageVisionApp.Models;
using Microsoft.Win32.SafeHandles;

namespace ImageVisionApp.Services;

internal sealed class FaceProfileStore {
    private const int SchemaVersion = 1;
    private const int EmbeddingLength = 128;
    private const double NormTolerance = 1e-6;
    private const int LockExclusive = 2;
    private const int LockNonBlocking = 4;

    private static readonly JsonSerializerOptions JsonOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        AllowDuplicateProperties = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    private readonly string checkoutRoot;
    private readonly string directory;
    private readonly string databasePath;
    private readonly string lockPath;

    // проверяем явный корень проекта и запоминаем абсолютный путь базы
    public FaceProfileStore(string checkoutRoot) {
        ArgumentException.ThrowIfNullOrWhiteSpace(checkoutRoot);
        if (!Path.IsPathFullyQualified(checkoutRoot)) {
            throw new ArgumentException("Путь к рабочей копии ImageVisionApp должен быть абсолютным.", nameof(checkoutRoot));
        }

        this.checkoutRoot = Path.GetFullPath(checkoutRoot);
        if (!HasCheckoutMarkers()) {
            throw new ArgumentException("В рабочей копии ImageVisionApp должны быть ImageVisionApp.csproj и .git.", nameof(checkoutRoot));
        }

        directory = Path.Combine(this.checkoutRoot, "face-data");
        databasePath = Path.Combine(directory, "profiles.json");
        lockPath = Path.Combine(directory, "profiles.lock");
    }

    // читаем и проверяем один снимок файла, на пустом старте папку не создаем
    public IReadOnlyList<FaceProfile> Load() {
        CheckStoragePaths();
        return Array.AsReadOnly(ReadDocument().Profiles.Select(ToSnapshot).ToArray());
    }

    // создаем профиль с новым ID и уже скопированными образцами текущего конвейера
    public FaceProfile Create(string name, IEnumerable<FaceEmbedding> samples) {
        string normalizedName = NormalizeName(name);
        List<float[]> values = CopySamples(samples);
        return Change(profiles => {
            var profile = new ProfileRecord(Guid.NewGuid(), normalizedName, values);
            profiles.Add(profile);
            return profile;
        });
    }

    // меняем только имя записи, найденной по ID
    public FaceProfile Rename(Guid id, string name) {
        string normalizedName = NormalizeName(name);
        return Change(profiles => {
            int index = FindIndex(profiles, id);
            ProfileRecord renamed = profiles[index] with { Name = normalizedName };
            profiles[index] = renamed;
            return renamed;
        });
    }

    // проверяем всю входную пачку до записи и дополняем образцы одного ID
    public FaceProfile AddSamples(Guid id, IEnumerable<FaceEmbedding> samples) {
        List<float[]> additions = CopySamples(samples);
        return Change(profiles => {
            int index = FindIndex(profiles, id);
            ProfileRecord current = profiles[index];
            ProfileRecord updated = current with { Samples = [.. current.Samples, .. additions] };
            profiles[index] = updated;
            return updated;
        });
    }

    // удаляем запись по ID и возвращаем ее снимок после успешной замены файла
    public FaceProfile Delete(Guid id) => Change(profiles => {
        int index = FindIndex(profiles, id);
        ProfileRecord removed = profiles[index];
        profiles.RemoveAt(index);
        return removed;
    });

    // перечитываем базу под блокировкой и подтверждаем изменение только после переноса файла
    private FaceProfile Change(Func<List<ProfileRecord>, ProfileRecord> change) {
        CheckStoragePaths();
        Directory.CreateDirectory(directory);
        using FileStream writeLock = AcquireWriteLock();

        ProfileDocument current = ReadDocument();
        ProfileRecord updated = change(current.Profiles);
        ValidateDocument(current);
        FaceProfile result = ToSnapshot(updated);
        WriteDocument(current);
        return result;
    }

    // проверяем рабочую копию и прямые пути перед чтением или созданием папки
    private void CheckStoragePaths() {
        if (!HasCheckoutMarkers()) {
            throw new IOException("Рабочая копия ImageVisionApp недоступна или в ней нет маркеров проекта.");
        }
        CheckStorageEntry(new DirectoryInfo(directory), isDirectory: true);
        CheckStorageEntry(new FileInfo(databasePath), isDirectory: false);
        CheckStorageEntry(new FileInfo(lockPath), isDirectory: false);
    }

    // принимаем и обычный .git, и файл-ссылку Git на отдельный каталог метаданных
    private bool HasCheckoutMarkers() {
        string git = Path.Combine(checkoutRoot, ".git");
        return File.Exists(Path.Combine(checkoutRoot, "ImageVisionApp.csproj")) &&
            (Directory.Exists(git) || File.Exists(git));
    }

    // отличаем отсутствующий путь от неверного типа или ссылки, в том числе оборванной
    private static void CheckStorageEntry(FileSystemInfo entry, bool isDirectory) {
        try {
            FileAttributes attributes = File.GetAttributes(entry.FullName);
            if ((attributes & FileAttributes.ReparsePoint) != 0 || entry.LinkTarget is not null) {
                throw new IOException($"Символическая ссылка недопустима в базе лиц: {entry.Name}.");
            }
            if (((attributes & FileAttributes.Directory) != 0) != isDirectory) {
                throw new IOException($"Неверный тип пути базы лиц: {entry.Name}.");
            }
        }
        catch (IOException error) when (error is FileNotFoundException or DirectoryNotFoundException) {
            if (entry.LinkTarget is not null) {
                throw new IOException($"Символическая ссылка недопустима в базе лиц: {entry.Name}.", error);
            }
        }
    }

    // удерживаем открытый дескриптор общей блокировки до конца транзакции
    private FileStream AcquireWriteLock() {
        FileStream? writeLock = null;
        try {
            writeLock = File.Open(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            // на macOS FileShare.None сам по себе может пропустить отказ системной блокировки
            if (OperatingSystem.IsMacOS() && Flock(writeLock.SafeFileHandle, LockExclusive | LockNonBlocking) != 0) {
                throw new IOException($"macOS отклонила файловую блокировку, код {Marshal.GetLastPInvokeError()}.");
            }
            return writeLock;
        }
        catch (Exception error) {
            writeLock?.Dispose();
            if (error is IOException or UnauthorizedAccessException) {
                throw new IOException("Не удалось получить блокировку базы лиц. Если база занята, повторите попытку.", error);
            }
            throw;
        }
    }

    // читаем прежний или новый файл целиком и отличаем отсутствие файла от ошибки чтения
    private ProfileDocument ReadDocument() {
        FileStream stream;
        try {
            stream = File.Open(databasePath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        }
        catch (FileNotFoundException) {
            return new ProfileDocument(SchemaVersion, FacePipelineService.PipelineVersion, []);
        }
        catch (DirectoryNotFoundException) when (!File.Exists(directory)) {
            return new ProfileDocument(SchemaVersion, FacePipelineService.PipelineVersion, []);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            throw new IOException("Не удалось открыть базу профилей лиц для чтения.", error);
        }

        using (stream) {
            ProfileDocument document;
            try {
                document = JsonSerializer.Deserialize<ProfileDocument>(stream, JsonOptions)
                    ?? throw new InvalidDataException("В файле базы профилей отсутствуют данные.");
            }
            catch (JsonException error) {
                throw new InvalidDataException("Поврежден JSON базы профилей лиц.", error);
            }
            ValidateDocument(document);
            return document;
        }
    }

    // сбрасываем готовый JSON на диск, закрываем его и переносим в пределах одной папки
    private void WriteDocument(ProfileDocument document) {
        string temporaryPath = Path.Combine(directory, $"profiles.{Guid.NewGuid():N}.tmp");
        bool ownsTemporaryFile = false;
        try {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                ownsTemporaryFile = true;
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporaryPath, databasePath, overwrite: true);
            ownsTemporaryFile = false;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
            throw new IOException("Не удалось сохранить базу профилей лиц. Изменение не подтверждено.", error);
        }
        finally {
            if (ownsTemporaryFile) {
                try {
                    File.Delete(temporaryPath);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) {
                    // сохраняем исходную ошибку записи, если папка больше не разрешает удаление
                }
            }
        }
    }

    // отклоняем несовместимую или неполную базу до выдачи снимка и до записи
    private static void ValidateDocument(ProfileDocument document) {
        if (document.Version != SchemaVersion) {
            throw new InvalidDataException($"Неподдерживаемая версия схемы базы лиц: {document.Version}.");
        }
        if (document.PipelineVersion != FacePipelineService.PipelineVersion) {
            throw new InvalidDataException("Версия конвейера базы лиц несовместима с текущими моделями.");
        }
        if (document.Profiles is null) {
            throw new InvalidDataException("В базе лиц отсутствует список профилей.");
        }

        HashSet<Guid> ids = new();
        foreach (ProfileRecord profile in document.Profiles) {
            if (profile is null || profile.Id == Guid.Empty || !ids.Add(profile.Id)) {
                throw new InvalidDataException("В базе лиц есть пустой или повторяющийся ID профиля.");
            }
            if (string.IsNullOrWhiteSpace(profile.Name) || profile.Name != profile.Name.Trim()) {
                throw new InvalidDataException($"У профиля {profile.Id} отсутствует корректное имя.");
            }
            if (profile.Samples is null || profile.Samples.Count == 0 ||
                profile.Samples.Any(values => values is null || !IsValidVector(values))) {
                throw new InvalidDataException($"У профиля {profile.Id} есть неверный набор образцов.");
            }
        }
    }

    // проверяем версию и копируем только признаки, выровненный фрагмент остается вне базы
    private static List<float[]> CopySamples(IEnumerable<FaceEmbedding> samples) {
        ArgumentNullException.ThrowIfNull(samples);
        var owned = new List<float[]>();
        foreach (FaceEmbedding sample in samples) {
            if (sample is null || sample.PipelineVersion != FacePipelineService.PipelineVersion) {
                throw new ArgumentException("Образец должен быть рассчитан текущей версией конвейера лиц.", nameof(samples));
            }
            float[] vector = sample.Values.ToArray();
            if (!IsValidVector(vector)) {
                throw new ArgumentException("Образец должен содержать 128 конечных чисел с нормой 1.", nameof(samples));
            }
            owned.Add(vector);
        }
        if (owned.Count == 0) {
            throw new ArgumentException("Профилю нужен хотя бы один образец.", nameof(samples));
        }
        return owned;
    }

    // сверяем длину и норму, исходные значения вектора не исправляем
    private static bool IsValidVector(ReadOnlySpan<float> vector) {
        if (vector.Length != EmbeddingLength) {
            return false;
        }
        double squaredNorm = 0;
        foreach (float value in vector) {
            if (!float.IsFinite(value)) {
                return false;
            }
            squaredNorm += (double)value * value;
        }
        return Math.Abs(Math.Sqrt(squaredNorm) - 1) <= NormTolerance;
    }

    // убираем внешние пробелы при вводе имени и отклоняем пустой результат
    private static string NormalizeName(string name) {
        if (string.IsNullOrWhiteSpace(name)) {
            throw new ArgumentException("Имя профиля не должно быть пустым.", nameof(name));
        }
        return name.Trim();
    }

    // находим ровно одну запись по уникальному ID, совпадение имен не имеет значения
    private static int FindIndex(List<ProfileRecord> profiles, Guid id) {
        int index = profiles.FindIndex(profile => profile.Id == id);
        if (index < 0) {
            throw new KeyNotFoundException($"Профиль {id} не найден.");
        }
        return index;
    }

    // передаем вызывающему проверенные значения без доступа к изменению записи
    private static FaceProfile ToSnapshot(ProfileRecord profile) => new(profile.Id, profile.Name, profile.Samples);

    // проверяем эксклюзивную блокировку на том же дескрипторе macOS
    [DllImport("libSystem.B.dylib", EntryPoint = "flock", SetLastError = true)]
    private static extern int Flock(SafeFileHandle handle, int operation);

    private sealed record ProfileDocument(int Version, string PipelineVersion, List<ProfileRecord> Profiles);
    private sealed record ProfileRecord(Guid Id, string Name, List<float[]> Samples);
}
