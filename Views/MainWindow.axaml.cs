using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ImageVisionApp.Models;
using ImageVisionApp.ViewModels;

namespace ImageVisionApp.Views;

public partial class MainWindow : Window
{
    public MainWindow(){
        InitializeComponent();
    }
// <IFilePickerService, реализацию FilePickerService>
    private async void OpenImageButton_OnClick(
        object? sender,
        RoutedEventArgs e
        ){
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Открыть изображение",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Изображения")
                    {
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

        try{
            await using var inputStream = await files[0].OpenReadAsync();
            using var memoryStream = new MemoryStream();

            await inputStream.CopyToAsync(memoryStream);

            if (DataContext is MainViewModel viewModel)
            {
                viewModel.LoadImage(//грузим
                    memoryStream.ToArray(),
                    files[0].Name);
            }
        }
        catch (Exception exception){
            if (DataContext is MainViewModel viewModel){
                viewModel.ShowError(
                    $"Не удалось открыть изображение: {exception.Message}");
            }
        }
    }
    // </IFilePickerService, реализацию FilePickerService>
    
    private void ConvertToGrayscaleButton_OnClick(
        object? sender,
        RoutedEventArgs e
        ){
        if (DataContext is not MainViewModel viewModel){
            return;
        }

        try{
            viewModel.ConvertImageToGrayscale();
        }
        catch (Exception exception){
            viewModel.ShowError(
                $"Не удалось перевести изображение в градации серого: " +
                exception.Message);
        }
    }

    private void ResetBrightnessButton_OnClick(
        object? sender,
        RoutedEventArgs e
        ) {

        if (DataContext is not MainViewModel viewModel) {
            return;
        }

        try {
            viewModel.ResetBrightness();
        }
        catch (Exception exception) {
            viewModel.ShowError(
                $"Не удалось сбросить яркость: " +
                exception.Message);
        }
    }

    private void ResetSaturationButton_OnClick(
        object? sender,
        RoutedEventArgs e
        ) {

        if (DataContext is not MainViewModel viewModel) {
            return;
        }

        try {
            viewModel.ResetSaturation();
        }
        catch (Exception exception) {
            viewModel.ShowError(
                $"Не удалось сбросить насыщенность: " +
                exception.Message);
        }
    }

    private void ResetContrastButton_OnClick(
        object? sender,
        RoutedEventArgs e
        ) {

        if (DataContext is not MainViewModel viewModel) {
            return;
        }

        try {
            viewModel.ResetContrast();
        }
        catch (Exception exception) {
            viewModel.ShowError(
                $"Не удалось сбросить контрастность: " +
                exception.Message);
        }
    }

    private void RotateClockwiseButton_OnClick(
        object? sender,
        RoutedEventArgs e
        ) {

        if (DataContext is not MainViewModel viewModel) {
            return;
        }

        try {
            viewModel.RotateClockwise();
        }
        catch (Exception exception) {
            viewModel.ShowError(
                $"Не удалось повернуть изображение: " +
                exception.Message);
        }
    }

    private void ResetAllTransformationsButton_OnClick(
        object? sender,
        RoutedEventArgs e
        ) {

        if (DataContext is not MainViewModel viewModel) {
            return;
        }

        try {
            viewModel.ResetAllTransformations();
        }
        catch (Exception exception) {
            viewModel.ShowError(
                $"Не удалось сбросить изменения: " +
                exception.Message);
        }
    }

    private async void SaveResultButton_OnClick(
        object? sender,
        RoutedEventArgs e
        ) {

        if (DataContext is not MainViewModel viewModel) {
            return;
        }

        try {
            byte[]? processedImageData =
                viewModel.CreateProcessedImageData();

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