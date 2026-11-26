using System;
using System.Collections.Generic;
using System.Linq;
using ImageVisionApp.Services;

namespace ImageVisionApp.Models;

internal sealed class FaceProfileSample {
    public long Number { get; }
    public string? Name { get; }
    public IReadOnlyList<float> Values { get; }
    public string Label => Name ?? $"Образец {Number}";

    // номер постоянный, а вектор доступен только для чтения
    internal FaceProfileSample(long number, string? name, float[] values) {
        Number = number;
        Name = name;
        Values = Array.AsReadOnly(values);
    }
}

internal sealed record FaceSampleInput(FaceEmbedding Embedding, string? Name);

internal sealed class FaceProfile {
    public Guid Id { get; }
    public string Name { get; }
    public IReadOnlyList<FaceProfileSample> Samples { get; }
    public FaceProfileAvatarPattern? StoredAvatar { get; }

    // выдаем только значения образцов, сохраняя массивы внутри снимка
    internal FaceProfile(Guid id, string name, IEnumerable<FaceProfileSample> samples,
        FaceProfileAvatarPattern? storedAvatar) {
        Id = id;
        Name = name;
        Samples = Array.AsReadOnly(samples.ToArray());
        StoredAvatar = storedAvatar;
    }
}
