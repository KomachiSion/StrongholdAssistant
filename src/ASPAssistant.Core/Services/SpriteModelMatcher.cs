using System.Text.Json;
using OpenCvSharp;
using OpenCvSharp.Dnn;

namespace ASPAssistant.Core.Services;

/// <summary>小人模型概率与逐名字模板最高分融合；归一化已包含在 ONNX 图内。</summary>
public sealed class SpriteModelMatcher : IDisposable
{
    public const double ModelWeight = .1;
    // 两局跨局预测分别校准为 .033005 / .096211，取较高值向上取整。
    public const double AutoFillGap = .09622;
    private readonly Net _net;
    private readonly double _modelWeight;
    private readonly Dictionary<string, int> _indices;

    public SpriteModelMatcher(string modelPath, string classesPath, double modelWeight = ModelWeight)
    {
        _modelWeight = modelWeight;
        _indices = JsonSerializer.Deserialize<string[]>(File.ReadAllText(classesPath))!
            .Select((name, index) => (name, index)).ToDictionary(p => p.name, p => p.index);
        // 读成字节再交给 OpenCV：按路径加载时用 ANSI 编码传路径，装在中文目录、系统又不是中文区域时会崩（v1.1.1 玩家日志）
        _net = CvDnn.ReadNetFromOnnx(File.ReadAllBytes(modelPath)) ?? throw new InvalidDataException("小人模型无法加载。");
        _net.SetPreferableBackend(Backend.OPENCV);
        _net.SetPreferableTarget(Target.CPU);
    }

    public float[] Predict(Mat crop)
    {
        if (crop.Empty() || crop.Width != 180 || crop.Height != 204 || crop.Type() != MatType.CV_8UC3)
            throw new ArgumentException("小人模型输入必须为 180×204 BGR 图块。", nameof(crop));
        using var blob = CvDnn.BlobFromImage(crop, 1d / 255, new Size(160, 192), swapRB: true, crop: false);
        lock (_net)
        {
            _net.SetInput(blob);
            using var prediction = _net.Forward();
            prediction.GetArray(out float[] values);
            return values;
        }
    }

    public SpriteCandidate[] Fuse(Mat crop, IReadOnlyList<SpriteCandidate> templates)
    {
        if (templates.Count == 0) return [];
        return Fuse(Predict(crop), templates);
    }

    public string[] Shortlist(float[] logits, IEnumerable<string> names, int count)
        => names.Distinct().OrderByDescending(n => logits[_indices[n]]).Take(count).ToArray();

    public SpriteCandidate[] Fuse(float[] logits, IReadOnlyList<SpriteCandidate> templates)
    {
        if (templates.Count == 0) return [];
        double max = templates.Max(c => (double)logits[_indices[c.Name]]);
        var weights = templates.Select(c => Math.Exp(logits[_indices[c.Name]] - max)).ToArray();
        double sum = weights.Sum();
        return templates.Select((c, i) => new SpriteCandidate(c.Name,
            (1 - _modelWeight) * c.Score + _modelWeight * weights[i] / sum))
            .OrderByDescending(c => c.Score).ToArray();
    }

    // 采集读取本次推理值；不额外运行模型。
    public double[] Probabilities(float[] logits, IReadOnlyList<SpriteCandidate> templates)
    {
        if (templates.Count == 0) return [];
        double max = templates.Max(c => (double)logits[_indices[c.Name]]);
        var weights = templates.Select(c => Math.Exp(logits[_indices[c.Name]] - max)).ToArray();
        double sum = weights.Sum();
        return weights.Select(w => w / sum).ToArray();
    }

    public void Dispose() => _net.Dispose();
}
