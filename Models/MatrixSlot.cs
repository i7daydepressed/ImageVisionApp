using System;

namespace ImageVisionApp.Models;

public enum MatrixSlotKind { Morphology, Filter }

// ячейка содержит один тип снимка, числа 0/1 не превращают фильтр в морфологию
public sealed class MatrixSlot {
    public MatrixSlotKind Kind { get; }
    public StructuralElement? Element { get; }
    public FilterKernel? Kernel { get; }

    private MatrixSlot(StructuralElement element) {
        Kind = MatrixSlotKind.Morphology;
        Element = element.CreateSnapshot();
    }

    private MatrixSlot(FilterKernel kernel) {
        Kind = MatrixSlotKind.Filter;
        Kernel = kernel.CreateSnapshot();
    }

    public static MatrixSlot ForElement(StructuralElement element) {
        ArgumentNullException.ThrowIfNull(element);
        return new MatrixSlot(element);
    }

    public static MatrixSlot ForKernel(FilterKernel kernel) {
        ArgumentNullException.ThrowIfNull(kernel);
        return new MatrixSlot(kernel);
    }

    public MatrixSlot CreateSnapshot() {
        return Element is StructuralElement element ? ForElement(element) : ForKernel(Kernel!);
    }

    public string Description => Element is StructuralElement element
        ? $"Морфология: {element.Rows}×{element.Columns}, активных {element.ActiveCellCount}, " +
          $"опора X={element.AnchorColumn + 1}, Y={element.AnchorRow + 1}"
        : $"Числовой фильтр: {Kernel!.Rows}×{Kernel.Columns}, " +
          $"опора X={Kernel.AnchorColumn + 1}, Y={Kernel.AnchorRow + 1}, " +
          $"делитель {Kernel.Divisor:G}, смещение {Kernel.Offset:G}";

    public static bool CanReplace(MatrixSlot? current, MatrixSlot incoming, out string error) {
        ArgumentNullException.ThrowIfNull(incoming);
        error = string.Empty;
        if (current is not null && current.Kind != incoming.Kind) {
            error = "В ячейке матрица другого типа. Выберите совместимую или пустую ячейку; для смены типа сначала очистите ее.";
            return false;
        }
        return true;
    }
}
