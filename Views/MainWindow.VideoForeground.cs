using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
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
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private readonly TabStrip videoForegroundTabs = new() { HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TabStripItem videoForegroundResultTab = new() { Content = "Результат", IsEnabled = false };
    private readonly ListBox videoForegroundViews = new();
    private readonly ComboBox videoForegroundSourcePicker = CreateVideoForegroundSourcePicker("Источник вычитания фона");
    private readonly ComboBox videoReplacementSourcePicker = CreateVideoForegroundSourcePicker("Источник замены фона");
    private readonly CheckBox videoForegroundMethod = new() { Content = "Вычитание фона" };
    private readonly Button videoForegroundMethodSettings = new() { Content = "Параметры", VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox videoReplacementMethod = new() { Content = "Замена фона" };
    private readonly Button videoReplacementSettings = new() { Content = "Параметры", VerticalAlignment = VerticalAlignment.Center };
    private readonly Button videoBackgroundPicker = new() { Content = "Выбрать фон…" };
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
            Content = new StackPanel { Spacing = 8, Children = { methodRow, replacementRow, hint } }
        };

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
        StackPanel replacementDetails = CreateVideoBackgroundSettings();
        void ShowReplacementSettings() {
            parametersTitle.Text = "Параметры замены фона";
            parametersContent.Content = replacementDetails;
        }
        videoReplacementSettings.Click += (_, _) => ShowReplacementSettings();
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

    // оба параметра меняют один источник, по которому MOG2 строит маску
    private static ComboBox CreateVideoForegroundSourcePicker(string label) {
        ComboBox picker = new() {
            ItemsSource = new[] { "Оригинал", "Результат редактирования", "Результат анализа" },
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        AutomationProperties.SetName(picker, label);
        picker.Bind(SelectingItemsControl.SelectedIndexProperty,
            new Binding(nameof(MainViewModel.SelectedVideoForegroundSourceIndex)) { Mode = BindingMode.TwoWay });
        return picker;
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
            new TextBlock { Text = "Источник" }, videoReplacementSourcePicker, notice } };
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

    // вход и результат занимают одну область, итоговый вид выбирается в общей панели отображения
    private Border CreateVideoForegroundImageCard() {
        videoForegroundTabs.Items.Add(new TabStripItem { Content = "Вход" });
        videoForegroundTabs.Items.Add(videoForegroundResultTab);
        videoForegroundTabs.SelectedIndex = 0;
        AutomationProperties.SetName(videoForegroundTabs, "Просмотр вычитания фона");
        AutomationProperties.SetName(videoForegroundViews, "Вид результата вычитания фона");
        void AddView(string title, string enabledProperty) {
            ListBoxItem item = new() { Content = title };
            BindAnalysisPopup(item, IsEnabledProperty, enabledProperty);
            videoForegroundViews.Items.Add(item);
        }
        AddView("Передний план", nameof(MainViewModel.VideoForegroundEnabled));
        AddView("Маска", nameof(MainViewModel.VideoForegroundEnabled));
        AddView("Заменённый фон", nameof(MainViewModel.CanUseVideoBackgroundReplacement));
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
            ReferenceEquals(control, videoForegroundSourcePicker) || ReferenceEquals(control, videoReplacementSourcePicker) ||
            e.Key == Key.Space && (ReferenceEquals(control, videoForegroundDisplayButton) ||
                ReferenceEquals(control, videoForegroundMethod) || ReferenceEquals(control, videoForegroundMethodSettings) ||
                ReferenceEquals(control, videoReplacementMethod) || ReferenceEquals(control, videoReplacementSettings) ||
                ReferenceEquals(control, videoBackgroundPicker) ||
                ReferenceEquals(control, videoForegroundApply) || ReferenceEquals(control, videoForegroundPageButton)));
}
