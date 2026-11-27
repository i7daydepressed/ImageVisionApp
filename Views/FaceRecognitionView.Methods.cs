using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using ImageVisionApp.Services;

namespace ImageVisionApp.Views;

internal sealed partial class FaceRecognitionView {
    private bool methodEnabled = true;
    private FaceRecognitionMode recognitionMode = FaceRecognitionMode.WithProfiles;

    // оставляем переключатель метода и его параметры в отдельной карточке рядом с предпросмотром
    private Border CreateMethodsCard() {
        CheckBox enabled = new() { Content = "Распознавание лиц", IsChecked = true };
        Button settings = new() { Content = "Параметры", VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(settings, "Параметры распознавания лиц");
        Grid row = new() { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 4, Children = { enabled } };
        Grid.SetColumn(settings, 1);
        row.Children.Add(settings);

        RadioButton detect = new() { Content = "Только обнаружение", GroupName = "FaceRecognitionMode" };
        RadioButton match = new() { Content = "С профилями", GroupName = "FaceRecognitionMode", IsChecked = true };
        StackPanel parameters = new() { Spacing = 4, IsVisible = false, Children = {
            new TextBlock { Text = "Режим распознавания" }, detect, match, CreateAppearanceControls()
        }};
        StackPanel methods = new() { Spacing = 8, Children = {
            new TextBlock { Text = "Методы лиц", FontSize = 17, FontWeight = FontWeight.Bold }, row, parameters
        }};
        settings.Click += (_, _) => {
            parameters.IsVisible = parametersExpanded = !parametersExpanded;
            UpdateAppearanceControls();
        };
        enabled.Click += (_, _) => SetMethod(enabled.IsChecked == true, recognitionMode);
        detect.Click += (_, _) => SetMethod(methodEnabled, FaceRecognitionMode.DetectOnly);
        match.Click += (_, _) => SetMethod(methodEnabled, FaceRecognitionMode.WithProfiles);
        return new Border {
            Padding = new Thickness(12), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Child = new ScrollViewer {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = methods
            }
        };
    }

    // сразу снимаем прежний слой и владельца, а новый запуск берет текущий оригинал
    private void SetMethod(bool enabled, FaceRecognitionMode mode) {
        if (methodEnabled == enabled && recognitionMode == mode) {
            return;
        }
        methodEnabled = enabled;
        recognitionMode = mode;
        UpdateAppearanceControls();
        ClearResult();
        UpdateVisiblePane();
        UpdateInput();
        SyncVideoMode();
        QueueRefresh();
    }
}
