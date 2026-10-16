using System;
using ImageVisionApp.Models;

namespace ImageVisionApp.Controls;

public sealed partial class StructuralElementEditor {
    public int SavedSlotCapacity => SavedSlotCount;

    public MatrixSlot? GetSavedMatrixSlotSnapshot(int index) {
        if (index < 0 || index >= SavedSlotCount) {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        // окно получает отдельную модель, черновик и примененная матрица остаются прежними
        // тип снимка сохраняем, чтобы окно проверяло совместимость
        return savedSlots[index]?.CreateSnapshot();
    }

    public StructuralElement? GetSavedSlotSnapshot(int index) {
        MatrixSlot? slot = GetSavedMatrixSlotSnapshot(index);
        if (slot?.Kind == MatrixSlotKind.Filter) {
            throw new InvalidOperationException("В ячейке числовой фильтр, а нужен структурный элемент");
        }
        return slot?.Element?.CreateSnapshot();
    }

    public bool TrySaveElementToSlot(int index, StructuralElement element, out string error) {
        ArgumentNullException.ThrowIfNull(element);
        return TrySaveMatrixToSlot(index, MatrixSlot.ForElement(element), out error);
    }

    public bool TrySaveMatrixToSlot(int index, MatrixSlot slot, out string error) {
        ArgumentNullException.ThrowIfNull(slot);
        error = string.Empty;
        if (index < 0 || index >= SavedSlotCount) {
            error = "Укажите существующую ячейку матрицы";
            return false;
        }
        if (!TryStoreSlot(index, slot.CreateSnapshot(), out error)) {
            return false;
        }
        SetSlotsStatus($"Матрица шага сохранена в ячейку {index + 1}");
        return true;
    }
}
