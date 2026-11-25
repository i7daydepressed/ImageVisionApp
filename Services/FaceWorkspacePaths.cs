using System;
using System.IO;

namespace ImageVisionApp.Services;

internal sealed record FaceWorkspacePaths(string CheckoutRoot) {
    public const string RootVariable = "IMAGEVISIONAPP_CHECKOUT_ROOT";
    public const string YuNetFileName = "face_detection_yunet_2023mar.onnx";
    public const string SFaceFileName = "face_recognition_sface_2021dec.onnx";

    public string ModelsDirectory => Path.Combine(CheckoutRoot, "face-models");

    // используем явный корень или поднимаемся от каталога сборки, не опираясь на рабочую папку
    public static FaceWorkspacePaths Resolve(string baseDirectory, string? configuredRoot) {
        if (configuredRoot is not null) {
            if (!Path.IsPathFullyQualified(configuredRoot) || !FaceProfileStore.IsCheckoutRoot(configuredRoot)) {
                throw new InvalidOperationException($"{RootVariable} должен содержать абсолютный путь к рабочей копии ImageVisionApp с .git и ImageVisionApp.csproj.");
            }
            return new FaceWorkspacePaths(Path.GetFullPath(configuredRoot));
        }

        for (DirectoryInfo? parent = new DirectoryInfo(baseDirectory); parent is not null; parent = parent.Parent) {
            if (FaceProfileStore.IsCheckoutRoot(parent.FullName)) {
                return new FaceWorkspacePaths(parent.FullName);
            }
        }
        throw new InvalidOperationException($"Не найдена рабочая копия ImageVisionApp. Укажите ее абсолютный путь через {RootVariable}.");
    }

    // загружаем модели только для фотоимпорта и показываем ожидаемые пути при ошибке
    public FacePipelineService CreatePipeline() {
        try {
            return new FacePipelineService(Path.Combine(ModelsDirectory, YuNetFileName), Path.Combine(ModelsDirectory, SFaceFileName));
        }
        catch (Exception error) {
            throw new InvalidOperationException(
                $"Модели для фото недоступны. Ожидаются {YuNetFileName} и {SFaceFileName} в папке {ModelsDirectory}.{Environment.NewLine}{error.Message}",
                error);
        }
    }
}
