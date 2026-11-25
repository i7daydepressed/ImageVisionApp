using System.Linq;
using System.Threading.Tasks;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private ScrollViewer? editingPage;
    private Grid? analysisPage;
    private Grid? videoForegroundPage;
    private FaceDatabaseView? facesPage;
    private bool facesCloseConfirmed;
    private bool facesClosePending;
    private bool IsFacesPageVisible => facesPage is not null && ReferenceEquals(pageHost.Content, facesPage);
    private bool IsFacesDatabaseVisible => IsFacesPageVisible && facesPage!.ShowsDatabase;

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

    private readonly ToggleButton facesPageButton = new() {
        Content = new PathIcon {
            Width = 19, Height = 19,
            Data = Geometry.Parse("M12,1 A11,11 0 1 0 12,23 A11,11 0 1 0 12,1 Z M12,3 A9,9 0 1 1 12,21 A9,9 0 1 1 12,3 Z M8,8 A1.4,1.4 0 1 0 8,10.8 A1.4,1.4 0 1 0 8,8 Z M16,8 A1.4,1.4 0 1 0 16,10.8 A1.4,1.4 0 1 0 16,8 Z M7.5,13 Q12,18 16.5,13 L18,14 Q12,21 6,14 Z")
        }
    };

    private StackPanel CreatePageNavigation() {
        // страницы и обработчики создаются один раз, переход меняет только pageHost
        editingPage = CreateAdvancedPage();
        analysisPage = CreateAnalysisPage();
        videoForegroundPage = CreateVideoForegroundPage();
        facesPage = new FaceDatabaseView(this);
        facesPage.SectionChanged += UpdateFacesSection;

        informationPageButton.Click += async (_, _) => await NavigateMainPageAsync(simplePage);
        editingPageButton.Click += async (_, _) => await NavigateMainPageAsync(editingPage);
        analysisPageButton.Click += async (_, _) => await NavigateMainPageAsync(analysisPage);
        videoForegroundPageButton.Click += async (_, _) => {
            if (await NavigateMainPageAsync(videoForegroundPage)) {
                videoForegroundPage?.Focus();
            }
        };
        AutomationProperties.SetName(facesPageButton, "Лица");
        ToolTip.SetTip(facesPageButton, "Лица");
        facesPageButton.Click += (_, _) => ShowMainPage(facesPage);

        StackPanel navigation = new StackPanel {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        navigation.Children.Add(informationPageButton);
        navigation.Children.Add(editingPageButton);
        navigation.Children.Add(analysisPageButton);
        navigation.Children.Add(videoForegroundPageButton);
        navigation.Children.Add(facesPageButton);
        Button settings = new() { Content = "⚙" };
        AutomationProperties.SetName(settings, "Настройки медиаконтейнера");
        ToolTip.SetTip(settings, "Настройки медиаконтейнера");
        settings.Bind(IsVisibleProperty, new Binding(nameof(MainViewModel.IsVideoOpen)));
        settings.Click += OpenMediaSettingsButton_OnClick;
        Border settingsHost = new() { Child = settings };
        navigation.Children.Add(settingsHost);
        return navigation;
    }

    // не переключаем вкладку, пока пользователь не подтвердил сброс или идет запись
    private async Task<bool> NavigateMainPageAsync(Control? page) {
        if (IsFacesPageVisible && !ReferenceEquals(page, facesPage)) {
            UpdateMainPageButtons(facesPage);
            if (!await facesPage!.ConfirmLeaveAsync() || mediaWindowClosed) {
                return false;
            }
        }
        ShowMainPage(page);
        return page is not null;
    }

    // сохраняем экземпляры страниц и при переходе меняем только видимость их работы
    private void ShowMainPage(Control? page) {
        if (page is null) {
            return;
        }

        if (!ReferenceEquals(pageHost.Content, page)) {
            bool wasFaces = IsFacesPageVisible;
            MainViewModel? viewModel = DataContext as MainViewModel;
            viewModel?.PauseVideoPlayback();
            if (wasFaces) {
                facesPage!.Deactivate();
            }
            pageHost.Content = page;
            if (wasFaces || IsFacesPageVisible) {
                UpdateFacesNavigation();
            }
            viewModel?.SetVideoEditorVisible(ReferenceEquals(page, editingPage));
            UpdateAnalysisSection();
            viewModel?.SetVideoForegroundVisible(ReferenceEquals(page, videoForegroundPage));
        }

        UpdateMainPageButtons(page);
    }

    // повторный щелчок по активной кнопке оставляет ее выбранной
    private void UpdateMainPageButtons(Control? page) {
        informationPageButton.IsChecked = ReferenceEquals(page, simplePage);
        editingPageButton.IsChecked = ReferenceEquals(page, editingPage);
        analysisPageButton.IsChecked = ReferenceEquals(page, analysisPage);
        videoForegroundPageButton.IsChecked = ReferenceEquals(page, videoForegroundPage);
        facesPageButton.IsChecked = IsFacesPageVisible;
    }

    // снимаем удержание клавиш и передаем экрану тот же документ окна
    private void UpdateFacesNavigation() {
        videoSpaceHeld = false;
        videoPreviewUpHeld = false;
        if (IsFacesPageVisible) {
            videoDragDocument = null;
            facesPage!.Activate();
        }
    }

    // скрываем общий таймлайн в базе и не переносим туда удержание клавиш
    private void UpdateFacesSection() {
        videoSpaceHeld = videoPreviewUpHeld = false;
        videoDragDocument = null;
        UpdateAnalysisSection();
    }

    // пробел на входе в «Лица» остается командой кнопки при открытом видео
    private bool IsFacesNavigationInput(object? source) =>
        source is Avalonia.Visual visual && visual.GetSelfAndVisualAncestors().Contains(facesPageButton);

    // откладываем закрытие до решения по черновику и освобождения распознавания
    private async void ConfirmFacesClose(object? sender, WindowClosingEventArgs args) {
        if (facesCloseConfirmed || facesPage is not { } faces) {
            return;
        }
        bool decision = IsFacesPageVisible && faces.NeedsLeaveDecision;
        if (!decision && !faces.HasRecognitionWork && !facesClosePending) {
            return;
        }
        args.Cancel = true;
        if (facesClosePending) {
            return;
        }
        facesClosePending = true;
        try {
            if (decision && !await faces.ConfirmLeaveAsync()) {
                return;
            }
            await faces.StopRecognitionAsync();
            await Task.Yield();
            facesCloseConfirmed = true;
            if (!mediaWindowClosed) {
                Close();
            }
        }
        finally {
            facesClosePending = false;
        }
    }
}
