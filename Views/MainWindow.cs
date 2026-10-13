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
using ImageVisionApp.Models;

namespace ImageVisionApp.Views;

public partial class MainWindow : Window {

    private readonly ContentControl pageHost = new ContentControl();
    private ScrollViewer? simplePage;

    private readonly HistogramPanel histogramPanel = new HistogramPanel();
    private readonly HistogramPanel advancedHistogramPanel = new HistogramPanel();
    private MainViewModel? histogramViewModel;

    public MainWindow() {// создаем окно
        Title = "Обработка изображений";
        Width = 1300;
        Height = 750;
        MinWidth = 1000;
        MinHeight = 600;
        Content = CreateLayout();
        DataContextChanged += MainWindow_OnDataContextChanged;
    }

    private Grid CreateLayout() {
        Grid layout = new Grid {
            RowDefinitions = new RowDefinitions("Auto,*")// ров дефенишен -
        };
        layout.Children.Add(CreateToolbar());

        // осн обл
        // боковой панели оставляем 310, остальное место отдаем картинке
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
        // При переходе сюда подставим расширенный экран, а при возврате вернем тот же pageScroll
        // Так обычный экран не придется создавать заново
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
        advancedHistogramPanel.ShowComparison(histogramViewModel?.HistogramComparison);
    }

    // реагирует только на новую пару гистограмм и передает ее панели
    private void HistogramViewModel_OnPropertyChanged(
        object? sender,
        PropertyChangedEventArgs e) {

        if (e.PropertyName == nameof(MainViewModel.HistogramComparison) &&
            sender is MainViewModel viewModel) {

            histogramPanel.ShowComparison(viewModel.HistogramComparison);
            advancedHistogramPanel.ShowComparison(viewModel.HistogramComparison);
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

        // страницу создаем один раз, при переходах меняем только содержимое pageHost
        ScrollViewer advancedPage = CreateAdvancedPage();

        advancedButton.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                // применяем текущую очередь к исходнику и обновляем картинку с гистограммой
                viewModel.ApplyAdvancedQueue();
            }

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

        // слева видна кнопка открытия или стрелка назад
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

    private ScrollViewer CreateAdvancedPage() {
        StackPanel advancedContent = new StackPanel();
        // сначала показываем очередь действий и предпросмотр изображения
        advancedContent.Children.Add(CreateAdvancedWorkspace());
        advancedContent.Children.Add(CreateAdvancedToolsPanel());
        // вторая панель показывает ту же пару гистограмм под инструментами
        advancedContent.Children.Add(advancedHistogramPanel);
        advancedContent.Children.Add(CreateStructuralElementCard());

        return new ScrollViewer {//скрол
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = advancedContent
        };
    }

    private Grid CreateAdvancedWorkspace() {
        Grid advancedWorkspace = new Grid {
            ColumnDefinitions = new ColumnDefinitions("260,*"),
            ColumnSpacing = 12,
            Margin = new Thickness(12),
            Height = 620
        };

        advancedWorkspace.Children.Add(CreateAdvancedQueuePanel());

        //здесь свои вкладки, но обе картинки берутся из общей MainViewModel
        TabControl advancedImageTabs = CreateImageTabs();
        advancedImageTabs.SelectedIndex = 1;

        Grid.SetColumn(advancedImageTabs, 1);
        advancedWorkspace.Children.Add(advancedImageTabs);

        return advancedWorkspace;
    }





    private Border CreateAdvancedToolsPanel() {
        // Внизу две области: слева быстрые действия, справа карточки с параметрами.
        Grid toolsLayout = new Grid {
            ColumnDefinitions = new ColumnDefinitions("260,*"),
            ColumnSpacing = 12
        };
        toolsLayout.Children.Add(CreateQuickActionsPanel());

        // карточки действий идут слева направо; если ширины не хватит, следующая перейдет ниже
        WrapPanel adjustmentCards = new WrapPanel {
            ItemSpacing = 12,
            LineSpacing = 12
        };
        adjustmentCards.Children.Add(CreateBrightnessCard());
        adjustmentCards.Children.Add(CreateSaturationCard());
        adjustmentCards.Children.Add(CreateContrastCard());
        adjustmentCards.Children.Add(CreateMorphologyCard());

        // карточки с параметрами ставим в правый столбец
        Grid.SetColumn(adjustmentCards, 1);
        toolsLayout.Children.Add(adjustmentCards);

        // общая рамка нижней панели охватывает обе области
        Border advancedToolsPanel = new Border {
            Margin = new Thickness(12),
            Padding = new Thickness(12),
            MinHeight = 180,
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = toolsLayout
        };
        return advancedToolsPanel;
    }

    private StackPanel CreateQuickActionsPanel() {
        Button addRotationButton = new Button {//ротейшен батен
            Content = "Добавить поворот 90°",
            Margin = new Thickness(12),
            HorizontalAlignment = HorizontalAlignment.Left
        };
        addRotationButton.Click += (_, _) => {//подписываем на ад
            if (DataContext is MainViewModel viewModel) {
                viewModel.AddAdvancedStep(ImageOperationType.Rotation, 90);
            }
        };

        // собираем быстрые действия в прежнем порядке, затем сброс и сохранение
        StackPanel toolsContent = new StackPanel {
            Spacing = 8 // расстояние между заголовком и кнопкой
        };
        toolsContent.Children.Add(new TextBlock {
            Text = "Инструменты",
            FontSize = 17
        });
        toolsContent.Children.Add(addRotationButton);

        Button addGrayscaleButton = new Button {//кнопградации
            Content = "Добавить градации серого",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        addGrayscaleButton.Click += (_, _) => {//подписываем кнопк
            if (DataContext is MainViewModel viewModel) {
                viewModel.AddAdvancedStep(ImageOperationType.Grayscale);
            }
        };
        toolsContent.Children.Add(addGrayscaleButton);
        // линейная коррекция добавляется как отдельный шаг после уже выбранных действий
        Button addLinearCorrectionButton = new Button {
            Content = "Добавить линейную коррекцию ЧБ",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        addLinearCorrectionButton.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                viewModel.AddAdvancedStep(ImageOperationType.LinearGrayscaleCorrection);
            }
        };
        toolsContent.Children.Add(addLinearCorrectionButton);
        // нелинейная коррекция тоже становится отдельным шагом очереди
        Button addNonlinearCorrectionButton = new Button {
            Content = "Добавить нелинейную коррекцию ЧБ",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        addNonlinearCorrectionButton.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                viewModel.AddAdvancedStep(ImageOperationType.NonlinearGrayscaleCorrection);
            }
        };
        toolsContent.Children.Add(addNonlinearCorrectionButton);
        // сохраняем результат текущей очереди через обработчик расширенного режима
        Button saveAdvancedButton = new Button {
            Content = "Сохранить результат",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        saveAdvancedButton.Click += SaveAdvancedResultButton_OnClick;
        // сбрасываем всю очередь; результат снова строится из исходного изображения
        Button resetAdvancedButton = new Button {
            Content = "Сбросить изменения",
            HorizontalAlignment = HorizontalAlignment.Left
        };
        resetAdvancedButton.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                viewModel.ResetAdvancedQueue();
            }
        };
        toolsContent.Children.Add(resetAdvancedButton);
        toolsContent.Children.Add(saveAdvancedButton);

        return toolsContent;
    }

    private Border CreateBrightnessCard() {
        // Поле и кнопка стоят рядом: сначала выбираем значение яркости, затем добавляем его в очередь.
        // Карточка яркости: выбранное значение, ползунок и добавление шага в очередь.
        return CreateAdjustmentCard(
            title: "Яркость",
            operationType: ImageOperationType.Brightness,
            sliderMinimum: -100,
            sliderMaximum: 100,
            initialValue: 20,
            inputMinimum: -100,
            inputMaximum: 100,
            buttonText: "Добавить яркость");
    }

    private Border CreateSaturationCard() {
        // карточка насыщенности у продвинутого режима{
        // 100% сохраняет исходные цвета, меньше 100 ослабляет их, больше 100 усиливает
        return CreateAdjustmentCard(
            title: "Насыщенность",
            operationType: ImageOperationType.Saturation,
            sliderMinimum: 0,
            sliderMaximum: 200,
            initialValue: 120,
            inputMinimum: 0,
            inputMaximum: null,
            buttonText: "Добавить насыщенность");
        //}
    }

    private Border CreateContrastCard() {
        // карточка контрастности выбирает значение для нового шага очереди
        return CreateAdjustmentCard(
            title: "Контрастность",
            operationType: ImageOperationType.Contrast,
            sliderMinimum: -100,
            sliderMaximum: 100,
            initialValue: 20,
            inputMinimum: -100,
            inputMaximum: 100,
            buttonText: "Добавить контрастность");
    }

    private Border CreateSidebar() {
        // первая страница показывает сведения об изображении без инструментов обработки
        return CreateScrollablePanel(CreateInformationPanel());
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
                CreateImageTab("Измененное", modifiedImage)
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
        // берем значение из DataContext, дальше обновления ловит сама привязка
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
