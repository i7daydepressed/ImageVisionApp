using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.Controls;

public sealed partial class StructuralElementEditor : UserControl {
    // режим задает одно действие по щелчку, значение, выделение и опору храним независимо
    private enum CellEditMode {
        ToggleValue,
        SelectCells,
        SetAnchor
    }

    private IPointer? brushPointer;
    private Point? previousBrushPoint;
    // кнопку и действие фиксируем в начале прохода, смена режима завершает жест
    private bool brushUsesRightButton;
    private CellEditMode brushMode;
    // в выделении весь проход добавляет или снимает отметки по состоянию начальной клетки
    private bool brushSelectCells;
    private readonly HashSet<(int Row, int Column)> visitedDuringStroke =
        new HashSet<(int Row, int Column)>();
    private readonly Button invertSelectedButton = new Button {
        Content = "Инвертировать выделенные",
        IsEnabled = false,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private CellEditMode editMode = CellEditMode.ToggleValue;
    // здесь только временные отметки с индексами от 0, в сохраняемую матрицу они не попадают
    private readonly HashSet<(int Row, int Column)> selectedCells =
        new HashSet<(int Row, int Column)>();
    // по координатам получаем уже созданную кнопку, чтобы группа не пересоздавала всю сетку
    private readonly Dictionary<(int Row, int Column), Button> cellButtons =
        new Dictionary<(int Row, int Column), Button>();
    private readonly ComboBox editModeInput = new ComboBox {
        ItemsSource = new string[] {
            "Изменять 0/1",
            "Выделять клетки",
            "Назначать опору"
        },
        SelectedIndex = 0,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly TextBlock editModeHint = new TextBlock {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12
    };
    private readonly TextBlock selectionCountText = new TextBlock {
        Text = "Выделено клеток: 0"
    };
    private readonly Button selectedZeroButton = new Button {
        Content = "Установить 0",
        IsEnabled = false
    };
    private readonly Button selectedOneButton = new Button {
        Content = "Установить 1",
        IsEnabled = false
    };
    private readonly Button clearSelectionButton = new Button {
        Content = "Снять выделение",
        IsEnabled = false,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private const int SavedSlotCount = 4;
    private readonly StructuralElementSlotStorage slotStorage =
        new StructuralElementSlotStorage(SavedSlotCount);
    private readonly StructuralElement?[] savedSlots = new StructuralElement?[SavedSlotCount];
    private readonly TextBlock[] slotDescriptions = new TextBlock[SavedSlotCount];
    private readonly Button[] slotSaveButtons = new Button[SavedSlotCount];
    private readonly Button[] slotLoadButtons = new Button[SavedSlotCount];
    private readonly Button[] slotClearButtons = new Button[SavedSlotCount];
    private readonly TextBlock slotsStatusText = new TextBlock {
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12
    };

    private const double CellSide = 36;
    private const double CellGap = 4;
    private const double RulerWidth = 44;
    private const double RulerHeight = 28;
    private static readonly IBrush RulerHoverBrush =
        new SolidColorBrush(Color.Parse("#FFC857"));
    private Point? hoverViewportPoint;
    private int? hoveredRow;
    private int? hoveredColumn;
    private int? inspectedRow;
    private int? inspectedColumn;
    private readonly List<Border> rowMarkers = new List<Border>();
    private readonly List<Border> columnMarkers = new List<Border>();
    private readonly Canvas columnRuler = new Canvas {
        Height = RulerHeight,
        Width = 0,
        ClipToBounds = true,
        IsHitTestVisible = false,
        HorizontalAlignment = HorizontalAlignment.Left
    };
    private readonly Canvas rowRuler = new Canvas {
        Width = RulerWidth,
        Height = 0,
        ClipToBounds = true,
        IsHitTestVisible = false,
        VerticalAlignment = VerticalAlignment.Top
    };
    private readonly TextBlock cellInfoText = new TextBlock {
        Text = "Наведите указатель на клетку",
        TextWrapping = TextWrapping.Wrap,
        MinHeight = 112
    };
    private bool[,] cells = new bool[1, 1];
    private int anchorRow;
    private int anchorColumn;
    private StructuralElement savedElement;
    private bool fitToViewport;
    private bool updatingZoom;
    private bool fitQueued;
    private double appliedZoomPercent = 100;
    private Vector? pendingScrollOffset;
    private int scrollOffsetRevision;

    private readonly TextBox rowsInput = new TextBox { Width = 110 };
    private readonly TextBox columnsInput = new TextBox { Width = 110 };
    private readonly TextBox anchorRowInput = new TextBox { Width = 110 };
    private readonly TextBox anchorColumnInput = new TextBox { Width = 110 };
    private readonly Grid matrixGrid = new Grid {
        RowSpacing = CellGap,
        ColumnSpacing = CellGap,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top
    };
    private readonly ScrollViewer matrixScroll = new ScrollViewer {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        IsScrollChainingEnabled = false,
        Background = Brushes.Transparent
    };
    private readonly Slider zoomSlider = new Slider {
        Width = 160,
        Minimum = 1,
        Maximum = 300,
        Value = 100,
        TickFrequency = 10,
        IsSnapToTickEnabled = false
    };
    private readonly TextBlock zoomText = new TextBlock {
        Text = "100%",
        Width = 64,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock zoomModeText = new TextBlock {
        Text = "Ручной",
        FontSize = 12,
        VerticalAlignment = VerticalAlignment.Center
    };

    private readonly TextBlock statusText = new TextBlock {
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.OrangeRed
    };

    public event Action<StructuralElement>? SelectionApplied;

    public StructuralElementEditor(
        StructuralElement initialElement,
        TextBlock description) {
        savedElement = initialElement;
        Content = CreateLayout(description);
        SetElement(initialElement);
        RestoreSavedSlots();
    }

    public void SetElement(StructuralElement element) {
        savedElement = element;
        LoadDraft(element);
    }

    private void LoadDraft(StructuralElement element) { // копируем снимок в черновик, последний примененный элемент не меняем
        EndBrushStroke();
        // меняем только черновик, поэтому сброс оставит прежний элемент
        cells = element.CopyCells();
        anchorRow = element.AnchorRow;
        anchorColumn = element.AnchorColumn;
        rowsInput.Text = element.Rows.ToString();
        columnsInput.Text = element.Columns.ToString();
        // в интерфейсе координаты начинаются с 1, в массиве с 0
        anchorRowInput.Text = (anchorRow + 1).ToString();
        anchorColumnInput.Text = (anchorColumn + 1).ToString();
        statusText.Text = "";
        hoverViewportPoint = null;
        hoveredRow = null;
        hoveredColumn = null;
        inspectedRow = null;
        inspectedColumn = null;
        // у нового снимка нет старых отметок, выбранный режим щелчка при этом сохраняем
        selectedCells.Clear();
        CancelPendingScrollOffset();
        RenderCells();
        if (fitToViewport) {
            ScheduleFit();
        }
        else {
            QueueScrollOffset(new Vector(0, 0));
        }
    }

    private void UpdateSelectionControls() { // согласуем счетчик, доступность кнопок и подсказку с отметками и режимом
        selectionCountText.Text = $"Выделено клеток: {selectedCells.Count}";
        // группа доступна в любом режиме, ее ограничивает только наличие отметок
        bool hasSelection = selectedCells.Count > 0;
        selectedZeroButton.IsEnabled = hasSelection;
        selectedOneButton.IsEnabled = hasSelection;
        invertSelectedButton.IsEnabled = hasSelection;
        clearSelectionButton.IsEnabled = hasSelection;
        editModeHint.Text = editMode switch {
            CellEditMode.SelectCells => "Зажмите ЛКМ и ведите: от невыделенной клетки — выделить путь, от выделенной — снять выделение. Значения 0/1 сохраняются",
            CellEditMode.SetAnchor => "Щелчок ЛКМ назначает единственную опору, значение 0/1 сохраняется",
            _ => "Щелчок или протягивание ЛКМ инвертирует 0/1, каждую клетку один раз за проход"
        };
        editModeHint.Text += "\nЗажатая ПКМ всегда инвертирует 0/1 независимо от режима";
    }

    private void SetSelectedCells(bool value) { // задаем одно 0/1 всем отметкам, чтобы менять разрозненный набор вместе
        if (selectedCells.Count == 0) {
            return;
        }
        // меняем только отмеченные позиции черновика, выделение и опора остаются прежними
        foreach ((int Row, int Column) coordinate in selectedCells) {
            cells[coordinate.Row, coordinate.Column] = value;
            RefreshCellVisual(coordinate.Row, coordinate.Column);
        }
        // сведения должны показать новое 0/1 у клетки под указателем или последнего щелчка
        UpdateSelectionControls();
        UpdateCellInfo();
        statusText.Text = $"Выделенным клеткам установлено {(value ? 1 : 0)}";
    }

    private void InvertSelectedCells() { // меняем 0/1 только у красных отметок, сохраняя набор и одну опору
        if (selectedCells.Count == 0) {
            return;
        }
        foreach ((int Row, int Column) coordinate in selectedCells) {
            cells[coordinate.Row, coordinate.Column] = !cells[coordinate.Row, coordinate.Column];
            RefreshCellVisual(coordinate.Row, coordinate.Column);
        }
        UpdateSelectionControls();
        UpdateCellInfo();
        statusText.Text = "Значения выделенных клеток инвертированы";
    }

    private void ClearCellSelection() { // снимаем отметки и рамки, сохраняя значения и единственную опору
        // берем копию координат до очистки, иначе не найдем кнопки со старой красной рамкой
        List<(int Row, int Column)> previousSelection =
            new List<(int Row, int Column)>(selectedCells);
        selectedCells.Clear();
        foreach ((int Row, int Column) coordinate in previousSelection) {
            RefreshCellVisual(coordinate.Row, coordinate.Column);
        }
        UpdateSelectionControls();
        UpdateCellInfo();
        statusText.Text = "";
    }

    private bool TryApplyDimensions() {
        // сначала проверяем все четыре поля, ошибка не должна менять массив или выделение
        if (!int.TryParse(rowsInput.Text, out int rows) || rows <= 0 ||
            !int.TryParse(columnsInput.Text, out int columns) || columns <= 0) {
            statusText.Text = "Число строк и столбцов должно быть целым и больше 0";
            return false;
        }
        if (!int.TryParse(anchorRowInput.Text, out int selectedRow) ||
            !int.TryParse(anchorColumnInput.Text, out int selectedColumn) ||
            selectedRow < 1 || selectedRow > rows ||
            selectedColumn < 1 || selectedColumn > columns) {
            statusText.Text = "Укажите опорную точку внутри выбранного размера матрицы";
            return false;
        }

        bool dimensionsChanged = rows != cells.GetLength(0) || columns != cells.GetLength(1);
        if (dimensionsChanged) {
            bool[,] resizedCells = new bool[rows, columns];
            int preservedRows = Math.Min(rows, cells.GetLength(0));
            int preservedColumns = Math.Min(columns, cells.GetLength(1));
            // сохраняем пересечение старой и новой матриц, новые ячейки остаются выключенными
            for (int row = 0; row < preservedRows; row++) {
                for (int column = 0; column < preservedColumns; column++) {
                    resizedCells[row, column] = cells[row, column];
                }
            }
            cells = resizedCells;
            // оставляем отметки только в сохранившемся верхнем левом пересечении
            selectedCells.RemoveWhere(coordinate =>
                coordinate.Row < 0 || coordinate.Row >= rows ||
                coordinate.Column < 0 || coordinate.Column >= columns);
        }

        anchorRow = selectedRow - 1;
        anchorColumn = selectedColumn - 1;
        // пересоздаем кнопки после принятого размера и опоры, режим и оставшиеся отметки сохраняем
        RenderCells();
        if (dimensionsChanged) {
            FitMatrix();
        }
        statusText.Text = "";
        return true;
    }

    private void HandleCellClick(int row, int column) { // выполняем только выбранное действие, чтобы щелчок не менял разные состояния сразу
        // координаты относятся к отображаемой клетке, даже если в полях уже введен другой размер
        // запоминаем ее для сведений и выполняем только выбранное действие над черновиком
        inspectedRow = row;
        inspectedColumn = column;
        switch (editMode) {
            case CellEditMode.ToggleValue:
                cells[row, column] = !cells[row, column];
                RefreshCellVisual(row, column);
                break;
            case CellEditMode.SelectCells:
                // для уже отмеченной клетки HashSet.Add вернет false, повторный щелчок снимет отметку
                if (!selectedCells.Add((row, column))) {
                    selectedCells.Remove((row, column));
                }
                RefreshCellVisual(row, column);
                break;
            case CellEditMode.SetAnchor:
                // обновляем старую и новую опору, чтобы голубая рамка осталась только у одной клетки
                int previousRow = anchorRow;
                int previousColumn = anchorColumn;
                anchorRow = row;
                anchorColumn = column;
                // поля опоры показывают фактический выбор, непримененные размеры оставляем как были
                anchorRowInput.Text = (row + 1).ToString();
                anchorColumnInput.Text = (column + 1).ToString();
                RefreshCellVisual(previousRow, previousColumn);
                RefreshCellVisual(row, column);
                break;
        }
        UpdateSelectionControls();
        UpdateCellInfo();
        statusText.Text = "";
    }

    private void RefreshCellVisual(int row, int column) { // обновляем одну кнопку по координатам без пересоздания сетки
        // после resize индекс содержит только текущие кнопки, контролы прежней сетки сюда не попадут
        if (cellButtons.TryGetValue((row, column), out Button? button)) {
            UpdateCellButton(button, row, column);
        }
    }

    private void RenderCells() {
        EndBrushStroke();
        // старые кнопки уходят из дерева, вместе с ними убираем ссылки из словаря
        cellButtons.Clear();
        matrixGrid.Children.Clear();
        matrixGrid.RowDefinitions.Clear();
        matrixGrid.ColumnDefinitions.Clear();
        double scale = zoomSlider.Value / 100;
        Size matrixSize = GetMatrixSize(zoomSlider.Value);
        matrixGrid.Width = matrixSize.Width;
        matrixGrid.Height = matrixSize.Height;
        appliedZoomPercent = zoomSlider.Value;
        double cellSize = CellSide * scale;
        matrixGrid.RowSpacing = CellGap * scale;
        matrixGrid.ColumnSpacing = CellGap * scale;
        for (int row = 0; row < cells.GetLength(0); row++) {
            matrixGrid.RowDefinitions.Add(new RowDefinition(new GridLength(cellSize)));
        }
        for (int column = 0; column < cells.GetLength(1); column++) {
            matrixGrid.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(cellSize)));
        }

        for (int row = 0; row < cells.GetLength(0); row++) {
            for (int column = 0; column < cells.GetLength(1); column++) {
                // каждая кнопка запоминает координаты своей ячейки, а не переменные цикла
                int cellRow = row;
                int cellColumn = column;
                Button cellButton = new Button {
                    Width = cellSize,
                    Height = cellSize,
                    MinWidth = 0,
                    MinHeight = 0,
                    Padding = new Thickness(0),
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    CornerRadius = new CornerRadius(0),
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    VerticalContentAlignment = VerticalAlignment.Stretch
                };
                UpdateCellButton(cellButton, cellRow, cellColumn);
                cellButtons.Add((cellRow, cellColumn), cellButton);
                cellButton.Click += (_, _) => HandleCellClick(cellRow, cellColumn);
                Grid.SetRow(cellButton, row);
                Grid.SetColumn(cellButton, column);
                matrixGrid.Children.Add(cellButton);
            }
        }
        if (inspectedRow.HasValue && inspectedColumn.HasValue &&
            (inspectedRow.Value >= cells.GetLength(0) || inspectedColumn.Value >= cells.GetLength(1))) {
            inspectedRow = null;
            inspectedColumn = null;
        }
        UpdateSelectionControls();
        BuildRulers();
        UpdateCoordinateView();
    }

    private void UpdateCellButton(Button button, int row, int column) {
        bool isActive = cells[row, column];
        bool isAnchor = row == anchorRow && column == anchorColumn;
        bool isSelected = selectedCells.Contains((row, column));
        double scale = zoomSlider.Value / 100;
        double cellSize = CellSide * scale;
        Border selectionFrame;
        Border visual;
        TextBlock number;
        // при zoom и группе сохраняем вложенные рамки и текст, меняем только их оформление
        if (button.Content is Border existingFrame &&
            existingFrame.Child is Border existingVisual &&
            existingVisual.Child is TextBlock existingNumber) {
            selectionFrame = existingFrame;
            visual = existingVisual;
            number = existingNumber;
        }
        else {
            number = new TextBlock {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeight.SemiBold
            };
            // собственная заливка внутри кнопки сохраняет цвет ячейки при наведении и нажатии
            visual = new Border { Child = number, CornerRadius = new CornerRadius(0) };
            // внешняя рамка выделения и внутренняя рамка опоры показываются независимо
            selectionFrame = new Border { Child = visual, CornerRadius = new CornerRadius(0) };
            button.Content = selectionFrame;
        }

        selectionFrame.Background = Brushes.Transparent;
        selectionFrame.BorderBrush = Brushes.Red;
        // на мелком масштабе ограничиваем рамки долей клетки, чтобы они не закрыли всю заливку
        selectionFrame.BorderThickness = new Thickness(
            isSelected ? Math.Min(2, cellSize / 6) : 0);
        visual.Background = isActive ? Brushes.Black : Brushes.White;
        visual.BorderBrush = isAnchor ? Brushes.DeepSkyBlue : Brushes.Gray;
        visual.BorderThickness = new Thickness(
            isAnchor ? Math.Min(2, cellSize / 4) : Math.Min(1, cellSize / 8));
        number.Text = isActive ? "1" : "0";
        number.Foreground = isActive ? Brushes.White : Brushes.Black;
        number.FontSize = Math.Min(40, 16 * scale);
        // маленькие ячейки показывают форму элемента, цифры возвращаются при приближении
        number.IsVisible = cellSize >= 12;
    }

    private bool TryCreateDraftSnapshot(out StructuralElement? snapshot) { // учитываем поля и проверяем весь черновик до записи или применения
        snapshot = null;
        EndBrushStroke();
        // учитываем числа в полях, даже если пользователь не нажал отдельную кнопку применения
        if (!TryApplyDimensions()) {
            return false;
        }
        try {
            // сохраняем весь черновик с одной опорой, временное выделение остается только в редакторе
            snapshot = new StructuralElement(cells, anchorRow, anchorColumn);
            return true;
        }
        catch (ArgumentException exception) {
            statusText.Text = exception.Message;
            return false;
        }
    }

    private void SaveSelection() {
        if (!TryCreateDraftSnapshot(out StructuralElement? selectedElement) || selectedElement is null) {
            return;
        }
        try {
            SelectionApplied?.Invoke(selectedElement);
            savedElement = selectedElement;
            statusText.Text = "";
        }
        catch (ArgumentException exception) {
            statusText.Text = exception.Message;
        }
    }
}
