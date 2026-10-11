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

    private Border CreateToolbar() {//покачто весь этот метод собирает всю вторую страницу
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

        Grid advancedWorkspace = new Grid {
            ColumnDefinitions = new ColumnDefinitions("260,*"),
            ColumnSpacing = 12,
            Margin = new Thickness(12),
            Height = 620
        };

        Grid queueContent = new Grid {
            RowDefinitions = new RowDefinitions("Auto,*"),
            RowSpacing = 8
        };

        queueContent.Children.Add(new TextBlock {
            Text = "Очередь действий",
            FontSize = 17
        });

        
        ListBox queueList = new ListBox {//внешеий вид элемента в очереди
            ItemTemplate = new FuncDataTemplate<ImageEditStep>((step, _) => {//получает один элемент из AdvancedSteps к примеру step может быть действием яркость +50
                Grid row = new Grid {
                    ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto,Auto"),
                    RowDefinitions = new RowDefinitions("Auto,Auto"),
                    ColumnSpacing = 6,
                    RowSpacing = 8,
                    MinHeight = 60
                };

                TextBlock name = new TextBlock {//текст блок шага
                    VerticalAlignment = VerticalAlignment.Center,
                    TextWrapping = TextWrapping.Wrap
                };
                name.Bind(
                    TextBlock.TextProperty,
                    new Avalonia.Data.Binding("DisplayName"));//название шага
                Grid.SetColumnSpan(name, 4);
                row.Children.Add(name);

                Button moveUpButton = new Button {
                    Content = "↑",
                    MinWidth = 28
                };
                moveUpButton.Click += (_, _) => {
                    if (DataContext is MainViewModel viewModel) {
                        viewModel.MoveAdvancedStep(step, -1);
                    }
                };
                Grid.SetRow(moveUpButton, 1);
                Grid.SetColumn(moveUpButton, 1);
                row.Children.Add(moveUpButton);

                Button moveDownButton = new Button {
                    Content = "↓",
                    MinWidth = 28
                };
                moveDownButton.Click += (_, _) => {
                    if (DataContext is MainViewModel viewModel) {
                        viewModel.MoveAdvancedStep(step, 1);
                    }
                };
                Grid.SetRow(moveDownButton, 1);
                Grid.SetColumn(moveDownButton, 2);
                row.Children.Add(moveDownButton);

                Button removeButton = new Button {//минусик для удаления
                    Content = "−",
                    MinWidth = 28
                };
                removeButton.Click += (_, _) => {// подписываем минусик
                    if (DataContext is MainViewModel viewModel) {
                        viewModel.RemoveAdvancedStep(step);
                    }
                    // запоминает step, для которого создали кнопку при нажатии вызывает MainViewModel.RemoveAdvancedStep(step), то есть удаляет именно это действие
                };

                Grid.SetRow(removeButton, 1);
                Grid.SetColumn(removeButton, 3);// ставит минус справа
                row.Children.Add(removeButton);
                return row;
            })
        };

        queueList.Bind(// берем действия из AdvancedSteps во ViewModel: при добавлении или удалении шага список обновится
            ItemsControl.ItemsSourceProperty,
            new Avalonia.Data.Binding("AdvancedSteps"));

        Grid.SetRow(queueList, 1);// заголовок остается в строке 0, а список ставим в строку 1 под ним
        queueContent.Children.Add(queueList);

        Border queuePlaceholder = new Border {//левая панель очереди
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = queueContent
        };


        advancedWorkspace.Children.Add(queuePlaceholder);

        //здесь свои вкладки, но обе картинки берутся из общей MainViewModel
        TabControl advancedImageTabs = CreateImageTabs();
        advancedImageTabs.SelectedIndex = 1;

        Grid.SetColumn(advancedImageTabs, 1);
        advancedWorkspace.Children.Add(advancedImageTabs);

        StackPanel advancedContent = new StackPanel();

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

        // Сначала показываем очередь действий и предпросмотр изображения.
        advancedContent.Children.Add(advancedWorkspace);

        //собираем содержимое НИЖНЕЙ панели страницы: заголовок и кнопку добавления поворота
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

        // Поле и кнопка стоят рядом: сначала выбираем значение яркости, затем добавляем его в очередь.
        // Карточка яркости: выбранное значение, ползунок и добавление шага в очередь.
        StackPanel brightnessControls = new StackPanel {
            Spacing = 8
        };
        brightnessControls.Children.Add(new TextBlock {
            Text = "Яркость",
            FontWeight = FontWeight.SemiBold
        });

        Slider brightnessSlider = new Slider {
            Width = 260,
            Minimum = -100,
            Maximum = 100,
            Value = 20,
            TickFrequency = 1,
            IsSnapToTickEnabled = true
        };

        // показываем текущее положение ползунка, не меняя само изображение
        TextBlock brightnessValueText = new TextBlock();
        brightnessValueText.Bind(
            TextBlock.TextProperty,
            new Binding("Value") {
                Source = brightnessSlider,
                StringFormat = "Текущее значение: {0:0}"
            });
        brightnessControls.Children.Add(brightnessValueText);
        brightnessControls.Children.Add(brightnessSlider);

        Button addBrightnessButton = new Button {
            Content = "Добавить яркость",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        addBrightnessButton.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                // только при нажатии создаем шаг со значением ползунка на этот момент
                viewModel.AddAdvancedStep(
                    ImageOperationType.Brightness,
                    brightnessSlider.Value);
            }
        };
        brightnessControls.Children.Add(addBrightnessButton);

        Border brightnessCard = new Border {
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = brightnessControls
        };

        // Внизу две области: слева быстрые действия, справа карточки с параметрами.
        Grid toolsLayout = new Grid {
            ColumnDefinitions = new ColumnDefinitions("260,*"),
            ColumnSpacing = 12
        };
        toolsLayout.Children.Add(toolsContent);

        // карточки действий идут слева направо; если ширины не хватит, следующая перейдет ниже
        WrapPanel adjustmentCards = new WrapPanel {
            ItemSpacing = 12,
            LineSpacing = 12
        };
        adjustmentCards.Children.Add(brightnessCard);

        // карточка насыщенности у продвинутого режима{
        StackPanel saturationControls = new StackPanel {
            Spacing = 8
        };
        saturationControls.Children.Add(new TextBlock {
            Text = "Насыщенность",
            FontWeight = FontWeight.SemiBold
        });

        // 100% сохраняет исходные цвета, меньше 100 ослабляет их, больше 100 усиливает
        Slider saturationSlider = new Slider {
            Width = 260,
            Minimum = 0,
            Maximum = 200,
            Value = 120,
            TickFrequency = 1,
            IsSnapToTickEnabled = true
        };

        // движение ползунка меняет только выбранное число, а не изображение
        TextBlock saturationValueText = new TextBlock();
        saturationValueText.Bind(
            TextBlock.TextProperty,
            new Binding("Value") {
                Source = saturationSlider,
                StringFormat = "Текущее значение: {0:0}%"
            });
        saturationControls.Children.Add(saturationValueText);
        saturationControls.Children.Add(saturationSlider);

        Button addSaturationButton = new Button {
            Content = "Добавить насыщенность",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        addSaturationButton.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                // каждый новый шаг получает значение ползунка на момент нажатия
                viewModel.AddAdvancedStep(
                    ImageOperationType.Saturation,
                    saturationSlider.Value);
            }
        };
        saturationControls.Children.Add(addSaturationButton);

        Border saturationCard = new Border {
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = saturationControls
        };
        adjustmentCards.Children.Add(saturationCard);
        //}

        // карточка контрастности выбирает значение для нового шага очереди
        StackPanel contrastControls = new StackPanel {
            Spacing = 8
        };
        contrastControls.Children.Add(new TextBlock {
            Text = "Контрастность",
            FontWeight = FontWeight.SemiBold
        });

        Slider contrastSlider = new Slider {
            Width = 260,
            Minimum = -100,
            Maximum = 100,
            Value = 20,
            TickFrequency = 1,
            IsSnapToTickEnabled = true
        };

        TextBlock contrastValueText = new TextBlock();
        contrastValueText.Bind(
            TextBlock.TextProperty,
            new Binding("Value") {
                Source = contrastSlider,
                StringFormat = "Текущее значение: {0:0}"
            });
        contrastControls.Children.Add(contrastValueText);
        contrastControls.Children.Add(contrastSlider);

        Button addContrastButton = new Button {
            Content = "Добавить контрастность",
            HorizontalAlignment = HorizontalAlignment.Right
        };
        addContrastButton.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                // берем значение ползунка при нажатии и добавляем его в конец очереди
                viewModel.AddAdvancedStep(
                    ImageOperationType.Contrast,
                    contrastSlider.Value);
            }
        };
        contrastControls.Children.Add(addContrastButton);

        Border contrastCard = new Border {
            Padding = new Thickness(12),
            BorderBrush = Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = contrastControls
        };
        adjustmentCards.Children.Add(contrastCard);

        // в правом столбце теперь находится контейнер карточек, а не одна карточка
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
        advancedContent.Children.Add(advancedToolsPanel);
        // вторая панель показывает ту же пару гистограмм под инструментами
        advancedContent.Children.Add(advancedHistogramPanel);

        ScrollViewer advancedPage = new ScrollViewer {//скрол
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = advancedContent
        };

        advancedButton.Click += (_, _) => {
            if (DataContext is MainViewModel viewModel) {
                viewModel.PrepareAdvancedQueue();//перед открытием расширенного вида переносим текущие настройки в очередь действий
                viewModel.ApplyAdvancedQueue();//применяем собранные шаги к исходнику и обновляем картинку с гистограммой
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
