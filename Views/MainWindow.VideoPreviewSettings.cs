using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using ImageVisionApp.Models;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private bool mediaSettingsOpen;

    // запоминаем выбранные настройки окна только после явного применения
    private async void OpenMediaSettingsButton_OnClick(object? sender, RoutedEventArgs e) {
        if (mediaSettingsOpen || DataContext is not MainViewModel { IsVideoOpen: true } viewModel) {
            return;
        }

        mediaSettingsOpen = true;
        try {
            VideoPreviewSettings? settings = await ShowVideoPreviewSettingsAsync(viewModel.PreviewSettings);
            if (settings is { } applied && !mediaWindowClosed && ReferenceEquals(DataContext, viewModel)) {
                viewModel.ApplyVideoPreviewSettings(applied);
            }
        }
        catch (Exception exception) {
            if (!mediaWindowClosed && ReferenceEquals(DataContext, viewModel)) {
                viewModel.ShowError($"Не удалось открыть настройки медиаконтейнера: {exception.Message}");
            }
        }
        finally {
            mediaSettingsOpen = false;
        }
    }

    // редактируем копию значений, отмена и закрытие окна не меняют действующие пределы
    private System.Threading.Tasks.Task<VideoPreviewSettings?> ShowVideoPreviewSettingsAsync(VideoPreviewSettings settings) {
        Window dialog = new() {
            Title = "Настройки медиаконтейнера",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        int[] budgets = [16, 32, 64, 128, 256];
        ComboBox workers = new() {
            ItemsSource = new[] { "Авто", "1", "2", "3", "4" },
            SelectedIndex = settings.MaximumWorkers,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ComboBox budget = new() {
            ItemsSource = budgets,
            SelectedIndex = Array.IndexOf(budgets, settings.BudgetMiB),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Button cancel = new() { Content = "Отмена", IsCancel = true };
        Button apply = new() { Content = "Применить", IsDefault = true };
        cancel.Click += (_, _) => dialog.Close(null);
        apply.Click += (_, _) => {
            if (workers.SelectedIndex >= 0 && budget.SelectedIndex >= 0) {
                dialog.Close(new VideoPreviewSettings(workers.SelectedIndex, budgets[budget.SelectedIndex]));
            }
        };
        dialog.Content = new StackPanel {
            Margin = new Thickness(16),
            Spacing = 8,
            Children = {
                new TextBlock { Text = "Максимум обработчиков" }, workers,
                new TextBlock { Text = "Бюджет буферов предпросмотра, МиБ" }, budget,
                new StackPanel {
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Orientation = Orientation.Horizontal,
                    Children = { cancel, apply }
                }
            }
        };
        return dialog.ShowDialog<VideoPreviewSettings?>(this);
    }
}
