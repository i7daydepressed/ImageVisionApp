using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using ImageVisionApp.Models;

namespace ImageVisionApp.Views;

internal sealed partial class FaceDatabaseView {
    private const int ComparisonCellWidth = 68;
    private const int ComparisonCellHeight = 30;
    private static readonly CultureInfo ComparisonCulture = CultureInfo.GetCultureInfo("ru-RU");
    private static readonly IBrush ComparisonLine = new SolidColorBrush(Colors.Gray, 0.55);
    private static readonly IBrush ComparisonHeader = new SolidColorBrush(Colors.Gray, 0.18);
    private readonly Grid comparisonCells = new() {
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top
    };
    private readonly Border comparisonViewport = new() {
        Background = Brushes.Transparent, ClipToBounds = true, Focusable = true, IsTabStop = true
    };
    private readonly ScrollBar comparisonHorizontal = new() {
        Orientation = Orientation.Horizontal, SmallChange = 1, AllowAutoHide = false, IsTabStop = false
    };
    private readonly ScrollBar comparisonVertical = new() {
        Orientation = Orientation.Vertical, SmallChange = 1, AllowAutoHide = false, IsTabStop = false
    };
    private readonly Grid comparisonTable = new() {
        RowDefinitions = new RowDefinitions("*,16"), ColumnDefinitions = new ColumnDefinitions("*,16"),
        IsVisible = false
    };
    private readonly TextBlock comparisonHint = new() {
        Text = "Чем выше косинусное сходство, тем ближе признаки, извлеченные из фото. " +
            "Знак «—» на диагонали обозначает один и тот же образец.",
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false
    };
    private readonly TextBlock comparisonEmpty = new() {
        Text = "Добавь еще один снимок, чтобы сравнить образцы",
        TextWrapping = TextWrapping.Wrap,
        IsVisible = false
    };
    private bool updatingComparisonAxes;

    // размещаем матрицу в ограниченной области и отдаем полосам прокрутки отдельные края
    private Grid CreateComparisonArea() {
        AutomationProperties.SetName(comparisonViewport, "Таблица косинусного сходства образцов");
        AutomationProperties.SetHelpText(comparisonViewport, "Стрелки прокручивают строки и столбцы");
        AutomationProperties.SetName(comparisonHorizontal, "Прокрутка столбцов");
        AutomationProperties.SetName(comparisonVertical, "Прокрутка строк");
        comparisonViewport.Child = comparisonCells;
        comparisonViewport.SizeChanged += (_, _) => UpdateComparisonAxes();
        comparisonViewport.KeyDown += ComparisonKeyDown;
        comparisonViewport.PointerPressed += (_, _) => comparisonViewport.Focus(NavigationMethod.Pointer);
        comparisonViewport.PointerWheelChanged += ComparisonWheel;
        comparisonHorizontal.ValueChanged += (_, _) => DrawComparison();
        comparisonVertical.ValueChanged += (_, _) => DrawComparison();

        comparisonTable.Children.Add(comparisonViewport);
        Grid.SetRow(comparisonHorizontal, 1);
        comparisonTable.Children.Add(comparisonHorizontal);
        Grid.SetColumn(comparisonVertical, 1);
        comparisonTable.Children.Add(comparisonVertical);
        Grid area = new() { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 8,
            Children = { comparisonHint } };
        Grid.SetRow(comparisonEmpty, 1);
        area.Children.Add(comparisonEmpty);
        Grid.SetRow(comparisonTable, 1);
        area.Children.Add(comparisonTable);
        return area;
    }

    // при смене профиля или вкладки начинаем таблицу с первой пары
    private void ShowComparison() {
        int count = savedSampleControls?.SelectedIndex == 1 ? sampleProfile?.Samples.Count ?? 0 : 0;
        comparisonEmpty.IsVisible = count == 1;
        comparisonTable.IsVisible = comparisonHint.IsVisible = count > 1;
        UpdateComparisonAxes(reset: true);
    }

    // по размеру области определяем число ячеек и пределы обеих полос
    private void UpdateComparisonAxes(bool reset = false) {
        if (!comparisonTable.IsVisible || sampleProfile is not { } profile) {
            comparisonCells.Children.Clear();
            return;
        }

        int count = profile.Samples.Count;
        Size size = comparisonViewport.Bounds.Size;
        int columns = Math.Clamp((int)(size.Width / ComparisonCellWidth) - 1, 1, count);
        int rows = Math.Clamp((int)(size.Height / ComparisonCellHeight) - 1, 1, count);
        updatingComparisonAxes = true;
        SetComparisonAxis(comparisonHorizontal, columns, count, reset);
        SetComparisonAxis(comparisonVertical, rows, count, reset);
        updatingComparisonAxes = false;
        DrawComparison();
    }

    // единица каждой полосы соответствует одному заголовку образца
    private static void SetComparisonAxis(ScrollBar axis, int visible, int count, bool reset) {
        axis.Maximum = count - visible;
        axis.ViewportSize = visible;
        axis.LargeChange = visible;
        axis.IsVisible = visible < count;
        if (reset) {
            axis.Value = 0;
        }
    }

    // пересчитываем только пересечения видимых строк и столбцов
    private void DrawComparison() {
        if (updatingComparisonAxes || !comparisonTable.IsVisible || sampleProfile is not { } profile) {
            return;
        }

        int firstRow = (int)Math.Round(comparisonVertical.Value, MidpointRounding.AwayFromZero);
        int firstColumn = (int)Math.Round(comparisonHorizontal.Value, MidpointRounding.AwayFromZero);
        int rows = (int)comparisonVertical.ViewportSize;
        int columns = (int)comparisonHorizontal.ViewportSize;
        comparisonCells.Children.Clear();
        comparisonCells.RowDefinitions.Clear();
        comparisonCells.ColumnDefinitions.Clear();
        for (int column = 0; column <= columns; column++) {
            comparisonCells.ColumnDefinitions.Add(new ColumnDefinition(ComparisonCellWidth, GridUnitType.Pixel));
        }
        for (int row = 0; row <= rows; row++) {
            comparisonCells.RowDefinitions.Add(new RowDefinition(ComparisonCellHeight, GridUnitType.Pixel));
            for (int column = 0; column <= columns; column++) {
                int sourceRow = firstRow + row - 1;
                int sourceColumn = firstColumn + column - 1;
                Border cell = CreateComparisonCell(profile.Samples, row, column, sourceRow, sourceColumn);
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                comparisonCells.Children.Add(cell);
            }
        }
    }

    // подписываем координаты пары, а на пересечении одинаковых номеров ставим прочерк
    private static Border CreateComparisonCell(IReadOnlyList<FaceProfileSample> samples,
        int row, int column, int sourceRow, int sourceColumn) {
        bool header = row == 0 || column == 0;
        string text;
        string description;
        if (row == 0 && column == 0) {
            text = "№";
            description = "Номера образцов";
        }
        else if (row == 0) {
            FaceProfileSample sample = samples[sourceColumn];
            text = sample.Number.ToString(CultureInfo.InvariantCulture);
            description = $"Столбец, образец {SampleReference(sample)}";
        }
        else if (column == 0) {
            FaceProfileSample sample = samples[sourceRow];
            text = sample.Number.ToString(CultureInfo.InvariantCulture);
            description = $"Строка, образец {SampleReference(sample)}";
        }
        else if (sourceRow == sourceColumn) {
            text = "—";
            description = $"Один и тот же образец {SampleReference(samples[sourceRow])}";
        }
        else {
            text = SampleCosine(samples[sourceRow].Values, samples[sourceColumn].Values).ToString("F2", ComparisonCulture);
            if (text == ComparisonCulture.NumberFormat.NegativeSign + "0,00") {
                text = "0,00";
            }
            description = $"Строка {SampleReference(samples[sourceRow])}, " +
                $"столбец {SampleReference(samples[sourceColumn])}, косинусное сходство {text}";
        }
        ComparisonCellLabel label = new() { Text = text, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, FontWeight = header ? FontWeight.SemiBold : FontWeight.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = ComparisonCellWidth - 4 };
        AutomationProperties.SetName(label, description);
        Border cell = new() { BorderBrush = ComparisonLine,
            BorderThickness = new Thickness(column == 0 ? 1 : 0, row == 0 ? 1 : 0, 1, 1),
            Background = header ? ComparisonHeader : null, Child = label };
        if (header && (row != 0 || column != 0)) {
            ToolTip.SetTip(cell, description);
        }
        return cell;
    }

    // поддерживаем обе оси трекпада и горизонтальную прокрутку колесом с Shift
    private void ComparisonWheel(object? sender, PointerWheelEventArgs args) {
        int horizontal = Math.Sign(args.Delta.X);
        int vertical = Math.Sign(args.Delta.Y);
        if (args.KeyModifiers.HasFlag(KeyModifiers.Shift)) {
            horizontal = horizontal == 0 ? vertical : horizontal;
            vertical = 0;
        }
        comparisonHorizontal.Value -= horizontal;
        comparisonVertical.Value -= vertical;
        args.Handled = true;
    }

    // стрелки двигают матрицу по одному образцу, PageUp и PageDown по видимой высоте
    private void ComparisonKeyDown(object? sender, KeyEventArgs args) {
        switch (args.Key) {
            case Key.Left: comparisonHorizontal.Value--; break;
            case Key.Right: comparisonHorizontal.Value++; break;
            case Key.Up: comparisonVertical.Value--; break;
            case Key.Down: comparisonVertical.Value++; break;
            case Key.PageUp: comparisonVertical.Value -= comparisonVertical.LargeChange; break;
            case Key.PageDown: comparisonVertical.Value += comparisonVertical.LargeChange; break;
            default: return;
        }
        args.Handled = true;
    }

    // учитываем реальные длины обоих сохраненных векторов и считаем суммы в double
    private static double SampleCosine(IReadOnlyList<float> first, IReadOnlyList<float> second) {
        double dot = 0;
        double firstSquared = 0;
        double secondSquared = 0;
        for (int i = 0; i < first.Count; i++) {
            double left = first[i];
            double right = second[i];
            dot += left * right;
            firstSquared += left * left;
            secondSquared += right * right;
        }
        return Math.Clamp(dot / (Math.Sqrt(firstSquared) * Math.Sqrt(secondSquared)), -1, 1);
    }

    private sealed class ComparisonCellLabel : TextBlock {
        protected override Type StyleKeyOverride => typeof(TextBlock);

        // сохраняем роль текста и передаем скринридеру номер пары вместе со значением
        protected override AutomationPeer OnCreateAutomationPeer() => new ComparisonCellPeer(this);
    }

    private sealed class ComparisonCellPeer(TextBlock owner) : TextBlockAutomationPeer(owner) {
        // стандартный текстовый элемент читает только выводимое в ячейке число
        protected override string? GetNameCore() => AutomationProperties.GetName(Owner) ?? base.GetNameCore();
    }
}
