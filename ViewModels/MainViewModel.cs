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

    //действия в левой панели расширенного режима: ListBox видит добавление и удаление шагов
    public ObservableCollection<ImageEditStep> AdvancedSteps { get; } = new();
    //эт коллекция похожая на список, которая сообщает подписчикам об изменении своего состава: добавили элемент, удалили, очистили или переместили. Для этого она выдаёт событие CollectionChanged.

    private ImageHistogramData? originalHistogram;//отрисовка гистограм

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
        // новый исходник начинает новую очередь
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

    private void UpdateModifiedImage(byte[] processedImageData) {
        // получаем готовые байты текущей очереди, сами настройки здесь не применяем

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

    //пересчитывает изображение по действиям из очереди и обновляет его гистограмму
    public void ApplyAdvancedQueue() {
        if (originalImageData is null) {
            StatusMessage = "Сначала выберите изображение";
            return;
        }

        byte[] processedImageData =
            imageProcessingService.ApplySteps(originalImageData, AdvancedSteps);

        UpdateModifiedImage(processedImageData);
    }

    //удаляет выбранное действие и пересчитывает изображение по оставшимся шагам
    public void RemoveAdvancedStep(ImageEditStep? step) {
        if (step is null || !AdvancedSteps.Remove(step)) {
            return;
        }

        ApplyAdvancedQueue();
    }

    // меняет положение шага и заново применяет очередь в новом порядке
    public void MoveAdvancedStep(ImageEditStep? step, int offset) {
        if (step is null || (offset != -1 && offset != 1)) {
            return;
        }

        int oldIndex = AdvancedSteps.IndexOf(step);
        int newIndex = oldIndex + offset;

        if (oldIndex < 0 || newIndex < 0 || newIndex >= AdvancedSteps.Count) {
            return;
        }

        AdvancedSteps.Move(oldIndex, newIndex);
        ApplyAdvancedQueue();
    }

    // добавляет действие в конец очереди и пересчитывает изображение по всем шагам
    public void AddAdvancedStep(ImageOperationType type, double parameter = 0) {
        if (originalImageData is null) {
            StatusMessage = "Сначала выберите изображение";
            return;
        }

        AdvancedSteps.Add(new ImageEditStep(type, parameter));
        ApplyAdvancedQueue();
        // цепочка здесь такая создаем ImageEditStep → добавляем в AdvancedSteps → ListBox получает новую строку → ApplyAdvancedQueue() пересчитывает картинку и гистограмму
    }

    // убирает все действия расширенного режима и пересчитывает результат из исходного изображения
    public void ResetAdvancedQueue() {
        if (originalImageData is null) {
            StatusMessage = "Сначала выберите изображение";
            return;
        }

        AdvancedSteps.Clear();
        ApplyAdvancedQueue();
    }

    // готовит результат расширенного редактора для сохранения
    public byte[]? CreateAdvancedProcessedImageData() {
        if (originalImageData is null) {
            StatusMessage = "Сначала выберите изображение";
            return null;
        }

        return imageProcessingService.ApplySteps(// берет исходное изображение и заново применяет к нему действия из AdvancedSteps по порядку
            originalImageData,
            AdvancedSteps);
    }

    public ImageHistogramComparison? CreateHistogramComparison() {
        if (originalImageData is null) {
            StatusMessage = "Сначала выберите изображение";
            return null;
        }

        // берем ту же пару, которую уже рассчитали для текущего предпросмотра
        return HistogramComparison;
    }

    public void ShowError(string message)
    {
        StatusMessage = message;
    }
}