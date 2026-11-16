using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

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

    /// <summary>判断面板要看的那一块（1920×1080 下 x 1110～1260、y 105～240）在截图里的位置；探针只截这一块。</summary>
    public static Rectangle PanelStrip(int width, int height)
    {
        double s = Math.Min(width / 1920d, height / 1080d);
        return new((int)(1110 * s), (int)(105 * s), (int)(150 * s), (int)(135 * s));
    }

    /// <summary>敌人面板最大能占的范围（1920×1080 下 x 1110～1670、y 95～855）。</summary>
    public static Rectangle PanelArea(int width, int height)
    {
        double s = Math.Min(width / 1920d, height / 1080d);
        return new((int)(1110 * s), (int)(95 * s), (int)(560 * s), (int)(760 * s));
    }

    /// <summary>
    /// 不用 OCR 判断敌人面板在不在：标题框里「敌人」两个白字占的比例很固定（约 3.7%），
    /// 面板左边缘内侧是均匀的深色。商店、大厅、编队页的白字更多或底色不匀，靠这两项分开。
    /// strip 是 PanelStrip 那一块，s 是截图相对 1920×1080 的缩放。
    /// </summary>
    public static bool PanelVisible(Bitmap strip, double s)
    {
        var data = strip.LockBits(new(0, 0, strip.Width, strip.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var px = new byte[data.Stride * data.Height];
        Marshal.Copy(data.Scan0, px, 0, px.Length);
        strip.UnlockBits(data);
        int stride = data.Stride;
        // 坐标相对 PanelStrip 左上角（1110, 105）
        (double Mean, double Std, double White) Scan(int x, int y, int w, int h)
        {
            double sum = 0, square = 0; int white = 0, count = 0;
            for (int row = (int)(y * s); row < Math.Min(strip.Height, (int)((y + h) * s)); row++)
            for (int col = (int)(x * s); col < Math.Min(strip.Width, (int)((x + w) * s)); col++)
            {
                int i = row * stride + col * 4;
                byte b = px[i], g = px[i + 1], r = px[i + 2];
                sum += b + g + r;
                square += b * b + g * g + r * r;
                if (Math.Min(r, Math.Min(g, b)) > 185) white++;
                count++;
            }
            if (count == 0) return (255, 255, 0);
            double mean = sum / (3 * count);
            return (mean, Math.Sqrt(Math.Max(0, square / (3 * count) - mean * mean)), (double)white / count);
        }
        var title = Scan(58, 0, 90, 40);
        var edge = Scan(2, 55, 10, 80);
        return title.White > .025 && title.White < .06 && edge.Mean < 25 && edge.Std < 12;
    }

    /// <summary>整张截图（PNG）上判断，样图和模拟器截图走这里。</summary>
    public static bool PanelVisible(byte[] png)
    {
        using var stream = new MemoryStream(png);
        using var image = new Bitmap(stream);
        return PanelVisible(image);
    }

    public static bool PanelVisible(Bitmap image)
    {
        var area = Rectangle.Intersect(PanelStrip(image.Width, image.Height), new(0, 0, image.Width, image.Height));
        if (area.Width <= 0 || area.Height <= 0) return false;
        using var strip = image.Clone(area, PixelFormat.Format32bppArgb);
        return PanelVisible(strip, Math.Min(image.Width / 1920d, image.Height / 1080d));
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
            if (_visible) AppLogger.Info("RoundEnemy", "敌人面板标题读出");
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
