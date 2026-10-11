using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using ImageVisionApp.Models;

namespace ImageVisionApp.Controls;

public sealed class StructuralElementEditor : UserControl {
    private bool[,] cells = new bool[1, 1];
    private int anchorRow;
    private int anchorColumn;
    private StructuralElement savedElement;

    private readonly TextBox rowsInput = new TextBox { Width = 110 };
    private readonly TextBox columnsInput = new TextBox { Width = 110 };
    private readonly TextBox anchorRowInput = new TextBox { Width = 110 };
    private readonly TextBox anchorColumnInput = new TextBox { Width = 110 };
    private readonly Grid matrixGrid = new Grid {
        RowSpacing = 4,
        ColumnSpacing = 4,
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top
    };
    private readonly ScrollViewer matrixScroll = new ScrollViewer {
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
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
    private readonly TextBlock statusText = new TextBlock {
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.OrangeRed
    };

    public event Action<StructuralElement>? SelectionApplied;

    public StructuralElementEditor(StructuralElement initialElement) {
        savedElement = initialElement;
        Content = CreateLayout();
        SetElement(initialElement);
    }

    public void SetElement(StructuralElement element) {
        savedElement = element;
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
        RenderCells();
    }

    private Grid CreateLayout() {
        WrapPanel inputs = new WrapPanel { ItemSpacing = 12, LineSpacing = 8 };
        inputs.Children.Add(CreateLabeledInput("Строки", rowsInput));
        inputs.Children.Add(CreateLabeledInput("Столбцы", columnsInput));
        inputs.Children.Add(CreateLabeledInput("Опорная строка", anchorRowInput));
        inputs.Children.Add(CreateLabeledInput("Опорный столбец", anchorColumnInput));

        Button applySizeButton = new Button { Content = "Изменить размер и опору" };
        applySizeButton.Click += (_, _) => { TryApplyDimensions(); };
        StackPanel settings = new StackPanel {
            Spacing = 12,
            Children = { inputs, applySizeButton }
        };

        Button zoomOutButton = new Button { Content = "−", MinWidth = 32 };
        zoomOutButton.Click += (_, _) => SetZoom(zoomSlider.Value / 1.25);
        Button zoomInButton = new Button { Content = "+", MinWidth = 32 };
        zoomInButton.Click += (_, _) => SetZoom(zoomSlider.Value * 1.25);
        Button actualSizeButton = new Button { Content = "100%" };
        actualSizeButton.Click += (_, _) => SetZoom(100);
        Button fitButton = new Button { Content = "Вместить" };
        fitButton.Click += (_, _) => FitMatrix();
        zoomSlider.PropertyChanged += (_, e) => {
            if (e.Property == Slider.ValueProperty) {
                ApplyZoom();
            }
        };

        WrapPanel zoomControls = new WrapPanel {
            ItemSpacing = 8,
            LineSpacing = 6,
            Children = { zoomOutButton, zoomSlider, zoomText, zoomInButton, actualSizeButton, fitButton }
        };
        matrixScroll.Content = matrixGrid;
        // перехватываем сочетание до обычной прокрутки, колесо без модификатора оставляем ScrollViewer
        matrixScroll.AddHandler(
            InputElement.PointerWheelChangedEvent,
            MatrixScroll_OnPointerWheelChanged,
            RoutingStrategies.Tunnel);
        Border matrixFrame = new Border {
            Padding = new Thickness(8),
            Background = new SolidColorBrush(Color.Parse("#262626")),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Child = matrixScroll
        };
        Grid matrixArea = new Grid {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 8
        };
        matrixArea.Children.Add(zoomControls);
        Grid.SetRow(matrixFrame, 1);
        matrixArea.Children.Add(matrixFrame);
        TextBlock zoomHint = new TextBlock {
            Text = "Ctrl/Cmd + колесо — масштаб. При сильном уменьшении виден рисунок без цифр.",
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(zoomHint, 2);
        matrixArea.Children.Add(zoomHint);

        // конечная высота строки ограничивает поле матрицы даже внутри общего ScrollViewer
        Grid editorBody = new Grid {
            ColumnDefinitions = new ColumnDefinitions("250,*"),
            ColumnSpacing = 16
        };
        editorBody.Children.Add(settings);
        Grid.SetColumn(matrixArea, 1);
        editorBody.Children.Add(matrixArea);

        Button resetButton = new Button { Content = "Сбросить черновик" };
        resetButton.Click += (_, _) => SetElement(savedElement);
        Button saveButton = new Button { Content = "Применить матрицу" };
        saveButton.Click += (_, _) => SaveSelection();
        StackPanel actions = new StackPanel {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Children = { resetButton, saveButton }
        };

        Grid layout = new Grid {
            RowDefinitions = new RowDefinitions("360,Auto,Auto"),
            RowSpacing = 10
        };
        layout.Children.Add(editorBody);
        Grid.SetRow(statusText, 1);
        layout.Children.Add(statusText);
        Grid.SetRow(actions, 2);
        layout.Children.Add(actions);
        return layout;
    }

    private void SetZoom(double percent) {
        zoomSlider.Value = Math.Clamp(percent, zoomSlider.Minimum, zoomSlider.Maximum);
    }

    private void ApplyZoom() {
        double scale = zoomSlider.Value / 100;
        double cellSize = 36 * scale;
        zoomText.Text = $"{zoomSlider.Value:0.##}%";
        matrixGrid.RowSpacing = 4 * scale;
        matrixGrid.ColumnSpacing = 4 * scale;
        foreach (RowDefinition row in matrixGrid.RowDefinitions) {
            row.Height = new GridLength(cellSize);
        }
        foreach (ColumnDefinition column in matrixGrid.ColumnDefinitions) {
            column.Width = new GridLength(cellSize);
        }
        // обновляем размер уже созданных кнопок, состав матрицы и черновик не меняем
        foreach (Control control in matrixGrid.Children) {
            if (control is Button button) {
                button.Width = cellSize;
                button.Height = cellSize;
                UpdateCellButton(button, Grid.GetRow(button), Grid.GetColumn(button));
            }
        }
    }

    private void FitMatrix() {
        double viewportWidth = matrixScroll.Viewport.Width;
        double viewportHeight = matrixScroll.Viewport.Height;
        if (viewportWidth <= 2 || viewportHeight <= 2) {
            return;
        }
        // считаем габариты при 100%, а не берем размеры уже увеличенной матрицы
        double originalWidth = cells.GetLength(1) * 36 + (cells.GetLength(1) - 1) * 4;
        double originalHeight = cells.GetLength(0) * 36 + (cells.GetLength(0) - 1) * 4;
        double scale = Math.Min(
            (viewportWidth - 2) / originalWidth,
            (viewportHeight - 2) / originalHeight);
        SetZoom(scale * 100);
        matrixScroll.Offset = new Vector(0, 0);
    }

    private void MatrixScroll_OnPointerWheelChanged(object? sender, PointerWheelEventArgs e) {
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) == 0 || e.Delta.Y == 0) {
            return;
        }
        SetZoom(e.Delta.Y > 0 ? zoomSlider.Value * 1.25 : zoomSlider.Value / 1.25);
        e.Handled = true;
    }
    private static StackPanel CreateLabeledInput(string label, TextBox input) {
        return new StackPanel {
            Width = 110,
            Spacing = 4,
            Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, input }
        };
    }

    private bool TryApplyDimensions() {
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
        }

        anchorRow = selectedRow - 1;
        anchorColumn = selectedColumn - 1;
        RenderCells();
        if (dimensionsChanged) {
            FitMatrix();
        }
        statusText.Text = "";
        return true;
    }

    private void RenderCells() {
        matrixGrid.Children.Clear();
        matrixGrid.RowDefinitions.Clear();
        matrixGrid.ColumnDefinitions.Clear();
        double scale = zoomSlider.Value / 100;
        double cellSize = 36 * scale;
        matrixGrid.RowSpacing = 4 * scale;
        matrixGrid.ColumnSpacing = 4 * scale;
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
                cellButton.Click += (_, _) => {
                    cells[cellRow, cellColumn] = !cells[cellRow, cellColumn];
                    UpdateCellButton(cellButton, cellRow, cellColumn);
                    statusText.Text = "";
                };
                Grid.SetRow(cellButton, row);
                Grid.SetColumn(cellButton, column);
                matrixGrid.Children.Add(cellButton);
            }
        }
    }

    private void UpdateCellButton(Button button, int row, int column) {
        bool isActive = cells[row, column];
        bool isAnchor = row == anchorRow && column == anchorColumn;
        double scale = zoomSlider.Value / 100;
        double cellSize = 36 * scale;
        Border visual;
        TextBlock number;
        if (button.Content is Border existingVisual && existingVisual.Child is TextBlock existingNumber) {
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
            button.Content = visual;
        }

        visual.Background = isActive ? Brushes.Black : Brushes.White;
        visual.BorderBrush = isAnchor ? Brushes.Red : Brushes.Gray;
        visual.BorderThickness = new Thickness(
            isAnchor ? Math.Min(2, cellSize / 4) : Math.Min(1, cellSize / 8));
        number.Text = isActive ? "1" : "0";
        number.Foreground = isActive ? Brushes.White : Brushes.Black;
        number.FontSize = Math.Min(40, 16 * scale);
        // маленькие ячейки показывают форму элемента, цифры возвращаются при приближении
        number.IsVisible = cellSize >= 12;
    }

    private void SaveSelection() {
        // учитываем числа в полях, даже если пользователь не нажал отдельную кнопку применения
        if (!TryApplyDimensions()) {
            return;
        }
        try {
            StructuralElement selectedElement =
                new StructuralElement(cells, anchorRow, anchorColumn);
            SelectionApplied?.Invoke(selectedElement);
            savedElement = selectedElement;
            statusText.Text = "";
        }
        catch (ArgumentException exception) {
            statusText.Text = exception.Message;
        }
    }
}
