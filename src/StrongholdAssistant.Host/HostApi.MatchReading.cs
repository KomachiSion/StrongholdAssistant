using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace StrongholdAssistant.Host;

public sealed partial class HostApi
{
    private MatchStrategyReader _strategyReader = null!;
    private CustomPickReader _customPickReader = null!;
    private RoundEnemyReader _roundEnemyReader = null!;
    private CovenantBarReader _covenantBarReader = null!;
    private DateTimeOffset _covenantReadAt = DateTimeOffset.MinValue;
    private (string? Match, int? Round) _enemyReadKey;
    private int _matchReadingGeneration;

    public void InitializeMatchReading(string data, IOcrEngine ocr)
    {
        _strategyReader = new(ocr, data, Catalog.GetProperty("strategies").EnumerateArray()
            .ToDictionary(s => s.GetProperty("id").GetString()!, s => s.GetProperty("name").GetString()!));
        _roundEnemyReader = new(ocr);
        _covenantBarReader = new(ocr, data, Catalog.GetProperty("covenants").EnumerateArray()
            .ToDictionary(c => c.GetProperty("id").GetString()!, c => c.GetProperty("name").GetString()!));
        _customPickReader = new(ocr, _operators.Where(o => o.Diy).Select(o => o.Name).ToArray());
    }

    public async Task ObserveMatchFrameAsync(byte[] frame, int width, int height, EconomyReading? reading, double uiScale)
    {
        string? match = _activeRecord;
        int generation = _matchReadingGeneration;
        if (reading is not null)
        {
            bool read;
            lock (_historyGate)
            {
                var now = DateTimeOffset.UtcNow;
                read = now - _covenantReadAt >= TimeSpan.FromSeconds(3);
                if (read) _covenantReadAt = now;
            }
            if (read)
            {
                int? round = Match.Economy?.Round;
                var result = await _covenantBarReader.ReadAsync(frame, uiScale);
                lock (_historyGate)
                {
                    if (generation == _matchReadingGeneration && match == _activeRecord && Match.Economy?.Round == round
                        && !result.Skipped && result.Items.Length > 0)
                        ApplyCovenantBar(result.Items, round);
                }
            }
        }
        if (reading is null)
        {
            var picks = await _customPickReader.ReadAsync(frame, width, height);
            if (generation == _matchReadingGeneration && match == _activeRecord && picks is { IsFinal: true })
                ApplyCustomPicks(picks.Cards.Where(c => c.Tier == 5).Select(c => c.Name!).ToArray(),
                    picks.Cards.Where(c => c.Tier == 6).Select(c => c.Name!).ToArray());
        }
        if (Match.StrategySource is null)
        {
            string? id = reading is { IsRest: true } ? _strategyReader.ReadPortrait(frame, uiScale).Id
                : await _strategyReader.ReadSelectionAsync(frame, width, height);
            if (generation == _matchReadingGeneration && match == _activeRecord && id is not null) ApplyRecognizedStrategy(id);
        }
        if (reading is not { IsRest: true } || Match.Economy is not { } economy || reading.Round != economy.Round) return;
        var key = (match, (int?)economy.Round);
        if (_enemyReadKey != key) { _roundEnemyReader.Reset(); _enemyReadKey = key; }
        var names = await _roundEnemyReader.ReadAsync(frame);
        if (generation == _matchReadingGeneration && match == _activeRecord && Match.Economy?.Round == economy.Round && names is { Length: > 0 })
            ApplyRoundEnemies(economy.Round, names);
    }

    private void ResetMatchReading()
    {
        _matchReadingGeneration++;
        _enemyReadKey = default;
        _covenantReadAt = DateTimeOffset.MinValue;
        Match = Match with { CovenantBar = [] };
    }

    internal void ApplyCovenantBar(CovenantBarReading[] items, int? round)
    {
        lock (_historyGate)
        {
            string at = DateTimeOffset.UtcNow.ToString("O");
            var merged = Match.CovenantBar.ToDictionary(c => c.Id);
            foreach (var item in items) merged[item.Id] = new(item.Id, item.Level, item.Layers, round, at);
            Match = Match with { CovenantBar = merged.Values.ToArray() };
            Changed?.Invoke("match", Match);
        }
    }

    internal void ApplyRecognizedStrategy(string id) => UpdateMatchStrategy(id, "recognized");

    // 每次读到可信最终状态均覆盖，空数组表示读到了但未选择。
    internal void ApplyCustomPicks(string[] tier5, string[] tier6)
    {
        lock (_historyGate)
        {
            Match = Match with { CustomPicks = new() { [5] = tier5, [6] = tier6 } };
            Changed?.Invoke("match", Match);
        }
    }

    internal void ApplyRoundEnemies(int round, IEnumerable<string> names)
    {
        lock (_historyGate)
        {
            // TODO(design): 同名敌人存在多个 ID 时暂取目录第一项，待定义是否按首领/难度细分。
            var catalog = Catalog.GetProperty("enemies").EnumerateArray().GroupBy(e => e.GetProperty("name").GetString()!)
                .ToDictionary(g => g.Key, g => g.First().GetProperty("id").GetString()!);
            var previous = Match.RoundEnemies is { } old && old.Round == round ? old.Enemies : [];
            var enemies = previous.Concat(names.Select(n => new RoundEnemy(catalog.GetValueOrDefault(n), n)))
                .DistinctBy(e => e.Name).ToArray();
            Match = Match with { RoundEnemies = new(round, DateTimeOffset.UtcNow.ToString("O"), enemies) };
            Changed?.Invoke("match", Match);
        }
    }
}
