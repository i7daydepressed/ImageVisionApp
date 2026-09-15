using System;
using System.IO;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
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
}