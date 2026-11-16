using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

public sealed partial class FieldTracker
{
    private Dictionary<FieldCell, int> _lastCells = [];
    private int? _observedMoney;
    private bool _pendingTrigger;
    private (DateTimeOffset At, string[] Names)? _purchase;
    private readonly Dictionary<FieldCell, string[]> _purchaseCandidates = [];

    private void ResetTriggers()
    {
        _lastCells.Clear(); _observedMoney = null; _pendingTrigger = false;
        _purchase = null; _purchaseCandidates.Clear();
    }

    private bool ObserveTriggers(FieldObservation o)
    {
        bool moneyChanged = o.Money is not null && _observedMoney is not null && o.Money != _observedMoney;
        bool decrease = o.Money < _observedMoney;
        // 资金先变化、角标后出现时，沿用这次扣款之前的整排卡。
        bool refreshed = _shop is { Count: > 0 } && o.Shop is { Count: > 0 }
            && o.Shop.All(p => _shop.GetValueOrDefault(p.Key) != p.Value);
        if (refreshed) _purchase = null;
        if (decrease && !refreshed && _shop is { Count: > 0 }
            && (o.At - _shopAt).TotalSeconds <= PurchaseWindowSeconds)
            _purchase = (o.At, _shop.Values.Distinct().ToArray());
        // TODO(design): 观测尚无零费选择标志；暂以主画面读到三张卡减少且资金未变识别三选一。
        // 拖动或商店未读到不能作为卡片退出的证据。
        if (!moneyChanged && o.MainScreen && o.DragOperator is null && _shop is { Count: 3 } && (o.At - _shopAt).TotalSeconds <= PurchaseWindowSeconds
            && o.Shop is { Count: < 3 } && o.Shop.All(p => _shop.GetValueOrDefault(p.Key) == p.Value))
            _purchase = (o.At, _shop.Values.Distinct().ToArray());
        if (_purchase is { } purchase && (o.At - purchase.At).TotalSeconds > PurchaseWindowSeconds)
            _purchase = null;
        // 拖动之后的新占用不能再配给拖动之前尚未落格的扣款。
        if (o.DragOperator is not null) _purchase = null;
        _observedMoney = o.Money ?? _observedMoney;
        _pendingTrigger |= moneyChanged || o.PanelOperator is not null || o.DragOperator is not null;
        if (!o.MainScreen || o.DragOperator is not null) { _lastCells.Clear(); return false; }
        bool stableChange = o.Cells.Any(p => p.Value != (_units.GetValueOrDefault(p.Key)?.Tier ?? 0)
            && _lastCells.TryGetValue(p.Key, out int previous) && previous == p.Value);
        _lastCells = new(o.Cells);
        bool run = State is null || _battle || o.Round != State.Round || _pendingTrigger || _drag is not null || stableChange;
        // 未触发身份处理时仍计算连续空扫描次数，避免旧的漏读身份永不过期。
        if (!run)
            foreach (var (cell, missing) in _missing.ToArray())
                if (o.Cells.GetValueOrDefault(cell, -1) == 0)
                    _missing[cell] = missing with { Scans = missing.Scans + 1 };
        if (run) _pendingTrigger = false;
        return run;
    }

    // 自动关闭时只维护购买基线和名单；不改对外快照，也不运行一般身份解析。
    private void ObservePurchasesOnly(FieldObservation o, bool triggered)
    {
        if (!o.MainScreen) return;
        if (triggered)
        {
            var added = o.Cells.Where(p => p.Value > 0 && !_units.ContainsKey(p.Key)).Select(p => p.Key).ToList();
            foreach (var (cell, tier) in o.Cells)
            {
                if (tier == 0) { _units.Remove(cell); _purchaseCandidates.Remove(cell); }
                else
                {
                    _units[cell] = new(cell.Zone, cell.Col, cell.Row, tier, null, null);
                    _anchors.Remove(cell);
                }
            }
            if (!_battle) ResolvePurchases(o, added);
            foreach (var (cell, unit) in _units.ToArray())
                _units[cell] = unit with { Operator = null, Source = null, Status = "unknown", Candidates = [] };
            _purchaseCandidates.Clear();
            _battle = false;
        }
        if (o.Shop is { } shop) { _shop = shop; _shopAt = o.At; }
        _money = o.Money ?? _money;
    }

    private void ResolvePurchases(FieldObservation o, List<FieldCell> added)
    {
        if (_purchase is not { } purchase) return;
        bool consumed = false;
        foreach (var cell in added.Where(c => c.Zone == "bench").ToArray())
        {
            var unit = _units[cell];
            // 购买记录沿用原有推断，独立于场上视觉身份。
            var purchaseRecord = unit;
            var names = purchase.Names.Where(n => _identityOperators.Count == 0 || HasTier(n, unit.Tier)).Distinct().ToArray();
            if (names.Length == 0) continue;
            consumed = true;
            _purchaseCandidates[cell] = names;
            if (names.Length == 1)
            {
                // 商店整排只有一个同阶名字也可能配错格，强视觉反证时保留待确认。
                var visual = purchase.Names.Length > 1 && o.Sprites?.TryGetValue(cell, out var picture) == true && _matchSprite is not null
                    ? Rank(picture, unit.Tier, false, MatchCandidates().Where(n => HasTier(n, unit.Tier)).Distinct().ToArray(), names)
                        .OrderByDescending(c => c.Score).ToArray() : [];
                var bought = visual.FirstOrDefault(c => c.Name == names[0]);
                bool unsupported = bought is not null && !visual.Take(3).Any(c => c.Name == names[0])
                    && visual[0].Score - bought.Score >= .05;
                purchaseRecord = unit with { Operator = names[0], Source = "purchase", Status = unsupported ? "pending" : "sure",
                    Candidates = unsupported ? new[] { new FieldCandidate(names[0], bought!.Score) }
                        .Concat(visual.Take(2).Select(c => new FieldCandidate(c.Name, c.Score))).ToArray() : [] };
            }
            else
            {
                var ranked = o.Sprites?.TryGetValue(cell, out var patch) == true && _matchSprite is not null
                    ? Rank(patch, unit.Tier, false, names, names).OrderByDescending(c => c.Score).ToArray() : [];
                bool automatic = ranked.Length > 1 && ranked[0].Score - ranked[1].Score >= IdentityAutoFillGap
                    && !FieldConfusionRules.NeedsConfirmation(ranked);
                var candidates = ranked.Select(c => new FieldCandidate(c.Name, c.Score))
                    .Concat(names.Where(n => !ranked.Any(c => c.Name == n)).Select(n => new FieldCandidate(n, 0))).ToArray();
                purchaseRecord = unit with { Operator = candidates[0].Operator, Source = "sprite",
                    Status = automatic ? "auto" : "pending", Candidates = candidates };
            }
            var identified = purchaseRecord;
            _gainContexts[cell] = new("purchase", null);
            _gainSources[cell] = "purchase";
            _untrackedGains.Remove(cell);
            _gains.RemoveAll(g => g.Cell == cell);
            added.Remove(cell);
            if (identified.Status is "sure" or "auto") Add(purchase.At, "buy", identified.Operator, null, cell);
        }
        if (consumed) _purchase = null;
    }
}
