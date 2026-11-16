using System.Drawing;
using System.Reflection;
using ASPAssistant.Core.Services;
using ASPAssistant.Tests.Services;
using ASPAssistant.Core.GameModes.GarrisonProtocol;

namespace StrongholdAssistant.Tests;

// 敌人面板快速通道：不用 OCR 认出面板；面板帧只读敌人，跳过商店和场上认人
[Collection("Native OCR")]
public sealed class EnemyPanelTests(EnemyRecognitionFixture fixture)
{
    private static string TestData => Path.Combine(AppContext.BaseDirectory, "TestData");

    [Fact]
    public void PanelCheckOnlyMatchesEnemyPanelAmongSamples()
    {
        var hits = Directory.GetFiles(TestData)
            .Where(f => f.EndsWith(".png") || f.EndsWith(".jpg") || f.EndsWith(".bmp"))
            .Where(f => { using var image = new Bitmap(f); return image.Width >= 800 && RoundEnemyReader.PanelVisible(image); })
            .Select(f => Path.GetFileName(f)).ToArray();
        Assert.Equal(["board-enemy-panel.png"], hits);
    }

    [Fact]
    public async Task PanelFrameReadsEnemiesAndSkipsFieldRecognition()
    {
        using var scanner = new OcrScannerService(new ScreenCaptureService(), new GarrisonOcrStrategy(),
            new ShopReader(fixture.Engine, []), fixture.Engine, new ASPAssistant.Core.GameState.GameState(), () => []);
        typeof(OcrScannerService).GetField("_lastEconomy", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(scanner, new EconomyReading(1, 4, 1, 4, true));
        int match = 0, field = 0, seen = 0;
        scanner.ObserveMatchFrameAsync = (_, _, _, _, _) => { match++; return Task.CompletedTask; };
        scanner.ObserveFieldAsync = (_, _, _, _, _, _) => { field++; return Task.CompletedTask; };
        scanner.EnemyPanelSeen += () => seen++;
        var shot = File.ReadAllBytes(Path.Combine(TestData, "board-enemy-panel.png"));
        using (var image = new Bitmap(new MemoryStream(shot)))
            await (Task)typeof(OcrScannerService).GetMethod("ScanFrameAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(scanner, [shot, image.Width, image.Height, DateTimeOffset.UtcNow, 0d, null, 0d])!;
        Assert.Equal((1, 0, 1), (match, field, seen));
    }
}
