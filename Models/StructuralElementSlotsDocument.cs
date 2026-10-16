namespace ImageVisionApp.Models;

// описывает файл слотов, рабочие матрицы восстанавливает и проверяет сервис
public sealed class StructuralElementSlotsDocument {
    public required int Version { get; init; }
    public required StructuralElementSlotData?[] Slots { get; init; }
}

// в JSON cells хранит строки 0/1, weights числовые строки, опора хранится с индексами от 0
public sealed class StructuralElementSlotData {
    // версия 2 различает morphology и filter, отсутствие типа допустимо только в старом формате 1
    public string? Kind { get; init; }
    public double[][]? Weights { get; init; }
    public double Divisor { get; init; } = 1;
    public double Offset { get; init; }
    public required int[][] Cells { get; init; }
    public required int AnchorRow { get; init; }
    public required int AnchorColumn { get; init; }
}
