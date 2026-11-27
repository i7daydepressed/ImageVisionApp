using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using ImageVisionApp.Services;

namespace ImageVisionApp.Views;

internal sealed partial class FaceRecognitionView {
    private static readonly ColorOption[] FrameColors = [
        new("Желтый", Colors.Gold), new("Бирюзовый", Colors.Turquoise), new("Зеленый", Colors.LimeGreen),
        new("Оранжевый", Colors.DarkOrange), new("Красный", Colors.OrangeRed), new("Фиолетовый", Colors.Violet),
        new("Белый", Colors.White)
    ];
    private static readonly ColorOption[] ProfileFrameColors = [new("Как у известных", null), .. FrameColors];
    private readonly StackPanel labelOptions = new() { Spacing = 3 };
    private readonly ComboBox frameColorMode = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly StackPanel commonColorOptions = new();
    private readonly StackPanel splitColorOptions = new() { Spacing = 5 };
    private readonly StackPanel profileColorOptions = new() { Spacing = 5 };
    private readonly ComboBox colorProfile = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ComboBox colorForProfile = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly TextBlock profileColorStatus = new() { TextWrapping = TextWrapping.Wrap };
    private Guid? selectedColorProfile;
    private bool parametersExpanded;
    private bool profileOptionsLoaded;
    private bool profileOptionsFailed;
    private Task profileOptionsRead = Task.CompletedTask;
    private CancellationTokenSource? stopProfileOptions;

    // цвет и подписи читают готовые совпадения и меняют только оформление предпросмотра
    private StackPanel CreateAppearanceControls() {
        FaceRecognitionAppearance appearance = preview.Appearance;
        CheckBox names = new() { Content = "Имена профилей", IsChecked = appearance.ShowProfileNames };
        CheckBox unknown = new() { Content = "Неизвестные лица", IsChecked = appearance.ShowUnknownLabels };
        names.IsCheckedChanged += (_, _) => {
            appearance.ShowProfileNames = names.IsChecked == true;
            preview.RefreshAppearance();
        };
        unknown.IsCheckedChanged += (_, _) => {
            appearance.ShowUnknownLabels = unknown.IsChecked == true;
            preview.RefreshAppearance();
        };
        labelOptions.Children.Add(names);
        labelOptions.Children.Add(unknown);

        AutomationProperties.SetName(frameColorMode, "Режим цвета рамок");
        frameColorMode.ItemsSource = new[] { "Один цвет для всех", "Известные и неизвестные", "По профилям" };
        frameColorMode.SelectedIndex = (int)appearance.ColorMode;
        frameColorMode.SelectionChanged += (_, _) => {
            int index = frameColorMode.SelectedIndex;
            if (index >= 0) {
                appearance.ColorMode = (FaceFrameColorMode)index;
                UpdateAppearanceControls();
                preview.RefreshAppearance();
            }
        };

        commonColorOptions.Children.Add(CreateColorRow("Общий цвет", "Общий цвет рамок", appearance.CommonColor,
            value => appearance.CommonColor = value));
        splitColorOptions.Children.Add(CreateColorRow("Цвет известных", "Цвет известных лиц", appearance.KnownColor,
            value => appearance.KnownColor = value));
        splitColorOptions.Children.Add(CreateColorRow("Цвет неизвестных", "Цвет неизвестных лиц", appearance.UnknownColor,
            value => appearance.UnknownColor = value));

        AutomationProperties.SetName(colorProfile, "Профиль для цвета");
        colorProfile.SelectionChanged += (_, _) => DisplaySelectedProfileColor();
        ConfigureColorPicker(colorForProfile, "Цвет профиля", ProfileFrameColors, null);
        colorForProfile.SelectionChanged += (_, _) => {
            if (colorProfile.SelectedItem is ProfileOption profile && colorForProfile.SelectedItem is ColorOption option &&
                option.Color != appearance.ReadProfileColor(profile.Id)) {
                appearance.SetProfileColor(profile.Id, option.Color);
                preview.RefreshAppearance();
            }
        };
        profileColorOptions.Children.Add(new TextBlock { Text = "Профиль" });
        profileColorOptions.Children.Add(colorProfile);
        profileColorOptions.Children.Add(SettingRow("Цвет профиля", colorForProfile));
        profileColorOptions.Children.Add(profileColorStatus);

        UpdateAppearanceControls();
        return new StackPanel { Spacing = 6, Children = {
            new Separator(), new TextBlock { Text = "Отображение", FontWeight = FontWeight.SemiBold },
            labelOptions, frameColorMode, commonColorOptions, splitColorOptions, profileColorOptions
        }};
    }

    // одна строка цвета меняет кисти готового слоя, сохраняя выбранный режим
    private Grid CreateColorRow(string caption, string accessibleName, Color selected, Action<Color> set) {
        ComboBox picker = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
        ConfigureColorPicker(picker, accessibleName, FrameColors, selected);
        picker.SelectionChanged += (_, _) => {
            if (picker.SelectedItem is ColorOption { Color: { } color }) {
                set(color);
                preview.RefreshAppearance();
            }
        };
        return SettingRow(caption, picker);
    }

    // держим подпись и палитру на одной строке в узкой панели
    private static Grid SettingRow(string caption, ComboBox picker) {
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,148"), ColumnSpacing = 4, Children = {
            new TextBlock { Text = caption, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap }
        }};
        Grid.SetColumn(picker, 1);
        row.Children.Add(picker);
        return row;
    }

    // выпадающий список показывает имя цвета и его образец, сброс оставляет словесную подсказку
    private static void ConfigureColorPicker(ComboBox picker, string accessibleName, ColorOption[] options, Color? selected) {
        AutomationProperties.SetName(picker, accessibleName);
        picker.ItemTemplate = new FuncDataTemplate<ColorOption>((option, _) => {
            StackPanel row = new() { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (option!.Color is { } color) {
                row.Children.Add(new Border { Width = 13, Height = 13, Background = new SolidColorBrush(color),
                    BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
                    VerticalAlignment = VerticalAlignment.Center });
            }
            row.Children.Add(new TextBlock { Text = option.Name });
            return row;
        });
        picker.ItemsSource = options;
        picker.SelectedItem = options.First(option => option.Color == selected);
    }

    // выбор строки показывает ее текущий цвет, одинаковые имена не связывают настройки разных ID
    private void DisplaySelectedProfileColor() {
        if (colorProfile.SelectedItem is ProfileOption profile) {
            selectedColorProfile = profile.Id;
            Color? color = preview.Appearance.ReadProfileColor(profile.Id);
            colorForProfile.SelectedItem = ProfileFrameColors.First(option => option.Color == color);
            colorForProfile.IsEnabled = true;
        }
        else {
            colorForProfile.IsEnabled = false;
        }
    }

    // только открытый режим по профилям требует прочитать список, детектор оставляет общий цвет
    private void UpdateAppearanceControls() {
        bool withProfiles = recognitionMode == FaceRecognitionMode.WithProfiles;
        FaceFrameColorMode mode = preview.Appearance.ColorMode;
        bool individual = withProfiles && mode == FaceFrameColorMode.PerProfile;
        labelOptions.IsVisible = frameColorMode.IsVisible = withProfiles;
        commonColorOptions.IsVisible = !withProfiles || mode == FaceFrameColorMode.Common;
        splitColorOptions.IsVisible = withProfiles && mode != FaceFrameColorMode.Common;
        profileColorOptions.IsVisible = individual;
        if (!active || !parametersExpanded || !individual) {
            profileOptionsFailed = false;
            stopProfileOptions?.Cancel();
            return;
        }
        if (!profileOptionsLoaded && !profileOptionsFailed && profileOptionsRead.IsCompleted) {
            profileColorStatus.Text = "Читаем список профилей…";
            profileColorStatus.IsVisible = true;
            colorProfile.IsEnabled = colorForProfile.IsEnabled = false;
            var stop = new CancellationTokenSource();
            stopProfileOptions = stop;
            profileOptionsRead = ReadColorProfilesAsync(stop);
        }
    }

    // из снимка базы оставляем в настройках только ID и имена, без сохраненных векторов
    private async Task ReadColorProfilesAsync(CancellationTokenSource stop) {
        try {
            ProfileOption[] profiles = await Task.Run(() => {
                stop.Token.ThrowIfCancellationRequested();
                var (_, catalogue) = FaceRecognitionService.LoadSource(FaceRecognitionMode.WithProfiles);
                var repeated = catalogue!.GroupBy(profile => profile.Name, StringComparer.Ordinal)
                    .Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.Ordinal);
                return catalogue!.Select((profile, index) => new ProfileOption(profile.Id, repeated.Contains(profile.Name)
                    ? $"№{index + 1} · {profile.Name}" : profile.Name)).ToArray();
            }, stop.Token);
            if (!stop.IsCancellationRequested) {
                Guid? previous = selectedColorProfile;
                profileOptionsLoaded = true;
                colorProfile.ItemsSource = profiles;
                colorProfile.SelectedItem = profiles.FirstOrDefault(profile => profile.Id == previous) ?? profiles.FirstOrDefault();
                colorProfile.IsEnabled = profiles.Length > 0;
                profileColorStatus.Text = profiles.Length == 0 ? "Список профилей пуст." : "";
                profileColorStatus.IsVisible = profiles.Length == 0;
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) {
        }
        catch (Exception error) {
            if (!stop.IsCancellationRequested) {
                profileOptionsFailed = true;
                profileColorStatus.Text = error.Message;
                profileColorStatus.IsVisible = true;
            }
        }
        finally {
            bool cancelled = stop.IsCancellationRequested;
            stopProfileOptions = null;
            stop.Dispose();
            if (cancelled) {
                Dispatcher.UIThread.Post(UpdateAppearanceControls);
            }
        }
    }

    // после посещения базы заново читаем список, а назначенные по ID цвета остаются
    private void ClearColorProfiles() {
        stopProfileOptions?.Cancel();
        profileOptionsLoaded = false;
        profileOptionsFailed = false;
        colorProfile.ItemsSource = null;
        colorProfile.IsEnabled = colorForProfile.IsEnabled = false;
    }

    private sealed record ColorOption(string Name, Color? Color) {
        // имя остается читаемым для штатного выбора и средств доступности
        public override string ToString() => Name;
    }

    private sealed record ProfileOption(Guid Id, string Name) {
        // комбо использует подписанное имя, при одинаковых именах в нем есть номер
        public override string ToString() => Name;
    }
}
