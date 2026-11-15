using ASPAssistant.Core.Models;
using OpenCvSharp;
using System.Runtime.InteropServices;

namespace ASPAssistant.Core.Services;

public sealed partial class FieldReader
{
    public bool BossRound { get; set; }
    public string? KnownMap { get; set; }
    private readonly Dictionary<FieldCell, (double X, double Y)> _spriteFeet = [];
    private Dictionary<FieldCell, FieldSpritePatch> ReadSprites(byte[] frame,
        IReadOnlyDictionary<FieldCell, int> cells)
    {
        using var raw = Cv2.ImDecode(frame, ImreadModes.Color);
        using var image = new Mat();
        Cv2.Resize(raw, image, new Size(1920, 1080));
        var result = new Dictionary<FieldCell, FieldSpritePatch>();
        foreach (var (cell, tier) in cells)
        {
            if (tier == 0) continue;
            bool bench = cell.Zone == "bench";
            var foot = _spriteFeet[cell];
            var patch = CropSprite(image, foot.X, foot.Y, bench);
            if (patch is not null) result[cell] = patch;
        }
        return result;
    }

    // 与冻结比对器相同的裁块；脚点坐标以1920×1080计。
    public static FieldSpritePatch? CropSprite(Mat image, double footX, double footY, bool bench)
    {
        int x = (int)Math.Round(footX), y = (int)Math.Round(footY) + (bench ? 0 : 24);
        var rect = new Rect(x - 90, y - 180, 180, 204);
        if (rect.X < 0 || rect.Y < 0 || rect.Right > image.Width || rect.Bottom > image.Height) return null;
        using var crop = new Mat(image, rect);
        using var center = new Mat(crop, new Rect(50, 40, 80, 136));
        using var small = new Mat();
        Cv2.Resize(center, small, new Size(20, 34));
        var pixels = new byte[20 * 34 * 3];
        Marshal.Copy(small.Data, pixels, 0, pixels.Length);
        return new(crop.ImEncode(".png"), pixels, bench);
    }
}
