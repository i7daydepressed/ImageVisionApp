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
    public Bitmap? AnalysisInputImage => IsVideoOpen ? AnalysisVideoInputImage :
        IsOriginalAnalysisSource ? OriginalImage : ModifiedImage;

    public bool HasAnalysisInput => IsVideoOpen ? TryResolveVideoAnalysisInput(out _) :
        CurrentDocument?.Kind is MediaKind.Image && (IsOriginalAnalysisSource ? originalImageData : modifiedImageData) is not null;
    public long AnalysisInputRevision => analysisInputRevision;

    public string AnalysisInputDescription {
        get {
            if (IsVideoOpen) {
                return DescribeVideoAnalysisInput();
            }
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
        if (IsVideoOpen) {
            return CaptureVideoAnalysisInput();
        }
        if (CurrentDocument?.Kind is not MediaKind.Image) {
            return null;
        }

        byte[]? imageData = IsOriginalAnalysisSource ? originalImageData : modifiedImageData;
        if (imageData is null) {
            return null;
        }

        return new AnalysisInputSnapshot(selectedAnalysisSource, analysisInputRevision, imageData);
    }

    private void ResetAnalysisInputAfterLoad() {
        // снимаем уже собранный результат предыдущего документа до смены источника
        analysisCompositionRequested = false;
        if (HasAnalysisResult) {
            ReplaceAnalysisComposition(null, null);
        }
        ResetVideoAnalysisAfterLoad();
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
        OnPropertyChanged(nameof(SelectedVideoAnalysisSourceIndex));
        OnPropertyChanged(nameof(IsOriginalAnalysisSource));
        OnPropertyChanged(nameof(IsEditedAnalysisSource));
        OnPropertyChanged(nameof(AnalysisInputImage));
        OnPropertyChanged(nameof(HasAnalysisInput));
        OnPropertyChanged(nameof(IsVideoAnalysisEditedInputPending));
        OnPropertyChanged(nameof(AnalysisInputDescription));
        // будущий запуск анализа слушает версию после обновления всех данных входа
        OnPropertyChanged(nameof(AnalysisInputRevision));
    }
}
