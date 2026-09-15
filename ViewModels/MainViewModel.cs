using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageVisionApp.Models;
using ImageVisionApp.Services;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel : ViewModelBase{
    private readonly ImageStatsFileService imageStatsFileService = new();
    private readonly ImageProcessingService imageProcessingService= new();

    // исходник изоб
    private byte[]? originalImageData;// для magick

    [ObservableProperty] //когда значение свойства изменилось, нужно уведомить интерфейс - тот подставит значение в внутренние созданные классы для отображения/работы визуализации
    public partial Bitmap? OriginalImage { get; set; }

    [ObservableProperty]
    public partial Bitmap? ModifiedImage { get; set; }

    [ObservableProperty]
    public partial ImageInfo? CurrentImageInfo { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }
        = "Изображение не выбрано";

    // 0 — исходная яркость без изменений
    [ObservableProperty]
    public partial double BrightnessAdjustment { get; set; }= 0;

    // 100 — исходная насыщенность изображения
    [ObservableProperty]
    public partial double SaturationPercentage { get; set; } = 100;

    // 0 — исходная контрастность без изменений
    [ObservableProperty]
    public partial double ContrastAdjustment { get; set; }= 0;

    public void LoadImage(byte[] imageData, string fileName){//реактим на событие загрузки збр

        // сохр исхд байты
        originalImageData = imageData;
        
        var newImageInfo =
                imageStatsFileService.readImageInfo(imageData, fileName);

        using var originalStream = new MemoryStream(imageData);
        using var modifiedStream = new MemoryStream(imageData);

        var newOriginalImage = new Bitmap(originalStream);
        var newModifiedImage = new Bitmap(modifiedStream);

        OriginalImage?.Dispose();
        ModifiedImage?.Dispose();

        OriginalImage = newOriginalImage;
        ModifiedImage = newModifiedImage;
        CurrentImageInfo = newImageInfo;
        StatusMessage = fileName;
    }

    public void ConvertImageToGrayscale(){
        if (originalImageData is null){
            StatusMessage = "Сначала выберите изображение";
            return;
        }

        // originalImageData - данные до изменения ползунков - любое изменение ползунков будет браться относительно ориг даты

        // наше серое изобрж
        byte[] grayscaleImageData = imageProcessingService.ConvertToGrayscale(originalImageData);

        // avalonia Bitmap принимает поток поэтому оборачиваем полученные байты в memstream
        using MemoryStream grayscaleImageStream = new MemoryStream(grayscaleImageData);

        Bitmap newModifiedImage = new Bitmap(grayscaleImageStream);

        ModifiedImage?.Dispose();

        // подставляем новое в вкладку
        ModifiedImage = newModifiedImage;
    }

    public void ShowError(string message)
    {
        StatusMessage = message;
    }
}