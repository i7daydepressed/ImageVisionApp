using System;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ImageVisionApp.Models;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private Grid CreateAnalysisPage() {
        Grid workspace = new Grid {
            ColumnDefinitions = new ColumnDefinitions("280,*"),
            ColumnSpacing = 12,
            Margin = new Thickness(12)
        };
        Border methodsCard = CreateAnalysisMethodsCard();
        workspace.Children.Add(methodsCard);
        Border imageCard = CreateAnalysisImageCard();
        Grid.SetColumn(imageCard, 1);
        workspace.Children.Add(imageCard);
        return CreateAnalysisSections(workspace);
    }

    private Border CreateAnalysisMethodsCard() {
        Grid layout = new Grid {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,*,Auto"),
            RowSpacing = 8
        };
        layout.Children.Add(CreateSectionHeading("Методы анализа"));

        TextBlock parametersTitle = new TextBlock { Text = "Параметры метода" };
        Grid.SetRow(parametersTitle, 2);
        layout.Children.Add(parametersTitle);
        ScrollViewer parametersScroll = new ScrollViewer {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock { Text = "Выберите метод для настройки", TextWrapping = TextWrapping.Wrap }
        };
        Grid.SetRow(parametersScroll, 3);
        layout.Children.Add(parametersScroll);
        // обновляем общую панель выбранного метода
        void ShowParameters(string title, StackPanel panel) {
            parametersTitle.Text = title;
            parametersScroll.Content = panel;
        }

        CheckBox edges = new CheckBox { Content = "Границы" };
        edges.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.IsEdgesAnalysisEnabled)) {
            Mode = BindingMode.TwoWay
        });
        Button settings = new Button { Content = "Параметры", VerticalAlignment = VerticalAlignment.Center };
        settings.Click += (_, _) => ShowParameters("Параметры границ", CreateEdgeParametersPanel());
        Grid edgeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        edgeRow.Children.Add(edges);
        Grid.SetColumn(settings, 1);
        edgeRow.Children.Add(settings);

        StackPanel methods = new StackPanel { Spacing = 8 };
        methods.Children.Add(edgeRow);
        CheckBox keypoints = new CheckBox { Content = "Ключевые точки" };
        keypoints.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.IsKeypointAnalysisEnabled)) {
            Mode = BindingMode.TwoWay
        });
        Button keypointSettings = new Button { Content = "Параметры", VerticalAlignment = VerticalAlignment.Center };
        keypointSettings.Click += (_, _) => ShowParameters("Параметры ключевых точек", CreateFastParametersPanel());
        Grid keypointRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        keypointRow.Children.Add(keypoints);
        Grid.SetColumn(keypointSettings, 1);
        keypointRow.Children.Add(keypointSettings);
        methods.Children.Add(keypointRow);
        CheckBox plate = new CheckBox { Content = "Область номера" };
        plate.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.IsPlateAnalysisSelected)) {
            Mode = BindingMode.TwoWay
        });
        Button plateSettings = new Button { Content = "Параметры", VerticalAlignment = VerticalAlignment.Center };
        plateSettings.Click += (_, _) => ShowParameters("Параметры поиска номера", CreatePlateParametersPanel());
        Grid plateRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        plateRow.Children.Add(plate);
        Grid.SetColumn(plateSettings, 1);
        plateRow.Children.Add(plateSettings);
        methods.Children.Add(plateRow);
        CheckBox segmentation = new CheckBox { Content = "Сегментация" };
        segmentation.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.IsSegmentationAnalysisEnabled)) {
            Mode = BindingMode.TwoWay
        });
        Button segmentationSettings = new Button { Content = "Параметры", VerticalAlignment = VerticalAlignment.Center };
        segmentationSettings.Click += (_, _) => ShowParameters("Параметры сегментации", CreateSegmentationParametersPanel());
        Grid segmentationRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        segmentationRow.Children.Add(segmentation);
        Grid.SetColumn(segmentationSettings, 1);
        segmentationRow.Children.Add(segmentationSettings);
        methods.Children.Add(segmentationRow);
        ScrollViewer methodsScroll = new ScrollViewer {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = methods
        };
        Grid.SetRow(methodsScroll, 1);
        layout.Children.Add(methodsScroll);

        CheckBox automatic = new CheckBox { Content = "Автоанализ" };
        automatic.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoOpen)) { Converter = BoolConverters.Not });
        automatic.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.IsAutoAnalysisEnabled)) {
            Mode = BindingMode.TwoWay
        });
        Button run = new Button { HorizontalAlignment = HorizontalAlignment.Stretch };
        run.Bind(ContentProperty, new Binding(nameof(MainViewModel.AnalysisRunButtonText)));
        run.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.CanRunAnalysis)));
        run.Click += RunAnalysisButton_OnClick;
        StackPanel actions = new StackPanel { Spacing = 8 };
        actions.Children.Add(automatic);
        actions.Children.Add(run);
        Grid.SetRow(actions, 4);
        layout.Children.Add(actions);
        return CreateAnalysisCard(layout);
    }

    private static StackPanel CreateEdgeParametersPanel() {
        StackPanel panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Метод" });
        ComboBox method = new ComboBox {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "Робертс", "Canny" }
        };
        method.Bind(SelectingItemsControl.SelectedIndexProperty,
            new Binding(nameof(MainViewModel.SelectedEdgeMethodIndex)) { Mode = BindingMode.TwoWay });
        panel.Children.Add(method);
        panel.Children.Add(CreateVideoAnalysisSourcePicker(
            "Источник границ", nameof(MainViewModel.SelectedVideoEdgeSourceIndex)));

        TextBlock robertsHint = new TextBlock {
            Text = "Для Робертса дополнительные параметры не требуются",
            TextWrapping = TextWrapping.Wrap
        };
        robertsHint.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsRobertsEdgeMethod)));
        panel.Children.Add(robertsHint);

        StackPanel canny = new StackPanel { Spacing = 8 };
        canny.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsCannyEdgeMethod)));
        canny.Children.Add(new TextBlock { Text = "Нижний порог" });
        TextBox lower = new TextBox { PlaceholderText = "50", HorizontalAlignment = HorizontalAlignment.Stretch };
        lower.Bind(TextBox.TextProperty, new Binding(nameof(MainViewModel.CannyLowerThresholdText)) {
            Mode = BindingMode.TwoWay
        });
        canny.Children.Add(lower);
        canny.Children.Add(new TextBlock { Text = "Верхний порог" });
        TextBox upper = new TextBox { PlaceholderText = "150", HorizontalAlignment = HorizontalAlignment.Stretch };
        upper.Bind(TextBox.TextProperty, new Binding(nameof(MainViewModel.CannyUpperThresholdText)) {
            Mode = BindingMode.TwoWay
        });
        canny.Children.Add(upper);
        canny.Children.Add(new TextBlock {
            Text = "0 ≤ нижний < верхний ≤ 32767. Дробные числа: точка или запятая. Значения порогов, не проценты",
            TextWrapping = TextWrapping.Wrap
        });
        TextBlock error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        error.Bind(TextBlock.TextProperty, new Binding(nameof(MainViewModel.CannyThresholdError)));
        error.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.HasCannyThresholdError)));
        canny.Children.Add(error);
        panel.Children.Add(canny);
        return panel;
    }

    // меняем только число видимых рамок, оставляя поиск общим для двух типов входа
    private static StackPanel CreatePlateParametersPanel() {
        StackPanel panel = new StackPanel { Spacing = 8 };
        TextBlock source = new() { Text = "Вход на видео: оригинал", TextWrapping = TextWrapping.Wrap };
        source.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoOpen)));
        panel.Children.Add(source);
        panel.Children.Add(new TextBlock {
            Text = "Поиск светлых табличек с темными символами. Найденные области отмечаются зелеными рамками.",
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock { Text = "Показывать области" });
        ComboBox mode = new() {
            ItemsSource = new[] { "Все", "До N лучших" },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(mode, "Показывать области номера");
        mode.Bind(ComboBox.SelectedIndexProperty, new Binding(nameof(MainViewModel.PlateDisplayModeIndex)) { Mode = BindingMode.TwoWay });
        panel.Children.Add(mode);
        NumericUpDown count = new() {
            Minimum = 1,
            Maximum = MainViewModel.MaximumPlateDisplayLimit,
            Increment = 1,
            FormatString = "0",
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(count, "Число лучших областей номера");
        count.Bind(NumericUpDown.ValueProperty, new Binding(nameof(MainViewModel.PlateDisplayLimit)) { Mode = BindingMode.TwoWay });
        count.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.HasPlateDisplayLimit)));
        panel.Children.Add(count);
        TextBlock summary = CreateBoundText(viewModel => viewModel.PlateDisplaySummary);
        summary.TextWrapping = TextWrapping.Wrap;
        summary.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.PlateDisplaySummary)) {
            Converter = StringConverters.IsNotNullOrEmpty
        });
        panel.Children.Add(summary);
        return panel;
    }

    private static StackPanel CreateFastParametersPanel() {
        StackPanel panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Метод: FAST 9/16" });
        TextBlock fastInput = new() { Text = "Вход FAST на видео: оригинал", TextWrapping = TextWrapping.Wrap };
        fastInput.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoOpen)));
        panel.Children.Add(fastInput);
        panel.Children.Add(new TextBlock { Text = "Порог FAST" });
        TextBox threshold = new TextBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        threshold.Bind(TextBox.TextProperty, new Binding(nameof(MainViewModel.FastThresholdText)) {
            Mode = BindingMode.TwoWay
        });
        panel.Children.Add(threshold);
        panel.Children.Add(new TextBlock {
            Text = "Целое число от 1 до 255. Порог задает минимальную разницу яркости для поиска точки",
            TextWrapping = TextWrapping.Wrap
        });
        CheckBox nonmax = new CheckBox { Content = "Подавление немаксимумов" };
        nonmax.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.FastNonmaxSuppression)) {
            Mode = BindingMode.TwoWay
        });
        panel.Children.Add(nonmax);
        TextBlock error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        error.Bind(TextBlock.TextProperty, new Binding(nameof(MainViewModel.FastThresholdError)));
        error.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.HasFastThresholdError)));
        panel.Children.Add(error);
        return panel;
    }

    private static StackPanel CreateSegmentationParametersPanel() {
        StackPanel panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Метод: K-means" });
        panel.Children.Add(CreateVideoAnalysisSourcePicker(
            "Источник сегментации", nameof(MainViewModel.SelectedVideoSegmentationSourceIndex)));
        TextBlock videoHint = new() {
            Text = "На видео K-means выполняется только для подложки «Сегментация». Галочка слева выбирает эту подложку.",
            TextWrapping = TextWrapping.Wrap
        };
        videoHint.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoOpen)));
        panel.Children.Add(videoHint);
        panel.Children.Add(new TextBlock { Text = "Число кластеров N" });
        TextBox clusters = new TextBox { PlaceholderText = "4", HorizontalAlignment = HorizontalAlignment.Stretch };
        clusters.Bind(TextBox.TextProperty, new Binding(nameof(MainViewModel.SegmentationClusterCountText)) {
            Mode = BindingMode.TwoWay
        });
        panel.Children.Add(clusters);
        panel.Children.Add(new TextBlock {
            Text = "Целое число от 2 до 20. Похожие цвета объединяются в группы. Результат выбирается как подложка",
            TextWrapping = TextWrapping.Wrap
        });
        TextBlock error = new TextBlock { Foreground = Brushes.IndianRed, TextWrapping = TextWrapping.Wrap };
        error.Bind(TextBlock.TextProperty, new Binding(nameof(MainViewModel.SegmentationValidationError)));
        error.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.HasSegmentationValidationError)));
        panel.Children.Add(error);
        return panel;
    }

    // показываем отдельно выбранный источник вычисления для каждого видео-метода
    private static StackPanel CreateVideoAnalysisSourcePicker(string label, string selectedProperty) {
        ComboBox source = new() {
            ItemsSource = new[] { "Оригинал", "Результат редактирования" },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(source, label);
        source.Bind(SelectingItemsControl.SelectedIndexProperty, new Binding(selectedProperty) { Mode = BindingMode.TwoWay });
        StackPanel panel = new() { Spacing = 4, Children = { new TextBlock { Text = label }, source } };
        panel.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoOpen)));
        return panel;
    }

    private Border CreateAnalysisImageCard() {
        Grid layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8 };
        VideoPreviewImage inputImage = new() { Stretch = Stretch.Uniform };
        inputImage.Bind(Image.SourceProperty,
            CompiledBinding.Create<MainViewModel, Bitmap?>(viewModel => viewModel.AnalysisInputImage));
        inputImage.Bind(VideoPreviewImage.RevisionProperty,
            CompiledBinding.Create<MainViewModel, int>(viewModel => viewModel.VideoAnalysisInputRevision));
        TextBlock pendingInput = new() {
            Text = "Готовится редакторский кадр",
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        pendingInput.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoAnalysisEditedInputPending)));
        Border inputPreview = CreateAnalysisPreview(new Grid { Children = { inputImage, pendingInput } });

        VideoPreviewImage resultImage = new() { Stretch = Stretch.Uniform };
        resultImage.Bind(Image.SourceProperty,
            CompiledBinding.Create<MainViewModel, Bitmap?>(viewModel => viewModel.AnalysisResultImage));
        resultImage.Bind(VideoPreviewImage.RevisionProperty,
            CompiledBinding.Create<MainViewModel, int>(viewModel => viewModel.VideoAnalysisInputRevision));
        TextBlock empty = new TextBlock {
            Text = "Результатов анализа нет",
            Foreground = Brushes.LightGray,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        empty.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsAnalysisResultEmpty)));
        Grid resultContent = new Grid();
        resultContent.Children.Add(resultImage);
        resultContent.Children.Add(empty);
        Border resultPreview = CreateAnalysisPreview(resultContent);
        resultPreview.IsVisible = false;

        Grid previews = new Grid();
        previews.Children.Add(inputPreview);
        previews.Children.Add(resultPreview);
        Grid.SetRow(previews, 1);
        layout.Children.Add(previews);
        TabStrip tabs = new TabStrip {
            ItemsSource = new[] { "Вход", "Результат" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Left
        };
        Button videoDisplay = CreateVideoAnalysisDisplayButton();
        videoDisplay.IsVisible = false;
        tabs.SelectionChanged += (_, e) => {
            if (!ReferenceEquals(e.Source, tabs)) {
                return;
            }
            bool result = tabs.SelectedIndex == 1;
            inputPreview.IsVisible = !result;
            resultPreview.IsVisible = result;
            videoDisplay.IsVisible = result;
            (DataContext as MainViewModel)?.SetVideoAnalysisResultTabSelected(result);
        };
        tabs.DataContextChanged += (_, _) =>
            (tabs.DataContext as MainViewModel)?.SetVideoAnalysisResultTabSelected(tabs.SelectedIndex == 1);
        Grid header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        header.Children.Add(tabs);
        TextBlock pipeline = CreateBoundText(viewModel => viewModel.VideoAnalysisPipelineDescription);
        pipeline.FontSize = 12;
        pipeline.LineHeight = 14;
        pipeline.MaxHeight = 42;
        pipeline.VerticalAlignment = VerticalAlignment.Center;
        pipeline.TextTrimming = TextTrimming.CharacterEllipsis;
        pipeline.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.VideoAnalysisPipelineDescription)) {
            Converter = StringConverters.IsNotNullOrEmpty
        });
        pipeline.Bind(ToolTip.TipProperty, new Binding(nameof(MainViewModel.VideoAnalysisPipelineDescription)));
        Grid.SetColumn(pipeline, 1);
        header.Children.Add(pipeline);
        Button displayButton = CreateAnalysisDisplayButton();
        displayButton.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoOpen)) { Converter = BoolConverters.Not });
        Border videoDisplayHost = new() { Child = videoDisplay };
        videoDisplayHost.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoOpen)));
        StackPanel actions = new() {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { openComparisonButton, displayButton, videoDisplayHost }
        };
        Grid.SetColumn(actions, 2);
        header.Children.Add(actions);
        layout.Children.Add(header);

        StackPanel descriptions = new StackPanel { Spacing = 4 };
        TextBlock inputDescription = CreateBoundText(viewModel => viewModel.AnalysisInputDescription);
        inputDescription.TextTrimming = TextTrimming.CharacterEllipsis;
        descriptions.Children.Add(inputDescription);
        TextBlock status = CreateBoundText(viewModel => viewModel.AnalysisStatusMessage);
        status.TextWrapping = TextWrapping.Wrap;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.MaxHeight = 56;
        descriptions.Children.Add(status);
        Grid footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        footer.Children.Add(descriptions);
        Button save = new Button { Content = "Сохранить результат", VerticalAlignment = VerticalAlignment.Center };
        save.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoOpen)) { Converter = BoolConverters.Not });
        save.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.CanSaveAnalysisResult)));
        save.Click += SaveAnalysisResultButton_OnClick;
        Grid.SetColumn(save, 1);
        footer.Children.Add(save);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        return CreateAnalysisCard(layout);
    }

    // меняем показ вкладки и подложку результата независимо от вычислительных источников методов
    private Button CreateVideoAnalysisDisplayButton() {
        StackPanel options = new() { Spacing = 6, Width = 264 };
        options.Children.Add(new TextBlock { Text = "Что показывать во «Входе»", FontWeight = FontWeight.SemiBold });
        options.Children.Add(CreateVideoAnalysisDisplayChoice("Оригинал", "VideoAnalysisInput", 0,
            nameof(MainViewModel.SelectedVideoAnalysisSourceIndex), (viewModel, value) => viewModel.SelectedVideoAnalysisSourceIndex = value));
        options.Children.Add(CreateVideoAnalysisDisplayChoice("Результат редактирования", "VideoAnalysisInput", 1,
            nameof(MainViewModel.SelectedVideoAnalysisSourceIndex), (viewModel, value) => viewModel.SelectedVideoAnalysisSourceIndex = value));
        options.Children.Add(new Separator());
        options.Children.Add(new TextBlock { Text = "Подложка результата", FontWeight = FontWeight.SemiBold });
        string[] backgrounds = ["Оригинал", "Результат редактирования", "Сегментация", "Без изображения"];
        for (int i = 0; i < backgrounds.Length; i++) {
            options.Children.Add(CreateVideoAnalysisDisplayChoice(backgrounds[i], "VideoAnalysisBackground", i,
                nameof(MainViewModel.SelectedVideoAnalysisBackgroundIndex),
                (viewModel, value) => viewModel.SelectedVideoAnalysisBackgroundIndex = value));
        }
        return CreateAnalysisDisplayFlyout(options);
    }

    // синхронизируем отметку одного варианта, выбор передаем только при щелчке пользователя
    private RadioButton CreateVideoAnalysisDisplayChoice(string title, string group, int index, string property,
        Action<MainViewModel, int> select) {
        RadioButton option = new() { Content = title, GroupName = group };
        option.Bind(ToggleButton.IsCheckedProperty, new Binding($"DataContext.{property}") {
            Source = this, Mode = BindingMode.OneWay,
            Converter = new FuncValueConverter<int, bool>(value => value == index)
        });
        option.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                select(viewModel, index);
            }
        };
        return option;
    }

    private Button CreateAnalysisDisplayButton() {
        StackPanel options = new StackPanel { Spacing = 8, Width = 260 };
        options.Children.Add(new TextBlock { Text = "Изображение для анализа", FontWeight = FontWeight.SemiBold });
        options.Children.Add(CreateAnalysisSourceOption(
            "Оригинал", AnalysisImageSource.Original, nameof(MainViewModel.IsOriginalAnalysisSource)));
        options.Children.Add(CreateAnalysisSourceOption(
            "Результат редактирования", AnalysisImageSource.Edited, nameof(MainViewModel.IsEditedAnalysisSource)));
        options.Children.Add(new Separator());
        options.Children.Add(new TextBlock { Text = "Подложка результата", FontWeight = FontWeight.SemiBold });
        options.Children.Add(CreateAnalysisBackgroundOption(
            "Входное изображение", AnalysisResultBackground.Input, nameof(MainViewModel.UseAnalysisInputBackground)));
        RadioButton segmentedBackground = CreateAnalysisBackgroundOption(
            "Сегментация", AnalysisResultBackground.Segmentation, nameof(MainViewModel.IsAnalysisSegmentationBackground));
        BindAnalysisPopup(segmentedBackground, IsEnabledProperty, nameof(MainViewModel.IsSegmentationAnalysisEnabled));
        options.Children.Add(segmentedBackground);
        options.Children.Add(CreateAnalysisBackgroundOption(
            "Без изображения", AnalysisResultBackground.Black, nameof(MainViewModel.IsAnalysisBlackBackground)));
        options.Children.Add(new Separator());
        options.Children.Add(new TextBlock { Text = "Отметки поверх подложки", FontWeight = FontWeight.SemiBold });
        CheckBox edges = new CheckBox { Content = "Границы" };
        BindAnalysisPopup(edges, ToggleButton.IsCheckedProperty, nameof(MainViewModel.ShowAnalysisEdges), BindingMode.TwoWay);
        BindAnalysisPopup(edges, IsEnabledProperty, nameof(MainViewModel.IsEdgesAnalysisEnabled));
        options.Children.Add(edges);
        CheckBox keypoints = new CheckBox { Content = "Ключевые точки" };
        BindAnalysisPopup(keypoints, ToggleButton.IsCheckedProperty, nameof(MainViewModel.ShowAnalysisKeypoints), BindingMode.TwoWay);
        BindAnalysisPopup(keypoints, IsEnabledProperty, nameof(MainViewModel.IsKeypointAnalysisEnabled));
        options.Children.Add(keypoints);
        CheckBox plate = new CheckBox { Content = "Область номера" };
        BindAnalysisPopup(plate, ToggleButton.IsCheckedProperty, nameof(MainViewModel.ShowAnalysisPlate), BindingMode.TwoWay);
        BindAnalysisPopup(plate, IsEnabledProperty, nameof(MainViewModel.IsPlateAnalysisEnabled));
        options.Children.Add(plate);

        return CreateAnalysisDisplayFlyout(options);
    }

    // помещаем параметры обеих ветвей в одинаковую прокручиваемую панель
    private static Button CreateAnalysisDisplayFlyout(Panel options) {
        return new Button {
            Content = "Параметры отображения",
            Flyout = new Flyout {
                Placement = PlacementMode.BottomEdgeAlignedRight,
                Content = new ScrollViewer {
                    Content = options, MaxHeight = 400,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                }
            },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private RadioButton CreateAnalysisSourceOption(string title, AnalysisImageSource source, string checkedProperty) {
        RadioButton option = new RadioButton { Content = title, GroupName = "AnalysisImageSource" };
        BindAnalysisPopup(option, ToggleButton.IsCheckedProperty, checkedProperty);
        option.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                viewModel.SelectedAnalysisSource = source;
            }
        };
        return option;
    }

    private RadioButton CreateAnalysisBackgroundOption(string title, AnalysisResultBackground background, string checkedProperty) {
        RadioButton option = new RadioButton { Content = title, GroupName = "AnalysisBackground" };
        BindAnalysisPopup(option, ToggleButton.IsCheckedProperty, checkedProperty);
        option.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                viewModel.SelectedAnalysisBackground = background;
            }
        };
        return option;
    }

    private void BindAnalysisPopup(
        AvaloniaObject control, AvaloniaProperty property, string viewModelProperty,
        BindingMode mode = BindingMode.OneWay) {

        // popup связывается с окном явно, наследование DataContext ему не требуется
        control.Bind(property, new Binding($"DataContext.{viewModelProperty}") { Source = this, Mode = mode });
    }

    private static Border CreateAnalysisCard(Control content) {
        return new Border {
            Padding = new Thickness(12), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch, Child = content
        };
    }

    private static Border CreateAnalysisPreview(Control content) {
        return new Border {
            Background = new SolidColorBrush(Color.Parse("#181818")),
            Padding = new Thickness(10), Child = content
        };
    }
}
