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
        // конечную высоту задает строка pageHost, содержимое карточек ее не увеличивает
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

        StackPanel methodNames = new StackPanel { Spacing = 8 };
        string[] names = { "Границы", "Ключевые точки", "Область номера", "Сегментация" };
        foreach (string name in names) {
            // в A1 показан каркас, неподключенный метод нельзя запустить как выполненный
            methodNames.Children.Add(new CheckBox { Content = name, IsEnabled = false });
        }

        ScrollViewer methodsScroll = new ScrollViewer {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = methodNames
        };
        Grid.SetRow(methodsScroll, 1);
        layout.Children.Add(methodsScroll);

        TextBlock parametersTitle = new TextBlock { Text = "Параметры метода" };
        Grid.SetRow(parametersTitle, 2);
        layout.Children.Add(parametersTitle);

        ScrollViewer parametersScroll = new ScrollViewer {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock { Text = "Метод не выбран", TextWrapping = TextWrapping.Wrap }
        };
        Grid.SetRow(parametersScroll, 3);
        layout.Children.Add(parametersScroll);

        StackPanel actions = new StackPanel { Spacing = 8 };
        actions.Children.Add(new CheckBox { Content = "Автоанализ", IsEnabled = false });
        actions.Children.Add(new Button {
            Content = "Выполнить выбранное",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = false
        });
        Grid.SetRow(actions, 4);
        layout.Children.Add(actions);
        return CreateAnalysisCard(layout);
    }

    private Border CreateAnalysisImageCard() {
        Grid layout = new Grid {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"),
            RowSpacing = 8
        };

        Image inputImage = new Image { Stretch = Stretch.Uniform };
        inputImage.Bind(Image.SourceProperty,
            CompiledBinding.Create<MainViewModel, Bitmap?>(viewModel => viewModel.AnalysisInputImage));

        Border inputPreview = CreateAnalysisPreview(inputImage);
        Border resultPreview = CreateAnalysisPreview(new TextBlock {
            Text = "Результатов анализа нет",
            Foreground = Brushes.LightGray,
            TextWrapping = TextWrapping.Wrap,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        });
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

        Grid footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 8 };
        TextBlock inputDescription = CreateBoundText(viewModel => viewModel.AnalysisInputDescription);
        inputDescription.TextWrapping = TextWrapping.Wrap;
        inputDescription.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(inputDescription);

        Button saveButton = new Button { Content = "Сохранить результат", IsEnabled = false };
        Grid.SetColumn(saveButton, 1);
        footer.Children.Add(saveButton);
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

        return new Button {
            Content = "Параметры отображения",
            Flyout = new Flyout { Content = options },
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private RadioButton CreateAnalysisSourceOption(
        string title,
        AnalysisImageSource source,
        string checkedProperty) {

        RadioButton option = new RadioButton { Content = title, GroupName = "AnalysisImageSource" };
        // содержимое Flyout получает значение через окно, не зависит от наследования DataContext в popup
        option.Bind(ToggleButton.IsCheckedProperty, new Binding($"DataContext.{checkedProperty}") {
            Source = this,
            Mode = BindingMode.OneWay
        });
        option.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                viewModel.SelectedAnalysisSource = source;
            }
        };
        return option;
    }

    private static Border CreateAnalysisCard(Control content) {
        return new Border {
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Child = content
        };
    }

    private static Border CreateAnalysisPreview(Control content) {
        return new Border {
            Background = new SolidColorBrush(Color.Parse("#181818")),
            Padding = new Thickness(10),
            Child = content
        };
    }
}
