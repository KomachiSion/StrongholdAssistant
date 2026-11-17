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
public sealed partial class SpriteMatcher : IDisposable
{
    public const double AutoFillGap = 0.04592923876191826;
    private sealed record Template(string Name, string SkinId, int Tier, int Size, Mat Color, Mat Mask, Point2d? Origin, int Variant = 0);
    private readonly double _resolution;
    private readonly bool _hasOrigins;
    private readonly double _pixelsPerUnit;
    public bool UsesPhysicalScale => _pixelsPerUnit > 0;
    private readonly List<Template> _source = [];
    // 比对线程独立持有缩放图，避免其他格换尺度时释放正在使用的 Mat。
    private readonly ThreadLocal<Dictionary<string, List<Template>>> _scaledByThread = new(() => new(StringComparer.Ordinal), true);
    private Dictionary<string, List<Template>> _scaled => _scaledByThread.Value!;

    public SpriteMatcher(string packagePath)
    {
        using var file = File.OpenRead(packagePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new BinaryReader(gzip, Encoding.UTF8);
        string version = Encoding.ASCII.GetString(reader.ReadBytes(8));
        if (version is not ("SPRT0001" or "SPRT0002" or "SPRT0003" or "SPRT0004"))
            throw new InvalidDataException("小人模板包版本不符。");
        // 旧包继续使用原有半尺寸口径，新包由生成器声明归一边长。
        _hasOrigins = version is "SPRT0003" or "SPRT0004";
        _resolution = version != "SPRT0001" ? reader.ReadUInt16() / 120.0 : .5;
        if (_resolution is not (.5 or 1)) throw new InvalidDataException("模板分辨率必须为60或120。");
        if (version == "SPRT0004")
        {
            _pixelsPerUnit = reader.ReadSingle();
            if (!double.IsFinite(_pixelsPerUnit) || _pixelsPerUnit <= 0)
                throw new InvalidDataException("骨骼单位比例必须为有限正数。");
        }
        int count = reader.ReadInt32();
        for (int i = 0; i < count; i++)
        {
            string name = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));
            string skinId = version != "SPRT0001" ? Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16())) : "default";
            int tier = reader.ReadUInt16(), width = reader.ReadUInt16(), height = reader.ReadUInt16();
            Point2d? origin = _hasOrigins ? new Point2d(reader.ReadSingle(), reader.ReadSingle()) : null;
            if (origin is { } p && (!double.IsFinite(p.X) || !double.IsFinite(p.Y)))
                throw new InvalidDataException("模板原点必须为有限坐标。");
            var color = new Mat(height, width, MatType.CV_8UC3);
            var mask = new Mat(height, width, MatType.CV_8UC1);
            var pixels = new byte[width * height * 3]; reader.BaseStream.ReadExactly(pixels);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, color.Data, pixels.Length);
            pixels = new byte[width * height]; reader.BaseStream.ReadExactly(pixels);
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, mask.Data, pixels.Length);
            _source.Add(new(name, skinId, tier, 0, color, mask, origin, i));
        }
    }

    /// <summary>按人工标定的脚点和格宽匹配，并排除前排遮挡轮廓。</summary>
    public SpriteMatch MatchCellWidth(FieldSpritePatch patch, int tier, IReadOnlyCollection<string> candidates,
        IReadOnlyDictionary<string, string?>? skins = null, int searchRadius = 3, bool quick = false)
    {
        if (!UsesPhysicalScale || patch.Geometry is not { } g)
            throw new ArgumentException("格宽匹配需要真实比例模板及本帧格子几何。");
        double dx = g.CellWidth * (patch.Bench ? .05017159418908035 : .061382994597481755);
        double dy = g.CellWidth * (patch.Bench ? .03892515182781064 : .1774000064377288);
        var region = patch.TemplateRegion;
        using var crop = Cv2.ImDecode(region?.Image ?? patch.Image, ImreadModes.Color);
        var anchor = new Point((int)Math.Round(90 + g.BadgeFootX + dx - Math.Round(g.BadgeFootX)) + (region?.OffsetX ?? 0),
            (int)Math.Round((patch.Bench ? 180 : 156) + g.BadgeFootY + dy - Math.Round(g.BadgeFootY)) + (region?.OffsetY ?? 0));
        using var blocked = patch.OcclusionMask is null ? new Mat() : Cv2.ImDecode(patch.OcclusionMask, ImreadModes.Grayscale);
        return MatchCropCore(crop, patch.Bench, tier, candidates, anchor, skins, g.CellWidth * .00271, blocked, patch, searchRadius, quick);
    }

    /// <summary>离线裁块入口。默认180×204；自定义锚点为相对裁块的脚点（旧包为外框底部）。</summary>
    public SpriteMatch MatchCrop(Mat crop, bool bench, int tier, IReadOnlyCollection<string> candidates,
        Point? searchAnchor = null, IReadOnlyDictionary<string, string?>? skins = null,
        double? screenPixelsPerUnit = null, Mat? occlusion = null, int searchRadius = 3)
        => MatchCropCore(crop, bench, tier, candidates, searchAnchor, skins, screenPixelsPerUnit, occlusion, null, searchRadius, false);

    private SpriteMatch MatchCropCore(Mat crop, bool bench, int tier, IReadOnlyCollection<string> candidates,
        Point? searchAnchor, IReadOnlyDictionary<string, string?>? skins,
        double? screenPixelsPerUnit, Mat? occlusion, FieldSpritePatch? fieldPatch, int searchRadius, bool quick)
    {
        using var timing = FieldRecognitionTiming.Measure("templates");
        if (crop.Empty() || crop.Type() != MatType.CV_8UC3)
            throw new ArgumentException("裁块必须为BGR图像。", nameof(crop));
        if (UsesPhysicalScale && (screenPixelsPerUnit is not { } unit || !double.IsFinite(unit) || unit <= 0))
            throw new ArgumentException("真实比例模板需要已标定的屏幕像素/骨骼单位。", nameof(screenPixelsPerUnit));
        using var half = new Mat();
        Cv2.Resize(crop, half, new Size((int)(crop.Width * _resolution), (int)(crop.Height * _resolution)));
        // 读取层的棋盘裁块向下移24像素，原始脚点在裁块内156；整备区为180。
        // 旧包保留冻结的外框底边口径，新包直接对齐骨骼原点。
        var anchor = searchAnchor ?? new Point(90, _hasOrigins ? (bench ? 180 : 156) : (bench ? 176 : 180));
        int bottom = (int)(anchor.Y * _resolution);
        int radius = (int)((UsesPhysicalScale ? searchRadius : 16) * _resolution);
        using var blocked = new Mat();
        if (occlusion is { } && !occlusion.Empty())
        {
            Cv2.Resize(occlusion, blocked, half.Size(), interpolation: InterpolationFlags.Nearest);
            // 搜索窗口内所有位移都排除前景，避免平移后把遮挡边缘重新算入。
            using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(radius * 4 + 1, radius * 4 + 1));
            Cv2.Dilate(blocked, blocked, kernel);
        }
        // 缩放缓存仍由扫描线程构建；各候选只读模板，并行时独占临时 Mat。
        using var neighbor = fieldPatch?.NeighborMask is { } bytes
            ? Cv2.ImDecode(bytes, ImreadModes.Grayscale) : new Mat();
        var work = candidates.Distinct(StringComparer.Ordinal).Select(name => (Name: name,
            Templates: Templates(AppearanceName(name, skins), screenPixelsPerUnit), Skin: skins?.GetValueOrDefault(name),
            Excluded: neighbor.Empty() ? new Mat() : NeighborExclusion(fieldPatch!, neighbor, tier, [name], skins))).ToArray();
        var ranked = new SpriteCandidate?[work.Length];
        Parallel.For(0, work.Length, new ParallelOptions { MaxDegreeOfParallelism = 4 }, index =>
        {
            var (name, templates, skin, excludedNeighbor) = work[index];
            using var candidateBlocked = excludedNeighbor;
            if (!candidateBlocked.Empty())
            {
                Cv2.Resize(candidateBlocked, candidateBlocked, half.Size(), interpolation: InterpolationFlags.Nearest);
                using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(radius * 4 + 1, radius * 4 + 1));
                Cv2.Dilate(candidateBlocked, candidateBlocked, kernel);
                if (!blocked.Empty()) Cv2.BitwiseOr(candidateBlocked, blocked, candidateBlocked);
            }
            var exclusion = candidateBlocked.Empty() ? blocked : candidateBlocked;
            using var scores = new Mat(); using var difference = new Mat();
            var values = new float[(radius * 2 + 1) * (radius * 2 + 1)];
            double best = -2;
            // 召唤物预筛每套皮肤仅取首尾外观和标定尺度；干员路径保持原样。
            var limits = quick ? templates.GroupBy(t => t.SkinId).ToDictionary(g => g.Key,
                g => (First: g.Min(t => t.Variant), Last: g.Max(t => t.Variant))) : null;
            foreach (var template in templates)
            {
                if (limits is not null && (template.Size != 100 || template.Variant != limits[template.SkinId].First
                    && template.Variant != limits[template.SkinId].Last)) continue;
                if (!AllowsSkin(template.SkinId, skin)) continue;
                if (template.Tier != tier || (!UsesPhysicalScale && (bench ? template.Size == 104 : template.Size == 168))) continue;
                var a = template.Color; var mask = template.Mask;
                int w = a.Width, h = a.Height;
                int center = (int)Math.Round(anchor.X * _resolution - (template.Origin?.X ?? w / 2.0));
                int top = template.Origin is { } origin ? (int)Math.Round(anchor.Y * _resolution - origin.Y) : bottom - h;
                int x0 = Math.Max(0, center - radius), x1 = Math.Min(half.Width, center + radius + w);
                int y0 = Math.Max(0, top - radius), y1 = Math.Min(half.Height, top + h + radius);
                if (x1 - x0 < w || y1 - y0 < h) continue;
                using var visible = new Mat();
                if (!exclusion.Empty())
                {
                    using var excluded = new Mat(exclusion, new Rect(x0, y0, w, h));
                    Cv2.BitwiseNot(excluded, visible);
                    Cv2.BitwiseAnd(mask, visible, visible);
                    if (Cv2.CountNonZero(visible) < 16) continue;
                    mask = visible;
                }
                using var roi = new Mat(half, new Rect(x0, y0, x1 - x0, y1 - y0));
                Cv2.MatchTemplate(roi, a, scores, TemplateMatchModes.CCoeffNormed, mask);
                // 与Python的nan_to_num一致，非有限值不能成为最佳位置。
                // 按分辨率取相同物理搜索范围；批量读取，避免逐像素跨托管边界。
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

    private List<Template> Templates(string name, double? screenPixelsPerUnit = null)
    {
        using var timing = FieldRecognitionTiming.Measure("templateScale");
        string key = UsesPhysicalScale ? $"{name}@{screenPixelsPerUnit:R}" : name;
        if (_scaled.TryGetValue(key, out var cached)) return cached;
        // 拟合投影每帧会微调格宽；每人仅留当前尺度，避免整局积累连续尺度的原生图像。
        if (UsesPhysicalScale)
            foreach (var old in _scaled.Keys.Where(k => k.StartsWith(name + "@", StringComparison.Ordinal)).ToArray())
            {
                foreach (var template in _scaled[old]) { template.Color.Dispose(); template.Mask.Dispose(); }
                _scaled.Remove(old);
            }
        var result = new List<Template>();
        foreach (var source in _source.Where(t => t.Name == name))
            foreach (bool flip in new[] { false, true })
            {
                using var color = new Mat(); using var mask = new Mat();
                if (flip) { Cv2.Flip(source.Color, color, FlipMode.Y); Cv2.Flip(source.Mask, mask, FlipMode.Y); }
                else { source.Color.CopyTo(color); source.Mask.CopyTo(mask); }
                foreach (int size in UsesPhysicalScale ? new[] { 97, 100, 103 } : new[] { 104, 120, 136, 152, 168 })
                {
                    double scale = UsesPhysicalScale
                        ? screenPixelsPerUnit!.Value * _resolution / _pixelsPerUnit * size / 100
                        : size * _resolution / Math.Max(color.Width, color.Height);
                    var target = new Size((int)Math.Round(color.Width * scale), (int)Math.Round(color.Height * scale));
                    var a = new Mat(); var b = new Mat();
                    Cv2.Resize(color, a, target); Cv2.Resize(mask, b, target, interpolation: InterpolationFlags.Nearest);
                    // 原点使用像素边界坐标；镜像后为宽度减X，缩放按实际取整后的宽高分别换算。
                    Point2d? origin = source.Origin is { } p
                        ? new Point2d((flip ? color.Width - p.X : p.X) * target.Width / color.Width,
                            p.Y * target.Height / color.Height) : null;
                    result.Add(new(name, source.SkinId, source.Tier, size, a, b, origin, source.Variant));
                }
            }
        _scaled.Add(key, result);
        return result;
    }

    public void Dispose()
    {
        foreach (var t in _source.Concat(_scaledByThread.Values.SelectMany(d => d.Values).SelectMany(v => v)))
        { t.Color.Dispose(); t.Mask.Dispose(); }
        _scaledByThread.Dispose();
    }
}
