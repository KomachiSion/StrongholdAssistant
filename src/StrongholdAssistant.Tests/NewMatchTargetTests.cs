using StrongholdAssistant.Host;

namespace StrongholdAssistant.Tests;

public sealed class NewMatchTargetTests
{
    private static Task<HostApi> Create(string dir) => HostApi.CreateAsync(
        Path.Combine(AppContext.BaseDirectory, "data"), dir);

    [Theory]
    [InlineData("opening")]
    [InlineData("boss")]
    [InlineData("reset")]
    public async Task NewMatchClearsPersistedTargetStageAndDerivedTracking(string trigger)
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var api = await Create(dir);
        api.ApplyRecognizedMatch(api.Match with { BossId = "old-boss" }, [], opening: true);
        var target = await api.SaveLineupAsync(new("", "目标", [new("初雪")], "user", null, DateTime.UtcNow));
        await api.SaveSessionAsync(new(target.Id, [], ["初雪", "泡泡"]) { Stage = 0 });
        SessionDto? pushed = null;
        api.Changed += (name, value) => { if (name == "sessionReset") pushed = (SessionDto)value; };
        if (trigger == "reset") await api.ResetMatchAsync();
        else api.ApplyRecognizedMatch(api.Match with { BossId = trigger == "boss" ? "new-boss" : null }, [], opening: trigger == "opening");
        Assert.NotNull(pushed);
        Assert.Null(pushed.TargetLineupId);
        Assert.Null(pushed.Stage);
        Assert.Equal(new[] { "泡泡" }, pushed.Tracked);
        Assert.DoesNotContain("初雪", api.TrackedNames);
        Assert.Null((await (await Create(dir)).SessionAsync()).TargetLineupId);
        Assert.Contains(await api.LineupsAsync(), l => l.Id == target.Id);
    }

    [Theory]
    [InlineData("boss")]
    [InlineData("covenant")]
    public async Task SupplementingPreviouslyUnreadInformationKeepsTarget(string supplement)
    {
        var api = await Create(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var full = api.Match;
        api.ApplyRecognizedMatch(full with { BossId = null, Covenants = full.Covenants.Take(1).ToArray() }, [], opening: true);
        var target = await api.SaveLineupAsync(new("", "目标", [new("初雪")], "user", null, DateTime.UtcNow));
        await api.SaveSessionAsync(new(target.Id, [], ["初雪"]) { Stage = 0 });
        var resets = 0;
        api.Changed += (name, _) => { if (name == "sessionReset") resets++; };
        api.ApplyRecognizedMatch(api.Match with
        {
            BossId = supplement == "boss" ? "recognized-boss" : null,
            Covenants = supplement == "covenant" ? full.Covenants : api.Match.Covenants
        }, []);
        var session = await api.SessionAsync();
        Assert.Equal(target.Id, session.TargetLineupId);
        Assert.Equal(0, session.Stage);
        Assert.Contains("初雪", session.Tracked);
        Assert.Contains("初雪", api.TrackedNames);
        Assert.Equal(0, resets);
    }

    [Fact]
    public async Task PartialRetryAndFailedScanKeepTarget()
    {
        var api = await Create(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        api.ApplyRecognizedMatch(api.Match, [], opening: true);
        var target = await api.SaveLineupAsync(new("", "目标", [new("初雪")], "user", null, DateTime.UtcNow));
        await api.SaveSessionAsync(new(target.Id, [], ["初雪"]) { Stage = 0 });
        api.ApplyRecognizedMatch(api.Match with { Covenants = [], BossId = null, EnemyTypes = [] }, []);
        Assert.Equal(target.Id, (await api.SessionAsync()).TargetLineupId);
        api.ReadInformation = () => throw new InvalidOperationException("未找到窗口");
        await Assert.ThrowsAsync<RecognitionException>(() => api.RecordInformationAsync(opening: true));
        Assert.Equal(target.Id, (await api.SessionAsync()).TargetLineupId);
        Assert.Equal(0, (await api.SessionAsync()).Stage);
    }
}
