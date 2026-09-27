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
    private readonly ImageHistogramService imageHistogramService = new ImageHistogramService();

    // исходник изоб
    private byte[]? originalImageData;// для magick
    private bool suppressTransformationUpdates;//флаг запрещающий автообновляться редактируемой картинки сразу после изменения одного какогото ползунка

    public ImageTransformationSettings Settings { get; } =
        new ImageTransformationSettings();//настройки изменения изображения

    public bool IsLinearCorrectionEnabled =>//буд вид для галочки // нелин корекц
        Settings.CorrectionMode ==
        GrayscaleCorrectionMode.Linear; //енеблет ли лин коррекция? CorrectionMode сейчас Linear?

    public bool IsNonlinearCorrectionEnabled =>// лин лог корекц
        Settings.CorrectionMode ==
        GrayscaleCorrectionMode.Nonlinear;

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

        bool newGrayscaleState =
            !Settings.IsGrayscaleEnabled;

        // меняем связанные настройки вместе,
        // чтобы изображение не пересобиралось между изменениями
        suppressTransformationUpdates = true;

        try {//выключаем коррекцию если у нас нет градации
            Settings.IsGrayscaleEnabled = newGrayscaleState;

            if (!newGrayscaleState) {
                Settings.CorrectionMode = GrayscaleCorrectionMode.None;
            }
        }
        finally {
            suppressTransformationUpdates = false;
        }

        // после изменения всех связанных настроек
        // пересобираем изображение только один раз
        UpdateModifiedImage();
    }

    public void ApplyLinearGrayscaleCorrection() {
        if (originalImageData is null) {
            StatusMessage = "Сначала выберите изображение";
            return;
        }

        // коррекция применяется только к серому изображению,
        // поэтому включаем оба параметра одним изменением
        suppressTransformationUpdates = true;
        bool shouldEnableCorrection = !IsLinearCorrectionEnabled;

        try {//вкл градацию
            if (shouldEnableCorrection) {
                Settings.IsGrayscaleEnabled = true;
                Settings.CorrectionMode = GrayscaleCorrectionMode.Linear;
            }
            else {
                // отключаем только коррекцию,
                // изображение оставляем серым
                Settings.CorrectionMode =
                    GrayscaleCorrectionMode.None;
            }
        }
        finally {
            suppressTransformationUpdates = false;
        }

        UpdateModifiedImage();

        StatusMessage = shouldEnableCorrection
            ? "Применена линейная коррекция изображения"
            : "Линейная коррекция отключена";
    }

    public void ApplyNonlinearLogGrayscaleCorrection() {
        if (originalImageData is null) {
            StatusMessage = "Сначала выберите изображение";
            return;
        }

        bool shouldEnableCorrection = !IsNonlinearCorrectionEnabled;

        suppressTransformationUpdates = true;

        try {
            if (shouldEnableCorrection) {// тоже ток к серому
                Settings.IsGrayscaleEnabled = true;
                Settings.CorrectionMode =
                    GrayscaleCorrectionMode.Nonlinear;
            }
            else {
                Settings.CorrectionMode = GrayscaleCorrectionMode.None;
            }
        }
        finally {
            suppressTransformationUpdates = false;
        }

        UpdateModifiedImage();

        StatusMessage = shouldEnableCorrection
            ? "Применена нелинейная логарифмическая коррекция изображения"
            : "Нелинейная коррекция отключена";
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

    public ImageHistogramComparison? CreateHistogramComparison() {
        if (originalImageData is null) {
            StatusMessage = "Сначала выберите изображение";
            return null;
        }

        byte[]? processedImageData =
            CreateProcessedImageData();

        if (processedImageData is null) {
            return null;
        }

        ImageHistogramData originalHistogram =
            imageHistogramService.CalculateHistogram(originalImageData);

        ImageHistogramData processedHistogram =
            imageHistogramService.CalculateHistogram(processedImageData);

        return new ImageHistogramComparison(
            originalHistogram,
            processedHistogram);
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
        PropertyChangedEventArgs e) {//вцелом измениения е типа проперте чангед евентс арг

        if (e.PropertyName == nameof(ImageTransformationSettings.CorrectionMode)) {
            OnPropertyChanged(nameof(IsLinearCorrectionEnabled));// единств точка обновления IsLinearCorrectionEnabled
            OnPropertyChanged(nameof(IsNonlinearCorrectionEnabled));//такж
        }

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