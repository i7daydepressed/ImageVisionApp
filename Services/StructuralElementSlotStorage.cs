using System;
using System.IO;
using System.Security;
using System.Text.Json;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

// отвечает только за формат и файловое хранение, не знает о кнопках и черновике редактора
public sealed class StructuralElementSlotStorage {
    private const int FormatVersion = 2;
    private readonly int slotCount;
    private readonly string? configuredFilePath;
    private bool writeAllowed;
    private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public StructuralElementSlotStorage(int slotCount, string? filePath = null) {
        if (slotCount <= 0) {
            throw new ArgumentOutOfRangeException(nameof(slotCount));
        }
        this.slotCount = slotCount;
        configuredFilePath = filePath;
    }

    public bool TryLoad(out MatrixSlot?[] slots, out string error) {
        slots = new MatrixSlot?[slotCount];
        error = "";
        writeAllowed = false;
        string path = configuredFilePath ?? "системная папка данных";
        try {
            path = GetFilePath();
            string json = File.ReadAllText(path);
            StructuralElementSlotsDocument? document =
                JsonSerializer.Deserialize<StructuralElementSlotsDocument>(json, JsonOptions);
            if (document is null || document.Version is not (1 or FormatVersion)) {
                throw new InvalidDataException("Неизвестная версия файла сохраненных матриц");
            }
            if (document.Slots is null || document.Slots.Length != slotCount) {
                throw new InvalidDataException($"Файл должен содержать ровно {slotCount} слота");
            }
            MatrixSlot?[] restored = new MatrixSlot?[slotCount];
            for (int index = 0; index < slotCount; index++) {
                StructuralElementSlotData? data = document.Slots[index];
                if (data is not null) {
                    restored[index] = ReadSlot(data, document.Version);
                }
            }
            // передаем результат только после проверки всех слотов, частично прочитанные данные не выдаем
            slots = restored;
            writeAllowed = true;
            return true;
        }
        catch (FileNotFoundException) {
            // первый запуск с пустыми слотами, сам файл появится при первой записи
            writeAllowed = true;
            return true;
        }
        catch (DirectoryNotFoundException) {
            writeAllowed = true;
            return true;
        }
        catch (Exception exception) when (IsStorageException(exception)) {
            error = $"Не удалось прочитать сохраненные матрицы ({path}): {exception.Message}. " +
                "Исходный файл не изменен. Исправьте или переместите его и перезапустите приложение.";
            return false;
        }
    }

    public bool TrySave(MatrixSlot?[] slots, out string error) {
        error = "";
        if (!writeAllowed) {
            error = "Запись слотов недоступна: хранилище не загружено. " +
                "Исправьте ошибку чтения и перезапустите приложение.";
            return false;
        }
        string? temporaryPath = null;
        string path = configuredFilePath ?? "системная папка данных";
        try {
            ArgumentNullException.ThrowIfNull(slots);
            if (slots.Length != slotCount) {
                throw new ArgumentException($"Для записи требуется ровно {slotCount} слота", nameof(slots));
            }
            StructuralElementSlotData?[] data = new StructuralElementSlotData?[slotCount];
            for (int index = 0; index < slotCount; index++) {
                MatrixSlot? element = slots[index];
                if (element is not null) {
                    data[index] = WriteSlot(element);
                }
            }
            StructuralElementSlotsDocument document = new StructuralElementSlotsDocument {
                Version = FormatVersion,
                Slots = data
            };
            path = GetFilePath();
            string? directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory)) {
                throw new IOException("Не удалось определить папку хранения матриц");
            }
            Directory.CreateDirectory(directory);
            // временный файл рядом с основным, чтобы замена оставалась в одной файловой системе
            temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            using (FileStream stream = new FileStream(
                temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                JsonSerializer.Serialize(stream, document, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            // основной файл заменяем только после полной записи и закрытия временного
            File.Move(temporaryPath, path, overwrite: true);
            temporaryPath = null;
            return true;
        }
        catch (Exception exception) when (IsStorageException(exception)) {
            error = $"Не удалось записать сохраненные матрицы ({path}): {exception.Message}. " +
                "Изменение слота не выполнено.";
            return false;
        }
        finally {
            if (temporaryPath is not null) {
                try {
                    File.Delete(temporaryPath);
                }
                catch (Exception exception) when (IsStorageException(exception)) {
                    // ошибка уборки временного файла не заменяет сообщение об основной ошибке записи
                }
            }
        }
    }

    private string GetFilePath() {
        if (configuredFilePath is not null) {
            return Path.GetFullPath(configuredFilePath);
        }
        string dataDirectory = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData,
            Environment.SpecialFolderOption.DoNotVerify);
        if (string.IsNullOrWhiteSpace(dataDirectory)) {
            throw new IOException("Системная папка данных пользователя недоступна");
        }
        return Path.Combine(dataDirectory, "ImageVisionApp", "structural-element-slots.json");
    }

    private static MatrixSlot ReadSlot(StructuralElementSlotData data, int version) {
        // прежние записи без типа читаем как морфологию, файл меняется только при успешном сохранении
        string kind = version == 1 ? "morphology" : data.Kind
            ?? throw new InvalidDataException("В ячейке отсутствует тип матрицы");
        if (version == 1 && data.Kind is not null && data.Kind != "morphology") {
            throw new InvalidDataException("Тип матрицы не соответствует версии файла");
        }
        if (kind == "morphology") {
            if (data.Weights is not null) {
                throw new InvalidDataException("Структурный элемент не должен содержать числовые веса");
            }
            return MatrixSlot.ForElement(ReadElement(data));
        }
        if (kind != "filter" || data.Cells is null || data.Cells.Length != 0 ||
            data.Weights is not double[][] weights || weights.Length == 0 ||
            weights[0] is null || weights[0].Length == 0) {
            throw new InvalidDataException("Неизвестный тип или неверное содержимое числовой матрицы");
        }
        int rows = weights.Length;
        int columns = weights[0].Length;
        double[,] values = new double[rows, columns];
        for (int row = 0; row < rows; row++) {
            if (weights[row] is null || weights[row].Length != columns) {
                throw new InvalidDataException("Строки числовой матрицы должны иметь одинаковую длину");
            }
            for (int column = 0; column < columns; column++) {
                values[row, column] = weights[row][column];
            }
        }
        return MatrixSlot.ForKernel(new FilterKernel(values, data.AnchorRow, data.AnchorColumn, data.Divisor, data.Offset));
    }

    private static StructuralElementSlotData WriteSlot(MatrixSlot slot) {
        if (slot.Element is StructuralElement element) {
            return WriteElement(element);
        }
        FilterKernel kernel = slot.Kernel!;
        double[][] weights = new double[kernel.Rows][];
        for (int row = 0; row < kernel.Rows; row++) {
            weights[row] = new double[kernel.Columns];
            for (int column = 0; column < kernel.Columns; column++) {
                weights[row][column] = kernel.GetWeight(row, column);
            }
        }
        return new StructuralElementSlotData {
            Kind = "filter", Cells = Array.Empty<int[]>(), Weights = weights,
            AnchorRow = kernel.AnchorRow, AnchorColumn = kernel.AnchorColumn,
            Divisor = kernel.Divisor, Offset = kernel.Offset
        };
    }

    private static StructuralElement ReadElement(StructuralElementSlotData data) {
        if (data.Cells is null || data.Cells.Length == 0 ||
            data.Cells[0] is null || data.Cells[0].Length == 0) {
            throw new InvalidDataException("Сохраненная матрица должна содержать строки и столбцы");
        }
        int rows = data.Cells.Length;
        int columns = data.Cells[0].Length;
        bool[,] cells = new bool[rows, columns];
        for (int row = 0; row < rows; row++) {
            int[]? values = data.Cells[row];
            if (values is null || values.Length != columns) {
                throw new InvalidDataException("Строки сохраненной матрицы должны иметь одинаковую длину");
            }
            for (int column = 0; column < columns; column++) {
                int value = values[column];
                if (value != 0 && value != 1) {
                    throw new InvalidDataException("Сохраненная матрица должна содержать только 0 и 1");
                }
                cells[row, column] = value == 1;
            }
        }
        // модель повторно проверяет опору и наличие активной клетки, затем сохраняет свою копию
        return new StructuralElement(cells, data.AnchorRow, data.AnchorColumn);
    }

    private static StructuralElementSlotData WriteElement(StructuralElement element) {
        int[][] cells = new int[element.Rows][];
        for (int row = 0; row < element.Rows; row++) {
            int[] values = new int[element.Columns];
            for (int column = 0; column < element.Columns; column++) {
                values[column] = element.IsActive(row, column) ? 1 : 0;
            }
            cells[row] = values;
        }
        return new StructuralElementSlotData {
            Kind = "morphology",
            Cells = cells,
            AnchorRow = element.AnchorRow,
            AnchorColumn = element.AnchorColumn
        };
    }

    private static bool IsStorageException(Exception exception) {
        return exception is IOException or InvalidDataException or UnauthorizedAccessException or
            JsonException or ArgumentException or NotSupportedException or SecurityException;
    }
}
