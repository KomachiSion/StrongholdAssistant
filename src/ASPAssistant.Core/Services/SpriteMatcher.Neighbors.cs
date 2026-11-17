using ASPAssistant.Core.Models;
using OpenCvSharp;

namespace ASPAssistant.Core.Services;

public sealed partial class SpriteMatcher
{
    public const double NeighborOcclusionThreshold = .40;
    public const double NeighborMinimumScore = .45;

    public FieldSpritePatch WithNeighbors(FieldSpritePatch target,
        IEnumerable<(FieldUnit Unit, FieldSpritePatch Patch)> neighbors,
        IReadOnlyDictionary<string, string?>? skins = null)
    {
        if (target.Bench || target.Geometry is not { } g || target.OcclusionRatio < NeighborOcclusionThreshold)
            return target;
        using var image = Cv2.ImDecode(target.TemplateRegion?.Image ?? target.Image, ImreadModes.Color);
        using var mask = new Mat(image.Size(), MatType.CV_8UC1, Scalar.All(0));
        var names = new HashSet<string>();
        foreach (var (unit, patch) in neighbors)
        {
            if (unit.Operator is not { } name || unit.Status is not ("auto" or "sure")
                || patch.Bench || patch.Geometry is not { } f
                || f.BadgeFootY > g.BadgeFootY + .25 * g.CellWidth) continue;
            using var silhouette = new Mat(image.Size(), MatType.CV_8UC1, Scalar.All(0));
            ProjectNeighbor(silhouette, target, f, name, unit.Tier, skins);
            // 保险只计入原180×204取框中确实有轮廓的已识别邻居。
            using var crop = new Mat(silhouette, new Rect(target.TemplateRegion?.OffsetX ?? 0,
                target.TemplateRegion?.OffsetY ?? 0, 180, 204));
            if (Cv2.CountNonZero(crop) == 0) continue;
            names.Add(name);
            Cv2.BitwiseOr(mask, silhouette, mask);
        }
        return target with { NeighborMask = names.Count == 0 ? null : mask.ImEncode(".png"), NeighborNames = names.ToArray() };
    }

    // 模型只推理一次；邻格保护整个候选池，再限制到本格范围，模板仍独立逐候选挖空。
    public FieldSpritePatch MaskNeighbors(FieldSpritePatch target, int tier, string[] candidates,
        IReadOnlyDictionary<string, string?>? skins = null)
    {
        return CropModelInput(MaskNeighborsUncropped(target, tier, candidates, skins), tier, candidates, skins);
    }

    private FieldSpritePatch MaskNeighborsUncropped(FieldSpritePatch target, int tier, string[] candidates,
        IReadOnlyDictionary<string, string?>? skins)
    {
        if (target.NeighborMask is null) return target;
        using var neighbor = Cv2.ImDecode(target.NeighborMask, ImreadModes.Grayscale);
        using var blocked = NeighborExclusion(target, neighbor, tier, candidates, skins);
        if (target.OcclusionMask is { } bytes)
        {
            using var front = Cv2.ImDecode(bytes, ImreadModes.Grayscale);
            Cv2.BitwiseOr(blocked, front, blocked);
        }
        return target with { OcclusionMask = blocked.ImEncode(".png") };
    }

    private Mat NeighborExclusion(FieldSpritePatch target, Mat neighbor, int tier,
        IEnumerable<string> candidates, IReadOnlyDictionary<string, string?>? skins)
    {
        using var own = new Mat(neighbor.Size(), MatType.CV_8UC1, Scalar.All(0));
        // 单个候选的正背面、帧与镜像取并集，外扩3像素容纳搜索位移。
        foreach (var name in candidates) ProjectNeighbor(own, target, target.Geometry!, name, tier, skins);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(7, 7));
        Cv2.Dilate(own, own, kernel);
        Cv2.BitwiseNot(own, own);
        var excluded = new Mat();
        Cv2.BitwiseAnd(neighbor, own, excluded);
        return excluded;
    }

    private void ProjectNeighbor(Mat destination, FieldSpritePatch target, FieldSpriteGeometry f,
        string name, int tier, IReadOnlyDictionary<string, string?>? skins)
    {
        var g = target.Geometry!;
        double left = Math.Round(g.BadgeFootX) - 90 - (target.TemplateRegion?.OffsetX ?? 0);
        double top = Math.Round(g.BadgeFootY) - 156 - (target.TemplateRegion?.OffsetY ?? 0);
        double footX = f.BadgeFootX + f.CellWidth * .061382994597481755 - left;
        double footY = f.BadgeFootY + f.CellWidth * .1774000064377288 - top;
        string? skin = skins?.GetValueOrDefault(name);
        foreach (var template in Templates(AppearanceName(name, skins), f.CellWidth * .00271).Where(t => t.Size == 100
            && t.Tier == tier && AllowsSkin(t.SkinId, skin)))
        {
            using var mask = new Mat();
            Cv2.Resize(template.Mask, mask, new Size((int)Math.Round(template.Mask.Width / _resolution),
                (int)Math.Round(template.Mask.Height / _resolution)), interpolation: InterpolationFlags.Nearest);
            var origin = template.Origin!.Value;
            var rect = new Rect((int)Math.Round(footX - origin.X / _resolution),
                (int)Math.Round(footY - origin.Y / _resolution), mask.Width, mask.Height);
            var clipped = rect & new Rect(0, 0, destination.Width, destination.Height);
            if (clipped.Width <= 0 || clipped.Height <= 0) continue;
            using var source = new Mat(mask, new Rect(clipped.X - rect.X, clipped.Y - rect.Y, clipped.Width, clipped.Height));
            using var output = new Mat(destination, clipped);
            Cv2.BitwiseOr(source, output, output);
        }
    }
}
