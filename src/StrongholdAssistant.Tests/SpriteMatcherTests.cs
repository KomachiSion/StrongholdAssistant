using ASPAssistant.Core.Services;
using OpenCvSharp;

namespace StrongholdAssistant.Tests;

// 模板许可尚待确认，仓库与CI默认不带包；缺包必须显示跳过，不能伪装通过。
public sealed class SpriteTheoryAttribute : TheoryAttribute
{
    public SpriteTheoryAttribute()
    {
        if (!File.Exists(SpriteMatcherTests.PackagePath)) Skip = "未生成本地小人模板包。";
    }
}

public sealed class SpriteMatcherTests
{
    internal static string PackagePath => Path.Combine(AppContext.BaseDirectory, "data", "sprite-templates.bin.gz");

    [SpriteTheory]
    [InlineData("N24", 70, 1, "杰克|灰喉|褐果", "灰喉|褐果|杰克",
        new[] { .45242109202286773, .42724444711325216, .33755525219089844 }, false)]
    [InlineData("N29", 66, 4, "夜莺|霍尔海雅", "霍尔海雅|夜莺",
        new[] { .45915700264421166, .35375150845331305 }, true)]
    [InlineData("N30", 60, 2, "云迹|承曦格雷伊|星极|真理", "云迹|星极|真理|承曦格雷伊",
        new[] { .39160145307872807, .3674857146307534, .3660956417228661, .2937121979892254 }, false)]
    public void CroppedCellMatchesFrozenPython(string id, int anchorX, int tier, string names,
        string expectedNames, double[] expectedScores, bool autoFill)
    {
        using var matcher = new SpriteMatcher(PackagePath);
        using var crop = Cv2.ImRead(Path.Combine(AppContext.BaseDirectory, "TestData", $"sprite-{id}.png"));
        var result = matcher.MatchCrop(crop, true, tier, names.Split('|'), new Point(anchorX, 176));
        Assert.Equal(expectedNames.Split('|'), result.Candidates.Select(c => c.Name));
        for (int i = 0; i < expectedScores.Length; i++)
            Assert.InRange(Math.Abs(expectedScores[i] - result.Candidates[i].Score), 0, .01);
        Assert.Equal(result.Candidates[0].Score - result.Candidates[1].Score, result.Gap);
        Assert.Equal(autoFill, result.CanAutoFill);

        if (id == "N24")
        {
            // 冻结规则明确要求单候选不自动填；候选和阶数必须共同约束结果。
            var single = matcher.MatchCrop(crop, true, tier, ["灰喉", "夜莺"], new Point(anchorX, 176));
            Assert.Equal("灰喉", Assert.Single(single.Candidates).Name);
            Assert.Null(single.Gap);
            Assert.False(single.CanAutoFill);
        }
    }
}
