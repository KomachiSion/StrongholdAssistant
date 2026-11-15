using System.Text.Json;
using ASPAssistant.Core.Services;
using StrongholdAssistant.Host;
namespace StrongholdAssistant.Tests;
public sealed class RosterStoreTests
{
    [Fact]
    public async Task ManualEditPersistsAndReadingReplacesEditedValues()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var data = Path.Combine(AppContext.BaseDirectory, "data");
        try
        {
            var api = await HostApi.CreateAsync(data, root);
            Assert.Empty(api.ReadRoster().Operators);
            using var patch = JsonDocument.Parse("{\"skill\":\"skchr_bison_2\",\"potential\":6}");
            api.SetRosterOperator("拜松", patch.RootElement);
            var reloaded = await HostApi.CreateAsync(data, root);
            Assert.Equal(6, reloaded.ReadRoster().Operators.Single().Potential);
            var card = new RosterCard(new("拜松", "char_325_bison#2", "skcom_def_up[3]", "", 1, DateTimeOffset.UtcNow.ToString("O")), true, 269, 208, 1, 1, "潜能1", 1, null, null);
            reloaded.MergeRoster(new([card, card], 1, 0, []), [], []);
            var roster = reloaded.ReadRoster();
            Assert.Single(roster.Operators);
            Assert.Null(roster.Operators[0].Edited);
            Assert.Equal(1, roster.Operators[0].Potential);
            Assert.Equal(new[] { "拜松" }, roster.CustomSquad);
            await reloaded.ExportBackupAsync(Path.Combine(root, "backup.json"));
            Assert.DoesNotContain("char_325_bison#2", await File.ReadAllTextAsync(Path.Combine(root, "backup.json")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LegacyEliteAndLevelAreIgnoredAndNotSaved()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var path = Path.Combine(root, "roster.json");
            await File.WriteAllTextAsync(path, """
                {"readAt":null,"operators":[{"name":"拜松","skin":null,"skill":null,"module":"","potential":6,"elite":2,"level":80,"readAt":"2026-11-15T00:00:00Z"}],"customSquad":[]}
                """);
            var api = await HostApi.CreateAsync(Path.Combine(AppContext.BaseDirectory, "data"), root);
            Assert.Equal(6, api.ReadRoster().Operators.Single().Potential);
            using var patch = JsonDocument.Parse("{\"potential\":1}");
            api.SetRosterOperator("拜松", patch.RootElement);
            using var saved = JsonDocument.Parse(await File.ReadAllTextAsync(path));
            var op = saved.RootElement.GetProperty("operators")[0];
            Assert.False(op.TryGetProperty("elite", out _));
            Assert.False(op.TryGetProperty("level", out _));
            Assert.Equal(1, op.GetProperty("potential").GetInt32());
        }
        finally { Directory.Delete(root, true); }
    }
}
