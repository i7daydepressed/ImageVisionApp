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

        MediaDocument? document = viewModel.CurrentDocument;
        mediaSettingsOpen = true;
        try {
            VideoPreviewSettings? settings = await ShowVideoPreviewSettingsAsync(viewModel);
            if (settings is { } applied && !mediaWindowClosed && ReferenceEquals(DataContext, viewModel) &&
                ReferenceEquals(viewModel.CurrentDocument, document)) {
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
        ComboBox mode = new() {
            ItemsSource = new[] { "Живой предпросмотр", "С подготовкой участка" },
            SelectedIndex = (int)settings.Mode,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        VideoArrowStep[] steps = [VideoArrowStep.OneFrame, VideoArrowStep.FiveFrames,
            VideoArrowStep.OneSecond, VideoArrowStep.ThreeSeconds];
        ComboBox arrowStep = new() {
            ItemsSource = new[] { "1 кадр", "5 кадров", "1 секунда", "3 секунды" },
            SelectedIndex = Array.IndexOf(steps, settings.ArrowStep),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(arrowStep, "Шаг стрелок");
        AutomationProperties.SetName(mode, "Режим предпросмотра");
        AutomationProperties.SetName(workers, "Максимум обработчиков");
        AutomationProperties.SetName(budget, "Бюджет буферов предпросмотра, МиБ");

        TextBlock modeHelp = new() { TextWrapping = TextWrapping.Wrap };
        TextBlock actualMode = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        StackPanel modeCard = new() { Spacing = 8, Children = { mode, modeHelp, actualMode } };
        mode.SelectionChanged += (_, _) => DescribeDraftPreviewMode(mode, modeHelp, workers);
        DescribeDraftPreviewMode(mode, modeHelp, workers);

        StackPanel playback = new() { Children = { BuildPreviewSettingsRow("Шаг стрелок", arrowStep) } };
        StackPanel performance = new() { Spacing = 8 };
        performance.Children.Add(BuildPreviewSettingsRow("Максимум обработчиков", workers));
        performance.Children.Add(BuildPreviewSettingsRow("Бюджет буферов, МиБ", budget));

        TextBlock status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 22, Margin = new Thickness(0, 0, 0, 4) };
        StackPanel load = new() { Children = { status } };
        IBrush rowDivider = new SolidColorBrush(Color.FromArgb(64, 128, 128, 128));
        TextBlock intervalP95 = new();
        TextBlock peakReserve = new();
        TextBlock preparedReserve = new();
        (string Label, TextBlock Value)[] rows = [
            ("Исходная частота", new TextBlock()),
            ("Расчётный интервал", new TextBlock()),
            ("Всего кадров в исходном ролике", new TextBlock()),
            ("Настройка числа обработчиков", new TextBlock()),
            ("Действующий предел обработчиков", new TextBlock()),
            ("Готовый запас", preparedReserve),
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
                ToolTip.SetTip(row, "При подготовке пик считается с начала текущего запроса. " +
                    "Для живого режима пик считается за время действия бюджета для открытого файла. " +
                    "При снижении бюджета ещё занятые буферы могут временно превышать лимит.");
            }
            if (ReferenceEquals(value, preparedReserve)) {
                row.Bind(IsVisibleProperty, preparedReserve.GetObservable(IsVisibleProperty));
            }
            load.Children.Add(row);
        }
        DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(333) };
        refresh.Tick += (_, _) => UpdatePreviewLoad(viewModel, status, actualMode, preparedReserve, rows);
        dialog.Opened += (_, _) => {
            UpdatePreviewLoad(viewModel, status, actualMode, preparedReserve, rows);
            refresh.Start();
        };
        dialog.Closed += (_, _) => refresh.Stop();
        Button cancel = new() { Content = "Отмена", IsCancel = true };
        Button apply = new() { Content = "Применить", IsDefault = true };
        cancel.Click += (_, _) => dialog.Close(null);
        apply.Click += (_, _) => {
            if (mode.SelectedIndex is 0 or 1 && workers.SelectedIndex >= 0 && budget.SelectedIndex >= 0 &&
                arrowStep.SelectedIndex >= 0) {
                dialog.Close(new VideoPreviewSettings(workers.SelectedIndex, budgets[budget.SelectedIndex]) {
                    Mode = (VideoPreviewMode)mode.SelectedIndex,
                    ArrowStep = steps[arrowStep.SelectedIndex]
                });
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
                            BuildPreviewSettingsCard("Режим предпросмотра", modeCard),
                            BuildPreviewSettingsCard("Управление воспроизведением", playback),
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

    // поясняем выбранный черновик, рабочий режим меняется только по кнопке применения
    private static void DescribeDraftPreviewMode(ComboBox mode, TextBlock description, ComboBox workers) {
        bool isPrepared = mode.SelectedIndex == (int)VideoPreviewMode.Prepared;
        description.Text = isPrepared
            ? "Кадры заранее обрабатывает один последовательный обработчик. Показ и звук ждут готового кадра."
            : "Показ идёт вместе со звуком. При нагрузке редактор может пропускать кадры.";
        workers.IsEnabled = !isPrepared;
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
    private static void UpdatePreviewLoad(MainViewModel viewModel, TextBlock status, TextBlock actualMode,
        TextBlock preparedReserve, (string Label, TextBlock Value)[] rows) {
        var (settings, effective, resources, peakActive, window, state) = viewModel.ReadVideoPreviewState();
        bool isPrepared = viewModel.ActiveVideoPreviewMode == VideoPreviewMode.Prepared;
        PreparedVideoWindow? prepared = viewModel.PreparedVideo;
        string mode = isPrepared ? "С подготовкой участка" : "Живой предпросмотр";
        string? modeNotice = viewModel.VideoPreviewModeNotice;
        actualMode.Text = modeNotice is null ? $"Сейчас · {mode}" : $"Сейчас · {mode}\n{modeNotice}";
        preparedReserve.IsVisible = isPrepared;
        status.Text = isPrepared ? "Живые замеры доступны в режиме «Живой предпросмотр»." : state;
        if (isPrepared) {
            window = null;
        }
        long reservedBytes = resources.ReservedBytes + (prepared?.ReservedBytes ?? 0);
        long peakBytes = isPrepared ? prepared?.PeakReservedBytes ?? 0 : resources.BudgetPeakReservedBytes;
        double? sourceRate = viewModel.CurrentVideoInfo?.FramesPerSecond;
        bool knownRate = sourceRate is > 0 && double.IsFinite(sourceRate.Value);
        bool hasChain = viewModel.IsVideoOpen && viewModel.AdvancedSteps.Count != 0;
        string[] values = [
            knownRate ? $"{sourceRate:0.###} кадр/с" : "Не определено",
            knownRate ? $"{1000.0 / sourceRate:0.##} мс" : "Не определено",
            viewModel.CurrentVideoInfo?.FrameCount?.ToString() ?? VideoInfo.UnknownValue,
            isPrepared ? "Последовательно" : settings.MaximumWorkers == 0 ? "Авто" : $"Вручную · {settings.MaximumWorkers}",
            isPrepared ? "1" : effective.ToString(),
            isPrepared ? viewModel.PreparedVideoBufferDescription : "—",
            isPrepared ? "—" : $"{resources.Active} / {peakActive?.ToString() ?? "—"}",
            FormatPreviewRate(window?.InputRate),
            FormatPreviewRate(window?.OutputRate),
            FormatPreviewDrops(window),
            hasChain && !isPrepared ? viewModel.VideoEditorChainInput.ToString() : "—",
            hasChain && !isPrepared ? resources.ChainDropped.ToString() : "—",
            FormatPreviewPercentile(window?.IntervalP95Ms),
            FormatPreviewPercentile(window?.LatencyP95Ms),
            window?.ReplacedPending.ToString() ?? "—",
            window?.Superseded.ToString() ?? "—",
            window?.BudgetWaiting.ToString() ?? "—",
            $"{reservedBytes / (1024.0 * 1024):0.##} / {settings.BudgetMiB} МиБ",
            viewModel.IsVideoOpen ? $"{peakBytes / (1024.0 * 1024):0.##} МиБ" : "—"
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
