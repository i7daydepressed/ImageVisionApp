using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using ImageVisionApp.ViewModels;
using ImageVisionApp.Views;

namespace ImageVisionApp;

public class App : Application {
    public override void Initialize() {
        // "Default" follows the system theme variant.
        RequestedThemeVariant = ThemeVariant.Default;
        DataTemplates.Add(new ViewLocator());
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            desktop.MainWindow = new MainWindow {
                DataContext = new MainViewModel(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
