using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ImageVisionApp.Models;
using ImageVisionApp.Services;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private bool mediaSettingsOpen;

    // запоминаем выбранные настройки окна только после явного применения
    private async void OpenMediaSettingsButton_OnClick(object? sender, RoutedEventArgs e) {
        if (mediaSettingsOpen || DataContext is not MainViewModel { IsVideoOpen: true } viewModel) {
            return;
        }

        mediaSettingsOpen = true;
        try {
            VideoPreviewSettings? settings = await ShowVideoPreviewSettingsAsync(viewModel);
            if (settings is { } applied && !mediaWindowClosed && ReferenceEquals(DataContext, viewModel)) {
                viewModel.ApplyVideoPreviewSettings(applied);
            }
        }
        catch (Exception exception) {
            if (!mediaWindowClosed && ReferenceEquals(DataContext, viewModel)) {
                viewModel.ShowError($"Не удалось открыть настройки медиаконтейнера: {exception.Message}");
            }
        }
        finally {
            mediaSettingsOpen = false;
        }
    }

    // редактируем копию значений, отмена и закрытие окна не меняют действующие пределы
    private Task<VideoPreviewSettings?> ShowVideoPreviewSettingsAsync(MainViewModel viewModel) {
        Window dialog = new() {
            Title = "Настройки медиаконтейнера",
            Width = 540,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        VideoPreviewSettings settings = viewModel.PreviewSettings;
        int[] budgets = [16, 32, 64, 128, 256];
        ComboBox workers = new() {
            ItemsSource = new[] { "Авто", "1", "2", "3", "4" },
            SelectedIndex = settings.MaximumWorkers,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ComboBox budget = new() {
            ItemsSource = budgets,
            SelectedIndex = Array.IndexOf(budgets, settings.BudgetMiB),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(workers, "Максимум обработчиков");
        AutomationProperties.SetName(budget, "Бюджет буферов предпросмотра, МиБ");

        StackPanel performance = new() { Spacing = 8 };
        performance.Children.Add(BuildPreviewSettingsRow("Максимум обработчиков", workers));
        performance.Children.Add(BuildPreviewSettingsRow("Бюджет буферов, МиБ", budget));

        TextBlock status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 22, Margin = new Thickness(0, 0, 0, 4) };
        StackPanel load = new() { Children = { status } };
        IBrush rowDivider = new SolidColorBrush(Color.FromArgb(64, 128, 128, 128));
        TextBlock intervalP95 = new();
        TextBlock peakReserve = new();
        (string Label, TextBlock Value)[] rows = [
            ("Исходная частота", new TextBlock()),
            ("Расчётный интервал", new TextBlock()),
            ("Всего кадров в исходном ролике", new TextBlock()),
            ("Применённый режим", new TextBlock()),
            ("Действующий предел обработчиков", new TextBlock()),
            ("Занято работ / пик наблюдения", new TextBlock()),
            ("Новых кадров на входе очереди обработки", new TextBlock()),
            ("Выдано в предпросмотр", new TextBlock()),
            ("Пропущено редактором за замер", new TextBlock()),
            ("Всего поступило кадров с текущей цепочкой", new TextBlock()),
            ("Всего пропущено с текущей цепочкой", new TextBlock()),
            ("Между выдачами в предпросмотре", intervalP95),
            ("От входа до предпросмотра", new TextBlock()),
            ("Заменено ожидающих заявок", new TextBlock()),
            ("Отброшено готовых кадров", new TextBlock()),
            ("Ожидали бюджета за замер", new TextBlock()),
            ("Резерв буферов / бюджет", new TextBlock()),
            ("Пиковый резерв буферов", peakReserve)
        ];
        foreach (var (label, value) in rows) {
            if (ReferenceEquals(value, intervalP95)) {
                load.Children.Add(new TextBlock {
                    Text = "За замер считаются вытеснения, подтверждённые к его концу. Общий счётчик включает поздние вытеснения.",
                    Margin = new Thickness(0, 5, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
                load.Children.Add(new TextBlock {
                    Text = "95% замеров уложились в указанные интервалы",
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    FontWeight = FontWeight.SemiBold
                });
            }
            Border row = new() {
                Padding = new Thickness(0, 4),
                BorderBrush = rowDivider,
                BorderThickness = ReferenceEquals(value, peakReserve) ? new Thickness(0) : new Thickness(0, 0, 0, 1),
                Child = BuildPreviewSettingsRow(label, value)
            };
            if (ReferenceEquals(value, peakReserve)) {
                ToolTip.SetTip(row, "Максимум фактического резерва для открытого файла при выбранном бюджете. " +
                    "При снижении бюджета ещё занятые буферы могут временно превышать лимит.");
            }
            load.Children.Add(row);
        }
        DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(333) };
        refresh.Tick += (_, _) => UpdatePreviewLoad(viewModel, status, rows);
        dialog.Opened += (_, _) => {
            UpdatePreviewLoad(viewModel, status, rows);
            refresh.Start();
        };
        dialog.Closed += (_, _) => refresh.Stop();
        Button cancel = new() { Content = "Отмена", IsCancel = true };
        Button apply = new() { Content = "Применить", IsDefault = true };
        cancel.Click += (_, _) => dialog.Close(null);
        apply.Click += (_, _) => {
            if (workers.SelectedIndex >= 0 && budget.SelectedIndex >= 0) {
                dialog.Close(new VideoPreviewSettings(workers.SelectedIndex, budgets[budget.SelectedIndex]));
            }
        };
        dialog.Content = new StackPanel {
            Margin = new Thickness(16),
            Spacing = 12,
            Children = {
                new ScrollViewer {
                    MaxHeight = 535,
                    Content = new StackPanel {
                        Spacing = 12,
                        Children = {
                            BuildPreviewSettingsCard("Производительность", performance),
                            BuildPreviewSettingsCard("Нагрузка предпросмотра", load)
                        }
                    }
                },
                new StackPanel {
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Orientation = Orientation.Horizontal,
                    Children = { cancel, apply }
                }
            }
        };
        return dialog.ShowDialog<VideoPreviewSettings?>(this);
    }

    // используем оформление карточек редактора и одинаковые отступы внутри формы
    private static Border BuildPreviewSettingsCard(string title, StackPanel content) => new() {
        Padding = new Thickness(12),
        BorderBrush = Brushes.Gray,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Child = new StackPanel {
            Spacing = 8,
            Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, content }
        }
    };

    // оставляем постоянную ширину колонки, чтобы обновление чисел не двигало подписи
    private static Grid BuildPreviewSettingsRow(string label, Control value) {
        TextBlock caption = new() {
            Text = label,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 12, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        value.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(value, 1);
        return new Grid { ColumnDefinitions = new ColumnDefinitions("*, 176"), Children = { caption, value } };
    }

    // показываем измеренное окно и текущие ресурсы отдельно от черновика и свойств исходника
    private static void UpdatePreviewLoad(MainViewModel viewModel, TextBlock status,
        (string Label, TextBlock Value)[] rows) {
        var (settings, effective, resources, peakActive, window, state) = viewModel.ReadVideoPreviewState();
        status.Text = state;
        double? sourceRate = viewModel.CurrentVideoInfo?.FramesPerSecond;
        bool knownRate = sourceRate is > 0 && double.IsFinite(sourceRate.Value);
        bool hasChain = viewModel.IsVideoOpen && viewModel.AdvancedSteps.Count != 0;
        string[] values = [
            knownRate ? $"{sourceRate:0.###} кадр/с" : "Не определено",
            knownRate ? $"{1000.0 / sourceRate:0.##} мс" : "Не определено",
            viewModel.CurrentVideoInfo?.FrameCount?.ToString() ?? VideoInfo.UnknownValue,
            settings.MaximumWorkers == 0 ? "Авто" : $"Вручную · {settings.MaximumWorkers}",
            effective.ToString(),
            $"{resources.Active} / {peakActive?.ToString() ?? "—"}",
            FormatPreviewRate(window?.InputRate),
            FormatPreviewRate(window?.OutputRate),
            FormatPreviewDrops(window),
            hasChain ? viewModel.VideoEditorChainInput.ToString() : "—",
            hasChain ? resources.ChainDropped.ToString() : "—",
            FormatPreviewPercentile(window?.IntervalP95Ms),
            FormatPreviewPercentile(window?.LatencyP95Ms),
            window?.ReplacedPending.ToString() ?? "—",
            window?.Superseded.ToString() ?? "—",
            window?.BudgetWaiting.ToString() ?? "—",
            $"{resources.ReservedBytes / (1024.0 * 1024):0.##} / {settings.BudgetMiB} МиБ",
            viewModel.IsVideoOpen ? $"{resources.BudgetPeakReservedBytes / (1024.0 * 1024):0.##} МиБ" : "—"
        ];
        for (int i = 0; i < rows.Length; i++) {
            var (label, value) = rows[i];
            if (value.Text != values[i]) {
                value.Text = values[i];
                AutomationProperties.SetName(value, $"{label}. {values[i].Replace('\n', ' ')}");
            }
        }
    }

    // считаем долю подтвержденных вытеснений среди входов того же замера
    private static string FormatPreviewDrops(VideoPreviewMeasurements.Window? window) {
        if (window is not { Input: > 0 } sample) {
            return "—";
        }

        return $"{1000.0 * sample.Dropped / sample.DurationMs:0.##} кадр/с\n" +
            $"{sample.Dropped} из {sample.Input} ({100.0 * sample.Dropped / sample.Input:0.##}%)";
    }

    // по частоте за окно выводим средний интервал и число кадров в секунду
    private static string FormatPreviewRate(double? rate) {
        if (rate is not { } framesPerSecond) {
            return "—";
        }

        string interval = framesPerSecond > 0 ? $"{1000.0 / framesPerSecond:0.##} мс" : "—";
        return $"{interval} | {framesPerSecond:0.##} кадр/с";
    }

    // показываем верхнюю границу для 95 процентов измерений
    private static string FormatPreviewPercentile(int? value) => value is { } ms ? $"≤ {ms} мс" : "—";
}
