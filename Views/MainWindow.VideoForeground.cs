using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private readonly TabStrip videoForegroundTabs = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TabStripItem videoForegroundResultTab = new() { Content = "Результат", IsEnabled = false };
    private readonly ListBox videoForegroundViews = new() { ItemsSource = new[] { "Передний план", "Маска" } };
    private readonly ComboBox videoForegroundSourcePicker = new() {
        ItemsSource = new[] { "Оригинал", "Результат редактирования", "Результат анализа" },
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly CheckBox videoForegroundMethod = new() { Content = "Вычитание фона" };
    private readonly Button videoForegroundMethodSettings = new() { Content = "Параметры", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button videoForegroundApply = new() { Content = "Применить", HorizontalAlignment = HorizontalAlignment.Stretch };
    private Button? videoForegroundDisplayButton;
    private int videoForegroundLastVideoTab = 1;

    // располагаем метод и просмотр так же, как на вкладке анализа
    private Grid CreateVideoForegroundPage() {
        Border methodsCard = CreateVideoForegroundMethodsCard();
        Border imageCard = CreateVideoForegroundImageCard();
        Grid.SetColumn(imageCard, 1);
        return new Grid {
            ColumnDefinitions = new ColumnDefinitions("280,*"), ColumnSpacing = 12, Margin = new Thickness(12),
            Focusable = true, Children = { methodsCard, imageCard }
        };
    }

    // галочка сразу меняет обработку, кнопка отдельно открывает сведения о выбранном методе
    private Border CreateVideoForegroundMethodsCard() {
        // фото скрывает отметку, настройку видео меняет только действие пользователя
        videoForegroundMethod.Bind(ToggleButton.IsCheckedProperty, new Binding(nameof(MainViewModel.CanDisplayVideoForegroundEffect)) {
            Mode = BindingMode.OneWay
        });
        videoForegroundMethod.Click += (_, _) => {
            if (DataContext is MainViewModel { IsVideoOpen: true } vm) {
                vm.VideoForegroundEnabled = videoForegroundMethod.IsChecked == true;
            }
        };
        Grid methodRow = new() {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4
        };
        methodRow.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.IsVideoOpen)));
        Grid.SetColumn(videoForegroundMethodSettings, 1);
        methodRow.Children.Add(videoForegroundMethod);
        methodRow.Children.Add(videoForegroundMethodSettings);
        TextBlock hint = CreateBoundText(vm => vm.VideoForegroundStatus);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoOpen)) { Converter = BoolConverters.Not });
        ScrollViewer methods = new() {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel { Spacing = 8, Children = { methodRow, hint } }
        };

        AutomationProperties.SetName(videoForegroundSourcePicker, "Источник вычитания фона");
        videoForegroundSourcePicker.Bind(SelectingItemsControl.SelectedIndexProperty,
            new Binding(nameof(MainViewModel.SelectedVideoForegroundSourceIndex)) { Mode = BindingMode.TwoWay });
        StackPanel details = new() {
            Spacing = 6,
            Children = {
                new TextBlock { Text = "Алгоритм · MOG2" }, new TextBlock { Text = "Источник" }, videoForegroundSourcePicker
            }
        };
        TextBlock parametersTitle = new() { Text = "Параметры метода" };
        ScrollViewer parametersContent = new() {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new TextBlock { Text = "Выберите метод для настройки", TextWrapping = TextWrapping.Wrap }
        };
        videoForegroundMethodSettings.Click += (_, _) => {
            parametersTitle.Text = "Параметры вычитания фона";
            parametersContent.Content = details;
        };
        parametersContent.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoOpen)));
        // в анализе над кнопкой для фото есть строка автоанализа высотой 32 и отступ 8
        videoForegroundApply.Bind(MarginProperty, new Binding(nameof(MainViewModel.IsVideoOpen)) {
            Converter = new FuncValueConverter<bool, Thickness>(video => video ? new Thickness(0) : new Thickness(0, 40, 0, 0))
        });
        videoForegroundApply.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.CanApplyVideoForeground)));
        videoForegroundApply.Click += (_, _) => {
            if (DataContext is MainViewModel { CanApplyVideoForeground: true } vm) {
                videoForegroundTabs.SelectedIndex = 1;
                vm.ApplyVideoForeground();
            }
        };
        Grid.SetRow(methods, 1);
        Grid.SetRow(parametersTitle, 2);
        Grid.SetRow(parametersContent, 3);
        Grid.SetRow(videoForegroundApply, 4);
        return CreateAnalysisCard(new Grid {
            RowDefinitions = new RowDefinitions("Auto,*,Auto,*,Auto"), RowSpacing = 8,
            Children = { CreateSectionHeading("Методы обработки"), methods, parametersTitle, parametersContent, videoForegroundApply }
        });
    }

    // вход и результат занимают одну область, выбор маски меняет только отображение результата
    private Border CreateVideoForegroundImageCard() {
        videoForegroundTabs.Items.Add(new TabStripItem { Content = "Вход" });
        videoForegroundTabs.Items.Add(videoForegroundResultTab);
        videoForegroundTabs.SelectedIndex = 0;
        AutomationProperties.SetName(videoForegroundTabs, "Просмотр вычитания фона");
        AutomationProperties.SetName(videoForegroundViews, "Вид результата вычитания фона");
        BindAnalysisPopup(videoForegroundViews, SelectingItemsControl.SelectedIndexProperty,
            nameof(MainViewModel.VideoForegroundViewIndex), BindingMode.TwoWay);
        BindAnalysisPopup(videoForegroundViews, IsEnabledProperty, nameof(MainViewModel.CanDisplayVideoForegroundEffect));
        videoForegroundDisplayButton = CreateAnalysisDisplayFlyout(new StackPanel {
            MinWidth = 180, Children = { videoForegroundViews }
        });
        videoForegroundDisplayButton.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.CanDisplayVideoForegroundEffect)));

        Grid header = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 8 };
        Grid.SetColumn(videoForegroundDisplayButton, 2);
        TextBlock path = CreateBoundText(vm => vm.VideoForegroundPath);
        path.FontSize = 12;
        path.TextTrimming = TextTrimming.CharacterEllipsis;
        path.MaxLines = 2;
        path.VerticalAlignment = VerticalAlignment.Center;
        path.Bind(ToolTip.TipProperty, new Binding(nameof(MainViewModel.VideoForegroundPath)));
        Grid.SetColumn(path, 1);
        header.Children.Add(videoForegroundTabs);
        header.Children.Add(path);
        header.Children.Add(videoForegroundDisplayButton);

        VideoPreviewImage input = new() { Stretch = Stretch.Uniform };
        input.Bind(Image.SourceProperty, CompiledBinding.Create<MainViewModel, Bitmap?>(vm => vm.VideoForegroundInputPreview));
        input.Bind(VideoPreviewImage.RevisionProperty, CompiledBinding.Create<MainViewModel, int>(vm => vm.VideoForegroundRevision));
        VideoPreviewImage result = new() { Stretch = Stretch.Uniform, IsVisible = false };
        result.Bind(Image.SourceProperty, CompiledBinding.Create<MainViewModel, Bitmap?>(vm => vm.VideoForegroundPreview));
        result.Bind(VideoPreviewImage.RevisionProperty, CompiledBinding.Create<MainViewModel, int>(vm => vm.VideoForegroundRevision));
        videoForegroundTabs.SelectionChanged += (_, e) => {
            if (ReferenceEquals(e.Source, videoForegroundTabs)) {
                bool showResult = videoForegroundTabs.SelectedIndex == 1;
                input.IsVisible = !showResult;
                result.IsVisible = showResult;
                if (DataContext is MainViewModel { IsVideoOpen: true } &&
                    videoForegroundTabs.SelectedIndex is 0 or 1) {
                    videoForegroundLastVideoTab = videoForegroundTabs.SelectedIndex;
                }
            }
        };
        TextBlock empty = new() {
            Text = "Файл не открыт", Foreground = Brushes.LightGray,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
        };
        empty.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.CurrentDocument)) { Converter = ObjectConverters.IsNull });
        Border preview = CreateAnalysisPreview(new Grid { Children = { input, result, empty } });
        TextBlock status = CreateBoundText(vm => vm.VideoForegroundStatus);
        status.TextWrapping = TextWrapping.Wrap;
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.MaxLines = 2;
        status.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoOpen)));
        status.Bind(ToolTip.TipProperty, new Binding(nameof(MainViewModel.VideoForegroundStatus)));

        Grid.SetRow(preview, 1);
        Grid.SetRow(status, 2);
        return CreateAnalysisCard(new Grid {
            RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8,
            Children = { header, preview, status }
        });
    }

    // для фото показываем оригинал, сохраняя выбор вкладки до следующего видео
    private void UpdateVideoForegroundDocument() {
        MainViewModel? vm = DataContext as MainViewModel;
        bool video = vm?.IsVideoOpen == true;
        videoForegroundResultTab.IsEnabled = video;
        videoForegroundTabs.SelectedIndex = video ? videoForegroundLastVideoTab : 0;
        if (!video) {
            videoForegroundDisplayButton?.Flyout?.Hide();
        }
    }

    // команды выбора вида и активации кнопок обрабатывает сам элемент управления
    private bool UsesVideoForegroundControlKey(KeyEventArgs e) =>
        (e.Key is Key.Space or Key.Left or Key.Right) && e.Source is Visual source &&
        source.GetSelfAndVisualAncestors().Any(control =>
            ReferenceEquals(control, videoForegroundTabs) || ReferenceEquals(control, videoForegroundViews) ||
            ReferenceEquals(control, videoForegroundSourcePicker) ||
            e.Key == Key.Space && (ReferenceEquals(control, videoForegroundDisplayButton) ||
                ReferenceEquals(control, videoForegroundMethod) || ReferenceEquals(control, videoForegroundMethodSettings) ||
                ReferenceEquals(control, videoForegroundApply) || ReferenceEquals(control, videoForegroundPageButton)));
}
