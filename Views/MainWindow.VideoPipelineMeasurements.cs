using System;
using Avalonia.Controls;
using Avalonia.Media;
using ImageVisionApp.Services;

namespace ImageVisionApp.Views;

public partial class MainWindow {
    // Показываем готовность отдельно от публикации и складываем только одновременные резервы.
    private sealed class VideoPipelineMeasurementPanel : StackPanel {
        private const string Missing = "н/д";

        private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap };
        private readonly StackPanel output = new() { Spacing = 4 };
        private readonly StackPanel stage = new() { Spacing = 4 };
        private readonly (string Label, TextBlock Value)[] outputRows;
        private readonly (string Label, TextBlock Value)[] stageRows;

        public VideoPipelineMeasurementPanel() {
            Spacing = 6;
            outputRows = AddRows(output, [
                "Готово кадров за проход", "Опубликовано кадров за проход", "Последний готовый кадр",
                "От входа до готовности", "От готовности до публикации", "От входа до публикации",
                "Автоматическое ожидание за проход", "Суммарный расчётный резерв сейчас / пик"
            ]);
            stageRows = AddRows(stage, [
                "Обработано кадров за проход", "Обработка последнего кадра",
                "Среднее время обработки кадра", "Расчётный резерв участка сейчас / пик"
            ]);
            Children.Add(status);
            Children.Add(output);
            Children.Add(stage);
        }

        public void Refresh(int scope, VideoPipelineMeasurements.Snapshot? sample) {
            IsVisible = sample is not null || scope == 3;
            status.Text = sample is null ? "Данных замера пока нет." :
                scope == 0 ? "Комбинированная цепочка" : "Участок комбинированной цепочки";
            status.FontWeight = sample is null ? FontWeight.Normal : FontWeight.SemiBold;
            output.IsVisible = sample is not null && scope == 0;
            stage.IsVisible = sample is not null && scope > 0;
            if (sample is not { } current) {
                return;
            }
            if (output.IsVisible) {
                var value = current.Output;
                RefreshRows(outputRows, [
                    FormatCount(value.ReadyFrames, value.LastReadyOrdinal is not null),
                    FormatCount(value.PublishedFrames, value.LastReadyOrdinal is not null),
                    value.LastReadyOrdinal is long ordinal ? $"№{ordinal}" : Missing,
                    FormatTime(value.LastReadyLatency), FormatTime(value.LastPresentationWait),
                    FormatTime(value.LastPresentationLatency),
                    FormatTime(value.AutomaticWait > TimeSpan.Zero || value.ReadyFrames > 0 ? value.AutomaticWait : null),
                    FormatReserve(current.ReservedBytes, current.PeakReservedBytes)
                ]);
            }
            else if (stage.IsVisible) {
                var value = scope switch {
                    1 => current.Editing,
                    2 => current.Analysis,
                    _ => current.VideoSequence
                };
                RefreshRows(stageRows, [
                    FormatCount(value.CompletedFrames, value.LastProcessing is not null),
                    FormatTime(value.LastProcessing), FormatTime(value.MeanProcessing),
                    FormatReserve(value.ReservedBytes, value.PeakReservedBytes)
                ]);
            }
        }

        private static (string Label, TextBlock Value)[] AddRows(StackPanel panel, string[] labels) {
            var rows = new (string, TextBlock)[labels.Length];
            for (int i = 0; i < labels.Length; i++) {
                var value = new TextBlock();
                rows[i] = (labels[i], value);
                panel.Children.Add(BuildPreviewSettingsRow(labels[i], value));
            }
            return rows;
        }

        private static void RefreshRows((string Label, TextBlock Value)[] rows, string[] values) {
            for (int i = 0; i < rows.Length; i++) {
                SetMetric(rows[i].Value, rows[i].Label, values[i]);
            }
        }

        private static string FormatCount(long count, bool measured) => measured ? count.ToString() : Missing;

        private static string FormatTime(TimeSpan? duration) =>
            duration is { } time ? $"{time.TotalMilliseconds:0.##} мс" : Missing;

        private static string FormatReserve(long bytes, long peak) => peak == 0 ? Missing :
            $"{bytes / (1024d * 1024):0.##} / {peak / (1024d * 1024):0.##} МиБ";
    }
}
