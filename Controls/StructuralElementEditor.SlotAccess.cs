using System;
using ImageVisionApp.Models;

namespace ImageVisionApp.Controls;

public sealed partial class StructuralElementEditor {
    public int SavedSlotCapacity => SavedSlotCount;

    public StructuralElement? GetSavedSlotSnapshot(int index) {
        if (index < 0 || index >= SavedSlotCount) {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        // окно получает отдельную модель, черновик и примененная матрица остаются прежними
        return savedSlots[index]?.CreateSnapshot();
    }

    public bool TrySaveElementToSlot(int index, StructuralElement element, out string error) {
        error = string.Empty;
        if (index < 0 || index >= SavedSlotCount) {
            error = "Укажите существующую ячейку матрицы";
            return false;
        }
        ArgumentNullException.ThrowIfNull(element);
        StructuralElement snapshot = element.CreateSnapshot();
        if (!TryStoreSlot(index, snapshot, out error)) {
            return false;
        }
        SetSlotsStatus($"Матрица шага сохранена в ячейку {index + 1}");
        return true;
    }
}
