using System;

namespace ImageVisionApp.Models;

internal sealed class FaceEmbedding {
    private readonly float[] values;

    public string PipelineVersion { get; }
    public FaceImage AlignedFace { get; }
    public ReadOnlySpan<float> Values => values;

    // сохраняем нормированные признаки и фрагмент вместе с версией расчета
    internal FaceEmbedding(string pipelineVersion, FaceImage alignedFace, float[] normalizedValues) {
        PipelineVersion = pipelineVersion;
        AlignedFace = alignedFace;
        values = normalizedValues;
    }
}
