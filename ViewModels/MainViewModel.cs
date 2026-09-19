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
    private bool suppressTransformationUpdates;//флаг запрещающий автообновляться редактируемой картинки сразу после изменения одного какогото ползунка

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
    // авалония автоприсваивает сюда значение по изменению ползунка
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
        IsGrayscaleEnabled = false;
        BrightnessAdjustment = 0;
        SaturationPercentage = 100;
        ContrastAdjustment = 0;
        
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

    private void UpdateModifiedImage() {
        if (originalImageData is null){
            return;
        }
        ImageTransformationSettings settings =
            new ImageTransformationSettings {
                IsGrayscaleEnabled = IsGrayscaleEnabled,
                BrightnessAdjustment = BrightnessAdjustment
            };

        // собираем результат заново с учетом всех настроек
        byte[] processedImageData =
            imageProcessingService.ApplyTransformations(//применяем фильтры
                originalImageData,
                settings);//фильтры

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
        UpdateModifiedImage();
    }
    
    // автоматически вызывается после перемещения ползунка 
    // в хамле Value="{Binding BrightnessAdjustment, Mode=TwoWay}"
    // реализуем предусмотренную генератором точку расширения 
    // условно это компилится в это
    // public double BrightnessAdjustment {
    //     get {
    //         return brightnessAdjustment;
    //     }

    //     set {
    //         if (brightnessAdjustment == value) {
    //             return;
    //         }

    //         brightnessAdjustment = value;

    //         OnPropertyChanged(
    //             nameof(BrightnessAdjustment));

    //         OnBrightnessAdjustmentChanged(value);
    //     }
    // }
    partial void OnBrightnessAdjustmentChanged(// если ползунок изменился вызывается этот метод
        double value
        ) {

        UpdateModifiedImage();
    }

    public void ShowError(string message)
    {
        StatusMessage = message;
    }
}