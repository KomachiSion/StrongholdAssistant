using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using OpenCvSharp;

namespace StrongholdAssistant.Tests;

public sealed class FieldViewportTests
{
    [Theory]
    [InlineData("field-57.jpg", true)]
    [InlineData("prep-ready-1920x1080.png", false)]
    public async Task WideViewportPreservesCellsSpritesAndMapsOverlayBack(string sample, bool shopVisible)
    {
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        var bytes = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", sample));
        using var original = Cv2.ImDecode(bytes, ImreadModes.Color);
        using var canonical = new Mat();
        Cv2.Resize(original, canonical, new Size(1920, 1080));
        using var wide = new Mat();
        Cv2.CopyMakeBorder(canonical, wide, 0, 0, 64, 64, BorderTypes.Constant, Scalar.All(0));
        var normalReader = new FieldReader(data, new EmptyOcr(), []);
        var wideReader = new FieldReader(data, new EmptyOcr(), []);
        var at = DateTimeOffset.UtcNow;
        var normal = await normalReader.ReadAsync(canonical.ImEncode(".png"), at, new(1, null, null, null, true), true, shopVisible, []);
        var actual = await wideReader.ReadAsync(wide.ImEncode(".png"), at, new(1, null, null, null, true), true, shopVisible, []);
        Assert.NotEmpty(normal.Sprites!);
        Assert.Equal(normal.Cells.OrderBy(p => p.Key.ToString()), actual.Cells.OrderBy(p => p.Key.ToString()));
        foreach (var (cell, patch) in normal.Sprites!)
        {
            Assert.Equal(patch.Image, actual.Sprites![cell].Image);
            var a = normalReader.OverlayGeometry!.Anchors[cell];
            var b = wideReader.OverlayGeometry!.Anchors[cell];
            Assert.Equal(a.X * 1920 + 64, b.X * 2048, 3);
            Assert.Equal(a.Y, b.Y, 6);
        }
        // 原画面中的鼠标遮挡必须落到转换后的同一格。
        var target = actual.Sprites!.Keys.First();
        var anchor = wideReader.OverlayGeometry!.Anchors[target];
        var obscured = await wideReader.ReadAsync(wide.ImEncode(".png"), at, new(1, null, null, null, true), true, shopVisible, [],
            new System.Drawing.RectangleF((float)anchor.X - .01f, (float)anchor.Y - .01f, .02f, .02f));
        Assert.DoesNotContain(target, obscured.Cells.Keys);
    }

    [Fact]
    public void TwoSameNameUnitsDoNotPromoteTheSecondTier()
    {
        var tracker = new FieldTracker();
        tracker.ConfigureIdentity([new() { Name = "甲", Tier = 1 }],
            (_, _, _) => new([new("甲", .9)], .9));
        var cells = new Dictionary<FieldCell, int> { [new("board", 1, 1)] = 1, [new("board", 2, 1)] = 1 };
        var observation = new FieldObservation(DateTimeOffset.UtcNow, true, "m01", 1, cells);
        tracker.Observe(observation, force: true);
        foreach (var cell in cells.Keys) tracker.SetOperator(cell, "甲", observation.At);
        tracker.Observe(observation with { At = observation.At.AddSeconds(1) }, force: true);
        Assert.Equal(2, tracker.State!.Units.Length);
        Assert.All(tracker.State.Units, u => { Assert.Equal("甲", u.Operator); Assert.Equal(1, u.Tier); });
    }

    private sealed class EmptyOcr : IOcrEngine
    {
        public Task<IReadOnlyList<OcrTextResult>> RecognizeRegionAsync(byte[] b, int x, int y, int w, int h)
            => Task.FromResult<IReadOnlyList<OcrTextResult>>([]);
        public Task<IReadOnlyList<OcrTextResult>> RecognizeLineAsync(byte[] b, int x, int y, int w, int h)
            => RecognizeRegionAsync(b, x, y, w, h);
        public Task<string?> FindCandidateInRegionAsync(byte[] b, int x, int y, int w, int h, IReadOnlyList<string> c)
            => Task.FromResult<string?>(null);
    }
}
