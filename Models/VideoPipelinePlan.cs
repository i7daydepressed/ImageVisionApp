using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

namespace ImageVisionApp.Models;

[Flags]
internal enum VideoPipelineStage {
    None = 0,
    Original = 1,
    Editing = 2,
    Analysis = 4,
    VideoSequence = 8
}

internal sealed class VideoPipelinePlan {
    private readonly VideoPipelineStage analysisInputs;
    private readonly VideoPipelineStage sequenceInputs;
    public ImmutableArray<VideoPipelineStage> Stages { get; }

    // сохраняем проверенные связи и порядок участков для выбранного выхода
    private VideoPipelinePlan(ImmutableArray<VideoPipelineStage> stages,
        VideoPipelineStage analysisInputs, VideoPipelineStage sequenceInputs) {
        Stages = stages;
        this.analysisInputs = analysisInputs;
        this.sequenceInputs = sequenceInputs;
    }

    private static readonly VideoPipelineStage[] StageOrder = [
        VideoPipelineStage.Original, VideoPipelineStage.Editing, VideoPipelineStage.Analysis, VideoPipelineStage.VideoSequence
    ];

    private const VideoPipelineStage ValidStages =
        VideoPipelineStage.Original | VideoPipelineStage.Editing | VideoPipelineStage.Analysis | VideoPipelineStage.VideoSequence;

    // проверяем все связи, затем оставляем в очереди только предков выбранного выхода
    public static bool TryCreate(VideoPipelineStage analysisInputs, VideoPipelineStage sequenceInputs,
        VideoPipelineStage output, [NotNullWhen(true)] out VideoPipelinePlan? plan, out string? error) {
        plan = null;
        error = null;
        if (((analysisInputs | sequenceInputs) & ~ValidStages) != 0 || !StageOrder.Contains(output)) {
            error = "Выбран неизвестный участок обработки";
            return false;
        }

        List<VideoPipelineStage> path = [];
        List<VideoPipelineStage> ordered = [];
        Dictionary<VideoPipelineStage, VideoPipelineStage> ancestors = [];
        foreach (VideoPipelineStage stage in StageOrder) {
            if (!Visit(stage, out error)) {
                return false;
            }
        }
        VideoPipelineStage requested = ancestors[output];
        plan = new([.. ordered.Where(stage => (requested & stage) != 0)], analysisInputs, sequenceInputs);
        return true;

        // предки попадают в очередь прежде потребителя, повтор в текущем пути означает цикл
        bool Visit(VideoPipelineStage stage, out string? failure) {
            failure = null;
            if (ancestors.ContainsKey(stage)) {
                return true;
            }
            int repeated = path.IndexOf(stage);
            if (repeated >= 0) {
                string cycle = string.Join(" → ", path.Skip(repeated).Append(stage).Select(GetStageName));
                failure = $"Связь недоступна: {cycle} образует цикл";
                return false;
            }

            path.Add(stage);
            VideoPipelineStage inputs = GetInputs(stage, analysisInputs, sequenceInputs);
            VideoPipelineStage reachable = stage;
            foreach (VideoPipelineStage input in StageOrder) {
                if ((inputs & input) == 0) {
                    continue;
                }
                if (!Visit(input, out failure)) {
                    return false;
                }
                reachable |= ancestors[input];
            }
            path.RemoveAt(path.Count - 1);
            ancestors.Add(stage, reachable);
            ordered.Add(stage);
            return true;
        }
    }

    // выводим каждый путь по проверенным связям, не выдавая параллельные входы за один линейный
    public IEnumerable<string> DescribePathsTo(VideoPipelineStage output) {
        string name = GetStageName(output);
        VideoPipelineStage inputs = GetInputs(output, analysisInputs, sequenceInputs);
        if (inputs == VideoPipelineStage.None) {
            yield return name;
        }
        foreach (VideoPipelineStage input in StageOrder) {
            if ((inputs & input) != 0) {
                foreach (string path in DescribePathsTo(input)) {
                    yield return $"{path} → {name}";
                }
            }
        }
    }

    // участок редактирования всегда начинается с оригинала, остальные получают выбранные входы
    private static VideoPipelineStage GetInputs(VideoPipelineStage stage,
        VideoPipelineStage analysisInputs, VideoPipelineStage sequenceInputs) => stage switch {
        VideoPipelineStage.Editing => VideoPipelineStage.Original,
        VideoPipelineStage.Analysis => analysisInputs,
        VideoPipelineStage.VideoSequence => sequenceInputs,
        _ => VideoPipelineStage.None
    };

    // сохраняем прежний выбор входа метода и переводим его в узел комбинированной очереди
    public static VideoPipelineStage FromAnalysisSource(AnalysisImageSource source) => source switch {
        AnalysisImageSource.Original => VideoPipelineStage.Original,
        AnalysisImageSource.Edited => VideoPipelineStage.Editing,
        _ => throw new ArgumentOutOfRangeException(nameof(source))
    };

    // используем одинаковые имена в сообщении об ошибке и в пути кадра
    public static string GetStageName(VideoPipelineStage stage) => stage switch {
        VideoPipelineStage.Original => "Оригинал",
        VideoPipelineStage.Editing => "Редактирование",
        VideoPipelineStage.Analysis => "Анализ",
        VideoPipelineStage.VideoSequence => "Видеоряд",
        _ => throw new ArgumentOutOfRangeException(nameof(stage))
    };
}
