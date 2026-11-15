using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace StrongholdAssistant.Tests;

public sealed class FieldTriggerTests
{
    private static readonly FieldCell Bench = new("bench", 1, 0);
    private static readonly DateTimeOffset Start = DateTimeOffset.Parse("2026-01-01T00:00:00Z");
    private static FieldObservation Frame(int second, int tier, int money = 10, Dictionary<int, string>? shop = null)
        => new(Start.AddSeconds(second), true, "m01", 1, new Dictionary<FieldCell, int> { [Bench] = tier }, shop, money);

    [Fact]
    public void IdleFramesFreezePendingAndRequireTwoConsecutiveOccupancyChanges()
    {
        int calls = 0;
        var t = new FieldTracker();
        t.ConfigureIdentity([new() { Name = "甲", Tier = 1 }, new() { Name = "乙", Tier = 1 }],
            (_, _, names) => { calls++; return new(names.Select(n => new SpriteCandidate(n, .5)).ToArray(), 0); });
        var first = Frame(0, 1) with { Sprites = new Dictionary<FieldCell, FieldSpritePatch> { [Bench] = new([], [1, 2, 3], true) } };
        t.Observe(first);
        var state = t.State;
        for (int second = 1; second <= 5; second++)
            Assert.False(t.Observe(first with { At = Start.AddSeconds(second), Sprites = new Dictionary<FieldCell, FieldSpritePatch> { [Bench] = new([], [9, 1, 8], true) } }));
        Assert.Same(state, t.State); Assert.Equal(1, calls);
        Assert.False(t.Observe(Frame(6, 0)));
        Assert.False(t.Observe(Frame(7, 1)));
        Assert.False(t.Observe(Frame(8, 0)));
        Assert.True(t.Observe(Frame(9, 0)));
        Assert.Empty(t.State!.Units);
    }

    [Theory]
    [InlineData(.1, "auto")]
    [InlineData(.01, "pending")]
    public void PurchaseRanksOnlyPreviousRowAtObservedTier(double gap, string status)
    {
        string[] matched = [];
        var t = new FieldTracker();
        t.ConfigureIdentity([new() { Name = "甲", Tier = 1 }, new() { Name = "乙", Tier = 1 },
            new() { Name = "丙", Tier = 2 }, new() { Name = "丁", Tier = 1 }],
            (_, _, names) => { matched = names; return new(names.Select((n, i) => new SpriteCandidate(n, .8 - i * gap)).ToArray(), gap); });
        Dictionary<int, string> shop = new() { [1] = "甲", [2] = "乙", [3] = "丙" };
        t.Observe(Frame(0, 0, shop: shop));
        t.Observe(Frame(1, 1, 9, shop) with { Sprites = new Dictionary<FieldCell, FieldSpritePatch> { [Bench] = new([], [1, 2, 3], true) } });
        Assert.Equal(new[] { "甲", "乙" }, matched);
        var unit = Assert.Single(t.State!.Units);
        Assert.Equal(status, unit.Status);
        Assert.Equal(new[] { "甲", "乙" }, unit.Candidates.Select(c => c.Operator));
        Assert.DoesNotContain(t.State.Roster, r => r.Operator == "乙");
    }

    [Fact]
    public void RefreshAndEquipmentSpendDoNotBecomePurchases()
    {
        var t = new FieldTracker();
        t.Observe(Frame(0, 0, shop: new() { [1] = "甲" }));
        t.Observe(Frame(1, 0, 9, new() { [1] = "乙" }));
        t.Observe(Frame(2, 1, 9, new() { [1] = "乙" }));
        t.Observe(Frame(3, 1, 9, new() { [1] = "乙" }));
        Assert.DoesNotContain(t.State!.Log, e => e.Kind == "buy");
        t.Observe(Frame(4, 1, 8, new() { [1] = "乙" }));
        Assert.DoesNotContain(t.State.Log, e => e.Kind == "buy");
    }

    [Fact]
    public void FreeThreeChoiceUsesPreviousCardsAndUniqueTierIsSure()
    {
        var t = new FieldTracker();
        t.ConfigureIdentity([new() { Name = "甲", Tier = 1 }, new() { Name = "乙", Tier = 2 }, new() { Name = "丙", Tier = 3 }],
            (_, _, _) => throw new Exception("同阶唯一时不比对小人"));
        t.Observe(Frame(0, 0, shop: new() { [1] = "甲", [2] = "乙", [3] = "丙" }));
        t.Observe(Frame(1, 1, shop: new()));
        t.Observe(Frame(2, 1, shop: new()));
        var unit = Assert.Single(t.State!.Units);
        Assert.Equal("甲", unit.Operator); Assert.Equal("sure", unit.Status);
        Assert.Equal("buy", Assert.Single(t.State.Log).Kind);
    }
}
