using System;
using System.Linq.Expressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow : Window {
    public MainWindow() {
        Title = "Обработка изображений";
        Width = 1300;
        Height = 750;
        MinWidth = 1000;
        MinHeight = 600;
        Content = CreateLayout();
        InitializeNumericInputs();
    }

    private Grid CreateLayout() {
        Grid layout = new Grid {
            RowDefinitions = new RowDefinitions("Auto,*")
        };
        layout.Children.Add(CreateToolbar());

        // осн обл
        // боковой панели оставляем 310, остальное место отдаём картинке
        Grid workspace = new Grid {
            ColumnDefinitions = new ColumnDefinitions("310,*"),
            ColumnSpacing = 12,
            Margin = new Thickness(12)
        };
        Grid.SetRow(workspace, 1);
        workspace.Children.Add(CreateSidebar());

        TabControl imageTabs = CreateImageTabs();
        Grid.SetColumn(imageTabs, 1);
        workspace.Children.Add(imageTabs);
        layout.Children.Add(workspace);
        return layout;
    }

    private Border CreateToolbar() {
        // верх панель выбора картинки
        Button openButton = new Button { Content = "Открыть изображение" };
        openButton.Click += OpenImageButton_OnClick;

        TextBlock statusText = CreateBoundText(viewModel => viewModel.StatusMessage);
        statusText.VerticalAlignment = VerticalAlignment.Center;

        return new Border {
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Child = new StackPanel {
                Orientation = Orientation.Horizontal,
                Spacing = 12,
                Children = { openButton, statusText }
            }
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
