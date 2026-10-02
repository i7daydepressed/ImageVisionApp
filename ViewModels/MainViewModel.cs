using System.ComponentModel;
using System.IO;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using ImageVisionApp.Models;
using ImageVisionApp.Services;
using System.Collections.ObjectModel;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel : ViewModelBase{
    private readonly ImageStatsFileService imageStatsFileService = new();
    private readonly ImageProcessingService imageProcessingService= new();
    private readonly ImageHistogramService imageHistogramService = new ImageHistogramService();

    // исходник изоб
    private byte[]? originalImageData;// для magick

    //если очередь изменили вручную, PrepareAdvancedQueue() не пересобирает ее из настроек простого режима
    //пока ручное редактирование выкл это false
    private bool advancedQueueCustomized;

    //действия в левой панели расширенного режима: ListBox видит добавление и удаление шагов
    public ObservableCollection<ImageEditStep> AdvancedSteps { get; } = new();
    //эт коллекция похожая на список, которая сообщает подписчикам об изменении своего состава: добавили элемент, удалили, очистили или переместили. Для этого она выдаёт событие CollectionChanged.

    private ImageHistogramData? originalHistogram;//отрисовка гистограм
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
    public partial ImageHistogramComparison? HistogramComparison { get; set; }// гистограммы исходника и текущего результата в одном объекте

    [ObservableProperty]
    public partial ImageInfo? CurrentImageInfo { get; set; }

    [ObservableProperty]
    public partial string StatusMessage { get; set; }
        = "Изображение не выбрано";

    public void LoadImage(byte[] imageData, string fileName){//реактим на событие загрузки збр
        // сохр исхд байты
        originalImageData = imageData;
        ResetTransformationSettings();
        // новое изображение начинает новую очередь: снимаем признак ручного редактирования и удаляем старые шаги
        advancedQueueCustomized = false;
        AdvancedSteps.Clear();
        
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
        // создание гистограмы
        originalHistogram = imageHistogramService.CalculateHistogram(imageData);
        HistogramComparison = new ImageHistogramComparison(
            originalHistogram,
            originalHistogram);
        StatusMessage = fileName;
    }

    private void UpdateModifiedImage() {
        // собираем результат заново с учетом всех настроек
        byte[]? processedImageData =
            CreateProcessedImageData();//изменяем тут

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
        // // считаем гистограмму по тем же байтам, из которых сделали измененное изображение
        if (originalHistogram is not null) {//обновляем гистограмму
            ImageHistogramData processedHistogram =
                imageHistogramService.CalculateHistogram(processedImageData);

            HistogramComparison = new ImageHistogramComparison(
                originalHistogram,
                processedHistogram);
        }
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

    // вызывается при нажатии расширенного редактирования
    public void PrepareAdvancedQueue() {//просто добавляем в очередь(AdvancedSteps.Add) продвинутого режима редактирование из первого
        // без исходника очередь составить нельзя; вручную изменённую очередь настройками простого режима не перезаписываем
        if (originalImageData is null || advancedQueueCustomized) {
            return;
        }//за очередью следит ListBox в CreateToolbar()

        // при повторном входе собираем очередь заново из настроек простого режима
        // Clear убирает прежние строки, чтобы одни и те же действия не появились дважды
        AdvancedSteps.Clear();

        // нейтральные настройки пропускаем: яркость и контраст 0, насыщенность 100% не меняют изображение
        if (Settings.BrightnessAdjustment != 0) {//яркость
            AdvancedSteps.Add(new ImageEditStep(
                ImageOperationType.Brightness,
                Settings.BrightnessAdjustment));
        }

        if (Settings.SaturationPercentage != 100) {//насыщенность
            AdvancedSteps.Add(new ImageEditStep(
                ImageOperationType.Saturation,
                Settings.SaturationPercentage));
        }

        if (Settings.ContrastAdjustment != 0) {//контраст
            AdvancedSteps.Add(new ImageEditStep(
                ImageOperationType.Contrast,
                Settings.ContrastAdjustment));
        }

        if (Settings.IsGrayscaleEnabled) {//коррекцию чб добавляем только после перевода в серый, поэтому она находится внутри этой ветки
            AdvancedSteps.Add(new ImageEditStep(ImageOperationType.Grayscale));

            if (Settings.CorrectionMode == GrayscaleCorrectionMode.Linear) {//лин кор
                AdvancedSteps.Add(new ImageEditStep(
                    ImageOperationType.LinearGrayscaleCorrection));
            }
            else if (Settings.CorrectionMode == GrayscaleCorrectionMode.Nonlinear) {//нелин кор
                AdvancedSteps.Add(new ImageEditStep(
                    ImageOperationType.NonlinearGrayscaleCorrection));
            }
        }

        if (Settings.RotationDegrees != 0) {//ротейт
            AdvancedSteps.Add(new ImageEditStep(
                ImageOperationType.Rotation,
                Settings.RotationDegrees));
        }
    }

    public byte[]? CreateProcessedImageData() {//создать итог байты изм избрж

        if (originalImageData is null) {
            StatusMessage = "Сначала выберите изображение";
            return null;
        }

        return imageProcessingService.ApplyTransformations(//ИЗМЕНЯЕМ ТУТ
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