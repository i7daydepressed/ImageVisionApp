using System;
using System.Collections.Generic;
using System.Linq;
using ImageVisionApp.Models;

namespace ImageVisionApp.ViewModels;

internal readonly record struct FaceImportSelection(FaceDetectionResult Detection, int Index);

internal sealed class FaceImportItem {
    public string FileName { get; }
    public FaceDetectionResult? Detection { get; }
    public byte[]? PreviewPng { get; }
    public string? Error { get; }
    public int? SelectedIndex { get; private set; }
    public bool NeedsSelection => Detection is { Faces.Count: > 1 } && SelectedIndex is null;

    public string Status => Error is not null ? Error : SelectedIndex is int index
        ? $"Выбрано лицо {index + 1}" : NeedsSelection
            ? $"Выберите лицо из {Detection!.Faces.Count}" : "Лицо не найдено";

    // сохраняем результат конкретного файла, единственная рамка сразу принята
    public FaceImportItem(string fileName, FaceDetectionResult? detection, byte[]? previewPng, string? error = null) {
        FileName = fileName;
        Detection = detection;
        PreviewPng = previewPng;
        Error = error;
        SelectedIndex = detection is { Faces.Count: 1 } ? 0 : null;
    }

    // меняем выбор только в пределах обнаружений этого файла
    public void SelectFace(int index) {
        if (Detection is null || (uint)index >= (uint)Detection.Faces.Count) {
            throw new ArgumentOutOfRangeException(nameof(index));
        }
        SelectedIndex = index;
    }
}

internal sealed class FaceProfileDraft {
    public Guid? ProfileId { get; }
    public List<FaceImportItem> Photos { get; } = new();
    public int AcceptedCount => Photos.Count(photo => photo.SelectedIndex is not null);
    public bool HasUnselectedFace => Photos.Any(photo => photo.NeedsSelection);

    // связываем добавление с ID профиля, пустой ID означает создание
    public FaceProfileDraft(Guid? profileId) => ProfileId = profileId;

    // отделяем неизменяемый пакет выбора для фонового расчета от списка файлов в окне
    public FaceImportSelection[] CollectSelections() {
        if (HasUnselectedFace) {
            throw new InvalidOperationException("Выберите лицо на каждом групповом фото или уберите это фото.");
        }
        return Photos.Where(photo => photo.SelectedIndex.HasValue)
            .Select(photo => new FaceImportSelection(photo.Detection!, photo.SelectedIndex!.Value)).ToArray();
    }
}
