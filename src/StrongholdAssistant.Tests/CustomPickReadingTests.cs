using System.Diagnostics;
using ASPAssistant.Core.Services;
using ASPAssistant.Tests.Services;
using StrongholdAssistant.Host;
using Xunit.Abstractions;

namespace StrongholdAssistant.Tests;

[Collection("Native OCR")]
public sealed class CustomPickReadingTests(EnemyRecognitionFixture fixture, ITestOutputHelper output)
{
    private static string Data => Path.Combine(AppContext.BaseDirectory, "data");
    private static byte[] Frame(string name) => File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData", name + ".png"));
    private static Task<HostApi> Api() => HostApi.CreateAsync(Data, Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));

    [Fact]
    public async Task SelectionReadsCardsAndBansWithoutPublishingFinalPicks()
    {
        var api = await Api();
        var names = api.Catalog.GetProperty("operators").EnumerateArray().Where(o => o.GetProperty("diy").GetBoolean())
            .Select(o => o.GetProperty("name").GetString()!).ToArray();
        var reader = new CustomPickReader(fixture.Engine, names);
        var result = await reader.ReadAsync(Frame("custom-picks-selection"), 1920, 1080);
        Assert.NotNull(result);
        Assert.False(result.IsFinal);
        Assert.Equal(6, result.Cards.Length);
        Assert.All(result.Cards, c => Assert.Equal(5, c.Tier));
        Assert.Equal(new string?[] { "赤刃明霄陈", null, "结城理", "望", "予愿安洁莉娜", "凯尔希·思衡托" }, result.Cards.Select(c => c.Name));
        Assert.Equal(new[] { 0, 1, 3 }, result.Cards.Where(c => c.State == CustomPickCardState.Banned).Select(c => c.Slot));
        Assert.All(result.Cards.Where(c => c.State != CustomPickCardState.Banned), c => Assert.Equal(CustomPickCardState.Unselected, c.State));
        output.WriteLine($"两排名条 OCR {result.CardsMs:F1} ms；V 每卡摊销 {result.CardsMs / 6:F1} ms；VI 无卡。");
        api.InitializeMatchReading(Data, fixture.Engine);
        await api.ObserveMatchFrameAsync(Frame("custom-picks-selection"), 1920, 1080, null, 1);
        Assert.Null(api.Match.CustomPicks);
    }

    [Fact]
    public async Task SoloFinalReadsTwoPlusTwoAndSelectionDoesNotOverwrite()
    {
        var api = await Api();
        var names = api.Catalog.GetProperty("operators").EnumerateArray().Where(o => o.GetProperty("diy").GetBoolean())
            .Select(o => o.GetProperty("name").GetString()!).ToArray();
        var result = await new CustomPickReader(fixture.Engine, names)
            .ReadAsync(Frame("custom-picks-solo-final-2v2"), 1920, 1080);
        Assert.NotNull(result);
        output.WriteLine($"最终名条：{string.Join(", ", result.Cards.Select(c => $"{c.Tier}:{c.Name}"))}；{result.CardsMs:F1} ms，摊销 {result.CardsMs / 4:F1} ms/卡");
        Assert.True(result.IsFinal);
        Assert.All(result.Cards, c => Assert.Equal(CustomPickCardState.Selected, c.State));
        api.InitializeMatchReading(Data, fixture.Engine);
        await api.ObserveMatchFrameAsync(Frame("custom-picks-solo-selecting-2v2"), 1920, 1080, null, 1);
        Assert.Null(api.Match.CustomPicks);
        await api.ObserveMatchFrameAsync(Frame("custom-picks-solo-final-2v2"), 1920, 1080, null, 1);
        Assert.Equal(new[] { "结城理", "凯尔希·思衡托" }, api.Match.CustomPicks![5]);
        Assert.Equal(new[] { "年", "珊比" }, api.Match.CustomPicks[6]);
        await api.ObserveMatchFrameAsync(Frame("custom-picks-solo-selecting-2v2"), 1920, 1080, null, 1);
        Assert.Equal(new[] { "结城理", "凯尔希·思衡托" }, api.Match.CustomPicks[5]);
        Assert.Equal(new[] { "年", "珊比" }, api.Match.CustomPicks[6]);
    }

    [Fact]
    public async Task FinalNoneUpdatesBothTiersThroughFrameObserver()
    {
        var api = await Api();
        api.InitializeMatchReading(Data, fixture.Engine);
        api.ApplyCustomPicks(["结城理"], ["望"]);
        var timer = Stopwatch.StartNew();
        await api.ObserveMatchFrameAsync(Frame("custom-picks-none"), 1920, 1080, null, 1);
        output.WriteLine($"最终空选择宿主识别 {timer.Elapsed.TotalMilliseconds:F1} ms");
        Assert.Empty(api.Match.CustomPicks![5]);
        Assert.Empty(api.Match.CustomPicks[6]);
    }
}
