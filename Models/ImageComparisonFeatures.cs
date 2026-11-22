using System;
using OpenCvSharp;

namespace ImageVisionApp.Models;

// хранит признаки деталей и запасную сетку независимо от bitmap окна
internal sealed class ImageComparisonFeatures {
    public const int Side = 32;
    public const int ColorCount = Side * Side * 3;
    public const int GradientCount = 2 * Side * (Side - 1);
    public const int SiftDescriptorSize = 128;

    private readonly byte[] rgb;
    private readonly short[] gradients;
    private readonly Point2f[] siftPoints;
    private readonly float[] siftDescriptors;

    public ReadOnlySpan<byte> Rgb => rgb;
    public ReadOnlySpan<short> Gradients => gradients;
    public ReadOnlySpan<Point2f> SiftPoints => siftPoints;
    public ReadOnlySpan<float> SiftDescriptors => siftDescriptors;

    // копируем признаки, чтобы ни фон, ни интерфейс не могли переписать снимок другого
    public ImageComparisonFeatures(ReadOnlySpan<byte> rgb, ReadOnlySpan<short> gradients,
        ReadOnlySpan<Point2f> siftPoints = default, ReadOnlySpan<float> siftDescriptors = default) {
        if (rgb.Length != ColorCount || gradients.Length != GradientCount) {
            throw new ArgumentException("Неверный размер сетки для сопоставления");
        }
        if (siftDescriptors.Length != siftPoints.Length * SiftDescriptorSize) {
            throw new ArgumentException("Неверный размер дескрипторов SIFT");
        }
        this.rgb = rgb.ToArray();
        this.gradients = gradients.ToArray();
        this.siftPoints = siftPoints.ToArray();
        this.siftDescriptors = siftDescriptors.ToArray();
    }
}
