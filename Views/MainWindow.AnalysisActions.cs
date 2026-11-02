using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private async void RunAnalysisButton_OnClick(object? sender, RoutedEventArgs e) {
        if (DataContext is MainViewModel viewModel) {
            await viewModel.RunSelectedAnalysisAsync();
        }
    }

    private async void SaveAnalysisResultButton_OnClick(object? sender, RoutedEventArgs e) {
        if (DataContext is not MainViewModel viewModel) {
            return;
        }
        // фиксируем именно показанную композицию, повторного вызова детектора здесь нет
        byte[]? png = viewModel.CaptureAnalysisResultPng();
        if (png is null) {
            return;
        }
        long revision = viewModel.AnalysisResultRevision;
        string fileName = viewModel.CurrentImageInfo?.FileName ?? "image";
        FilePickerFileType pngType = new FilePickerFileType("PNG") { Patterns = ["*.png"] };
        try {
            IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {
                Title = "Сохранить результат анализа",
                SuggestedFileName = $"{Path.GetFileNameWithoutExtension(fileName)}_analysis.png",
                DefaultExtension = "png",
                FileTypeChoices = [pngType],
                SuggestedFileType = pngType
            });
            if (file is null) {
                return;
            }
            if (!viewModel.CanSaveAnalysisResult || revision != viewModel.AnalysisResultRevision) {
                viewModel.ShowError("Результат анализа изменился, повторите сохранение");
                return;
            }
            // после подтверждения сохраняем зафиксированный PNG, изменения UI не меняют его байты
            await using Stream stream = await file.OpenWriteAsync();
            if (stream.CanSeek) {
                stream.SetLength(0);
            }
            await stream.WriteAsync(png);
            await stream.FlushAsync();
            viewModel.StatusMessage = $"Результат анализа сохранен: {file.Name}";
        }
        catch (Exception exception) {
            viewModel.ShowError($"Не удалось сохранить результат анализа: {exception.Message}");
        }
    }
}
