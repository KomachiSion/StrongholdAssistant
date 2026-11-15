using ASPAssistant.Core.Models;

namespace ASPAssistant.Core.Services;

/// <summary>只消费观测，不接触截图、OCR、时钟或存储。</summary>
public sealed partial class FieldTracker
{
    public FieldState? State { get; private set; }
    private readonly Dictionary<FieldCell, FieldUnit> _units = new();
    private readonly List<FieldEvent> _log = [];
    private IReadOnlyDictionary<int, string>? _shop;
    private DateTimeOffset _shopAt;
    private const double PurchaseWindowSeconds = 6;
    private readonly List<(DateTimeOffset At, IReadOnlyDictionary<int, string> Cards)> _shopHistory = [];
    private readonly List<(DateTimeOffset At, int Slot, string Name)> _buys = [];
    private readonly List<(DateTimeOffset At, FieldCell Cell)> _gains = [];
    private string? _drag;
    private FieldEquipment[]? _dragEquipment;
    private Dictionary<FieldCell, FieldUnit>? _beforeDrag;
    private int? _money, _dragMoney;
    private bool _battle;
    private readonly Dictionary<FieldCell, int> _tierChanges = [];

    public void Reset()
    {
        ResetIdentity();
        State = null; _units.Clear(); _log.Clear(); _shop = null;
        _buys.Clear(); _gains.Clear(); _drag = null; _dragEquipment = null; _beforeDrag = null;
        _shopHistory.Clear();
        _money = _dragMoney = null; _battle = false;
        _tierChanges.Clear();
    }

    public bool SetOperator(FieldCell cell, string? name, DateTimeOffset at)
    {
        // TODO(design): 确认未识别到占用的格子能否手动建人；当前只允许给已有占用指定身份。
        if (!_units.TryGetValue(cell, out var unit)) throw new ArgumentException("这格还没有识别到干员。");
        _units[cell] = unit with { Operator = name, Source = name is null ? null : "manual", Status = name is null ? "unknown" : "sure", Candidates = [] };
        ConfirmIdentity(cell, name, at);
        return Publish(State!.MapId, State.Round, at);
    }

    public bool Observe(FieldObservation o)
    {
        if (!o.IsRest) { _battle = true; _drag = null; _dragEquipment = null; _beforeDrag = null; _shop = null; _shopHistory.Clear(); _buys.Clear(); _gains.Clear(); _tierChanges.Clear(); return false; }
        if (o.DragOperator is { } drag)
        {
            _tierChanges.Clear();
            foreach (var cell in _missing.Where(p => p.Value.Unit.Operator == drag).Select(p => p.Key).ToArray())
                _missing.Remove(cell);
            if (_drag is null) { _beforeDrag = new(_units); _dragMoney = _money; }
            _drag = drag;
            if (o.Equipment is not null) _dragEquipment = o.Equipment;
            return false;
        }
        if (!o.MainScreen)
        {
            CaptureEquipmentGain(o, _units.Values);
            _tierChanges.Clear();
            // 详情或短暂遮挡不代表刷新；购买段按观测时间让历史过期。
            return ApplyEquipment(o) && Publish(State?.MapId, o.Round, o.At);
        }
        RestoreMissing(o);
        var before = _beforeDrag ?? new(_units);
        var dragged = _drag;
        var removed = before.Keys.Where(c => o.Cells.TryGetValue(c, out int t) && t == 0).ToList();
        var added = o.Cells.Where(p => p.Value > 0 && !before.ContainsKey(p.Key)).Select(p => p.Key).ToList();
        foreach (var (cell, tier) in o.Cells)
        {
            if (tier == 0) _units.Remove(cell);
            else if (!_units.TryGetValue(cell, out var old) || old.Tier != tier)
                _units[cell] = new(cell.Zone, cell.Col, cell.Row, tier, null, null);
        }
        if (!_battle && _drag is { } name)
        {
            if (removed.Count == 1 && added.Count == 1)
            {
                var from = removed[0]; var to = added[0];
                _units[to] = _units[to] with { Operator = name, Source = "drag", Status = "sure", Candidates = [],
                    Elite = before[from].Operator == name ? before[from].Elite : null, Equipment = _dragEquipment ?? before[from].Equipment };
                Add(o.At, "move", name, from, to); removed.Clear(); added.Clear();
            }
            else if (removed.Count == 1 && added.Count == 0 && o.Money > _dragMoney)
            {
                Add(o.At, "sell", name, removed[0], null); removed.Clear();
            }
            else if (removed.Count == 0 && added.Count == 0)
            {
                var changed = o.Cells.Where(p => p.Value > 0 && before.TryGetValue(p.Key, out var u)
                    && u.Tier != p.Value).Select(p => p.Key).ToArray();
                if (changed.Length == 2)
                {
                    var a = changed[0]; var b = changed[1];
                    bool known = before[a].Tier == _units[b].Tier && before[b].Tier == _units[a].Tier;
                    foreach (var (from, to) in new[] { (a, b), (b, a) })
                    {
                        var u = before[from];
                        _units[to] = _units[to] with { Operator = known ? u.Operator : null, Source = known ? u.Source : null, Status = known ? u.Status : "unknown", Candidates = known ? u.Candidates : [], Elite = known ? u.Elite : null, Equipment = known ? u.Equipment : null };
                        Add(o.At, "move", known ? u.Operator : null, from, to);
                    }
                    var origin = changed.Where(c => before[c].Operator == name).ToArray();
                    if (known && origin.Length == 1)
                    {
                        var to = origin[0] == a ? b : a;
                        _units[to] = _units[to] with { Operator = name, Source = "drag", Status = "sure", Candidates = [], Equipment = _dragEquipment ?? _units[to].Equipment };
                    }
                }
                else
                {
                    // 同阶换位与原位取消无法仅凭角标区分；清除可能被交换的身份。
                    var origins = before.Values.Where(u => u.Operator == name).ToArray();
                    if (origins.Length == 1)
                    {
                        var possible = before.Values.Where(u => u.Tier == origins[0].Tier).ToArray();
                        if (possible.Length == 1 && _dragEquipment is not null)
                            _units[origins[0].Cell] = _units[origins[0].Cell] with { Equipment = _dragEquipment };
                        else if (possible.Length == 2)
                        {
                            foreach (var u in possible)
                            {
                                _units[u.Cell] = _units[u.Cell] with { Operator = null, Source = null, Status = "unknown", Candidates = [], Equipment = null };
                                Add(o.At, "move", null, u.Cell, possible.Single(p => p.Cell != u.Cell).Cell);
                            }
                        }
                        // TODO(design): 三名以上同阶且占用不变时，角标无法定位交换双方；当前清除该阶身份，不编造移动事件。
                        else if (possible.Length > 2)
                            foreach (var u in possible) _units[u.Cell] = _units[u.Cell] with { Operator = null, Source = null, Status = "unknown", Candidates = [], Equipment = null };
                    }
                }
            }
        }
        CaptureGainContext(o, before, dragged);
        _drag = null; _dragEquipment = null; _beforeDrag = null;
        if (!_battle)
        {
            _gains.RemoveAll(g => !_units.ContainsKey(g.Cell));
            foreach (var c in added.Where(c => c.Zone == "bench"))
            {
                _gains.RemoveAll(g => g.Cell == c);
                _gains.Add((o.At, c));
            }
            _shopHistory.RemoveAll(s => (o.At - s.At).TotalSeconds > PurchaseWindowSeconds);
            if (o.Shop is { } shop)
            {
                // 三选一会暂时覆盖普通商店；只比较仍有原卡、且没有换入新名字的快照。
                // 整批替换属于刷新或选择结束，不能把第一个消失的名字写进唯一新增格。
                var previous = _shop is not null && (o.At - _shopAt).TotalSeconds <= PurchaseWindowSeconds
                    ? _shop : null;
                var comparable = _shopHistory.AsEnumerable().Reverse().Select(s => s.Cards)
                    .Prepend(previous).FirstOrDefault(p => p is not null
                        && shop.All(c => p.GetValueOrDefault(c.Key) == c.Value)
                        && (shop.Count > 0 || p.Count == 1));
                if (comparable is not null)
                    foreach (var (slot, cardName) in comparable)
                        if (!shop.ContainsKey(slot) && !_buys.Any(b => b.Slot == slot && b.Name == cardName)
                            && !_log.Any(e => e.Kind == "buy" && e.Operator == cardName
                                && e.At == o.At.ToString("O")))
                            _buys.Add((o.At, slot, cardName));
                if (previous is not null && !previous.OrderBy(p => p.Key).SequenceEqual(shop.OrderBy(p => p.Key)))
                    _shopHistory.Add((_shopAt, previous));
                _shop = shop; _shopAt = o.At;
            }
            foreach (var buy in _buys.ToArray())
            {
                if (o.Shop?.GetValueOrDefault(buy.Slot) == buy.Name)
                {
                    _log.RemoveAll(e => e.Kind == "buy" && e.Operator == buy.Name && e.To is null
                        && e.At == buy.At.ToString("O"));
                    _buys.Remove(buy); continue;
                }
                bool SameTier(string name, FieldCell cell) => !_identityOperators.Any(p => p.Name == name)
                    || _identityOperators.Any(p => p.Name == name && p.Tier == _units[cell].Tier);
                var candidates = _gains.Where(g => Math.Abs((g.At - buy.At).TotalSeconds) <= PurchaseWindowSeconds
                    && _units.ContainsKey(g.Cell) && SameTier(buy.Name, g.Cell)
                    && !_buys.Any(other => other != buy
                        && Math.Abs((g.At - other.At).TotalSeconds) <= PurchaseWindowSeconds
                        && SameTier(other.Name, g.Cell))).ToArray();
                int i = candidates.Length == 1 ? _gains.IndexOf(candidates[0]) : -1;
                if (i >= 0)
                {
                    var c = _gains[i].Cell;
                    _units[c] = _units[c] with { Operator = buy.Name, Source = "purchase", Status = "sure", Candidates = [] };
                    // 已公布但尚未定位的购买，在同一条日志上补格；不要重复记一次购买。
                    _log.RemoveAll(e => e.Kind == "buy" && e.Operator == buy.Name && e.To is null
                        && e.At == buy.At.ToString("O") || e.Kind == "gain" && e.To == c
                        && e.At == _gains[i].At.ToString("O"));
                    Add(buy.At, "buy", buy.Name, null, c); _gains.RemoveAt(i); _buys.Remove(buy);
                    _shopHistory.RemoveAll(s => s.Cards.GetValueOrDefault(buy.Slot) == buy.Name);
                    added.Remove(c);
                }
                else if ((o.At - buy.At).TotalSeconds > 1.6)
                {
                    if (!_log.Any(e => e.Kind == "buy" && e.Operator == buy.Name && e.At == buy.At.ToString("O")))
                        Add(buy.At, "buy", buy.Name, null, null);
                    if ((o.At - buy.At).TotalSeconds > PurchaseWindowSeconds)
                    {
                        _buys.Remove(buy);
                        _shopHistory.RemoveAll(s => s.Cards.GetValueOrDefault(buy.Slot) == buy.Name);
                    }
                }
            }
            // 购买先占用新增格；剩下唯一的同阶空出与新增才推断为漏面板移动。
            if (o.Sprites is null && removed.Count == 1 && added.Count == 1 && before[removed[0]].Tier == _units[added[0]].Tier)
            {
                var from = removed[0]; var to = added[0]; var unit = before[from];
                _units[to] = _units[to] with { Operator = unit.Operator, Source = unit.Source, Status = unit.Status, Candidates = unit.Candidates, Elite = unit.Elite, Equipment = unit.Equipment };
                Add(o.At, "move", unit.Operator, from, to);
                _gains.RemoveAll(g => g.Cell == from || g.Cell == to);
                removed.Clear();
            }
            foreach (var c in removed) Add(o.At, "lost", before[c].Operator, c, null);
            foreach (var gain in _gains.Where(g => (o.At - g.At).TotalSeconds > 1.6).ToArray())
            {
                if (!_log.Any(e => e.Kind == "gain" && e.To == gain.Cell && e.At == gain.At.ToString("O")))
                    Add(gain.At, "gain", null, null, gain.Cell);
                if ((o.At - gain.At).TotalSeconds > PurchaseWindowSeconds) _gains.Remove(gain);
            }
        }
        DebounceTier(o, before, dragged);
        // 仅在原格升一阶且同名两份消失时，沿用原格装备；不改变既有身份判定。
        foreach (var (cell, unit) in _units.ToArray())
            if (before.TryGetValue(cell, out var old) && old.Operator is not null && unit.Tier == old.Tier + 1
                && before.Values.Count(u => u.Cell != cell && u.Operator == old.Operator && u.Tier == old.Tier
                    && o.Cells.TryGetValue(u.Cell, out int tier) && tier == 0) == 2)
                _units[cell] = unit with { Equipment = old.Equipment };
        ResolveIdentity(o, before);
        ApplyEquipment(o);
        // 战斗后的第一帧不推断购买，但要为下一帧保留商店基线。
        if (o.Shop is { } currentShop) { _shop = currentShop; _shopAt = o.At; }
        _battle = false; _money = o.Money ?? _money;
        return Publish(o.MapId ?? State?.MapId, o.Round, o.At);
    }

    private void DebounceTier(FieldObservation o, Dictionary<FieldCell, FieldUnit> before, string? dragged)
    {
        var pending = new Dictionary<FieldCell, int>();
        foreach (var (cell, tier) in o.Cells)
        {
            if (tier == 0 || !before.TryGetValue(cell, out var old)
                || old.Status is not ("sure" or "auto") || old.Tier == tier) continue;
            // 先让真实购买、拖动和现有三合一判定处理；只有无事件的阶数变化等待第二次。
            bool changedByEvent = dragged is not null && old.Operator == dragged
                || _units[cell].Source is "purchase" or "drag" || _log.Any(e => e.At == o.At.ToString("O")
                && e.Kind is "move" or "sell" or "buy" && (e.From == cell || e.To == cell));
            bool merge = old.Operator is not null && old.Status == "sure" && tier == old.Tier + 1
                && before.Values.Count(u => u.Cell != cell && u.Operator == old.Operator && u.Status == "sure"
                    && u.Tier == old.Tier && o.Cells.GetValueOrDefault(u.Cell, -1) == 0) == 2;
            if (changedByEvent || merge || _tierChanges.GetValueOrDefault(cell) == tier) continue;
            _units[cell] = old;
            pending[cell] = tier;
        }
        _tierChanges.Clear();
        foreach (var (cell, tier) in pending) _tierChanges[cell] = tier;
    }

    private bool ApplyEquipment(FieldObservation o)
    {
        if (o.PanelOperator is null || o.Equipment is null) return false;
        var matches = _units.Values.Where(u => u.Operator == o.PanelOperator).ToArray();
        // 没有选中格的可靠证据时，同名多份不写。
        if (matches.Length != 1) return false;
        var unit = matches[0];
        if (unit.Equipment?.SequenceEqual(o.Equipment) == true) return false;
        _units[unit.Cell] = unit with { Equipment = o.Equipment };
        return true;
    }

    private void Add(DateTimeOffset at, string kind, string? name, FieldCell? from, FieldCell? to)
    {
        if (from is not null && kind is "move" or "sell") _missing.Remove(from);
        TrackRosterEvent(at, kind, name, to);
        _log.Add(new(at.ToString("O"), kind, name, from, to));
        _log.Sort((a, b) => string.CompareOrdinal(b.At, a.At));
        if (_log.Count > 20) _log.RemoveRange(20, _log.Count - 20);
    }

    private bool Publish(string? map, int? round, DateTimeOffset at)
    {
        var units = _units.Values.OrderBy(u => u.Zone == "bench" ? 0 : 1).ThenBy(u => u.Row).ThenBy(u => u.Col).ToArray();
        if (State is { } old && old.MapId == map && old.Round == round && old.Units.SequenceEqual(units) && old.Log.SequenceEqual(_log) && old.Roster.SequenceEqual(_roster.Values)) return false;
        State = new(map, round, at.ToString("O"), units, _log.ToArray()) { Roster = _roster.Values.ToArray() };
        return true;
    }
}
