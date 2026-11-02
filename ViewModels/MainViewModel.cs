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

    // байты уже показанного результата, чтение входа анализа не запускает очередь повторно
    private byte[]? modifiedImageData;

    //действия в левой панели расширенного режима: ListBox видит добавление и удаление шагов
    public ObservableCollection<ImageEditStep> AdvancedSteps { get; } = new();
    //эт коллекция похожая на список, которая сообщает подписчикам об изменении своего состава: добавили элемент, удалили, очистили или переместили. Для этого она выдаёт событие CollectionChanged.

    private ImageHistogramData? originalHistogram;//отрисовка гистограм

    // матрица для следующего морфологического шага, отдельно от уже добавленной очереди
    public StructuralElement SelectedStructuralElement { get; private set; } =
        StructuralElement.CreateCross();

    public string StructuralElementDescription =>
        $"строк (Y): {SelectedStructuralElement.Rows}, " +
        $"столбцов (X): {SelectedStructuralElement.Columns}, " +
        $"активных ячеек: {SelectedStructuralElement.ActiveCellCount}, " +
        $"опора: X = {SelectedStructuralElement.AnchorColumn + 1}, " +
        $"Y = {SelectedStructuralElement.AnchorRow + 1}";

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

    public void LoadImage(byte[] imageData, string fileName) {
        // сначала готовим новый документ, при ошибке прежние байты, очередь и Bitmap остаются
        byte[] nextImageData = (byte[])imageData.Clone();
        ImageInfo nextImageInfo = imageStatsFileService.readImageInfo(nextImageData, fileName);
        ImageHistogramData nextOriginalHistogram = imageHistogramService.CalculateHistogram(nextImageData);
        ImageHistogramComparison nextComparison = new ImageHistogramComparison(
            nextOriginalHistogram, nextOriginalHistogram);

        using MemoryStream originalStream = new MemoryStream(nextImageData);
        using MemoryStream modifiedStream = new MemoryStream(nextImageData);
        Bitmap nextOriginalImage = new Bitmap(originalStream);
        Bitmap nextModifiedImage;
        try {
            nextModifiedImage = new Bitmap(modifiedStream);
        }
        catch {
            nextOriginalImage.Dispose();
            throw;
        }

        Bitmap? previousOriginalImage = OriginalImage;
        Bitmap? previousModifiedImage = ModifiedImage;

        // кэш и предпросмотры публикуют один подготовленный документ
        originalImageData = nextImageData;
        modifiedImageData = nextImageData;
        originalHistogram = nextOriginalHistogram;
        AdvancedSteps.Clear();
        OriginalImage = nextOriginalImage;
        ModifiedImage = nextModifiedImage;
        CurrentImageInfo = nextImageInfo;
        HistogramComparison = nextComparison;
        StatusMessage = fileName;
        ResetAnalysisInputAfterLoad();

        previousOriginalImage?.Dispose();
        previousModifiedImage?.Dispose();
    }

    private void UpdateModifiedImage(byte[] processedImageData) {
        // получаем готовые байты текущей очереди, сами настройки здесь не применяем

        ImageHistogramComparison? nextComparison = null;
        // // считаем гистограмму по тем же байтам, из которых сделали измененное изображение
        if (originalHistogram is not null) {//обновляем гистограмму
            ImageHistogramData processedHistogram =
                imageHistogramService.CalculateHistogram(processedImageData);

            nextComparison = new ImageHistogramComparison(
                originalHistogram,
                processedHistogram);
        }

        using MemoryStream processedImageStream =
            new MemoryStream(processedImageData);

        // avalonia Bitmap принимает поток поэтому оборачиваем полученные байты в memstream
        Bitmap newModifiedImage =
            new Bitmap(processedImageStream);

        // сначала подготовили обе части результата, теперь заменяем показанное состояние
        Bitmap? previousImage = ModifiedImage;
        modifiedImageData = processedImageData;
        ModifiedImage = newModifiedImage;
        if (nextComparison is not null) {
            HistogramComparison = nextComparison;
        }
        NotifyEditedAnalysisInputChanged();
        previousImage?.Dispose();
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

    public void SetStructuralElement(StructuralElement structuralElement) {
        SelectedStructuralElement = structuralElement;
        // карточка обновит сведения о выбранной матрице, очередь и предпросмотр не меняем
        OnPropertyChanged(nameof(SelectedStructuralElement));
        OnPropertyChanged(nameof(StructuralElementDescription));
    }

    public void ShowError(string message)
    {
        StatusMessage = message;
    }
}