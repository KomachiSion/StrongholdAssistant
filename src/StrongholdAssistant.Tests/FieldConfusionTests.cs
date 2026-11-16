using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;
using OpenCvSharp;

namespace StrongholdAssistant.Tests;

public sealed class FieldConfusionTests
{
    [Theory]
    [InlineData("灰喉", "摆渡人", .149, "pending")]
    [InlineData("摆渡人", "灰喉", .149, "pending")]
    [InlineData("灰喉", "摆渡人", .15, "auto")]
    [InlineData("灰喉", "杰克", .149, "auto")]
    public void CloseConfusionOnlyChangesStatus(string first, string second, double gap, string expected)
    {
        var tracker = new FieldTracker();
        tracker.ConfigureIdentity([new() { Name = first, Tier = 1 }, new() { Name = second, Tier = 1 }],
            (_, _, _) => new([new(first, .5 + gap), new(second, .5)], null));
        var cell = new FieldCell("board", 1, 1);
        using var image = new Mat(204, 180, MatType.CV_8UC3, Scalar.All(100));
        tracker.Observe(new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"), true, "m01", 1,
            new Dictionary<FieldCell, int> { [cell] = 1 })
        { Sprites = new Dictionary<FieldCell, FieldSpritePatch> { [cell] = FieldReader.CropSprite(image, 90, 156, false)! } }, force: true);
        var unit = Assert.Single(tracker.State!.Units);
        Assert.Equal(expected, unit.Status);
        Assert.Equal(first, unit.Operator);
        Assert.Equal(new[] { first, second }, unit.Candidates.Select(c => c.Operator));
    }
}
