using System;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Interactivity;
using ImageVisionApp.Models;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

// хранит текстовый черновик, подтверждение передает владельцу очереди
public sealed class FilterKernelWindow : Window {
    private readonly Func<FilterKernel, string?> applyKernel;
    private readonly TextBox rowsInput = new TextBox { Width = 80 };
    private readonly TextBox columnsInput = new TextBox { Width = 80 };
    private readonly TextBox anchorXInput = new TextBox { Width = 80 };
    private readonly TextBox anchorYInput = new TextBox { Width = 80 };
    private readonly TextBox divisorInput = new TextBox { Width = 120 };
    private readonly TextBox offsetInput = new TextBox { Width = 120 };
    private readonly Grid matrixGrid = new Grid { RowSpacing = 4, ColumnSpacing = 4 };
    private readonly TextBlock statusText = new TextBlock {
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.OrangeRed,
        IsVisible = false
    };
    private TextBox[,] weightInputs = new TextBox[0, 0];
    private Border[,] cellBorders = new Border[0, 0];

    public FilterKernelWindow(
        FilterKernel initialKernel, string title, string actionLabel,
        Func<FilterKernel, string?> applyKernel) {
        ArgumentNullException.ThrowIfNull(initialKernel);
        ArgumentNullException.ThrowIfNull(applyKernel);
        this.applyKernel = applyKernel;
        Title = title;
        Width = 760;
        Height = 620;
        MinWidth = 580;
        MinHeight = 520;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        rowsInput.Text = initialKernel.Rows.ToString(CultureInfo.InvariantCulture);
        columnsInput.Text = initialKernel.Columns.ToString(CultureInfo.InvariantCulture);
        anchorXInput.Text = (initialKernel.AnchorColumn + 1).ToString(CultureInfo.InvariantCulture);
        anchorYInput.Text = (initialKernel.AnchorRow + 1).ToString(CultureInfo.InvariantCulture);
        divisorInput.Text = initialKernel.Divisor.ToString("G17", CultureInfo.InvariantCulture);
        offsetInput.Text = initialKernel.Offset.ToString("G17", CultureInfo.InvariantCulture);
        string[,] values = new string[initialKernel.Rows, initialKernel.Columns];
        for (int row = 0; row < initialKernel.Rows; row++) {
            for (int column = 0; column < initialKernel.Columns; column++) {
                values[row, column] = initialKernel.GetWeight(row, column).ToString("G17", CultureInfo.InvariantCulture);
            }
        }
        BuildMatrix(values);
        anchorXInput.TextChanged += (_, _) => RefreshAnchorHighlight();
        anchorYInput.TextChanged += (_, _) => RefreshAnchorHighlight();
        Button resizeButton = new Button {
            Content = "Изменить размер",
            VerticalAlignment = VerticalAlignment.Bottom
        };
        resizeButton.Click += (_, _) => ResizeMatrix();
        Button applyButton = new Button { Content = actionLabel };
        applyButton.Click += (_, _) => ApplyDraft();
        Button cancelButton = new Button { Content = "Отмена" };
        cancelButton.Click += (_, _) => Close();
        AddHandler(InputElement.KeyDownEvent, (_, e) => {
            if (e.Key == Key.Escape) {
                e.Handled = true;
                Close();
            }
        }, RoutingStrategies.Tunnel);
        StackPanel header = new StackPanel {
            Spacing = 10,
            Children = {
                new TextBlock { Text = title, FontSize = 18, FontWeight = FontWeight.SemiBold },
                new TextBlock {
                    Text = "Ввод меняет только черновик. Строки — Y, столбцы — X. Опора выделена синей рамкой; координаты начинаются с 1.",
                    TextWrapping = TextWrapping.Wrap
                },
                new WrapPanel {
                    ItemSpacing = 10, LineSpacing = 8,
                    Children = { Field("Строк (Y)", rowsInput), Field("Столбцов (X)", columnsInput), resizeButton }
                },
                new WrapPanel {
                    ItemSpacing = 10, LineSpacing = 8,
                    Children = {
                        Field("Опора X", anchorXInput), Field("Опора Y", anchorYInput),
                        Field("Делитель", divisorInput), Field("Смещение", offsetInput)
                    }
                },
                new TextBlock {
                    Text = "Канал = сумма(вес × сосед) / делитель + смещение. Затем ограничение 0–255 и округление. Матрица не отражается; за краем повторяется ближайший пиксель. Дроби: точка или запятая, без разделителей тысяч.",
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap
                }
            }
        };
        // таблица занимает оставшееся место, действия остаются под ней при прокрутке
        Grid layout = new Grid {
            Margin = new Thickness(16),
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 12
        };
        layout.Children.Add(header);
        ScrollViewer matrixScroll = new ScrollViewer {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = matrixGrid
        };
        Grid.SetRow(matrixScroll, 1);
        layout.Children.Add(matrixScroll);
        StackPanel footer = new StackPanel {
            Spacing = 8,
            Children = {
                statusText,
                new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Children = { applyButton, cancelButton } }
            }
        };
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        Content = layout;
    }

    private static StackPanel Field(string label, TextBox input) {
        return new StackPanel { Spacing = 4, Children = { new TextBlock { Text = label }, input } };
    }

    private void BuildMatrix(string[,] values) {
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
        matrixGrid.Children.Clear();
        matrixGrid.RowDefinitions = new RowDefinitions("Auto");
        matrixGrid.ColumnDefinitions = new ColumnDefinitions("Auto");
        matrixGrid.Children.Add(nextGrid);
        weightInputs = nextInputs;
        cellBorders = nextBorders;
        RefreshAnchorHighlight();
    }

    private string[,] ReadWeights() {
        string[,] values = new string[weightInputs.GetLength(0), weightInputs.GetLength(1)];
        for (int row = 0; row < values.GetLength(0); row++) {
            for (int column = 0; column < values.GetLength(1); column++) {
                values[row, column] = weightInputs[row, column].Text ?? string.Empty;
            }
        }
        return values;
    }

    private void RefreshAnchorHighlight() {
        bool validX = FilterKernelInput.TryReadPositiveInteger(anchorXInput.Text, out int x);
        bool validY = FilterKernelInput.TryReadPositiveInteger(anchorYInput.Text, out int y);
        bool valid = validX && validY;
        for (int row = 0; row < cellBorders.GetLength(0); row++) {
            for (int column = 0; column < cellBorders.GetLength(1); column++) {
                cellBorders[row, column].BorderBrush = valid && row == y - 1 && column == x - 1
                    ? Brushes.DodgerBlue : Brushes.Transparent;
            }
        }
    }

    private void ResizeMatrix() {
        if (!FilterKernelInput.TryReadPositiveInteger(rowsInput.Text, out int rows) ||
            !FilterKernelInput.TryReadPositiveInteger(columnsInput.Text, out int columns)) {
            SetError("Размеры должны быть положительными целыми числами");
            return;
        }
        try {
            string[,] previous = ReadWeights();
            string[,] next = new string[rows, columns];
            for (int row = 0; row < rows; row++) {
                for (int column = 0; column < columns; column++) {
                    next[row, column] = row < previous.GetLength(0) && column < previous.GetLength(1)
                        ? previous[row, column] : "0";
                }
            }
            BuildMatrix(next);
            if (FilterKernelInput.TryReadPositiveInteger(anchorXInput.Text, out int x) && x > columns) {
                anchorXInput.Text = columns.ToString(CultureInfo.InvariantCulture);
            }
            if (FilterKernelInput.TryReadPositiveInteger(anchorYInput.Text, out int y) && y > rows) {
                anchorYInput.Text = rows.ToString(CultureInfo.InvariantCulture);
            }
            RefreshAnchorHighlight();
            SetError(string.Empty);
        }
        catch (Exception exception) {
            SetError($"Не удалось изменить размер: {exception.Message}");
        }
    }

    private void ApplyDraft() {
        if (!FilterKernelInput.TryReadPositiveInteger(rowsInput.Text, out int rows) ||
            !FilterKernelInput.TryReadPositiveInteger(columnsInput.Text, out int columns) ||
            rows != weightInputs.GetLength(0) || columns != weightInputs.GetLength(1)) {
            SetError("Укажите размеры и нажмите «Изменить размер», чтобы обновить таблицу");
            return;
        }
        try {
            if (!FilterKernelInput.TryCreate(ReadWeights(), anchorYInput.Text, anchorXInput.Text,
                    divisorInput.Text, offsetInput.Text, out FilterKernel? kernel, out string error)) {
                SetError(error);
                return;
            }
            string? applyError = applyKernel(kernel!);
            if (applyError is not null) {
                SetError(applyError);
                return;
            }
            Close();
        }
        catch (Exception exception) {
            SetError($"Не удалось применить матрицу: {exception.Message}");
        }
    }

    private void SetError(string message) {
        statusText.Text = message;
        statusText.IsVisible = message.Length > 0;
    }
}
