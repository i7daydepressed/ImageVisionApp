using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private ScrollViewer? editingPage;
    private Grid? analysisPage;
    private Grid? videoForegroundPage;

    private readonly ToggleButton informationPageButton = new ToggleButton {
        Content = "Информация",
        IsChecked = true
    };

    private readonly ToggleButton editingPageButton = new ToggleButton {
        Content = "Редактирование"
    };

    private readonly ToggleButton analysisPageButton = new ToggleButton {
        Content = "Анализ"
    };

    private readonly ToggleButton videoForegroundPageButton = new() {
        Content = "Работа с видеорядом"
    };

    private StackPanel CreatePageNavigation() {
        // страницы и обработчики создаются один раз, переход меняет только pageHost
        editingPage = CreateAdvancedPage();
        analysisPage = CreateAnalysisPage();
        videoForegroundPage = CreateVideoForegroundPage();

        informationPageButton.Click += (_, _) => ShowMainPage(simplePage);
        editingPageButton.Click += (_, _) => ShowMainPage(editingPage);
        analysisPageButton.Click += (_, _) => ShowMainPage(analysisPage);
        videoForegroundPageButton.Click += (_, _) => {
            ShowMainPage(videoForegroundPage);
            videoForegroundPage?.Focus();
        };

        StackPanel navigation = new StackPanel {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        navigation.Children.Add(informationPageButton);
        navigation.Children.Add(editingPageButton);
        navigation.Children.Add(analysisPageButton);
        navigation.Children.Add(videoForegroundPageButton);
        Button settings = new() { Content = "⚙" };
        AutomationProperties.SetName(settings, "Настройки медиаконтейнера");
        ToolTip.SetTip(settings, "Настройки медиаконтейнера");
        settings.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoOpen)));
        settings.Click += OpenMediaSettingsButton_OnClick;
        navigation.Children.Add(settings);
        return navigation;
    }

    private void ShowMainPage(Control? page) {
        if (page is null) {
            return;
        }

        if (!ReferenceEquals(pageHost.Content, page)) {
            MainViewModel? viewModel = DataContext as MainViewModel;
            viewModel?.PauseVideoPlayback();
            pageHost.Content = page;
            viewModel?.SetVideoEditorVisible(ReferenceEquals(page, editingPage));
            UpdateAnalysisSection();
            viewModel?.SetVideoForegroundVisible(ReferenceEquals(page, videoForegroundPage));
        }

        // повторный щелчок по активной кнопке оставляет ее выбранной
        informationPageButton.IsChecked = ReferenceEquals(page, simplePage);
        editingPageButton.IsChecked = ReferenceEquals(page, editingPage);
        analysisPageButton.IsChecked = ReferenceEquals(page, analysisPage);
        videoForegroundPageButton.IsChecked = ReferenceEquals(page, videoForegroundPage);
    }
}
