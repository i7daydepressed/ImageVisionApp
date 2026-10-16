using Avalonia.Controls;
using Avalonia.Layout;

namespace ImageVisionApp.Controls;

public sealed partial class StructuralElementEditor {
    private TabControl? settingsTabs;
    private TabControl? matrixTabs;
    private int selectedMatrixMode;
    private bool synchronizingMatrixTabs;

    private static TabControl CreateMatrixTabs(Control morphology, Control filter) {
        return new TabControl {
            VerticalAlignment = VerticalAlignment.Top,
            Items = {
                new TabItem { Header = new TextBlock { Text = "Морфология", FontSize = 15 }, Content = morphology },
                new TabItem { Header = new TextBlock { Text = "Числовой фильтр", FontSize = 15 }, Content = filter }
            },
            SelectedIndex = 0
        };
    }

    private void SelectMatrixMode(int index) {
        if (synchronizingMatrixTabs || index is < 0 or > 1) {
            return;
        }
        synchronizingMatrixTabs = true;
        try {
            EndBrushStroke();
            selectedMatrixMode = index;
            if (settingsTabs is not null) {
                settingsTabs.SelectedIndex = index;
            }
            if (matrixTabs is not null) {
                matrixTabs.SelectedIndex = index;
            }
            // ячейки общие, их содержимое при переключении не меняется
            SetSlotsStatus(string.Empty);
        }
        finally {
            synchronizingMatrixTabs = false;
        }
    }
}
