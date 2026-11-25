using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using ImageVisionApp.Models;

namespace ImageVisionApp.Services;

internal sealed record FaceRecognitionMatch(FaceProfile? Profile, double? BestScore, double? NextScore);
internal sealed record RecognizedFace(FaceBounds Bounds, FaceRecognitionMatch Match);
internal sealed record FaceRecognitionFrame(FaceImage Image, IReadOnlyList<RecognizedFace> Faces, bool EmptyCatalogue);

internal sealed class FaceRecognitionService(FacePipelineService pipeline, IReadOnlyList<FaceProfile> profiles) : IDisposable {
    internal const double AcceptanceThreshold = 0.363;

    // выравниваем каждое лицо по точкам той же детекции и связываем решения с ее пикселями
    public FaceRecognitionFrame Recognize(FaceImage image, CancellationToken token) {
        FaceDetectionResult detection = pipeline.Detect(image, token);
        var faces = new RecognizedFace[detection.Faces.Count];
        for (int index = 0; index < faces.Length; index++) {
            token.ThrowIfCancellationRequested();
            FaceRecognitionMatch match = new(null, null, null);
            if (profiles.Count > 0) {
                try {
                    match = Match(pipeline.ExtractEmbedding(detection, index, token), profiles, token);
                }
                catch (InvalidDataException) {
                    // неудачное выравнивание оставляет рамку неизвестной, остальные лица еще пригодны
                }
            }
            faces[index] = new RecognizedFace(detection.Faces[index].Bounds, match);
        }
        return new FaceRecognitionFrame(image, Array.AsReadOnly(faces), profiles.Count == 0);
    }

    // берем максимум по образцам каждого профиля и отклоняем ничью между разными ID
    internal static FaceRecognitionMatch Match(FaceEmbedding embedding, IReadOnlyList<FaceProfile> catalogue,
        CancellationToken token) {
        if (embedding.PipelineVersion != FacePipelineService.PipelineVersion) {
            throw new ArgumentException("Версия признаков несовместима с базой лиц.", nameof(embedding));
        }

        FaceProfile? best = null;
        double bestScore = double.NegativeInfinity;
        double nextScore = double.NegativeInfinity;
        foreach (FaceProfile profile in catalogue) {
            double score = double.NegativeInfinity;
            foreach (IReadOnlyList<float> sample in profile.Samples) {
                token.ThrowIfCancellationRequested();
                score = Math.Max(score, Cosine(embedding.Values, sample));
            }
            if (score > bestScore) {
                best = profile;
                nextScore = bestScore;
                bestScore = score;
            }
            else {
                nextScore = Math.Max(nextScore, score);
            }
        }
        return new FaceRecognitionMatch(bestScore >= AcceptanceThreshold && bestScore > nextScore ? best : null,
            double.IsFinite(bestScore) ? bestScore : null, double.IsFinite(nextScore) ? nextScore : null);
    }

    // отбрасываем четвертый байт плотного кадра VLC, а остальные каналы оставляем на своих местах
    internal static FaceImage CopyVideoImage(VideoEditorFrame frame, CancellationToken token) {
        ReadOnlySpan<byte> bgra = frame.Bgra.Span;
        int width = frame.Size.Width;
        int height = frame.Size.Height;
        byte[] bgr = new byte[FaceImage.GetBgr8Length(width, height)];
        int input = 0;
        int output = 0;
        for (int y = 0; y < height; y++) {
            token.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++, input += 4, output += 3) {
                bgr[output] = bgra[input];
                bgr[output + 1] = bgra[input + 1];
                bgr[output + 2] = bgra[input + 2];
            }
        }
        return FaceImage.TakeBgr8(width, height, bgr);
    }

    // считаем косинус с реальными нормами обоих сохраненных массивов
    private static double Cosine(ReadOnlySpan<float> first, IReadOnlyList<float> second) {
        double dot = 0;
        double firstSquared = 0;
        double secondSquared = 0;
        for (int index = 0; index < first.Length; index++) {
            double left = first[index];
            double right = second[index];
            dot += left * right;
            firstSquared += left * left;
            secondSquared += right * right;
        }
        return Math.Clamp(dot / (Math.Sqrt(firstSquared) * Math.Sqrt(secondSquared)), -1, 1);
    }

    // освобождаем модели после последнего кадра покидаемого посещения
    public void Dispose() => pipeline.Dispose();
}
