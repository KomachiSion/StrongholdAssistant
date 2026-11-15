using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace StrongholdAssistant.Tests;

public sealed class FieldIdentityTests
{
    private static readonly FieldCell A = new("board", 1, 1), B = new("board", 2, 1), C = new("board", 3, 1), Bench = new("bench", 1, 0);
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static FieldSpritePatch Pic(int shift = 0, bool bench = false)
    {
        using var image = new OpenCvSharp.Mat(204, 180, OpenCvSharp.MatType.CV_8UC3);
        var pixels = new byte[204 * 180 * 3];
        new Random(shift).NextBytes(pixels);
        System.Runtime.InteropServices.Marshal.Copy(pixels, 0, image.Data, pixels.Length);
        return FieldReader.CropSprite(image, 90, bench ? 180 : 156, bench)!;
    }
    private static FieldObservation Frame(double sec, params (FieldCell Cell, int Tier, FieldSpritePatch? Patch)[] cells)
        => new(Start.AddSeconds(sec), true, "m01", 1, cells.ToDictionary(p => p.Cell, p => p.Tier))
        { Sprites = cells.Where(p => p.Tier > 0 && p.Patch is not null).ToDictionary(p => p.Cell, p => p.Patch!) };
    private static void ObserveStable(FieldTracker t, FieldObservation o)
    {
        t.Observe(o);
        t.Observe(o with { At = o.At.AddMilliseconds(100) });
    }
    private static FieldTracker Tracker(Func<FieldSpritePatch, int, string[], SpriteMatch>? match = null)
    {
        var t = new FieldTracker();
        t.ConfigureIdentity([new() { Name = "甲", Tier = 1 }, new() { Name = "乙", Tier = 1 },
            new() { Name = "丁", Tier = 1 },
            new() { Name = "丙", Tier = 2, Obtain = "策略「赠送」第 2 回合赠送" }],
            match ?? ((_, _, names) => new(names.Select((n, i) => new SpriteCandidate(n, .8 - i * .1)).ToArray(), .1)));
        return t;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TierFlickerKeepsConfirmedAndAutoIdentity(bool confirm)
    {
        var t = Tracker();
        t.AddRoster("甲", "purchase", Start); t.AddRoster("乙", "purchase", Start);
        t.Observe(Frame(0, (A, 1, Pic())));
        if (confirm) t.SetOperator(A, "甲", Start);
        var original = Assert.Single(t.State!.Units);
        t.Observe(Frame(1, (A, 2, Pic(1))));
        Assert.Equal(original, Assert.Single(t.State.Units));
        t.Observe(Frame(2, (A, 1, Pic(2))));
        t.Observe(Frame(3, (A, 2, Pic(3))));
        Assert.Equal(original, Assert.Single(t.State.Units));
        t.Observe(Frame(4, (A, 3, Pic(4))));
        Assert.Equal(original, Assert.Single(t.State.Units));
        t.Observe(Frame(5, (A, 2, Pic(5))));
        Assert.Equal(original, Assert.Single(t.State.Units));
        t.Observe(Frame(6, (A, 2, Pic(6))));
        var changed = Assert.Single(t.State.Units);
        Assert.Equal(2, changed.Tier); Assert.NotEqual("甲", changed.Operator);
    }

    [Fact]
    public void UnconfirmedTierFrameDoesNotTeachOldIdentityToAnotherCell()
    {
        var t = Tracker();
        t.AddRoster("甲", "purchase", Start); t.AddRoster("乙", "purchase", Start);
        ObserveStable(t, Frame(0, (A, 1, Pic()))); t.SetOperator(A, "甲", Start);
        var other = Pic(10);
        ObserveStable(t, Frame(1, (A, 2, other)));
        ObserveStable(t, Frame(2, (A, 1, Pic()), (B, 1, other)));
        // 新格应独立比对，不能直接承接上一帧未确认图块的错误姓名。
        Assert.Equal("sprite", t.State!.Units.Single(u => u.Cell == B).Source);
    }

    [Fact]
    public void EmptyOrInvisibleScanBreaksConsecutiveTierReadings()
    {
        var t = Tracker();
        t.Observe(Frame(0, (A, 1, Pic()))); t.SetOperator(A, "甲", Start);
        t.Observe(Frame(1, (A, 2, Pic(1))));
        t.Observe(Frame(2));
        t.Observe(Frame(3, (A, 2, Pic(1))));
        Assert.Equal(1, Assert.Single(t.State!.Units).Tier);
        t.Observe(Frame(4, (A, 0, null)));
        t.Observe(Frame(5, (A, 2, Pic(1))));
        t.Observe(Frame(6, (A, 2, Pic(1))));
        Assert.Equal(2, Assert.Single(t.State.Units).Tier);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ContinuousOccupancyKeepsIdentityDespiteAnimationAndBattle(bool confirm)
    {
        int calls = 0;
        var t = Tracker((_, _, names) => { calls++; return new(names.Select((n, i) => new SpriteCandidate(n, .8 - i * .1)).ToArray(), .1); });
        t.AddRoster("甲", "purchase", Start); t.AddRoster("乙", "purchase", Start);
        t.Observe(Frame(0, (A, 1, Pic())));
        if (confirm) t.SetOperator(A, "甲", Start);
        int initial = calls;
        t.Observe(Frame(.5) with { IsRest = false });
        t.Observe(Frame(1, (A, 1, Pic(100))));
        Assert.Equal(initial, calls); Assert.Equal(confirm ? "sure" : "auto", t.State!.Units[0].Status);
        Assert.Equal("甲", t.State.Units[0].Operator);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TwoMissingScansKeepIdentityWithoutMatching(bool confirm)
    {
        int calls = 0;
        var t = Tracker((_, _, names) => { calls++; return new(names.Select(n => new SpriteCandidate(n, .5)).ToArray(), 0); });
        ObserveStable(t, Frame(0, (A, 1, Pic())));
        if (confirm) t.SetOperator(A, "甲", Start);
        var original = t.State!.Units[0];
        t.Observe(Frame(4, (A, 0, null)));
        t.Observe(Frame(8, (A, 0, null)));
        ObserveStable(t, Frame(12, (A, 1, Pic(100))));
        Assert.Equal(original, Assert.Single(t.State!.Units));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExpiredOrSoldCellDoesNotRestoreIdentity(bool sold)
    {
        int calls = 0;
        var t = Tracker((_, _, names) => { calls++; return new(names.Select(n => new SpriteCandidate(n, .5)).ToArray(), 0); });
        ObserveStable(t, Frame(0, (A, 1, Pic())) with { Money = 4 });
        t.SetOperator(A, "乙", Start);
        t.AddRoster("甲", "purchase", Start);
        t.AddRoster("丁", "purchase", Start);
        if (sold) ObserveStable(t, Frame(1) with { DragOperator = "乙", Money = 4 });
        ObserveStable(t, Frame(4, (A, 0, null)) with { Money = sold ? 5 : 4 });
        if (!sold)
        {
            ObserveStable(t, Frame(8, (A, 0, null)));
            ObserveStable(t, Frame(12, (A, 0, null)));
        }
        ObserveStable(t, Frame(sold ? 5 : 16, (A, 1, Pic(100))));
        Assert.True(calls > 1);
        Assert.Equal("pending", Assert.Single(t.State!.Units).Status);
    }

    [Theory]
    [InlineData(false, "pending")]
    [InlineData(true, "auto")]
    public void HalfGapOnlyAppliesToPlayerConfirmedName(bool confirmed, string expected)
    {
        var t = Tracker((_, _, names) => new(names.Select((n, i) => new SpriteCandidate(n, .8 - i * .03)).ToArray(), .03));
        t.AddRoster("甲", "purchase", Start); t.AddRoster("乙", "purchase", Start);
        ObserveStable(t, Frame(0, (A, 1, Pic())));
        if (confirmed) t.SetOperator(A, "甲", Start);
        ObserveStable(t, Frame(4, (A, 0, null), (B, 1, Pic(100))));
        Assert.Equal(expected, Assert.Single(t.State!.Units).Status);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.1)]
    public void PersonalTemplateSearchesPositionAndScale(double scale)
    {
        using var source = new OpenCvSharp.Mat(204, 180, OpenCvSharp.MatType.CV_8UC3, new OpenCvSharp.Scalar(20, 20, 20));
        for (int i = 0; i < 8; i++)
            OpenCvSharp.Cv2.Circle(source, new OpenCvSharp.Point(70 + i % 3 * 20, 60 + i * 13), 8,
                new OpenCvSharp.Scalar(40 + i * 20, 200 - i * 15, 80 + i * 10), -1);
        using var transform = new OpenCvSharp.Mat(2, 3, OpenCvSharp.MatType.CV_64FC1);
        transform.Set(0, 0, scale); transform.Set(0, 1, 0d); transform.Set(0, 2, 90 * (1 - scale) + 8);
        transform.Set(1, 0, 0d); transform.Set(1, 1, scale); transform.Set(1, 2, 108 * (1 - scale) - 6);
        using var moved = new OpenCvSharp.Mat();
        OpenCvSharp.Cv2.WarpAffine(source, moved, transform, source.Size());
        var a = FieldReader.CropSprite(source, 90, 156, false)!;
        var b = FieldReader.CropSprite(moved, 90, 156, false)!;
        Assert.True(SpriteMatcher.MatchPersonal(a, b) > .9);
        Assert.True(FieldTracker.SpriteSimilarity(a, b) < FieldTracker.SameSpriteScore);
    }

    [Fact]
    public void PurchaseWinsOverRemovedLookalikeAndAddsRoster()
    {
        var t = Tracker();
        t.Observe(Frame(0, (A, 1, Pic()), (Bench, 0, null)) with { Money = 4, Shop = new Dictionary<int, string> { [1] = "乙" } });
        t.SetOperator(A, "甲", Start);
        t.Observe(Frame(.8, (A, 0, null), (Bench, 1, Pic())) with { Money = 3, Shop = new Dictionary<int, string>() });
        var u = Assert.Single(t.State!.Units);
        Assert.Equal("乙", u.Operator); Assert.Equal("purchase", u.Source); Assert.Equal("sure", u.Status);
        Assert.Contains(t.State.Roster, r => r.Operator == "乙" && r.Source == "purchase");
    }

    [Theory]
    [InlineData(false, "乙")]
    [InlineData(true, "甲")]
    public void ConfirmedTopThreeNameIsPreferredOnlyWhenNotElsewhere(bool occupied, string expected)
    {
        var t = Tracker((_, _, names) => new(names.Select((n, i) => new SpriteCandidate(n, .8 - i * .01)).ToArray(), .01));
        t.AddRoster("甲", "purchase", Start); t.AddRoster("乙", "purchase", Start);
        ObserveStable(t, Frame(0, (A, 1, Pic()))); t.SetOperator(A, "乙", Start);
        ObserveStable(t, Frame(1, (A, occupied ? 1 : 0, occupied ? Pic() : null), (B, 1, Pic(100))));
        var unit = t.State!.Units.Single(u => u.Cell == B);
        Assert.Equal(expected, unit.Operator);
        Assert.Equal("pending", unit.Status);
    }

    [Theory]
    [InlineData(.1, "auto")]
    [InlineData(.01, "pending")]
    public void ConfirmedNameCanAutoFillUntrackedArrivalOnlyWithEnoughGap(double gap, string status)
    {
        var t = Tracker((_, _, names) => new(names.Select((n, i) => new SpriteCandidate(n, .8 - i * gap)).ToArray(), gap));
        ObserveStable(t, Frame(0, (A, 1, Pic())) with { Money = 4 }); t.SetOperator(A, "甲", Start);
        ObserveStable(t, Frame(1, (A, 0, null), (Bench, 1, null)) with { Money = 4 });
        ObserveStable(t, Frame(3, (Bench, 1, Pic(100, true))) with { Money = 3 });
        Assert.Equal("甲", t.State!.Units[0].Operator);
        Assert.Equal(status, t.State.Units[0].Status);
    }

    [Fact]
    public void MoveNeedsSameTierAndSimilarPicture()
    {
        var t = Tracker(); ObserveStable(t, Frame(0, (A, 1, Pic()))); t.SetOperator(A, "甲", Start);
        ObserveStable(t, Frame(1, (A, 0, null), (B, 1, Pic())));
        Assert.Equal("carry", Assert.Single(t.State!.Units).Source);
        Assert.Equal("甲", t.State.Units[0].Operator);
        ObserveStable(t, Frame(2, (B, 0, null), (C, 2, Pic(90))));
        Assert.NotEqual("carry", Assert.Single(t.State.Units).Source);
    }

    [Fact]
    public void ThreeConfirmedCopiesMergeIntoElite()
    {
        var t = Tracker(); ObserveStable(t, Frame(0, (A, 1, Pic()), (B, 1, Pic()), (C, 1, Pic())));
        foreach (var c in new[] { A, B, C }) t.SetOperator(c, "甲", Start);
        ObserveStable(t, Frame(1, (A, 2, Pic(30)), (B, 0, null), (C, 0, null)));
        var u = Assert.Single(t.State!.Units);
        Assert.Equal("甲", u.Operator); Assert.Equal("merge", u.Source); Assert.Equal("sure", u.Status); Assert.True(u.Elite);
        ObserveStable(t, Frame(2, (A, 2, Pic(100))));
        Assert.Equal("甲", t.State.Units[0].Operator); Assert.True(t.State.Units[0].Elite);
    }

    [Fact]
    public void OnlyActiveRosterNameAtTierNeedsNoMatcher()
    {
        var t = Tracker((_, _, _) => throw new Exception("唯一候选不应调用比对器"));
        t.AddRoster("甲", "purchase", Start);
        t.Observe(Frame(0, (A, 1, Pic())));
        Assert.Equal("only", Assert.Single(t.State!.Units).Source);
        Assert.Empty(t.State.Units[0].Candidates);
    }

    [Theory]
    [InlineData(.8, .6, "auto")]
    [InlineData(.8, .78, "pending")]
    [InlineData(.2, .1, "pending")]
    public void GapAndLowScoreSelectStatus(double first, double second, string expected)
    {
        var t = Tracker((_, _, names) => new(names.Select((n, i) => new SpriteCandidate(n, i == 0 ? first : second)).ToArray(), first - second));
        t.AddRoster("甲", "purchase", Start); t.AddRoster("乙", "purchase", Start);
        t.Observe(Frame(0, (A, 1, Pic())));
        Assert.Equal(expected, t.State!.Units[0].Status);
        Assert.Equal(first < FieldTracker.MinimumRosterScore ? 3 : 2, t.State.Units[0].Candidates.Length);
        if (first < FieldTracker.MinimumRosterScore) Assert.Contains(t.State.Units[0].Candidates, c => c.Operator == "丁");
    }

    [Fact]
    public void ConfirmationLearnsPictureAndUntrackedGainRemainsPending()
    {
        var t = Tracker(); t.AddRoster("甲", "purchase", Start);
        t.Observe(Frame(0, (Bench, 1, Pic(10, true))) with { GainSource = "equipment" });
        t.Observe(Frame(2, (Bench, 1, Pic(10, true))));
        Assert.Equal("pending", t.State!.Units[0].Status);
        t.SetOperator(Bench, "乙", Start);
        Assert.Contains(t.State.Roster, r => r.Operator == "乙" && r.Source == "equipment");
        t.Observe(Frame(3, (Bench, 0, null)));
        t.Observe(Frame(4, (Bench, 1, Pic(10, true))));
        Assert.Equal("乙", t.State!.Units[0].Operator);
        t.Observe(Frame(6, (Bench, 1, Pic(10, true))));
        Assert.Equal("sure", t.State!.Units[0].Status);
        Assert.Equal("乙", t.State.Units[0].Operator);
        t.Reset(); Assert.Null(t.State);
        t.Observe(Frame(7)); Assert.Empty(t.State!.Roster);
    }

    [Fact]
    public void PendingSurvivesDelayedGainAndTemporaryMissingBadge()
    {
        int calls = 0;
        var t = Tracker((_, _, names) => { calls++; return new(names.Select(n => new SpriteCandidate(n, .5)).ToArray(), 0); });
        t.Observe(Frame(0, (Bench, 1, Pic(0, true))));
        var candidates = t.State!.Units[0].Candidates;
        t.Observe(Frame(2, (Bench, 1, Pic(0, true))));
        Assert.Same(candidates, t.State.Units[0].Candidates);
        t.Observe(Frame(3, (Bench, 0, null)));
        t.Observe(Frame(4, (Bench, 1, Pic(0, true))));
        Assert.Equal("pending", t.State.Units[0].Status);
        Assert.Same(candidates, t.State.Units[0].Candidates);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData(false, "auto", false)]
    [InlineData(true, "sure", false)]
    [InlineData(false, "auto", true)]
    [InlineData(true, "sure", true)]
    public void RememberedIdentitySurvivesBattleAndLaterArrival(bool confirm, string status, bool bench)
    {
        var t = Tracker(); t.AddRoster("甲", "purchase", Start); t.AddRoster("乙", "purchase", Start);
        ObserveStable(t, Frame(0, (A, 1, Pic())));
        if (confirm) t.SetOperator(A, "甲", Start);
        ObserveStable(t, Frame(1, (A, 0, null)));
        ObserveStable(t, Frame(2) with { IsRest = false });
        ObserveStable(t, Frame(3, (bench ? Bench : B, 1, Pic(0, bench))) with { Round = 2 });
        var unit = Assert.Single(t.State!.Units);
        Assert.Equal("甲", unit.Operator); Assert.Equal(status, unit.Status); Assert.Equal("carry", unit.Source);
    }

    [Fact]
    public void SeedHonorsGiftRoundAndSellKeepsRoster()
    {
        var t = Tracker();
        t.SeedRoster("赠送", ["甲"], 1, Start); t.Observe(Frame(0, (A, 1, Pic())));
        Assert.DoesNotContain(t.State!.Roster, r => r.Operator == "丙");
        t.SeedRoster("赠送", ["甲"], 2, Start); t.Observe(Frame(.5, (A, 1, Pic())) with { Money = 4, Round = 2 });
        Assert.Contains(t.State.Roster, r => r.Operator == "丙" && r.Source == "strategy");
        t.Observe(Frame(1) with { DragOperator = "甲", Money = 4 });
        t.Observe(Frame(2, (A, 0, null)) with { Money = 5 });
        Assert.True(t.State.Roster.Single(r => r.Operator == "甲").Sold);
    }
}
