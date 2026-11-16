using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using ASPAssistant.Core.Services;
using OpenCvSharp;

namespace StrongholdAssistant.Tests;

public sealed class SpriteMatcherSkinTests
{
    [Theory]
    [InlineData(30, false)]
    [InlineData(60, true)]
    public void PhysicalScalePreservesDifferentWidthsAndMirroredOrigins(int width, bool flip)
    {
        string file = Path.GetTempFileName();
        using var image = new Mat(32, width, MatType.CV_8UC3);
        var pixels = new byte[32 * width * 3]; new Random(223).NextBytes(pixels);
        Marshal.Copy(pixels, 0, image.Data, pixels.Length);
        try
        {
            using (var writer = new BinaryWriter(new GZipStream(File.Create(file), CompressionLevel.Fastest)))
            {
                writer.Write(Encoding.ASCII.GetBytes("SPRT0004")); writer.Write((ushort)60);
                writer.Write(.25f); writer.Write(1);
                Write(writer, "甲", "default", image, new Point2d(12, 28));
            }
            using var matcher = new SpriteMatcher(file);
            using var crop = new Mat(204, 180, MatType.CV_8UC3, Scalar.All(0));
            using var source = new Mat();
            if (flip) Cv2.Flip(image, source, FlipMode.Y); else image.CopyTo(source);
            // 同一屏幕比例下，各模板保留自己的宽度，不统一最长边。
            using var placed = new Mat(crop, new Rect(120 - (flip ? width - 12 : 12) * 2, 100, width * 2, 64));
            Cv2.Resize(source, placed, placed.Size(), interpolation: InterpolationFlags.Nearest);
            Assert.True(matcher.MatchCrop(crop, false, 1, ["甲"], new Point(120, 156), screenPixelsPerUnit: .5).Candidates.Single().Score > .99);
            // 人工标定入口在旧裁块内移脚点，尺度仍由格宽统一决定。
            double cellWidth = .5 / .00271;
            int calibratedX = (int)Math.Round(90.49 + cellWidth * .061382994597481755);
            int calibratedY = (int)Math.Round(155.51 + cellWidth * .1774000064377288);
            using var calibrated = new Mat(204, 180, MatType.CV_8UC3, Scalar.All(0));
            using var target = new Mat(calibrated, new Rect(calibratedX - (flip ? width - 12 : 12) * 2,
                calibratedY - 56, width * 2, 64));
            Cv2.Resize(source, target, target.Size(), interpolation: InterpolationFlags.Nearest);
            var aligned = new ASPAssistant.Core.Models.FieldSpritePatch(calibrated.ImEncode(".png"), [], false)
            { Geometry = new(cellWidth, 0, 0, 1000.49, 599.51) };
            Assert.True(matcher.MatchCellWidth(aligned, 1, ["甲"]).Candidates.Single().Score > .99);
            Assert.Throws<ArgumentException>(() => matcher.MatchCrop(crop, false, 1, ["甲"]));
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public void FootOriginSurvivesScalingAndMirroring(bool flip, bool bench)
    {
        string file = Path.GetTempFileName();
        using var image = new Mat(40, 60, MatType.CV_8UC3);
        var pixels = new byte[40 * 60 * 3]; new Random(219).NextBytes(pixels);
        Marshal.Copy(pixels, 0, image.Data, pixels.Length);
        try
        {
            using (var writer = new BinaryWriter(new GZipStream(File.Create(file), CompressionLevel.Fastest)))
            {
                writer.Write(Encoding.ASCII.GetBytes("SPRT0003")); writer.Write((ushort)60); writer.Write(1);
                Write(writer, "甲", "default", image, new Point2d(15, 30));
            }
            using var matcher = new SpriteMatcher(file);
            using var crop = new Mat(204, 180, MatType.CV_8UC3, Scalar.All(0));
            using var source = new Mat();
            if (flip) Cv2.Flip(image, source, FlipMode.Y); else image.CopyTo(source);
            // 原点不在外框底部，也不在水平中心；脚点正好落到新包默认锚点。
            using var placed = new Mat(crop, new Rect(flip ? 0 : 60, (bench ? 180 : 156) - 60, 120, 80));
            Cv2.Resize(source, placed, placed.Size(), interpolation: InterpolationFlags.Nearest);
            Assert.True(matcher.MatchCrop(crop, bench, 1, ["甲"]).Candidates.Single().Score > .99);
        }
        finally { File.Delete(file); }
    }

    [Theory]
    [InlineData(60)]
    [InlineData(120)]
    public void NewPackageUsesBestSkinAndRosterKeepsDefault(int size)
    {
        string file = Path.GetTempFileName();
        using var costume = new Mat(size, size, MatType.CV_8UC3);
        var pixels = new byte[size * size * 3]; new Random(214).NextBytes(pixels);
        Marshal.Copy(pixels, 0, costume.Data, pixels.Length);
        using var ordinary = new Mat(size, size, MatType.CV_8UC3);
        Cv2.Flip(costume, ordinary, FlipMode.X);
        try
        {
            using (var writer = new BinaryWriter(new GZipStream(File.Create(file), CompressionLevel.Fastest)))
            {
                writer.Write(Encoding.ASCII.GetBytes("SPRT0002")); writer.Write((ushort)size); writer.Write(3);
                Write(writer, "甲", "default", ordinary);
                Write(writer, "甲", "甲@skin#1", costume);
                Write(writer, "乙", "default", ordinary);
            }
            using var matcher = new SpriteMatcher(file);
            using var crop = new Mat(204, 180, MatType.CV_8UC3, Scalar.All(0));
            using var placed = new Mat(crop, new Rect(30, 56, 120, 120));
            Cv2.Resize(costume, placed, placed.Size());
            var all = matcher.MatchCrop(crop, true, 1, ["甲", "乙"]);
            Assert.Equal("甲", all.Candidates[0].Name);
            var selected = matcher.MatchCrop(crop, true, 1, ["甲"], skins: new Dictionary<string,string?> { ["甲"] = "甲@skin#1" });
            Assert.Equal(all.Candidates[0].Score, selected.Candidates[0].Score, 8);
            var other = matcher.MatchCrop(crop, true, 1, ["甲"], skins: new Dictionary<string,string?> { ["甲"] = "甲@skin#2" });
            Assert.True(selected.Candidates[0].Score > other.Candidates[0].Score + .1);
            using var ordinaryPlaced = new Mat(crop, new Rect(30,56,120,120));
            Cv2.Resize(ordinary,ordinaryPlaced,ordinaryPlaced.Size());
            var fallback = matcher.MatchCrop(crop,true,1,["甲"],skins:new Dictionary<string,string?> { ["甲"]="甲@skin#1" });
            Assert.True(fallback.Candidates[0].Score > .9);
        }
        finally { File.Delete(file); }
    }

    private static void Write(BinaryWriter writer,string name,string skin,Mat image,Point2d? origin = null)
    {
        foreach(string value in new[] { name,skin }) { var b=Encoding.UTF8.GetBytes(value);writer.Write((ushort)b.Length);writer.Write(b); }
        writer.Write((ushort)1);writer.Write((ushort)image.Width);writer.Write((ushort)image.Height);
        if (origin is { } p) { writer.Write((float)p.X); writer.Write((float)p.Y); }
        var pixels=new byte[image.Total()*image.ElemSize()];Marshal.Copy(image.Data,pixels,0,pixels.Length);writer.Write(pixels);
        writer.Write(Enumerable.Repeat((byte)255,image.Width*image.Height).ToArray());
    }
}
