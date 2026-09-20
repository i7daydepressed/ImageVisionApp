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
        // собираем результат заново с учетом всех настроек
        byte[]? processedImageData =
            CreateProcessedImageData();

        if (processedImageData is null) {
            return;
        }

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

    public void ResetBrightness() {// сбросить яркость
        if (originalImageData is null) {
            StatusMessage = "Сначала выберите изображение";
            return;
        }

        // изменение свойства автоматически вызовет
        // Settings_PropertyChanged и пересоберет изображение
        Settings.BrightnessAdjustment = 0;
    }

    public void ResetSaturation() {// сброс насыщенности
        if (originalImageData is null) {
            StatusMessage = "Сначала выберите изображение";
            return;
        }

        Settings.SaturationPercentage = 100;
    }

    public void ResetContrast() {//сброс контраста

        if (originalImageData is null) {
            StatusMessage = "Сначала выберите изображение";
            return;
        }

        Settings.ContrastAdjustment = 0;
    }

    public byte[]? CreateProcessedImageData() {//создать итог байты изм избрж

        if (originalImageData is null) {
            StatusMessage = "Сначала выберите изображение";
            return null;
        }

        return imageProcessingService.ApplyTransformations(
            originalImageData,
            Settings);
    }

    public void ResetAllTransformations() {//сбросить изменения
        if (originalImageData is null) {
            StatusMessage = "Сначала выберите изображение";
            return;
        }

        // во время группового сброса автоматические
        // обновления временно отключаются
        ResetTransformationSettings();

        // псле изменения всех настроек
        // пересобираем изображение
        UpdateModifiedImage();
    }

    public void RotateClockwise() {//повернуть вправо на 90
        if (originalImageData is null) {
            StatusMessage = "Сначала выберите изображение";
            return;
        }

        Settings.RotationDegrees =
            (Settings.RotationDegrees + 90) % 360;
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