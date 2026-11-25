using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;

namespace ImageVisionApp.Views;

internal sealed partial class FaceDatabaseView {
    private static readonly CultureInfo ComparisonCulture = CultureInfo.GetCultureInfo("ru-RU");
    private readonly ComboBox comparisonAnchor = new() { MinWidth = 130 };
    private readonly ListBox comparisonRows = new() { Background = Brushes.Transparent };
    private readonly TextBlock comparisonHint = new() {
        Text = "Чем выше косинусное сходство, тем ближе признаки, извлеченные из фото",
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center
    };
    private readonly TextBlock comparisonEmpty = new() {
        Text = "Добавь еще один снимок, чтобы сравнить образцы",
        TextWrapping = TextWrapping.Wrap
    };

    // оставляем выбор образца сверху, прокручиваем только результаты внутри карточки
    private Grid CreateComparisonArea() {
        AutomationProperties.SetName(comparisonAnchor, "Сравнить с");
        AutomationProperties.SetName(comparisonRows, "Результаты сравнения образцов");
        ScrollViewer.SetHorizontalScrollBarVisibility(comparisonRows, ScrollBarVisibility.Disabled);
        // при смене результатов контейнер временно получает пустые данные
        comparisonRows.ItemTemplate = new FuncDataTemplate<ComparisonRow>(
            (row, _) => row is null ? null : CreateComparisonRow(row), supportsRecycling: false);
        comparisonRows.ContainerPrepared += (_, args) => {
            if (args.Container is ListBoxItem { Content: ComparisonRow row } item) {
                item.Padding = new Thickness(8, 6);
                item.HorizontalContentAlignment = HorizontalAlignment.Stretch;
                AutomationProperties.SetName(item, row.AccessibleName);
            }
        };
        Grid anchorRow = new() { ColumnDefinitions = new ColumnDefinitions("Auto,Auto,*"), ColumnSpacing = 10, Children = {
            new TextBlock { Text = "Сравнить с", VerticalAlignment = VerticalAlignment.Center }
        }};
        Grid.SetColumn(comparisonAnchor, 1);
        anchorRow.Children.Add(comparisonAnchor);
        Grid.SetColumn(comparisonHint, 2);
        anchorRow.Children.Add(comparisonHint);
        Grid area = new() { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 10,
            Children = { anchorRow } };
        Grid.SetRow(comparisonEmpty, 1);
        area.Children.Add(comparisonEmpty);
        Grid.SetRow(comparisonRows, 1);
        area.Children.Add(comparisonRows);
        return area;
    }

    // ранжируем образцы текущего профиля по полному косинусу, сохраняя порядок добавления при равенстве
    private void ShowComparison() {
        int anchor = comparisonAnchor.SelectedIndex;
        if (sampleProfile is not { } profile || (uint)anchor >= (uint)profile.Samples.Count ||
            savedSampleControls?.SelectedIndex != 1) {
            comparisonRows.ItemsSource = null;
            comparisonRows.IsVisible = false;
            comparisonEmpty.IsVisible = false;
            comparisonHint.IsVisible = false;
            return;
        }

        comparisonEmpty.IsVisible = profile.Samples.Count == 1;
        comparisonRows.IsVisible = comparisonHint.IsVisible = comparisonAnchor.IsEnabled = profile.Samples.Count > 1;
        IReadOnlyList<float> reference = profile.Samples[anchor];
        ComparisonRow[] ranked = Enumerable.Range(0, profile.Samples.Count)
            .Where(index => index != anchor)
            .Select(index => (Index: index, Score: SampleCosine(reference, profile.Samples[index])))
            .OrderByDescending(pair => pair.Score)
            .ThenBy(pair => pair.Index)
            .Select((pair, order) => new ComparisonRow(pair.Index + 1, order == 0 ? anchor + 1 : null, pair.Score))
            .ToArray();
        comparisonRows.ItemsSource = ranked;
        if (ranked.Length > 0) {
            comparisonRows.ScrollIntoView(0);
        }
    }

    // выводим номер и округленное значение в одной строке, дополнение есть только у первой пары
    private static Grid CreateComparisonRow(ComparisonRow row) {
        StackPanel sample = new() { Spacing = 2, Children = {
            new TextBlock { Text = row.Label, FontWeight = FontWeight.SemiBold }
        }};
        if (row.NearestHint is { } nearest) {
            sample.Children.Add(new TextBlock { Text = nearest });
        }
        Grid content = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 10,
            Children = { sample } };
        TextBlock score = new() { Text = row.ScoreText, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(score, 1);
        content.Children.Add(score);
        return content;
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
        return dot / (Math.Sqrt(firstSquared) * Math.Sqrt(secondSquared));
    }

    private sealed record ComparisonRow(int Number, int? NearestTo, double Score) {
        public string Label => $"Образец №{Number}";
        public string? NearestHint => NearestTo is int anchor ? $"Ближе всего к №{anchor}" : null;
        public string ScoreText => $"Косинусное сходство {Score.ToString("F2", ComparisonCulture)}";
        public string AccessibleName => NearestHint is { } hint
            ? $"{Label}, {hint}, {ScoreText}"
            : $"{Label}, {ScoreText}";
    }
}
