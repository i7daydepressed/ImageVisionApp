using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using ImageVisionApp.Models;
using ImageVisionApp.Services;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    private bool openingMedia;
    private bool mediaWindowClosed;

    // <IFilePickerService, реализацию FilePickerService>
    // выбираем фото или видео и заменяем документ только после подтверждения и подготовки
    private async void OpenMediaButton_OnClick(object? sender, RoutedEventArgs e) {
        if (openingMedia || DataContext is not MainViewModel viewModel) {
            return;
        }

        openingMedia = true;
        try {
            IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {
                Title = "Открыть файл",
                AllowMultiple = false,
                FileTypeFilter = [
                    new FilePickerFileType("Фото и видео") {
                        Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.webp", "*.mp4", "*.mkv"]
                    }
                ]
            });
            if (files.Count == 0 || mediaWindowClosed || !ReferenceEquals(DataContext, viewModel)) {
                return;
            }

            IStorageFile file = files[0];
            if (viewModel.CurrentDocument is not null && !await ConfirmReplaceDocumentAsync()) {
                return;
            }

            if (mediaWindowClosed || !ReferenceEquals(DataContext, viewModel)) {
                return;
            }

            if (Path.GetExtension(file.Name).ToLowerInvariant() is ".mp4" or ".mkv") {
                string sourcePath = file.TryGetLocalPath()
                    ?? throw new InvalidDataException("Для открытия видео нужен доступный локальный путь к файлу");
                (byte[] previewPng, long? fileSizeBytes, VideoFileIdentity? identity) = await Task.Run(() => {
                    VideoFileIdentity? current = VideoFileIdentity.TryCapture(sourcePath);
                    byte[] png = VideoFirstFrameReader.ReadPng(sourcePath);
                    current?.EnsureCurrent(sourcePath);
                    return (png, current?.Length ?? ReadVideoFileSize(sourcePath), current);
                });
                if (!mediaWindowClosed && ReferenceEquals(DataContext, viewModel)) {
                    viewModel.LoadVideoFirstFrame(previewPng, file.Name, sourcePath, fileSizeBytes, identity);
                }
            }
            else {
                await using Stream inputStream = await file.OpenReadAsync();
                using MemoryStream memoryStream = new();
                await inputStream.CopyToAsync(memoryStream);
                if (!mediaWindowClosed && ReferenceEquals(DataContext, viewModel)) {
                    viewModel.LoadImage(memoryStream.ToArray(), file.Name);
                }
            }
        }
        catch (Exception exception) {
            if (!mediaWindowClosed && ReferenceEquals(DataContext, viewModel)) {
                viewModel.ShowError($"Не удалось открыть файл: {exception.Message}");
            }
        }
        finally {
            openingMedia = false;
        }
    }

    // читаем размер исходного файла, ошибка этого поля не мешает открыть видео
    private static long? ReadVideoFileSize(string sourcePath) {
        try {
            return new FileInfo(sourcePath).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
            return null;
        }
    }

    // предупреждаем о сбросе перед заменой файла и возвращаем явное согласие
    private Task<bool> ConfirmReplaceDocumentAsync() {
        Window confirmation = new() {
            Title = "Заменить открытый файл?",
            Width = 440,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        Button cancel = new() { Content = "Отмена", IsCancel = true };
        cancel.Click += (_, _) => confirmation.Close(false);
        Button open = new() { Content = "Открыть файл" };
        open.Click += (_, _) => confirmation.Close(true);
        confirmation.Content = new StackPanel {
            Margin = new Thickness(16),
            Spacing = 12,
            Children = {
                new TextBlock {
                    Text = "После успешного открытия нового файла очередь действий и результаты анализа будут сброшены.",
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel {
                    Spacing = 8,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Orientation = Orientation.Horizontal,
                    Children = { cancel, open }
                }
            }
        };
        return confirmation.ShowDialog<bool>(this);
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

    [Obsolete("Отдельное окно гистограмм отключено. Используйте встроенные панели.")]
    private async void OpenHistogramsButton_OnClick(
        object? sender,
        RoutedEventArgs e
        ) {

        if (DataContext is not MainViewModel viewModel ||
            viewModel.CurrentDocument?.Kind != MediaKind.Image) {
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
