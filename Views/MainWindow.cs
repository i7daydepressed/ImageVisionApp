using System;
using System.Linq.Expressions;
using Avalonia;
using Avalonia.Controls;
using System.ComponentModel;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow : Window {

    private readonly ContentControl pageHost = new ContentControl();
    private ScrollViewer? simplePage;

    private readonly Button openButton = new Button {
        Content = "Открыть изображение"
    };

    private readonly Button advancedButton = new Button {
        Content = "Расширенное редактирование →"
    };

    private readonly Button backButton = new Button {
        Content = "← К простому виду",
        IsVisible = false
    };

    private readonly Border advancedPage = new Border {
        Padding = new Thickness(20),
        Child = new TextBlock {
            Text = "Расширенное редактирование — здесь разместим предпросмотр и очередь действий",
            FontSize = 18
        }
    };
    private readonly HistogramPanel histogramPanel = new HistogramPanel();
    private MainViewModel? histogramViewModel;

    public MainWindow() {// создаем окно
        Title = "Обработка изображений";
        Width = 1300;
        Height = 750;
        MinWidth = 1000;
        MinHeight = 600;
        Content = CreateLayout();
        DataContextChanged += MainWindow_OnDataContextChanged;
        InitializeNumericInputs();
    }

    private Grid CreateLayout() {
        Grid layout = new Grid {
            RowDefinitions = new RowDefinitions("Auto,*")// ров дефенишен -
        };
        layout.Children.Add(CreateToolbar());

        // осн обл
        // боковой панели оставляем 310, остальное место отдаём картинке
        Grid workspace = new Grid {
            ColumnDefinitions = new ColumnDefinitions("310,*"),
            ColumnSpacing = 12,
            Margin = new Thickness(12),
            Height = 620,
        };
        
        workspace.Children.Add(CreateSidebar());

        TabControl imageTabs = CreateImageTabs();
        Grid.SetColumn(imageTabs, 1);
        workspace.Children.Add(imageTabs);
        
        StackPanel page = new StackPanel();
        page.Children.Add(workspace);
        page.Children.Add(histogramPanel);

        ScrollViewer pageScroll = new ScrollViewer {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = page
        };

        // pageScroll уже содержит весь обычный экран: настройки, изображение и гистограммы
        // Сохраняем ссылку на эту страницу, а в окно ставим pageHost — место для текущего экрана
        // При переходе сюда подставим расширенный экран, а при возврате вернём тот же pageScroll
        // Так обычный экран не придётся создавать заново
        simplePage = pageScroll;
        pageHost.Content = simplePage;
        // simplePage хранит страницу, а pageHost.Content определяет, какая страница сейчас показана
        Grid.SetRow(pageHost, 1);
        layout.Children.Add(pageHost);

        return layout;
    }

    // находит ViewModel окна и подписывается на ее изменения
    private void MainWindow_OnDataContextChanged(object? sender, EventArgs e) {
        if (histogramViewModel is not null) {//// отписываемся от прежней ViewModel, чтобы она больше не обновляла эту панель
            histogramViewModel.PropertyChanged -= HistogramViewModel_OnPropertyChanged;
        }

        histogramViewModel = DataContext as MainViewModel;

        if (histogramViewModel is not null) {
            histogramViewModel.PropertyChanged += HistogramViewModel_OnPropertyChanged;
        }

        histogramPanel.ShowComparison(histogramViewModel?.HistogramComparison);
    }

    // реагирует только на новую пару гистограмм и передаёт ее панели
    private void HistogramViewModel_OnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e) {

        if (e.PropertyName == nameof(MainViewModel.HistogramComparison) &&
            sender is MainViewModel viewModel) {

            histogramPanel.ShowComparison(viewModel.HistogramComparison);
        }
    }

    private Border CreateToolbar() {
        // верх панель выбора картинки
        Button openButton = new Button {
            Content = "Открыть изображение"
        };
        openButton.Click += OpenImageButton_OnClick;

        Button advancedButton = new Button {
            Content = "Расширенное редактирование →"
        };

        Button backButton = new Button {
            Content = "← К простому виду",
            IsVisible = false
        };

        Border advancedPage = new Border {
            Padding = new Thickness(20),
            Child = new TextBlock {
                Text = "Расширенное редактирование: здесь будут предпросмотр и очередь действий",
                FontSize = 18
            }
        };

        advancedButton.Click += (_, _) => {
            // меняем страницу внутри окна; обычную страницу оставляем в simplePage
            pageHost.Content = advancedPage;
            openButton.IsVisible = false;
            advancedButton.IsVisible = false;
            backButton.IsVisible = true;
        };

        backButton.Click += (_, _) => {
            pageHost.Content = simplePage;//симл пейдж
            backButton.IsVisible = false;
            openButton.IsVisible = true;
            advancedButton.IsVisible = true;
        };

        TextBlock statusText =
            CreateBoundText(viewModel => viewModel.StatusMessage);
        statusText.VerticalAlignment = VerticalAlignment.Center;

        Grid toolbarContent = new Grid {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 12
        };

        // Слева видна кнопка открытия или стрелка назад
        toolbarContent.Children.Add(openButton);
        toolbarContent.Children.Add(backButton);

        Grid.SetColumn(statusText, 1);
        toolbarContent.Children.Add(statusText);

        Grid.SetColumn(advancedButton, 2);
        toolbarContent.Children.Add(advancedButton);

        return new Border {
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = toolbarContent
        };
    }

    private TabControl CreateSidebar() {
        return new TabControl {
            SelectedIndex = 0,
            // это раскладка заголовков вкладок, само содержимое задаём в Content
            ItemsPanel = new FuncTemplate<Panel?>(() => new StackPanel {
                Orientation = Orientation.Horizontal
            }),
            Items = {
                new TabItem {
                    Header = "Информация",
                    FontSize = 14,
                    Content = CreateScrollablePanel(CreateInformationPanel())
                },
                new TabItem {
                    Header = "Преобразования",
                    FontSize = 14,
                    Content = CreateScrollablePanel(CreateTransformationsPanel())
                }
            }
        };
    }

    private static Border CreateScrollablePanel(Control content) {
        // пркртк
        return new Border {
            Padding = new Thickness(14),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = new ScrollViewer {
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                Content = content
            }
        };
    }

    private static TabControl CreateImageTabs() {
        // поле изображения
        // у каждой вкладки своя картинка, при обработке исходник не подменяем
        Image originalImage = new Image { Stretch = Stretch.Uniform };
        originalImage.Bind(Image.SourceProperty,
            CompiledBinding.Create<MainViewModel, Bitmap?>(
                viewModel => viewModel.OriginalImage));

        Image modifiedImage = new Image { Stretch = Stretch.Uniform };
        modifiedImage.Bind(Image.SourceProperty,
            CompiledBinding.Create<MainViewModel, Bitmap?>(
                viewModel => viewModel.ModifiedImage));

        return new TabControl {
            // центрирование
            ItemsPanel = new FuncTemplate<Panel?>(() => new WrapPanel {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center
            }),
            Items = {
                CreateImageTab("Оригинал", originalImage),
                CreateImageTab("Изменённое", modifiedImage)
            }
        };
    }

    private static TabItem CreateImageTab(string header, Image image) {
        return new TabItem {
            Header = header,
            Content = new Border {
                Background = new SolidColorBrush(Color.Parse("#181818")),
                Padding = new Thickness(10),
                Child = image
            }
        };
    }

    private static TextBlock CreateBoundText<TValue>(
        Expression<Func<MainViewModel, TValue>> expression) {

        TextBlock textBlock = new TextBlock();
        // берём значение из DataContext, дальше обновления ловит сама привязка
        textBlock.Bind(TextBlock.TextProperty, CompiledBinding.Create(expression));
        return textBlock;
    }

    private static TextBlock CreateSectionHeading(string text) {
        return new TextBlock {
            Text = text,
            FontSize = 17,
            FontWeight = FontWeight.Bold
        };
    }

    private static Separator CreateSectionSeparator() {
        return new Separator { Margin = new Thickness(0, 8) };
    }
}
