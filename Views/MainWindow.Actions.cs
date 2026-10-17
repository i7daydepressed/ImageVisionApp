using System;
using System.Collections.Generic;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ImageVisionApp.Models;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    // <IFilePickerService, реализацию FilePickerService>
    private async void OpenImageButton_OnClick(
        object? sender,
        RoutedEventArgs e
        ) {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Открыть изображение",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Изображения") {
                        Patterns =
                        [
                            "*.jpg",
                            "*.jpeg",
                            "*.png",
                            "*.bmp",
                            "*.webp"
                        ]
                    }
                ]
            });

        if (files.Count == 0)
            return;

        try {
            await using Stream inputStream = await files[0].OpenReadAsync();
            using MemoryStream memoryStream = new MemoryStream();

            await inputStream.CopyToAsync(memoryStream);

            if (DataContext is MainViewModel viewModel) {
                viewModel.LoadImage(//грузим
                    memoryStream.ToArray(),
                    files[0].Name);
            }
        }
        catch (Exception exception) {
            if (DataContext is MainViewModel viewModel) {
                viewModel.ShowError(
                    $"Не удалось открыть изображение: {exception.Message}");
            }
        }
    }
    // </IFilePickerService, реализацию FilePickerService>

    // кнопка расширенного режима сохраняет результат очереди AdvancedSteps
    private async void SaveAdvancedResultButton_OnClick(
        object? sender,
        RoutedEventArgs e
        ) {
        await SaveResultAsync();
    }

    // строим результат очереди, затем показываем диалог и записываем пнг в файл
    private async System.Threading.Tasks.Task SaveResultAsync() {

        if (DataContext is not MainViewModel viewModel) {
            return;
        }

        try {
            // сохранение применяет те же шаги, что и предпросмотр
            byte[]? processedImageData =
                viewModel.CreateAdvancedProcessedImageData();

            if (processedImageData is null) {
                return;
            }

            string originalFileName =
                viewModel.CurrentImageInfo?.FileName ?? "image";
            string suggestedFileName =
                $"{Path.GetFileNameWithoutExtension(originalFileName)}_result.png";
            FilePickerFileType pngFileType =
                new FilePickerFileType("PNG") {
                    Patterns = ["*.png"]
                };
            FilePickerSaveOptions saveOptions =
                new FilePickerSaveOptions {
                    Title = "Сохранить результат",
                    SuggestedFileName = suggestedFileName,
                    DefaultExtension = "png",
                    FileTypeChoices = [pngFileType],
                    SuggestedFileType = pngFileType
                };

            IStorageFile? outputFile =
                await StorageProvider.SaveFilePickerAsync(saveOptions);

            if (outputFile is null) {
                return;
            }

            await using Stream outputStream =
                await outputFile.OpenWriteAsync();

            await outputStream.WriteAsync(processedImageData);

            viewModel.StatusMessage =
                $"Результат сохранён: {outputFile.Name}";
        }
        catch (Exception exception) {
            viewModel.ShowError(
                $"Не удалось сохранить результат: " +
                exception.Message);
        }
    }

    private async void OpenHistogramsButton_OnClick(
        object? sender,
        RoutedEventArgs e
        ) {

        if (DataContext is not MainViewModel viewModel) {
            return;
        }

        try {
            ImageHistogramComparison? comparison =
                viewModel.CreateHistogramComparison();

            if (comparison is null) {
                return;
            }

            HistogramWindow histogramWindow =
                new HistogramWindow(comparison);

            await histogramWindow.ShowDialog(this);
        }
        catch (Exception exception) {
            viewModel.ShowError(
                $"Не удалось открыть гистограммы: " +
                exception.Message);
        }
    }
}
