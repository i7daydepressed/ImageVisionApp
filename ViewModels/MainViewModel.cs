using System.ComponentModel;
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

    public ImageTransformationSettings Settings { get; } =
        new ImageTransformationSettings();//настройки изменения изображения

    public MainViewModel() {
        Settings.PropertyChanged += Settings_PropertyChanged;
    }

    [ObservableProperty] //когда значение свойства изменилось, нужно уведомить интерфейс - тот подставит значение в внутренние созданные классы для отображения/работы визуализации
    public partial Bitmap? OriginalImage { get; set; }

    [ObservableProperty]
    public partial Bitmap? ModifiedImage { get; set; }

    [ObservableProperty]
    public partial ImageInfo? CurrentImageInfo { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }
        = "Изображение не выбрано";

    public void LoadImage(byte[] imageData, string fileName){//реактим на событие загрузки збр

        // сохр исхд байты
        originalImageData = imageData;
        ResetTransformationSettings();
        
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
        // собираем результат заново с учетом всех настроек
        byte[] processedImageData =
            imageProcessingService.ApplyTransformations(//применяем фильтры
                originalImageData,
                Settings);//фильтры

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
        Settings.IsGrayscaleEnabled =
            !Settings.IsGrayscaleEnabled;
    }
    
    private void Settings_PropertyChanged(// если ползунок изменился вызывается этот метод
        object? sender,
        PropertyChangedEventArgs e) {

        if (suppressTransformationUpdates) {
            return;
        }

        if (originalImageData is null) {
            return;
        }

        // пересобираем
        UpdateModifiedImage();
    }

    private void ResetTransformationSettings() {
        suppressTransformationUpdates = true;

        try {
            Settings.Reset();
        }
        finally {
            suppressTransformationUpdates = false;
        }
    }

    public void ShowError(string message)
    {
        StatusMessage = message;
    }
}