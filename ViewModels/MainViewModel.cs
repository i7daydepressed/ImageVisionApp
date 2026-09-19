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

    // 100 — исходная насыщенность
    [ObservableProperty]
    public partial double SaturationPercentage { get; set; } = 100;

    // 0 — исходная контрастность без изменений
    [ObservableProperty]
    public partial double ContrastAdjustment { get; set; }= 0;

    // false — изображение цветное
    // true — применяются градации серого
    [ObservableProperty]
    public partial bool IsGrayscaleEnabled { get; set; } = false;



    public void LoadImage(byte[] imageData, string fileName){//реактим на событие загрузки збр

        // сохр исхд байты
        originalImageData = imageData;
        // Новый файл открывается без преобразований предыдущего.
        IsGrayscaleEnabled = false;// хз по идее надо джсончике параметры сохранять
        
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

    private void updateModifiedImage() {
        if (originalImageData is null){
            return;
        }

        // собираем результат заново с учетом всех настроек
        byte[] processedImageData =
            imageProcessingService.ApplyTransformations(//применяем фильтры
                originalImageData,
                IsGrayscaleEnabled);

        using MemoryStream processedImageStream =
            new MemoryStream(processedImageData);

        // avalonia Bitmap принимает поток поэтому оборачиваем полученные байты в memstream
        Bitmap newModifiedImage =
            new Bitmap(processedImageStream);

        ModifiedImage?.Dispose();

        ModifiedImage = newModifiedImage;
    }

    public void ConvertImageToGrayscale() {
        if (originalImageData is null) {
            StatusMessage = "Сначала выберите изображение";
            return;
        }

        // серое енаблед
        IsGrayscaleEnabled = !IsGrayscaleEnabled;
        // пересобираем
        updateModifiedImage();
    }


    public void ShowError(string message)
    {
        StatusMessage = message;
    }
}