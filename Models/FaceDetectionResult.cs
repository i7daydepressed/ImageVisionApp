using System;
using System.Collections.Generic;

namespace ImageVisionApp.Models;

internal readonly record struct FacePoint(float X, float Y);

internal readonly record struct FaceBounds(float Left, float Top, float Right, float Bottom);

internal readonly record struct FaceLandmarks(
    FacePoint RightEye, FacePoint LeftEye, FacePoint NoseTip, FacePoint RightMouthCorner, FacePoint LeftMouthCorner);

internal readonly record struct FaceDetection(FaceBounds Bounds, FaceLandmarks Landmarks, float Score);

internal sealed class FaceDetectionResult {
    private readonly object sessionKey;

    public FaceImage Source { get; }
    public IReadOnlyList<FaceDetection> Faces { get; }

    // связываем список с точным снимком и сессией, рассчитавшей его
    internal FaceDetectionResult(object sessionKey, FaceImage source, FaceDetection[] faces) {
        this.sessionKey = sessionKey;
        Source = source;
        Faces = Array.AsReadOnly(faces);
    }

    // принимаем из результата только индекс, принадлежащий той же сессии
    internal FaceDetection Select(object sessionKey, int index) {
        if (!ReferenceEquals(this.sessionKey, sessionKey)) {
            throw new ArgumentException("Результат детекции принадлежит другой сессии моделей.");
        }
        if ((uint)index >= (uint)Faces.Count) {
            throw new ArgumentOutOfRangeException(nameof(index), "Выбранного лица нет в результате детекции.");
        }
        return Faces[index];
    }
}
