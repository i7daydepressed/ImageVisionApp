using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private ScrollViewer? editingPage;
    private Grid? analysisPage;

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

    private StackPanel CreatePageNavigation() {
        // страницы и обработчики создаются один раз, переход меняет только pageHost
        editingPage = CreateAdvancedPage();
        analysisPage = CreateAnalysisPage();

        informationPageButton.Click += (_, _) => ShowMainPage(simplePage);
        editingPageButton.Click += (_, _) => ShowMainPage(editingPage);
        analysisPageButton.Click += (_, _) => ShowMainPage(analysisPage);

        StackPanel navigation = new StackPanel {
            Orientation = Orientation.Horizontal,
            Spacing = 8
        };
        navigation.Children.Add(informationPageButton);
        navigation.Children.Add(editingPageButton);
        navigation.Children.Add(analysisPageButton);
        return navigation;
    }

    private void ShowMainPage(Control? page) {
        if (page is null) {
            return;
        }

        if (!ReferenceEquals(pageHost.Content, page)) {
            pageHost.Content = page;
        }

        // повторный щелчок по активной кнопке оставляет ее выбранной
        informationPageButton.IsChecked = ReferenceEquals(page, simplePage);
        editingPageButton.IsChecked = ReferenceEquals(page, editingPage);
        analysisPageButton.IsChecked = ReferenceEquals(page, analysisPage);
    }
}
