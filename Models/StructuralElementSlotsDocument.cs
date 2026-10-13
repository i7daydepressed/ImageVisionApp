namespace ImageVisionApp.Models;

// описывает файл слотов, рабочие матрицы восстанавливает и проверяет сервис
public sealed class StructuralElementSlotsDocument {
    public required int Version { get; init; }
    public required StructuralElementSlotData?[] Slots { get; init; }
}

// в JSON каждая строка является отдельным массивом 0/1, опора хранится с индексами от 0
public sealed class StructuralElementSlotData {
    public required int[][] Cells { get; init; }
    public required int AnchorRow { get; init; }
    public required int AnchorColumn { get; init; }
}
