using System;
using System.Collections.Generic;
using System.Linq;

namespace ImageVisionApp.Models;

internal sealed class FaceProfile {
    public Guid Id { get; }
    public string Name { get; }
    public IReadOnlyList<IReadOnlyList<float>> Samples { get; }

    // выдаем только значения образцов, сохраняя массивы внутри снимка
    internal FaceProfile(Guid id, string name, IEnumerable<float[]> samples) {
        Id = id;
        Name = name;
        Samples = Array.AsReadOnly(samples.Select(values => (IReadOnlyList<float>)Array.AsReadOnly(values)).ToArray());
    }
}
