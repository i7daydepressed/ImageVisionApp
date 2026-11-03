using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
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
        workspace.Children.Add(CreateAnalysisMethodsCard());
        Border imageCard = CreateAnalysisImageCard();
        Grid.SetColumn(imageCard, 1);
        workspace.Children.Add(imageCard);
        return workspace;
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

        CheckBox edges = new CheckBox { Content = "Границы" };
        edges.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.IsEdgesAnalysisEnabled)) {
            Mode = BindingMode.TwoWay
        });
        Button settings = new Button { Content = "Параметры", VerticalAlignment = VerticalAlignment.Center };
        settings.Click += (_, _) => {
            parametersTitle.Text = "Параметры границ";
            parametersScroll.Content = CreateEdgeParametersPanel();
        };
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
        keypointSettings.Click += (_, _) => {
            parametersTitle.Text = "Параметры ключевых точек";
            parametersScroll.Content = CreateFastParametersPanel();
        };
        Grid keypointRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        keypointRow.Children.Add(keypoints);
        Grid.SetColumn(keypointSettings, 1);
        keypointRow.Children.Add(keypointSettings);
        methods.Children.Add(keypointRow);
        methods.Children.Add(new CheckBox { Content = "Область номера", IsEnabled = false });
        CheckBox segmentation = new CheckBox { Content = "Сегментация" };
        segmentation.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.IsSegmentationAnalysisEnabled)) {
            Mode = BindingMode.TwoWay
        });
        Button segmentationSettings = new Button { Content = "Параметры", VerticalAlignment = VerticalAlignment.Center };
        segmentationSettings.Click += (_, _) => {
            parametersTitle.Text = "Параметры сегментации";
            parametersScroll.Content = CreateSegmentationParametersPanel();
        };
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
        automatic.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.IsAutoAnalysisEnabled)) {
            Mode = BindingMode.TwoWay
        });
        Button run = new Button { Content = "Выполнить выбранное", HorizontalAlignment = HorizontalAlignment.Stretch };
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

    private static StackPanel CreateFastParametersPanel() {
        StackPanel panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Метод: FAST 9/16" });
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

    private Border CreateAnalysisImageCard() {
        Grid layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8 };
        Image inputImage = new Image { Stretch = Stretch.Uniform };
        inputImage.Bind(Image.SourceProperty,
            CompiledBinding.Create<MainViewModel, Bitmap?>(viewModel => viewModel.AnalysisInputImage));
        Border inputPreview = CreateAnalysisPreview(inputImage);

        Image resultImage = new Image { Stretch = Stretch.Uniform };
        resultImage.Bind(Image.SourceProperty,
            CompiledBinding.Create<MainViewModel, Bitmap?>(viewModel => viewModel.AnalysisResultImage));
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
        tabs.SelectionChanged += (_, e) => {
            if (!ReferenceEquals(e.Source, tabs)) {
                return;
            }
            inputPreview.IsVisible = tabs.SelectedIndex != 1;
            resultPreview.IsVisible = tabs.SelectedIndex == 1;
        };
        Grid header = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        header.Children.Add(tabs);
        Button displayButton = CreateAnalysisDisplayButton();
        Grid.SetColumn(displayButton, 1);
        header.Children.Add(displayButton);
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
        save.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.CanSaveAnalysisResult)));
        save.Click += SaveAnalysisResultButton_OnClick;
        Grid.SetColumn(save, 1);
        footer.Children.Add(save);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
        return CreateAnalysisCard(layout);
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
        options.Children.Add(new CheckBox { Content = "Область номера", IsEnabled = false });

        return new Button {
            Content = "Параметры отображения",
            Flyout = new Flyout {
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
