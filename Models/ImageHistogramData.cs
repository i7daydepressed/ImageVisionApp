using System;
using System.Collections.Generic;

namespace ImageVisionApp.Models;

public sealed class ImageHistogramData {

    public IReadOnlyList<long> RedValues { get; }
    public IReadOnlyList<long> GreenValues { get; }
    public IReadOnlyList<long> BlueValues { get; }

    public ImageHistogramData(
        long[] redValues,
        long[] greenValues,
        long[] blueValues
        ) {

        if (redValues.Length != 256 ||
            greenValues.Length != 256 ||
            blueValues.Length != 256) {

            throw new ArgumentException("каждый канал гистограммы должен содержать 256 значений");
        }

        RedValues = redValues;
        GreenValues = greenValues;
        BlueValues = blueValues;
    }
}