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
            parametersScroll.Content = CreateRobertsParametersPanel();
        };
        Grid edgeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4 };
        edgeRow.Children.Add(edges);
        Grid.SetColumn(settings, 1);
        edgeRow.Children.Add(settings);

        StackPanel methods = new StackPanel { Spacing = 8 };
        methods.Children.Add(edgeRow);
        foreach (string name in new[] { "Ключевые точки", "Область номера", "Сегментация" }) {
            methods.Children.Add(new CheckBox { Content = name, IsEnabled = false });
        }
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

    private static StackPanel CreateRobertsParametersPanel() {
        StackPanel panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock { Text = "Метод" });
        panel.Children.Add(new ComboBox {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Items = {
                new ComboBoxItem { Content = "Робертс" },
                new ComboBoxItem { Content = "Canny", IsEnabled = false }
            },
            SelectedIndex = 0
        });
        panel.Children.Add(new TextBlock {
            Text = "Для Робертса дополнительные параметры не требуются",
            TextWrapping = TextWrapping.Wrap
        });
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
            "Входное изображение", true, nameof(MainViewModel.UseAnalysisInputBackground)));
        options.Children.Add(new RadioButton { Content = "Сегментация", IsEnabled = false });
        options.Children.Add(CreateAnalysisBackgroundOption(
            "Без изображения", false, nameof(MainViewModel.IsAnalysisBlackBackground)));
        options.Children.Add(new Separator());
        options.Children.Add(new TextBlock { Text = "Отметки поверх подложки", FontWeight = FontWeight.SemiBold });
        CheckBox edges = new CheckBox { Content = "Границы" };
        BindAnalysisPopup(edges, ToggleButton.IsCheckedProperty, nameof(MainViewModel.ShowAnalysisEdges), BindingMode.TwoWay);
        BindAnalysisPopup(edges, IsEnabledProperty, nameof(MainViewModel.IsEdgesAnalysisEnabled));
        options.Children.Add(edges);
        options.Children.Add(new CheckBox { Content = "Ключевые точки", IsEnabled = false });
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

    private RadioButton CreateAnalysisBackgroundOption(string title, bool useInput, string checkedProperty) {
        RadioButton option = new RadioButton { Content = title, GroupName = "AnalysisBackground" };
        BindAnalysisPopup(option, ToggleButton.IsCheckedProperty, checkedProperty);
        option.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                viewModel.UseAnalysisInputBackground = useInput;
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
