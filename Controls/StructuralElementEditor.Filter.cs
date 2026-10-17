using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ImageVisionApp.Models;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Controls;

public sealed partial class StructuralElementEditor {
    // начальные нули существуют только в текстовом черновике, невалидный FilterKernel не создаем
    private readonly TextBox filterRows = new TextBox { Text = "0", Width = 110 };
    private readonly TextBox filterColumns = new TextBox { Text = "0", Width = 110 };
    private readonly TextBox filterAnchorX = new TextBox { Text = "0", Width = 110 };
    private readonly TextBox filterAnchorY = new TextBox { Text = "0", Width = 110 };
    private readonly TextBox filterDivisor = new TextBox { Text = "0", Width = 110 };
    private readonly TextBox filterOffset = new TextBox { Text = "0", Width = 110 };
    private readonly TextBlock filterStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, IsVisible = false };
    private readonly Grid filterGrid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
    private TextBox[,] filterWeights = new TextBox[0, 0];
    private Border[,] filterCellBorders = new Border[0, 0];
    private Control? filterScrollTarget;
    public event Func<FilterKernel, string?>? FilterApplyRequested;

    private Border CreateFilterSettingsCard() {
        filterAnchorX.TextChanged += (_, _) => RefreshFilterAnchor();
        filterAnchorY.TextChanged += (_, _) => RefreshFilterAnchor();
        Button resize = new Button { Content = "Изменить размер", HorizontalAlignment = HorizontalAlignment.Stretch };
        resize.Click += (_, _) => ResizeFilterMatrix();
        Button apply = new Button { Content = "Добавить в очередь", HorizontalAlignment = HorizontalAlignment.Stretch };
        apply.Click += (_, _) => TryApplyFilterDraft(out _);
        Button reset = new Button { Content = "Сбросить черновик", HorizontalAlignment = HorizontalAlignment.Stretch };
        reset.Click += (_, _) => {
            filterRows.Text = filterColumns.Text = filterAnchorX.Text = filterAnchorY.Text = "0";
            filterDivisor.Text = filterOffset.Text = "0";
            BuildFilterMatrix(new string[0, 0]);
            SetFilterStatus(string.Empty);
        };
        WrapPanel inputs = new WrapPanel {
            ItemSpacing = 12, LineSpacing = 8,
            Children = {
                CreateLabeledInput("Строки (Y)", filterRows), CreateLabeledInput("Столбцы (X)", filterColumns),
                CreateLabeledInput("Опора X", filterAnchorX), CreateLabeledInput("Опора Y", filterAnchorY),
                CreateLabeledInput("Делитель", filterDivisor), CreateLabeledInput("Смещение", filterOffset)
            }
        };
        Border card = MatrixCard(new StackPanel {
            Spacing = 10,
            Children = {
                new TextBlock { Text = "Числовой фильтр", FontWeight = FontWeight.SemiBold }, inputs, resize,
                new TextBlock {
                    Text = "Канал = сумма(вес × сосед) / делитель + смещение. Дроби: точка или запятая. Ввод меняет только черновик.",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12
                }, filterStatus, apply, reset
            }
        });
        filterScrollTarget = card;
        return card;
    }

    private Border CreateFilterMatrixCard() {
        return MatrixCard(new StackPanel {
            Spacing = 10,
            Children = {
                new TextBlock {
                    Text = "Коэффициенты вводятся в ячейки. Синяя рамка — опора. Для начала задайте положительные размеры и нажмите «Изменить размер».",
                    TextWrapping = TextWrapping.Wrap, FontSize = 12
                },
                new ScrollViewer {
                    MaxHeight = 360,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    Content = filterGrid
                }
            }
        });
    }

    private static Border MatrixCard(Control content) {
        return new Border {
            Padding = new Thickness(12), BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top,
            Child = content
        };
    }

    public void ShowFilterEditor(string? error = null) {
        SelectMatrixMode(1);
        if (error is not null) {
            SetFilterStatus("Недопустимые значения: " + error);
        }
        // переключение вкладок меняет layout, прокручиваем к параметрам после его обновления
        Dispatcher.UIThread.Post(() => filterScrollTarget?.BringIntoView(), DispatcherPriority.Loaded);
    }

    public bool TryGetFilterDraft(out FilterKernel? kernel, out string error) {
        kernel = null;
        if (!FilterKernelInput.TryReadPositiveInteger(filterRows.Text, out int rows) ||
            !FilterKernelInput.TryReadPositiveInteger(filterColumns.Text, out int columns)) {
            error = "Количество строк и столбцов должно быть целым и больше 0";
            return false;
        }
        if (rows != filterWeights.GetLength(0) || columns != filterWeights.GetLength(1)) {
            error = "Нажмите «Изменить размер», чтобы обновить таблицу";
            return false;
        }
        return FilterKernelInput.TryCreate(ReadFilterWeights(), filterAnchorY.Text, filterAnchorX.Text,
            filterDivisor.Text, filterOffset.Text, out kernel, out error);
    }

    public bool TryApplyFilterDraft(out string error) {
        error = string.Empty;
        try {
            if (!TryGetFilterDraft(out FilterKernel? kernel, out error)) {
                ShowFilterEditor(error);
                return false;
            }
            Func<FilterKernel, string?>? apply = FilterApplyRequested;
            string? applyError = apply is null ? "Очередь недоступна" : apply(kernel!);
            if (applyError is not null) {
                error = applyError;
                SelectMatrixMode(1);
                SetFilterStatus(error);
                return false;
            }
            SetFilterStatus("Фильтр добавлен в очередь", false);
            return true;
        }
        catch (Exception exception) {
            error = $"Не удалось применить матрицу: {exception.Message}";
            SetFilterStatus(error);
            return false;
        }
    }

    private void LoadFilterDraft(FilterKernel kernel) {
        string[,] values = new string[kernel.Rows, kernel.Columns];
        for (int row = 0; row < kernel.Rows; row++) {
            for (int column = 0; column < kernel.Columns; column++) {
                values[row, column] = kernel.GetWeight(row, column).ToString("G17", CultureInfo.InvariantCulture);
            }
        }
        BuildFilterMatrix(values);
        filterRows.Text = kernel.Rows.ToString(CultureInfo.InvariantCulture);
        filterColumns.Text = kernel.Columns.ToString(CultureInfo.InvariantCulture);
        filterAnchorX.Text = (kernel.AnchorColumn + 1).ToString(CultureInfo.InvariantCulture);
        filterAnchorY.Text = (kernel.AnchorRow + 1).ToString(CultureInfo.InvariantCulture);
        filterDivisor.Text = kernel.Divisor.ToString("G17", CultureInfo.InvariantCulture);
        filterOffset.Text = kernel.Offset.ToString("G17", CultureInfo.InvariantCulture);
        RefreshFilterAnchor();
        SetFilterStatus(string.Empty);
    }

    private void SetFilterStatus(string message, bool isError = true) {
        filterStatus.Text = message;
        filterStatus.IsVisible = message.Length > 0;
        filterStatus.Foreground = isError ? Brushes.OrangeRed : Brushes.Gray;
    }

    private void BuildFilterMatrix(string[,] values) {
        int rows = values.GetLength(0);
        int columns = values.GetLength(1);
        TextBox[,] nextInputs = new TextBox[rows, columns];
        Border[,] nextBorders = new Border[rows, columns];
        Grid nextGrid = new Grid {
            RowDefinitions = new RowDefinitions(string.Join(",", Enumerable.Repeat("Auto", rows + 1))),
            ColumnDefinitions = new ColumnDefinitions(string.Join(",", Enumerable.Repeat("Auto", columns + 1))),
            RowSpacing = 4,
            ColumnSpacing = 4
        };
        for (int column = 0; column < columns; column++) {
            TextBlock label = new TextBlock { Text = $"X={column + 1}", HorizontalAlignment = HorizontalAlignment.Center };
            Grid.SetColumn(label, column + 1);
            nextGrid.Children.Add(label);
        }
        for (int row = 0; row < rows; row++) {
            TextBlock label = new TextBlock { Text = $"Y={row + 1}", VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(label, row + 1);
            nextGrid.Children.Add(label);
            for (int column = 0; column < columns; column++) {
                TextBox input = new TextBox { Text = values[row, column], Width = 96 };
                Border border = new Border {
                    Padding = new Thickness(2),
                    BorderThickness = new Thickness(2),
                    BorderBrush = Brushes.Transparent,
                    Child = input
                };
                nextInputs[row, column] = input;
                nextBorders[row, column] = border;
                Grid.SetRow(border, row + 1);
                Grid.SetColumn(border, column + 1);
                nextGrid.Children.Add(border);
            }
        }
        // сначала построили новый набор контролов, затем заменяем текущую таблицу
        filterGrid.Children.Clear();
        filterGrid.RowDefinitions = new RowDefinitions("Auto");
        filterGrid.ColumnDefinitions = new ColumnDefinitions("Auto");
        filterGrid.Children.Add(nextGrid);
        filterWeights = nextInputs;
        filterCellBorders = nextBorders;
        RefreshFilterAnchor();
    }

    private string[,] ReadFilterWeights() {
        string[,] values = new string[filterWeights.GetLength(0), filterWeights.GetLength(1)];
        for (int row = 0; row < values.GetLength(0); row++) {
            for (int column = 0; column < values.GetLength(1); column++) {
                values[row, column] = filterWeights[row, column].Text ?? string.Empty;
            }
        }
        return values;
    }

    private void RefreshFilterAnchor() {
        bool validX = FilterKernelInput.TryReadPositiveInteger(filterAnchorX.Text, out int x);
        bool validY = FilterKernelInput.TryReadPositiveInteger(filterAnchorY.Text, out int y);
        bool valid = validX && validY;
        for (int row = 0; row < filterCellBorders.GetLength(0); row++) {
            for (int column = 0; column < filterCellBorders.GetLength(1); column++) {
                filterCellBorders[row, column].BorderBrush = valid && row == y - 1 && column == x - 1
                    ? Brushes.DodgerBlue : Brushes.Transparent;
            }
        }
    }

    private void ResizeFilterMatrix() {
        if (!FilterKernelInput.TryReadPositiveInteger(filterRows.Text, out int rows) ||
            !FilterKernelInput.TryReadPositiveInteger(filterColumns.Text, out int columns)) {
            SetFilterStatus("Размеры должны быть положительными целыми числами");
            return;
        }
        try {
            string[,] previous = ReadFilterWeights();
            string[,] next = new string[rows, columns];
            for (int row = 0; row < rows; row++) {
                for (int column = 0; column < columns; column++) {
                    next[row, column] = row < previous.GetLength(0) && column < previous.GetLength(1)
                        ? previous[row, column] : "0";
                }
            }
            BuildFilterMatrix(next);
            if (FilterKernelInput.TryReadPositiveInteger(filterAnchorX.Text, out int x) && x > columns) {
                filterAnchorX.Text = columns.ToString(CultureInfo.InvariantCulture);
            }
            if (FilterKernelInput.TryReadPositiveInteger(filterAnchorY.Text, out int y) && y > rows) {
                filterAnchorY.Text = rows.ToString(CultureInfo.InvariantCulture);
            }
            RefreshFilterAnchor();
            SetFilterStatus(string.Empty);
        }
        catch (Exception exception) {
            SetFilterStatus($"Не удалось изменить размер: {exception.Message}");
        }
    }

}
