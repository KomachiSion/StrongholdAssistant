using System.Drawing;
using System.Text.Json;

namespace ASPAssistant.Core.Services;

/// <summary>圆内头像相关性匹配，以及选定策略页的名称读取。</summary>
public sealed class MatchStrategyReader(IOcrEngine ocr, string dataDirectory, IReadOnlyDictionary<string, string> names)
{
    private readonly Dictionary<string, double[]> _portraits = JsonSerializer.Deserialize<Dictionary<string, double[]>>(
        File.ReadAllText(Path.Combine(dataDirectory, "strategy_portraits.json")))!
        .ToDictionary(p => p.Key, p => Normalize(p.Value));

    private static double[] Normalize(double[] values)
    {
        double mean = values.Average();
        var result = values.Select(v => v - mean).ToArray();
        double length = Math.Sqrt(result.Sum(v => v * v));
        return length == 0 ? result : result.Select(v => v / length).ToArray();
    }

    public (string? Id, double Score, double Margin) ReadPortrait(byte[] frame, double uiScale = 1)
    {
        using var stream = new MemoryStream(frame);
        using var image = new Bitmap(stream);
        double scale = Math.Min(image.Width / 1920d, image.Height / 1080d) * uiScale;
        using var small = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(small))
            g.DrawImage(image, new Rectangle(0, 0, 32, 32),
                new RectangleF((float)(image.Width - 126 * scale), (float)(257 * scale),
                    (float)(116 * scale), (float)(116 * scale)), GraphicsUnit.Pixel);
        var pixels = new List<double>();
        for (int y = 0; y < 32; y++) for (int x = 0; x < 32; x++)
        {
            if ((x - 15.5) * (x - 15.5) + (y - 15.5) * (y - 15.5) >= 144) continue;
            var c = small.GetPixel(x, y);
            pixels.Add(.299 * c.R + .587 * c.G + .114 * c.B);
        }
        var sample = Normalize(pixels.ToArray());
        var scores = _portraits.Select(p => (Id: p.Key, Score: p.Value.Zip(sample, (a, b) => a * b).Sum()))
            .OrderByDescending(p => p.Score).Take(2).ToArray();
        double margin = scores[0].Score - scores[1].Score;
        return (scores[0].Score >= .75 && margin >= .15 ? scores[0].Id : null, scores[0].Score, margin);
    }

    public async Task<string?> ReadSelectionAsync(byte[] frame, int width, int height)
    {
        double s = Math.Min(width / 1920d, height / 1080d);
        var lines = await ocr.RecognizeRegionAsync(frame, (int)(1100 * s), (int)(420 * s), (int)(720 * s), (int)(400 * s));
        string text = string.Concat(lines.Select(l => l.Text.Replace(" ", "")));
        if (!text.Contains("已选定策略")) return null;
        var matches = names.Where(p => text.Contains(p.Value, StringComparison.Ordinal)).Select(p => p.Key).ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }
}
