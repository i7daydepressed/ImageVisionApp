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

    // по умолчанию импорт получает полный конвейер, детекция требует только YuNet
    public FacePipelineService CreatePipeline(FaceRecognitionMode mode = FaceRecognitionMode.WithProfiles) {
        bool detectOnly = mode == FaceRecognitionMode.DetectOnly;
        try {
            return new FacePipelineService(Path.Combine(ModelsDirectory, YuNetFileName),
                detectOnly ? null : Path.Combine(ModelsDirectory, SFaceFileName));
        }
        catch (Exception error) {
            string expected = detectOnly ? YuNetFileName : $"{YuNetFileName} и {SFaceFileName}";
            throw new InvalidOperationException(
                $"Модели лиц недоступны. Файлы для этого режима: {expected} в папке {ModelsDirectory}.{Environment.NewLine}{error.Message}",
                error);
        }
    }
}
