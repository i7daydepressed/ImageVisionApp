using System;
using System.Linq;
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
        int[] budgets = [16, 32, 64, 128, 256, 512, VideoPreviewSettings.BudgetLimitMiB];
        ComboBox workers = new() {
            ItemsSource = Enumerable.Range(0, VideoPreviewSettings.WorkerLimit + 1).Select(limit => limit == 0 ? "Авто" : limit.ToString()).ToArray(),
            SelectedIndex = settings.MaximumWorkers,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ComboBox budget = new() {
            ItemsSource = budgets,
            SelectedIndex = Array.IndexOf(budgets, settings.BudgetMiB),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ComboBox analysisWorkers = new() {
            ItemsSource = Enumerable.Range(1, VideoPreviewSettings.WorkerLimit).ToArray(),
            SelectedIndex = settings.AnalysisMaximumWorkers - 1,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ComboBox analysisBudget = new() {
            ItemsSource = budgets,
            SelectedIndex = Array.IndexOf(budgets, settings.AnalysisBudgetMiB),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ComboBox mode = new() {
            ItemsSource = new[] { "Живой предпросмотр", "С подготовкой участка", "Строгий показ" },
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
        AutomationProperties.SetName(workers, "Редактирование. Максимум обработчиков");
        AutomationProperties.SetName(budget, "Редактирование. Бюджет буферов, МиБ");
        AutomationProperties.SetName(analysisWorkers, "Анализ. Максимум обработчиков");
        AutomationProperties.SetName(analysisBudget, "Анализ. Бюджет буферов, МиБ");
        const string budgetHelp = "Предел для учитываемых кадровых буферов в оперативной памяти.";
        ToolTip.SetTip(budget, budgetHelp);
        ToolTip.SetTip(analysisBudget, budgetHelp);

        TextBlock modeHelp = new() { TextWrapping = TextWrapping.Wrap };
        TextBlock actualMode = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };
        CheckBox strictBuffer = new() {
            Content = "Копить запас перед продолжением",
            IsChecked = settings.BufferBeforeStrictResume
        };
        ToolTip.SetTip(strictBuffer, "При накоплении плеер ждёт непрерывный запас. " +
            "При снятой галочке показ продолжается с готовностью следующего кадра.");
        StackPanel modeCard = new() { Spacing = 8, Children = { mode, modeHelp, strictBuffer, actualMode } };
        mode.SelectionChanged += (_, _) => DescribeDraftPreviewMode(mode, modeHelp, workers, strictBuffer);
        DescribeDraftPreviewMode(mode, modeHelp, workers, strictBuffer);

        StackPanel playback = new() { Children = { BuildPreviewSettingsRow("Шаг стрелок", arrowStep) } };
        StackPanel performance = new() { Spacing = 8 };
        performance.Children.Add(BuildPreviewSettingsRow("Максимум обработчиков", workers));
        performance.Children.Add(BuildPreviewSettingsRow("Бюджет буферов, МиБ", budget));
        performance.Children.Add(new TextBlock {
            Text = $"«Авто» пробует до {VideoPreviewSettings.AutoWorkerLimit} обработчиков. Вручную доступно до {VideoPreviewSettings.WorkerLimit}.",
            TextWrapping = TextWrapping.Wrap
        });
        StackPanel analysisPerformance = new() { Spacing = 8, Children = {
            BuildPreviewSettingsRow("Максимум обработчиков", analysisWorkers),
            BuildPreviewSettingsRow("Бюджет буферов, МиБ", analysisBudget),
            new TextBlock {
                Text = "На стоп-кадре могут одновременно работать до трёх выбранных методов. " +
                    "Предварительное редактирование использует бюджет редактора. " +
                    "Лимиты относятся к рабочим буферам, которые учитывает приложение.",
                TextWrapping = TextWrapping.Wrap
            }
        } };

        TextBlock status = new() { TextWrapping = TextWrapping.Wrap, MinHeight = 22, Margin = new Thickness(0, 0, 0, 4) };
        StackPanel load = new() { Children = { status } };
        IBrush rowDivider = new SolidColorBrush(Color.FromArgb(64, 128, 128, 128));
        TextBlock intervalP95 = new();
        TextBlock peakReserve = new();
        TextBlock preparedReserve = new() { TextWrapping = TextWrapping.Wrap };
        TextBlock strictOutputTotal = new();
        TextBlock strictWait = new();
        TextBlock replacedPending = new();
        TextBlock superseded = new();
        TextBlock waitingForBudget = new();
        TextBlock[] strictOnlyRows = [strictOutputTotal, strictWait];
        TextBlock[] liveOnlyRows = [replacedPending, superseded, waitingForBudget];
        TextBlock measurementNote = new() {
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap
        };
        (string Label, TextBlock Value)[] rows = [
            ("Исходная частота", new TextBlock()),
            ("Расчётный интервал", new TextBlock()),
            ("Всего кадров в исходном ролике", new TextBlock()),
            ("Настройка числа обработчиков", new TextBlock()),
            ("Предел по кадру и бюджету", new TextBlock()),
            ("Готовый запас", preparedReserve),
            ("Занято работ / пик наблюдения", new TextBlock()),
            ("Новых кадров на входе очереди обработки", new TextBlock()),
            ("Выдано в предпросмотр", new TextBlock()),
            ("Пропущено редактором за замер", new TextBlock()),
            ("Всего поступило кадров с текущей цепочкой", new TextBlock()),
            ("Всего выдано за строгий проход", strictOutputTotal),
            ("Всего пропущено с текущей цепочкой", new TextBlock()),
            ("Автоматическое ожидание за проход", strictWait),
            ("Между выдачами в предпросмотре", intervalP95),
            ("От входа до предпросмотра", new TextBlock()),
            ("Заменено ожидающих заявок", replacedPending),
            ("Отброшено готовых кадров", superseded),
            ("Ожидали бюджета за замер", waitingForBudget),
            ("Резерв буферов / бюджет", new TextBlock()),
            ("Редактирование стоп-кадра для анализа", new TextBlock()),
            ("Пиковый резерв буферов потока", peakReserve)
        ];
        foreach (var (label, value) in rows) {
            if (ReferenceEquals(value, intervalP95)) {
                load.Children.Add(measurementNote);
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
            if (ReferenceEquals(value, preparedReserve) || strictOnlyRows.Contains(value) || liveOnlyRows.Contains(value)) {
                row.Bind(IsVisibleProperty, value.GetObservable(IsVisibleProperty));
            }
            load.Children.Add(row);
        }
        ComboBox metricScope = new() {
            ItemsSource = new[] { "Весь выход", "Редактирование", "Анализ" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(metricScope, "Область замера");
        TextBlock visibleStatus = new() { TextWrapping = TextWrapping.Wrap };
        TextBlock nominalRate = new();
        TextBlock visibleRate = new();
        TextBlock visibleInterval = new();
        TextBlock visibleReserve = new() { TextWrapping = TextWrapping.Wrap };
        StackPanel visibleLoad = new() { Spacing = 8, Children = {
            visibleStatus,
            BuildPreviewSettingsRow("Частота исходной дорожки", nominalRate),
            BuildPreviewSettingsRow("Выдано в видимый предпросмотр", visibleRate),
            BuildPreviewSettingsRow("Между выдачами (95% замеров)", visibleInterval),
            BuildPreviewSettingsRow("Резерв буферов пути показа", visibleReserve),
            new TextBlock { Text = "Время ожидания кадров при воспроизведении входит в замер.",
                TextWrapping = TextWrapping.Wrap }
        } };
        TextBlock analysisStatus = new() { TextWrapping = TextWrapping.Wrap };
        (string Label, TextBlock Value)[] analysisRows = [
            ("От «Применить» до готового результата", new TextBlock()),
            ("Выполнение методов", new TextBlock()),
            ("Сборка и выдача изображения", new TextBlock()),
            ("Допуск / пик параллельных методов за замер", new TextBlock()),
            ("Допуск методов сейчас", new TextBlock()),
            ("Рабочие буферы сейчас / бюджет", new TextBlock()),
            ("Резерв на выполнение методов за замер", new TextBlock())
        ];
        StackPanel analysisLoad = new() { Spacing = 8, Children = { analysisStatus } };
        foreach (var (label, value) in analysisRows) {
            analysisLoad.Children.Add(BuildPreviewSettingsRow(label, value));
        }
        StackPanel measurements = new() { Spacing = 8, Children = { metricScope, visibleLoad, load, analysisLoad } };

        // выбор среза показывает фактический выход или отдельный этап, не меняя настройки работы
        void RefreshMeasurements() {
            UpdatePreviewLoad(viewModel, status, actualMode, preparedReserve, rows);
            bool isStrict = viewModel.ActiveVideoPreviewMode == VideoPreviewMode.Strict;
            foreach (TextBlock value in strictOnlyRows) {
                value.IsVisible = isStrict;
            }
            foreach (TextBlock value in liveOnlyRows) {
                value.IsVisible = !isStrict;
            }
            measurementNote.Text = isStrict
                ? "Итоги относятся к текущему строгому проходу. Частота и интервалы считаются по окну замера. Задержка до показа включает ожидание готового кадра."
                : "За замер считаются вытеснения, подтверждённые к его концу. Общий счётчик включает поздние вытеснения.";
            bool displayed = metricScope.SelectedIndex == 0;
            bool displayAnalysis = displayed && viewModel.IsVideoAnalysisOutputSelected;
            load.IsVisible = metricScope.SelectedIndex == 1;
            analysisLoad.IsVisible = metricScope.SelectedIndex == 2 || displayAnalysis;
            visibleLoad.IsVisible = displayed && !displayAnalysis;
            if (visibleLoad.IsVisible) {
                var (window, picture) = viewModel.ReadVisibleVideoOutput();
                visibleStatus.Text = picture;
                double? sourceRate = viewModel.CurrentVideoInfo?.FramesPerSecond;
                SetMetric(nominalRate, "Частота исходной дорожки", sourceRate is > 0 && double.IsFinite(sourceRate.Value)
                    ? FormatPreviewRate(sourceRate) : VideoInfo.UnknownValue);
                SetMetric(visibleRate, "Выдано в видимый предпросмотр", FormatPreviewRate(window?.OutputRate));
                SetMetric(visibleInterval, "Между выдачами (95% замеров)", FormatPreviewPercentile(window?.IntervalP95Ms));
                var (bytes, path) = viewModel.ReadVisibleVideoOutputBuffers();
                SetMetric(visibleReserve, "Резерв буферов пути показа", $"{bytes / (1024.0 * 1024):0.##} МиБ\n{path}");
            }
            if (analysisLoad.IsVisible) {
                UpdateVideoAnalysisLoad(viewModel, displayed, analysisStatus, analysisRows);
            }
        }

        DispatcherTimer refresh = new() { Interval = TimeSpan.FromMilliseconds(333) };
        metricScope.SelectionChanged += (_, _) => RefreshMeasurements();
        refresh.Tick += (_, _) => RefreshMeasurements();
        dialog.Opened += (_, _) => {
            RefreshMeasurements();
            refresh.Start();
        };
        dialog.Closed += (_, _) => refresh.Stop();
        Button cancel = new() { Content = "Отмена", IsCancel = true };
        Button apply = new() { Content = "Применить", IsDefault = true };
        cancel.Click += (_, _) => dialog.Close(null);
        apply.Click += (_, _) => {
            if (mode.SelectedIndex >= 0 && workers.SelectedIndex >= 0 && budget.SelectedIndex >= 0 &&
                arrowStep.SelectedIndex >= 0 && analysisWorkers.SelectedIndex >= 0 && analysisBudget.SelectedIndex >= 0) {
                dialog.Close(new VideoPreviewSettings(workers.SelectedIndex, budgets[budget.SelectedIndex]) {
                    Mode = (VideoPreviewMode)mode.SelectedIndex,
                    BufferBeforeStrictResume = strictBuffer.IsChecked == true,
                    ArrowStep = steps[arrowStep.SelectedIndex],
                    AnalysisMaximumWorkers = analysisWorkers.SelectedIndex + 1,
                    AnalysisBudgetMiB = budgets[analysisBudget.SelectedIndex]
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
                            BuildPreviewSettingsCard("Производительность · Редактирование", performance),
                            BuildPreviewSettingsCard("Производительность · Анализ", analysisPerformance),
                            BuildPreviewSettingsCard("Нагрузка предпросмотра", measurements)
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
    private static void DescribeDraftPreviewMode(ComboBox mode, TextBlock description, ComboBox workers, CheckBox strictBuffer) {
        VideoPreviewMode selection = (VideoPreviewMode)mode.SelectedIndex;
        description.Text = selection switch {
            VideoPreviewMode.Prepared => "Кадры заранее обрабатывает один последовательный обработчик. Показ и звук ждут готового кадра.",
            VideoPreviewMode.Strict => "Кадры показываются по порядку. Пока следующий кадр готовится, картинка, звук и время стоят.",
            _ => "Показ идёт вместе со звуком. При нагрузке редактор может пропускать кадры."
        };
        workers.IsEnabled = selection != VideoPreviewMode.Prepared;
        strictBuffer.IsVisible = selection == VideoPreviewMode.Strict;
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
        VideoPreviewMode activeMode = viewModel.ActiveVideoPreviewMode;
        bool isPrepared = activeMode == VideoPreviewMode.Prepared;
        bool isStrict = activeMode == VideoPreviewMode.Strict;
        bool isLive = activeMode == VideoPreviewMode.Live;
        PreparedVideoWindow? prepared = viewModel.PreparedVideo;
        string mode = activeMode switch {
            VideoPreviewMode.Prepared => "С подготовкой участка",
            VideoPreviewMode.Strict => "Строгий показ",
            _ => "Живой предпросмотр"
        };
        string? modeNotice = viewModel.VideoPreviewModeNotice;
        actualMode.Text = modeNotice is null ? $"Сейчас · {mode}" : $"Сейчас · {mode}\n{modeNotice}";
        preparedReserve.IsVisible = !isLive;
        status.Text = activeMode switch {
            VideoPreviewMode.Prepared => "С подготовкой участка · один последовательный обработчик. " +
                "Частота показа выбранного вида доступна в разделе «Весь выход».",
            VideoPreviewMode.Strict => viewModel.PlaybackStatus.Notice ?? "Строгий показ · выдача по порядку",
            _ => state
        };
        var strict = isStrict ? viewModel.ReadStrictVideoPreviewState() : default;
        if (!isLive) {
            window = strict.Window;
        }
        var (analysisInputReserve, analysisInputTime, reused) = viewModel.ReadVideoEditorAnalysisWork();
        if (analysisInputReserve > 0) {
            status.Text = "Готовим изменённый стоп-кадр для анализа.";
        }
        else if (analysisInputTime is not null) {
            status.Text = "Подготовлен изменённый стоп-кадр для анализа.";
        }
        else if (reused) {
            status.Text = "Анализ использовал готовый изменённый стоп-кадр.";
        }
        long reservedBytes = resources.ReservedBytes + (prepared?.ReservedBytes ?? 0) + analysisInputReserve;
        long peakBytes = isLive ? resources.BudgetPeakReservedBytes : prepared?.PeakReservedBytes ?? 0;
        double? sourceRate = viewModel.CurrentVideoInfo?.FramesPerSecond;
        bool knownRate = sourceRate is > 0 && double.IsFinite(sourceRate.Value);
        bool hasChain = viewModel.IsVideoOpen && viewModel.AdvancedSteps.Count != 0;
        string[] values = [
            knownRate ? $"{sourceRate:0.###} кадр/с" : "Не определено",
            knownRate ? $"{1000.0 / sourceRate:0.##} мс" : "Не определено",
            viewModel.CurrentVideoInfo?.FrameCount?.ToString() ?? VideoInfo.UnknownValue,
            isPrepared ? "Последовательно" : settings.MaximumWorkers == 0 ? "Авто" : $"Вручную · {settings.MaximumWorkers}",
            isPrepared ? "1" : isStrict ? (prepared?.WorkerLimit ?? 0).ToString() : effective.ToString(),
            isLive ? "—" : viewModel.PreparedVideoBufferDescription,
            isLive ? $"{resources.Active} / {peakActive?.ToString() ?? "—"}" :
                isStrict ? $"{prepared?.ActiveWorkers ?? 0} / {prepared?.PeakActiveWorkers ?? 0}" : "—",
            FormatPreviewFrameRate(window, isStrict, output: false),
            FormatPreviewFrameRate(window, isStrict, output: true),
            isStrict && window is not null ? "0 кадр." : FormatPreviewDrops(window),
            isStrict ? strict.InputTotal.ToString() : hasChain && isLive ? viewModel.VideoEditorChainInput.ToString() : "—",
            isStrict ? strict.OutputTotal.ToString() : "—",
            isStrict ? "0" : hasChain && isLive ? resources.ChainDropped.ToString() : "—",
            isStrict ? $"{strict.AutomaticWaitMs / 1000.0:0.###} с" : "—",
            FormatPreviewPercentile(window?.IntervalP95Ms),
            FormatPreviewPercentile(window?.LatencyP95Ms),
            window?.ReplacedPending.ToString() ?? "—",
            window?.Superseded.ToString() ?? "—",
            window?.BudgetWaiting.ToString() ?? "—",
            $"{reservedBytes / (1024.0 * 1024):0.##} / {settings.BudgetMiB} МиБ",
            reused ? "Без пересчёта" : FormatAnalysisTime(analysisInputTime),
            viewModel.IsVideoOpen ? $"{peakBytes / (1024.0 * 1024):0.##} МиБ" : "—"
        ];
        for (int i = 0; i < rows.Length; i++) {
            SetMetric(rows[i].Value, rows[i].Label, values[i]);
        }
    }

    // показываем время завершенного стоп-кадра и текущий рабочий резерв участка анализа
    private static void UpdateVideoAnalysisLoad(MainViewModel model, bool displayed, TextBlock status,
        (string Label, TextBlock Value)[] rows) {
        var (workers, bytes, sample, state) = model.ReadVideoAnalysisState(displayed);
        status.Text = $"Анализ · {state}";
        string[] values = [
            FormatAnalysisTime(sample?.Presentation),
            FormatAnalysisTime(sample?.Calculation),
            FormatAnalysisTime(sample?.Composition),
            sample is { } measured ? $"{measured.Workers} / {measured.PeakWorkers}" : "—",
            workers > 0 ? workers.ToString() : "—",
            $"{bytes / (1024.0 * 1024):0.##} / {model.PreviewSettings.AnalysisBudgetMiB} МиБ",
            sample is { } stored ? $"{stored.ReservedBytes / (1024.0 * 1024):0.##} МиБ" : "—"
        ];
        for (int i = 0; i < rows.Length; i++) {
            SetMetric(rows[i].Value, rows[i].Label, values[i]);
        }
    }

    // обновляем значение и его доступное имя только при изменении
    private static void SetMetric(TextBlock value, string label, string text) {
        if (value.Text != text) {
            value.Text = text;
            AutomationProperties.SetName(value, $"{label}. {text.Replace('\n', ' ')}");
        }
    }

    // одно вычисление на паузе измеряем временем, без частоты кадров
    private static string FormatAnalysisTime(TimeSpan? duration) =>
        duration is { } time ? $"{time.TotalMilliseconds:0.##} мс" : "—";

    // считаем долю подтвержденных вытеснений среди входов того же замера
    private static string FormatPreviewDrops(VideoPreviewMeasurements.Window? window) {
        if (window is not { Input: > 0 } sample) {
            return "—";
        }

        return $"{1000.0 * sample.Dropped / sample.DurationMs:0.##} кадр/с\n" +
            $"{sample.Dropped} из {sample.Input} ({100.0 * sample.Dropped / sample.Input:0.##}%)";
    }

    // в строгом режиме показываем счетчик даже за слишком короткое окно
    private static string FormatPreviewFrameRate(VideoPreviewMeasurements.Window? window, bool strict, bool output) {
        if (window is not { } sample) {
            return "—";
        }

        double rate = output ? sample.OutputRate : sample.InputRate;
        if (!strict) {
            return FormatPreviewRate(rate);
        }

        int frames = output ? sample.Output : sample.Input;
        return sample.DurationMs < PreviewCoordinator.MinimumStrictRateWindowMs
            ? $"{frames} кадр."
            : $"{frames} кадр. · {rate:0.##} кадр/с";
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
