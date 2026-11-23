using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private const string MovingBlurTitle = "Размытие движущихся областей";
    private const string OpticalFlowTitle = "Оптический поток";
    private readonly TabStrip videoForegroundTabs = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TabStripItem videoForegroundResultTab = new() { Content = "Результат", IsEnabled = false };
    private readonly StackPanel videoForegroundDisplayOptions = new() { Spacing = 6, Width = 264 };
    private readonly ComboBox videoForegroundSourcePicker = CreateVideoForegroundSourcePicker("Источник вычитания фона");
    private readonly ComboBox videoReplacementSourcePicker = CreateVideoForegroundSourcePicker("Источник замены фона");
    private readonly ComboBox videoMovingBlurSourcePicker = CreateVideoForegroundSourcePicker("Источник размытия");
    private readonly ComboBox videoSubtractionRasterPicker = new() {
        ItemsSource = new[] { "Передний план", "Маска", "Задний план" },
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly CheckBox videoForegroundMethod = new() { Content = "Вычитание фона" };
    private readonly Button videoForegroundMethodSettings = new() { Content = "Параметры", VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox videoReplacementMethod = new() { Content = "Замена фона" };
    private readonly Button videoReplacementSettings = new() { Content = "Параметры", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button videoBackgroundPicker = new() { Content = "Выбрать фон…" };
    private readonly CheckBox videoMovingBlurMethod = new() {
        Content = MovingBlurTitle,
        ContentTemplate = new FuncDataTemplate<string>((title, _) => new TextBlock { Text = title, TextWrapping = TextWrapping.Wrap })
    };
    private readonly Button videoMovingBlurSettings = new() { Content = "Параметры", VerticalAlignment = VerticalAlignment.Center };
    private readonly Slider videoMovingBlurRadius = new() {
        Minimum = 1, Maximum = 12, TickFrequency = 1, IsSnapToTickEnabled = true,
        SmallChange = 1, LargeChange = 1, HorizontalAlignment = HorizontalAlignment.Stretch
    };
    private readonly CheckBox videoOpticalFlowMethod = new() { Content = OpticalFlowTitle };
    private readonly Button videoOpticalFlowSettings = new() { Content = "Параметры", VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox videoOpticalFlowSourcePicker = CreateVideoForegroundSourcePicker("Источник оптического потока");
    private readonly Button videoForegroundApply = new() { Content = "Применить", HorizontalAlignment = HorizontalAlignment.Stretch };
    private Button? videoForegroundDisplayButton;
    private int videoForegroundLastVideoTab = 1;
    private bool pickingVideoBackground;

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
        Grid methodRow = CreateVideoForegroundMethodRow(videoForegroundMethod, videoForegroundMethodSettings,
            nameof(MainViewModel.VideoForegroundEnabled));
        Grid replacementRow = CreateVideoForegroundMethodRow(videoReplacementMethod, videoReplacementSettings,
            nameof(MainViewModel.VideoBackgroundReplacementEnabled));
        Grid blurRow = CreateVideoForegroundMethodRow(videoMovingBlurMethod, videoMovingBlurSettings,
            nameof(MainViewModel.VideoMovingBlurEnabled));
        Grid opticalFlowRow = CreateVideoForegroundMethodRow(videoOpticalFlowMethod, videoOpticalFlowSettings,
            nameof(MainViewModel.VideoOpticalFlowEnabled));
        videoOpticalFlowMethod.Click += (_, _) => {
            if (DataContext is MainViewModel { IsVideoOpen: true } vm) {
                vm.VideoOpticalFlowEnabled = videoOpticalFlowMethod.IsChecked == true;
            }
        };
        AutomationProperties.SetName(videoMovingBlurMethod, MovingBlurTitle);
        videoMovingBlurMethod.Click += (_, _) => {
            if (DataContext is MainViewModel { IsVideoOpen: true } vm) {
                vm.VideoMovingBlurEnabled = videoMovingBlurMethod.IsChecked == true;
            }
        };
        videoForegroundMethod.Click += (_, _) => {
            if (DataContext is MainViewModel { IsVideoOpen: true } vm) {
                vm.VideoForegroundEnabled = videoForegroundMethod.IsChecked == true;
            }
        };
        TextBlock hint = CreateBoundText(vm => vm.VideoForegroundStatus);
        hint.TextWrapping = TextWrapping.Wrap;
        hint.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoOpen)) { Converter = BoolConverters.Not });
        ScrollViewer methods = new() {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = new StackPanel { Spacing = 8, Children = { methodRow, replacementRow, blurRow, opticalFlowRow, hint } }
        };

        BindVideoForegroundPicker(videoForegroundSourcePicker, nameof(MainViewModel.SelectedVideoSubtractionSourceIndex),
            vm => vm.SelectedVideoSubtractionSourceIndex, (vm, index) => vm.SelectedVideoSubtractionSourceIndex = index);
        BindVideoForegroundPicker(videoReplacementSourcePicker, nameof(MainViewModel.SelectedVideoReplacementSourceIndex),
            vm => vm.SelectedVideoReplacementSourceIndex, (vm, index) => vm.SelectedVideoReplacementSourceIndex = index);
        BindVideoForegroundPicker(videoMovingBlurSourcePicker, nameof(MainViewModel.SelectedVideoBlurSourceIndex),
            vm => vm.SelectedVideoBlurSourceIndex, (vm, index) => vm.SelectedVideoBlurSourceIndex = index);
        BindVideoForegroundPicker(videoOpticalFlowSourcePicker, nameof(MainViewModel.SelectedVideoFlowSourceIndex),
            vm => vm.SelectedVideoFlowSourceIndex, (vm, index) => vm.SelectedVideoFlowSourceIndex = index);
        AutomationProperties.SetName(videoSubtractionRasterPicker, "Вид вычитания фона");
        BindVideoForegroundPicker(videoSubtractionRasterPicker, nameof(MainViewModel.SelectedVideoSubtractionRasterIndex),
            vm => vm.SelectedVideoSubtractionRasterIndex, (vm, index) => vm.SelectedVideoSubtractionRasterIndex = index);

        StackPanel details = new() {
            Spacing = 6,
            Children = {
                new TextBlock { Text = "Алгоритм · MOG2" },
                new TextBlock { Text = "Источник расчёта" }, videoForegroundSourcePicker,
                new TextBlock { Text = "Вид вычитания фона" }, videoSubtractionRasterPicker
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
        StackPanel replacementDetails = CreateVideoBackgroundSettings();
        void ShowReplacementSettings() {
            parametersTitle.Text = "Параметры замены фона";
            parametersContent.Content = replacementDetails;
        }
        videoReplacementSettings.Click += (_, _) => ShowReplacementSettings();
        StackPanel blurDetails = CreateVideoMovingBlurSettings();
        videoMovingBlurSettings.Click += (_, _) => {
            parametersTitle.Text = "Параметры размытия";
            parametersContent.Content = blurDetails;
        };
        StackPanel opticalFlowDetails = new() {
            Spacing = 6,
            Children = { new TextBlock { Text = "Алгоритм · Фарнебэк" },
                new TextBlock { Text = "Источник расчёта" }, videoOpticalFlowSourcePicker }
        };
        videoOpticalFlowSettings.Click += (_, _) => {
            parametersTitle.Text = "Параметры оптического потока";
            parametersContent.Content = opticalFlowDetails;
        };
        videoReplacementMethod.Click += async (_, _) => {
            if (DataContext is not MainViewModel { IsVideoOpen: true } vm) {
                return;
            }
            if (videoReplacementMethod.IsChecked == true && !vm.HasVideoBackgroundImage) {
                // отмена выбора оставляет метод в прежнем состоянии
                videoReplacementMethod.SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                ShowReplacementSettings();
                await PickVideoBackgroundAsync();
            }
            else {
                vm.VideoBackgroundReplacementEnabled = videoReplacementMethod.IsChecked == true;
            }
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

    // показываем галочку только на видео, оставляя ее значение в модели при открытии фото
    private static Grid CreateVideoForegroundMethodRow(CheckBox method, Button settings, string enabledProperty) {
        method.Bind(ToggleButton.IsCheckedProperty, new MultiBinding {
            Converter = BoolConverters.And, Mode = BindingMode.OneWay,
            Bindings = { new Binding(nameof(MainViewModel.IsVideoOpen)), new Binding(enabledProperty) }
        });
        Grid.SetColumn(settings, 1);
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4, Children = { method, settings } };
        row.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.IsVideoOpen)));
        return row;
    }

    // каждый метод выбирает свой вход из тех же источников кадров
    private static ComboBox CreateVideoForegroundSourcePicker(string label) {
        ComboBox picker = new() {
            ItemsSource = new[] { "Оригинал", "Результат редактирования", "Результат анализа" },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(picker, label);
        return picker;
    }

    // передаем выбор модели и восстанавливаем принятую позицию после отказа
    private void BindVideoForegroundPicker(ComboBox picker, string property,
        Func<MainViewModel, int> read, Action<MainViewModel, int> write) {
        picker.Bind(SelectingItemsControl.SelectedIndexProperty,
            new Binding(property) { Mode = BindingMode.OneWay });
        bool restoringSelection = false;
        picker.SelectionChanged += (_, e) => {
            if (restoringSelection || !ReferenceEquals(e.Source, picker) || picker.SelectedIndex < 0 ||
                DataContext is not MainViewModel { IsVideoOpen: true } model) {
                return;
            }
            restoringSelection = true;
            try {
                write(model, picker.SelectedIndex);
                picker.SetCurrentValue(SelectingItemsControl.SelectedIndexProperty, read(model));
            }
            finally {
                restoringSelection = false;
            }
        };
    }

    // радиус передается в модель целыми пикселями, в том числе при вводе стрелками
    private StackPanel CreateVideoMovingBlurSettings() {
        AutomationProperties.SetName(videoMovingBlurRadius, "Радиус размытия");
        videoMovingBlurRadius.Bind(RangeBase.ValueProperty, new Binding(nameof(MainViewModel.VideoMovingBlurRadius)) {
            Mode = BindingMode.TwoWay
        });
        TextBlock radius = new();
        radius.Bind(TextBlock.TextProperty, new Binding(nameof(MainViewModel.VideoMovingBlurRadius)) {
            StringFormat = "Радиус {0} пикс."
        });
        return new StackPanel { Spacing = 4, Children = { radius, videoMovingBlurRadius,
            new TextBlock { Text = "Источник расчёта" }, videoMovingBlurSourcePicker } };
    }

    // выбранное имя остается доступным целиком даже в узкой панели
    private StackPanel CreateVideoBackgroundSettings() {
        AutomationProperties.SetName(videoBackgroundPicker, "Выбрать изображение для замены фона");
        videoBackgroundPicker.Click += async (_, _) => await PickVideoBackgroundAsync();
        TextBlock fileName = new() { TextTrimming = TextTrimming.CharacterEllipsis };
        fileName.Bind(TextBlock.TextProperty, new Binding(nameof(MainViewModel.VideoBackgroundImageName)) {
            TargetNullValue = "Фон не выбран"
        });
        fileName.Bind(ToolTip.TipProperty, new Binding(nameof(MainViewModel.VideoBackgroundImageName)));
        TextBlock notice = CreateBoundText(vm => vm.VideoBackgroundSelectionNotice);
        notice.TextWrapping = TextWrapping.Wrap;
        notice.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.VideoBackgroundSelectionNotice)) {
            Converter = StringConverters.IsNotNullOrEmpty
        });
        return new StackPanel { Spacing = 4, Children = { videoBackgroundPicker, fileName,
            new TextBlock { Text = "По центру с обрезкой", TextWrapping = TextWrapping.Wrap },
            new TextBlock { Text = "Источник расчёта" }, videoReplacementSourcePicker, notice } };
    }

    // ответ системного диалога относится только к видео, для которого открыли выбор
    private async Task PickVideoBackgroundAsync() {
        if (pickingVideoBackground || mediaWindowClosed ||
            DataContext is not MainViewModel { IsVideoOpen: true, CurrentDocument: { } document } vm) {
            return;
        }
        bool IsCurrent() => !mediaWindowClosed && ReferenceEquals(DataContext, vm) && ReferenceEquals(vm.CurrentDocument, document);
        pickingVideoBackground = true;
        IReadOnlyList<IStorageFile>? files = null;
        try {
            files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Выбрать фон для видео", AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("Изображения") { Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.webp"] }]
            });
            if (files.Count > 0 && IsCurrent()) {
                await vm.ChooseVideoBackgroundAsync(files[0]);
            }
        }
        catch (Exception error) {
            if (IsCurrent()) {
                vm.ShowError($"Не удалось выбрать фон: {error.Message}");
            }
        }
        finally {
            if (files is not null) {
                foreach (IStorageFile file in files) {
                    file.Dispose();
                }
            }
            pickingVideoBackground = false;
        }
    }

    // выбираем общую подложку, поверх которой выводятся включенные слои
    private Button CreateVideoForegroundDisplayButton() {
        videoForegroundDisplayOptions.Children.Add(new TextBlock {
            Text = "Подложка результата", FontWeight = FontWeight.SemiBold
        });
        string[] titles = ["Оригинал", "Результат редактирования", "Результат анализа", "Вычитание фона", "Заменённый фон"];
        RadioButton[] choices = new RadioButton[titles.Length];
        for (int index = 0; index < titles.Length; index++) {
            RadioButton choice = CreateVideoAnalysisDisplayChoice(titles[index], "VideoSequenceBackdrop", index,
                nameof(MainViewModel.SelectedVideoSequenceBackdropIndex), SelectBackdrop);
            choices[index] = choice;
            videoForegroundDisplayOptions.Children.Add(choice);
        }
        BindAnalysisPopup(choices[3], IsEnabledProperty, nameof(MainViewModel.CanUseVideoSubtractionBackdrop));
        BindAnalysisPopup(choices[4], IsEnabledProperty, nameof(MainViewModel.CanUseVideoBackgroundReplacement));
        ToolTip.SetTip(choices[3], "Доступно при включённом вычитании фона.");
        ToolTip.SetShowOnDisabled(choices[3], true);
        ToolTip.SetTip(choices[4], "Доступно при включённой замене и выбранном изображении фона.");
        ToolTip.SetShowOnDisabled(choices[4], true);
        Button button = CreateAnalysisDisplayFlyout(videoForegroundDisplayOptions);
        button.Bind(IsEnabledProperty, new Binding(nameof(MainViewModel.IsVideoOpen)));
        return button;

        void SelectBackdrop(MainViewModel model, int index) {
            model.SelectedVideoSequenceBackdropIndex = index;
            // при отказе возвращаем видимую отметку, которую снял RadioButton
            int acceptedIndex = model.SelectedVideoSequenceBackdropIndex;
            if (acceptedIndex != index) {
                choices[index].SetCurrentValue(ToggleButton.IsCheckedProperty, false);
                choices[acceptedIndex].SetCurrentValue(ToggleButton.IsCheckedProperty, true);
            }
        }
    }

    // вход и результат занимают одну область, итоговый вид выбирается в общей панели отображения
    private Border CreateVideoForegroundImageCard() {
        videoForegroundTabs.Items.Add(new TabStripItem { Content = "Вход" });
        videoForegroundTabs.Items.Add(videoForegroundResultTab);
        videoForegroundTabs.SelectedIndex = 0;
        AutomationProperties.SetName(videoForegroundTabs, "Просмотр видеоряда");
        videoForegroundDisplayButton = CreateVideoForegroundDisplayButton();

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
            ReferenceEquals(control, videoForegroundTabs) || ReferenceEquals(control, videoForegroundDisplayOptions) ||
            ReferenceEquals(control, videoForegroundSourcePicker) || ReferenceEquals(control, videoReplacementSourcePicker) ||
            ReferenceEquals(control, videoMovingBlurSourcePicker) || ReferenceEquals(control, videoMovingBlurRadius) ||
            ReferenceEquals(control, videoOpticalFlowSourcePicker) || ReferenceEquals(control, videoSubtractionRasterPicker) ||
            e.Key == Key.Space && (ReferenceEquals(control, videoForegroundDisplayButton) ||
                ReferenceEquals(control, videoForegroundMethod) || ReferenceEquals(control, videoForegroundMethodSettings) ||
                ReferenceEquals(control, videoReplacementMethod) || ReferenceEquals(control, videoReplacementSettings) ||
                ReferenceEquals(control, videoMovingBlurMethod) || ReferenceEquals(control, videoMovingBlurSettings) ||
                ReferenceEquals(control, videoOpticalFlowMethod) || ReferenceEquals(control, videoOpticalFlowSettings) ||
                ReferenceEquals(control, videoBackgroundPicker) ||
                ReferenceEquals(control, videoForegroundApply) || ReferenceEquals(control, videoForegroundPageButton)));
}
