using System;
using Avalonia.Media.Imaging;
using ImageVisionApp.Models;

namespace ImageVisionApp.ViewModels;

public partial class MainViewModel {
    private AnalysisImageSource selectedAnalysisSource = AnalysisImageSource.Original;
    private long analysisInputRevision;

    public AnalysisImageSource SelectedAnalysisSource {
        get => selectedAnalysisSource;
        set {
            if (value != AnalysisImageSource.Original && value != AnalysisImageSource.Edited) {
                throw new ArgumentOutOfRangeException(nameof(value));
            }

            if (selectedAnalysisSource == value) {
                return;
            }

            selectedAnalysisSource = value;
            NotifyAnalysisInputChanged();
        }
    }

    public bool IsOriginalAnalysisSource =>
        selectedAnalysisSource == AnalysisImageSource.Original;

    public bool IsEditedAnalysisSource =>
        selectedAnalysisSource == AnalysisImageSource.Edited;

    // это ссылка на предпросмотр документа, третья страница не освобождает этот Bitmap
    public Bitmap? AnalysisInputImage => IsOriginalAnalysisSource ? OriginalImage : ModifiedImage;

    public bool HasAnalysisInput => AnalysisInputImage is not null;
    public long AnalysisInputRevision => analysisInputRevision;

    public string AnalysisInputDescription {
        get {
            Bitmap? image = AnalysisInputImage;
            if (image is null) {
                return "Изображение не выбрано";
            }

            string sourceName = IsOriginalAnalysisSource ? "Оригинал" : "Результат редактирования";
            return $"{sourceName} · {image.PixelSize.Width} × {image.PixelSize.Height}";
        }
    }

    // вызывается из потока интерфейса, пока источник и его версия согласованы
    public AnalysisInputSnapshot? CaptureAnalysisInput() {
        byte[]? imageData = IsOriginalAnalysisSource ? originalImageData : modifiedImageData;
        if (imageData is null) {
            return null;
        }

        return new AnalysisInputSnapshot(selectedAnalysisSource, analysisInputRevision, imageData);
    }

    private void ResetAnalysisInputAfterLoad() {
        selectedAnalysisSource = AnalysisImageSource.Original;
        NotifyAnalysisInputChanged();
    }

    private void NotifyEditedAnalysisInputChanged() {
        if (IsEditedAnalysisSource) {
            NotifyAnalysisInputChanged();
        }
    }

    private void NotifyAnalysisInputChanged() {
        analysisInputRevision++;
        InvalidateAllAnalysis();
        OnPropertyChanged(nameof(SelectedAnalysisSource));
        OnPropertyChanged(nameof(IsOriginalAnalysisSource));
        OnPropertyChanged(nameof(IsEditedAnalysisSource));
        OnPropertyChanged(nameof(AnalysisInputImage));
        OnPropertyChanged(nameof(HasAnalysisInput));
        OnPropertyChanged(nameof(AnalysisInputDescription));
        // будущий запуск анализа слушает версию после обновления всех данных входа
        OnPropertyChanged(nameof(AnalysisInputRevision));
    }
}
