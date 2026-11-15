using System.IO.Compression;
using System.Text;
using System.Runtime.InteropServices;
using ASPAssistant.Core.Models;
using OpenCvSharp;

namespace ASPAssistant.Core.Services;

public sealed record SpriteCandidate(string Name, double Score);
public sealed record SpriteMatch(IReadOnlyList<SpriteCandidate> Candidates, double? Gap)
{
    public bool CanAutoFill => Gap >= SpriteMatcher.AutoFillGap;
}

/// <summary>冻结的小人蒙版比对。单实例由扫描线程持有并释放，不负责候选来源或扫描调度。</summary>
public sealed class SpriteMatcher : IDisposable
{
    public const double AutoFillGap = 0.04592923876191826;
    private sealed record Template(string Name, int Tier, int Size, Mat Color, Mat Mask);
    private readonly List<Template> _source = [];
    private readonly Dictionary<string, List<Template>> _scaled = new(StringComparer.Ordinal);

    public SpriteMatcher(string packagePath)
    {
        using var file = File.OpenRead(packagePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip, Encoding.UTF8);
        if (Encoding.ASCII.GetString(reader.ReadBytes(8)) != "SPRT0001")
            throw new InvalidDataException("小人模板包版本不符。");
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string name = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));
            int tier = reader.ReadUInt16(), width = reader.ReadUInt16(), height = reader.ReadUInt16();
            var color = new Mat(height, width, MatType.CV_8UC3);
            var mask = new Mat(height, width, MatType.CV_8UC1);
            var pixels = new byte[width * height * 3]; reader.BaseStream.ReadExactly(pixels);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, color.Data, pixels.Length);
            pixels = new byte[width * height]; reader.BaseStream.ReadExactly(pixels);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, mask.Data, pixels.Length);
            _source.Add(new(name, tier, 0, color, mask));
        }
    }

    /// <summary>棋盘格中心沿用识别结果投影，包括首领左右镜头；格坐标与FieldCell一致。</summary>
    public SpriteMatch MatchBoard(byte[] frame, BoardProjection projection, int col, int row,
        int tier, IReadOnlyCollection<string> candidates)
    {
        using var image = Cv2.ImDecode(frame, ImreadModes.Color);
        var p = projection.Project(col + .5, row + .5);
        return MatchImage(image, p.X * image.Width, p.Y * image.Height, false, tier, candidates);
    }

    /// <summary>输入当前截图像素坐标的格子脚点；整备区不根据棋盘侧别推断朝向。</summary>
    public SpriteMatch Match(byte[] frame, double footX, double footY, bool bench,
        int tier, IReadOnlyCollection<string> candidates)
    {
        using var image = Cv2.ImDecode(frame, ImreadModes.Color);
        return MatchImage(image, footX, footY, bench, tier, candidates);
    }

    private SpriteMatch MatchImage(Mat image, double footX, double footY, bool bench,
        int tier, IReadOnlyCollection<string> candidates)
    {
        if (image.Empty()) throw new ArgumentException("截图无法解码。");
        // 冻结参数以1920×1080定义，先归一整帧，再按原脚本取180×204裁块。
        double sx = 1920.0 / image.Width, sy = 1080.0 / image.Height;
        using var normalized = new Mat();
        Cv2.Resize(image, normalized, new Size(1920, 1080));
        int x = (int)Math.Round(footX * sx), y = (int)Math.Round(footY * sy) + (bench ? 0 : 24);
        var rect = new Rect(x - 90, y - 180, 180, 204);
        if (rect.X < 0 || rect.Y < 0 || rect.Right > normalized.Width || rect.Bottom > normalized.Height)
            return new([], null);
        using var crop = new Mat(normalized, rect);
        return MatchCrop(crop, bench, tier, candidates);
    }

    /// <summary>离线裁块入口。默认180×204；更窄的单格裁块需提供相对裁块的模板底部锚点。</summary>
    public SpriteMatch MatchCrop(Mat crop, bool bench, int tier, IReadOnlyCollection<string> candidates,
        Point? searchAnchor = null)
    {
        if (crop.Empty() || crop.Type() != MatType.CV_8UC3)
            throw new ArgumentException("裁块必须为BGR图像。", nameof(crop));
        using var half = new Mat();
        Cv2.Resize(crop, half, new Size(crop.Width / 2, crop.Height / 2));
        var anchor = searchAnchor ?? new Point(90, bench ? 176 : 180);
        int bottom = anchor.Y / 2;
        // 缩放缓存仍由扫描线程构建；各候选只读模板，并行时独占临时 Mat。
        var work = candidates.Distinct(StringComparer.Ordinal).Select(name => (Name: name, Templates: Templates(name))).ToArray();
        var ranked = new SpriteCandidate?[work.Length];
        Parallel.For(0, work.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
        {
            var (name, templates) = work[index];
            using var scores = new Mat(); using var difference = new Mat();
            var values = new float[17 * 17];
            double best = -2;
            foreach (var template in templates)
            {
                if (template.Tier != tier || (bench ? template.Size == 104 : template.Size == 168)) continue;
                var a = template.Color; var mask = template.Mask;
                int w = a.Width, h = a.Height;
                int center = (int)Math.Round(anchor.X / 2.0 - w / 2.0);
                int x0 = Math.Max(0, center - 8), x1 = Math.Min(half.Width, center + 8 + w);
                int y0 = Math.Max(0, bottom - h - 8), y1 = Math.Min(half.Height, bottom + 8);
                if (x1 - x0 < w || y1 - y0 < h) continue;
                using var roi = new Mat(half, new Rect(x0, y0, x1 - x0, y1 - y0));
                Cv2.MatchTemplate(roi, a, scores, TemplateMatchModes.CCoeffNormed, mask);
                // 与Python的nan_to_num一致，非有限值不能成为最佳位置。
                // 搜索范围每轴最多 ±8；批量读取，避免逐像素跨托管边界。
                int count = scores.Rows * scores.Cols, bestIndex = 0;
                Marshal.Copy(scores.Data, values, 0, count);
                double corr = double.NegativeInfinity;
                for (int i = 0; i < count; i++)
                {
                    double value = float.IsFinite(values[i]) ? values[i] : -1;
                    if (value > corr) { corr = value; bestIndex = i; }
                }
                var location = new Point(bestIndex % scores.Cols, bestIndex / scores.Cols);
                using var patch = new Mat(roi, new Rect(location.X, location.Y, w, h));
                Cv2.Absdiff(patch, a, difference);
                var mean = Cv2.Mean(difference, mask);
                double color = 1 - (mean.Val0 + mean.Val1 + mean.Val2) / (3 * 255);
                best = Math.Max(best, .75 * corr + .25 * color);
            }
            if (best > -2) ranked[index] = new(name, best);
        });
        var sorted = ranked.OfType<SpriteCandidate>().OrderByDescending(r => r.Score).ThenBy(r => r.Name, StringComparer.Ordinal).ToArray();
        return new(sorted, sorted.Length > 1 ? sorted[0].Score - sorted[1].Score : null);
    }

    // 个人图块没有透明通道，以中心80×136区域作蒙版；半尺寸、位置±8与冻结比对口径一致。
    public static double MatchPersonal(FieldSpritePatch template, FieldSpritePatch target)
    {
        using var source = Cv2.ImDecode(template.Image, ImreadModes.Color);
        using var image = Cv2.ImDecode(target.Image, ImreadModes.Color);
        using var center = new Mat(source, new Rect(50, 40, 80, 136));
        using var half = new Mat();
        Cv2.Resize(image, half, new Size(90, 102));
        using var scaled = new Mat(); using var scores = new Mat(); using var difference = new Mat();
        double best = -1;
        var values = new float[17 * 17];
        foreach (double scale in new[] { .8, .9, 1, 1.1, 1.2 })
        {
            int w = (int)Math.Round(40 * scale), h = (int)Math.Round(68 * scale);
            Cv2.Resize(center, scaled, new Size(w, h));
            int x = (int)Math.Round(45 - w / 2.0), y = (int)Math.Round(54 - h / 2.0);
            int x0 = Math.Max(0, x - 8), y0 = Math.Max(0, y - 8);
            int x1 = Math.Min(half.Width, x + w + 8), y1 = Math.Min(half.Height, y + h + 8);
            using var roi = new Mat(half, new Rect(x0, y0, x1 - x0, y1 - y0));
            Cv2.MatchTemplate(roi, scaled, scores, TemplateMatchModes.CCoeffNormed);
            int count = scores.Rows * scores.Cols, bestIndex = 0;
            Marshal.Copy(scores.Data, values, 0, count);
            double corr = double.NegativeInfinity;
            for (int i = 0; i < count; i++)
            {
                double value = float.IsFinite(values[i]) ? values[i] : -1;
                if (value > corr) { corr = value; bestIndex = i; }
            }
            using var patch = new Mat(roi, new Rect(bestIndex % scores.Cols, bestIndex / scores.Cols, w, h));
            Cv2.Absdiff(patch, scaled, difference);
            var mean = Cv2.Mean(difference);
            double color = 1 - (mean.Val0 + mean.Val1 + mean.Val2) / (3 * 255);
            best = Math.Max(best, .75 * corr + .25 * color);
        }
        return best;
    }

    private List<Template> Templates(string name)
    {
        if (_scaled.TryGetValue(name, out var cached)) return cached;
        var result = new List<Template>();
        foreach (var source in _source.Where(t => t.Name == name))
            foreach (bool flip in new[] { false, true })
            {
                using var color = new Mat(); using var mask = new Mat();
                if (flip) { Cv2.Flip(source.Color, color, FlipMode.Y); Cv2.Flip(source.Mask, mask, FlipMode.Y); }
                else { source.Color.CopyTo(color); source.Mask.CopyTo(mask); }
                foreach (int size in new[] { 104, 120, 136, 152, 168 })
                {
                    double scale = size * .5 / Math.Max(color.Width, color.Height);
                    var target = new Size((int)Math.Round(color.Width * scale), (int)Math.Round(color.Height * scale));
                    var a = new Mat(); var b = new Mat();
                    Cv2.Resize(color, a, target); Cv2.Resize(mask, b, target, interpolation: InterpolationFlags.Nearest);
                    result.Add(new(name, source.Tier, size, a, b));
                }
            }
        _scaled.Add(name, result);
        return result;
    }

    public void Dispose()
    {
        foreach (var t in _source.Concat(_scaled.Values.SelectMany(v => v)))
        { t.Color.Dispose(); t.Mask.Dispose(); }
    }
}
