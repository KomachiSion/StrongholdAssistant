using System.Drawing;
using System.Drawing.Imaging;

namespace ASPAssistant.Core.Services;

/// <summary>标题确认后读取亮色名称；标题和列表分别缓存，滚动后才重新读取。</summary>
public sealed class RoundEnemyReader(IOcrEngine ocr)
{
    private byte[]? _titlePixels, _listPixels;
    private bool _visible;
    public void Reset() { _titlePixels = _listPixels = null; _visible = false; }

    private static byte[] Pixels(Bitmap image, Rectangle area)
    {
        using var small = new Bitmap(80, 40);
        using (var g = Graphics.FromImage(small)) g.DrawImage(image, new Rectangle(0, 0, 80, 40), area, GraphicsUnit.Pixel);
        var result = new byte[80 * 40];
        for (int y = 0; y < 40; y++) for (int x = 0; x < 80; x++)
            result[y * 80 + x] = small.GetPixel(x, y).G;
        return result;
    }

    private static bool Same(byte[]? previous, byte[] current) => previous is not null
        && previous.Zip(current, (a, b) => Math.Abs(a - b)).Average() < 2;

    public async Task<string[]?> ReadAsync(byte[] frame)
    {
        using var stream = new MemoryStream(frame);
        using var image = new Bitmap(stream);
        double s = Math.Min(image.Width / 1920d, image.Height / 1080d);
        Rectangle Region(int x, int y, int w, int h) => new((int)(x * s), (int)(y * s), (int)(w * s), (int)(h * s));
        var title = Region(1168, 105, 90, 40);
        var titlePixels = Pixels(image, title);
        if (!Same(_titlePixels, titlePixels))
        {
            _titlePixels = titlePixels;
            var heading = await ocr.RecognizeRegionAsync(frame, title.X, title.Y, title.Width, title.Height);
            _visible = heading.Any(t => t.Text.Replace(" ", "") == "敌人");
            if (!_visible) _listPixels = null;
        }
        if (!_visible) return null;
        // 面板高度随列表长度变化，沿暗底找到底边，不把后面的棋盘文字一起读入。
        int bottom = (int)(855 * s), brightRows = 0;
        for (int y = (int)(160 * s); y < bottom; y++)
        {
            int bright = 0, count = 0;
            for (int x = (int)(1250 * s); x < (int)(1620 * s); x += 4)
            {
                if (image.GetPixel(x, y).G > 60) bright++;
                count++;
            }
            brightRows = bright > count * .65 ? brightRows + 1 : 0;
            if (brightRows == 3) { bottom = y - 2; break; }
        }
        var list = Region(1250, 155, 420, 700);
        list.Height = bottom - list.Top;
        var pixels = Pixels(image, list);
        if (Same(_listPixels, pixels)) return null;
        // 名称是亮白色，能力说明为灰色；只保留名称，避免把说明当成未知敌人。
        using var names = image.Clone(list, PixelFormat.Format24bppRgb);
        for (int y = 0; y < names.Height; y++) for (int x = 0; x < names.Width; x++)
        {
            var c = names.GetPixel(x, y);
            names.SetPixel(x, y, Math.Min(c.R, Math.Min(c.G, c.B)) >= 185 ? Color.White : Color.Black);
        }
        using var encoded = new MemoryStream();
        names.Save(encoded, ImageFormat.Png);
        var texts = await ocr.RecognizeRegionAsync(encoded.ToArray(), 0, 0, names.Width, names.Height);
        _listPixels = pixels;
        return texts.Where(t => t.Box.H >= 16 * s && t.Box.H <= 40 * s)
            .OrderBy(t => t.Box.Y).Select(t => t.Text.Trim()).Where(t => t.Length > 0).Distinct().ToArray();
    }
}
