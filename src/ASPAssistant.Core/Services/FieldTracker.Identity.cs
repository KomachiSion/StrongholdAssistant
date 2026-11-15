using ASPAssistant.Core.Models;
using System.Text.RegularExpressions;

namespace ASPAssistant.Core.Services;

public sealed partial class FieldTracker
{
    public const double MinimumRosterScore = .35;
    public const double SameSpriteScore = .92;
    private readonly Dictionary<string, FieldRosterEntry> _roster = new(StringComparer.Ordinal);
    private readonly Dictionary<FieldCell, FieldSpritePatch> _pictures = [];
    private readonly Dictionary<FieldCell, FieldSpritePatch> _anchors = [];
    private readonly List<(FieldUnit Unit, FieldSpritePatch Patch)> _learned = [];
    private readonly HashSet<(string Name, int Tier)> _confirmed = [];
    private readonly HashSet<string> _playerConfirmed = [];
    private readonly Dictionary<FieldCell, (FieldUnit Unit, FieldSpritePatch Patch, DateTimeOffset At, int Scans)> _missing = [];
    private readonly HashSet<FieldCell> _returned = [];
    private readonly Dictionary<FieldCell, (FieldUnit Unit, FieldSpritePatch Patch)> _history = [];
    private readonly Dictionary<FieldCell, string> _gainSources = [];
    private readonly HashSet<FieldCell> _untrackedGains = [];
    private IReadOnlyList<Operator> _identityOperators = [];
    private Func<FieldSpritePatch, int, string[], SpriteMatch>? _matchSprite;
    private string? _strategyName;
    private sealed record GainContext(string Source, string[]? Names);
    private readonly Dictionary<FieldCell, GainContext> _gainContexts = [];
    private (DateTimeOffset At, GainContext Context)? _recentGift;

    public void ConfigureIdentity(IReadOnlyList<Operator> operators,
        Func<FieldSpritePatch, int, string[], SpriteMatch> match)
    { _identityOperators = operators; _matchSprite = match; }

    public void SeedRoster(string? strategyName, IEnumerable<string> custom, int? round, DateTimeOffset at)
    {
        _strategyName = strategyName;
        foreach (var op in _identityOperators)
        {
            var gift = Regex.Match(op.Obtain ?? "", "策略「(.+?)」第\\s*(\\d+)\\s*回合赠送");
            if (gift.Success && gift.Groups[1].Value == strategyName && round >= int.Parse(gift.Groups[2].Value))
                AddRoster(op.Name, "strategy", at);
        }
        foreach (string name in custom) AddRoster(name, "custom", at);
    }

    public void AddRoster(string name, string source, DateTimeOffset at)
    {
        if (!_roster.ContainsKey(name)) _roster[name] = new(name, source, at.ToString("O"), false);
    }

    private void ResetIdentity()
    { _roster.Clear(); _pictures.Clear(); _anchors.Clear(); _learned.Clear(); _confirmed.Clear(); _playerConfirmed.Clear(); _missing.Clear(); _returned.Clear(); _history.Clear(); _gainSources.Clear(); _untrackedGains.Clear(); _gainContexts.Clear(); _recentGift = null; _strategyName = null; }

    // 在购买配对前保存来源证据；配上的购买仍由原流程优先确定身份。
    private void CaptureGainContext(FieldObservation o, Dictionary<FieldCell, FieldUnit> before, string? dragged)
    {
        var board = before.Values.Where(u => u.Zone == "board").ToArray();
        var removed = before.Values.Where(u => o.Cells.GetValueOrDefault(u.Cell, -1) == 0).ToArray();
        bool ethan = _log.Any(e => e.Kind == "sell" && e.Operator == "伊桑" && e.At == o.At.ToString("O"))
            || dragged == "伊桑" && o.Money > _dragMoney && removed.Any(u => u.Operator == "伊桑");
        bool mechanic = _battle && board.Any(u => u.Operator == "机械师"
            && (u.Elite == true || board.Count(v => v.Row == u.Row) >= 3));
        if (ethan || mechanic)
        {
            var names = board.Where(u => !ethan || !removed.Contains(u)).Select(u => u.Operator).Distinct().ToHashSet();
            var counts = _identityOperators.Where(p => names.Contains(p.Name))
                .SelectMany(p => p.CoreCovenants.Concat(p.AdditionalCovenants).Distinct())
                .GroupBy(c => c).ToArray();
            var covenants = counts.Where(g => g.Count() == counts.Max(c => c.Count())).Select(g => g.Key).ToHashSet();
            var candidates = _identityOperators.Where(p => (!ethan || p.Name != "伊桑")
                && p.CoreCovenants.Concat(p.AdditionalCovenants).Any(covenants.Contains)).Select(p => p.Name).ToArray();
            // 没有读到场上盟约时保留同阶全集，不把空证据当成排除所有人。
            // TODO(design): 机械师没有独立来源值，暂归 strategy；需要区分时扩展来源定义。
            _recentGift = (o.At, new(ethan ? "ethan" : "strategy", candidates.Length == 0 ? null : candidates));
        }
        else if (_battle && _strategyName == "得闲饮茶" && o.Round == 3)
            _recentGift = (o.At, new("strategy", _identityOperators.Where(p => p.Tier is 2 or 4).Select(p => p.Name).ToArray()));

        if (!ethan && !mechanic) CaptureEquipmentGain(o, before.Values);

        foreach (var unit in _units.Values.Where(u => u.Zone == "bench" && !before.ContainsKey(u.Cell)))
        {
            GainContext context = _recentGift is {} recent && (o.At - recent.At).TotalSeconds <= 3
                ? recent.Context : StrategyGain(o, before, removed);
            if (o.GainSource is not null) context = context with { Source = o.GainSource };
            _gainContexts[unit.Cell] = context;
            _gainSources[unit.Cell] = context.Source;
            if (_battle) _untrackedGains.Add(unit.Cell);
        }
    }

    private void CaptureEquipmentGain(FieldObservation o, IEnumerable<FieldUnit> units)
    {
        var used = units.SelectMany(u => (u.Equipment ?? []).Where(e => e.Name is "简易通讯机" or "寻呼模块" or "信标")
            .Where(e => o.Cells.GetValueOrDefault(u.Cell, -1) == 0 || o.PanelOperator == u.Operator
                && o.Equipment is not null && !o.Equipment.Any(n => n.Name == e.Name))
            .Select(e => (Unit: u, Equipment: e))).FirstOrDefault();
        if (used.Unit is null) return;
        bool choice = used.Equipment.Name is "寻呼模块" or "信标";
        var op = _identityOperators.FirstOrDefault(p => p.Name == used.Unit.Operator);
        string? covenant = op?.CoreCovenants.Concat(op.AdditionalCovenants).FirstOrDefault();
        var names = o.Shop?.Values.Distinct().ToArray();
        if (!choice || names is not { Length: > 0 })
            names = covenant is null || used.Equipment.Name == "信标" ? null
                : _identityOperators.Where(p => p.CoreCovenants.Contains(covenant) || p.AdditionalCovenants.Contains(covenant)).Select(p => p.Name).ToArray();
        _recentGift = (o.At, new(choice ? "dispatch" : "equipment", names));
    }

    private GainContext StrategyGain(FieldObservation o, Dictionary<FieldCell, FieldUnit> before, FieldUnit[] removed)
    {
        // TODO(design): 观测没有调度等级和供选模式；目前按已读阶数、卡名和策略缩候选。
        string? covenant = _strategyName switch
        {
            "人才盲盒" => "维多利亚", "团伙行动" => "叙拉古", "猎人战法" => "阿戈尔",
            "骑士团长" => "卡西米尔", "流动乐章" => "莱塔尼亚", "无惧风雪" => "乌萨斯", "黑白相易" => "炎", _ => null
        };
        if (covenant is not null)
            return new("strategy", _identityOperators.Where(p => (p.CoreCovenants.Contains(covenant) || p.AdditionalCovenants.Contains(covenant))
                && (_strategyName != "黑白相易" || removed.Length == 0 || p.Tier <= removed.Max(u => u.Tier))).Select(p => p.Name).ToArray());
        if (_strategyName == "幻画为真") return new("strategy", before.Values.Where(u => u.Operator is not null).Select(u => u.Operator!).Distinct().ToArray());
        if (_strategyName is "纸片替身" or "不稳定要素")
            return new("strategy", removed.Length == 0 ? null : _identityOperators.Where(p => removed.Any(u => p.Tier == u.Tier + (_strategyName == "不稳定要素" ? 1 : 0))).Select(p => p.Name).ToArray());
        if (_strategyName is "最佳品味" or "打包速递" or "替身娃娃")
        {
            var cards = _shop?.Where(p => o.Shop is not null && !o.Shop.ContainsKey(p.Key)).Select(p => p.Value).Distinct().ToArray();
            return new("strategy", cards is { Length: > 0 } ? cards : null);
        }
        if (_strategyName is "集结指示" or "协力共进")
        {
            var cards = o.Shop?.Values.Distinct().ToArray();
            return new("dispatch", cards is { Length: > 0 } ? cards : null);
        }
        // 固定赠送也属于来源候选，只在本回合进入休整期时使用。
        var gifts = _identityOperators.Where(p => Regex.IsMatch(p.Obtain ?? "", $"策略「{Regex.Escape(_strategyName ?? "")}」第\\s*{o.Round}\\s*回合赠送")).Select(p => p.Name).ToArray();
        return _battle && gifts.Length > 0 ? new("strategy", gifts) : new("manual", null);
    }

    private void TrackRosterEvent(DateTimeOffset at, string kind, string? name, FieldCell? to)
    {
        if (kind == "gain" && to is not null)
        {
            _untrackedGains.Add(to);
            // 未知来源保留待确认锚点；有来源范围或本阶唯一推断时重新核实。
            if (_units.GetValueOrDefault(to)?.Source == "only"
                || _gainContexts.GetValueOrDefault(to)?.Names is not null) _anchors.Remove(to);
        }
        if (name is null) return;
        if (kind == "buy")
        {
            AddRoster(name, "purchase", at);
            _roster[name] = _roster[name] with { Sold = false };
        }
        if (kind == "sell" && _roster.TryGetValue(name, out var item))
            _roster[name] = item with { Sold = !_units.Values.Any(u => u.Operator == name) };
    }

    private void ConfirmIdentity(FieldCell cell, string? name, DateTimeOffset at)
    {
        if (name is null)
        {
            if (_pictures.TryGetValue(cell, out var unknown)) _anchors[cell] = unknown;
            return;
        }
        _untrackedGains.Remove(cell);
        _playerConfirmed.Add(name);
        _gains.RemoveAll(g => g.Cell == cell);
        AddRoster(name, _gainSources.GetValueOrDefault(cell, "manual"), at);
        _roster[name] = _roster[name] with { Sold = false };
        if (_pictures.TryGetValue(cell, out var patch))
        {
            _anchors[cell] = patch;
            // 手动纠正同时替换相似图块的旧自动身份。
            _learned.RemoveAll(p => p.Unit.Tier == _units[cell].Tier
                && SpriteSimilarity(p.Patch, patch) >= SameSpriteScore);
            Remember(_units[cell], patch);
            _history[cell] = (_units[cell], patch);
        }
    }

    public static double SpriteSimilarity(FieldSpritePatch a, FieldSpritePatch b)
    {
        double ma = a.Pixels.Average(x => (double)x), mb = b.Pixels.Average(x => (double)x);
        double xy = 0, xx = 0, yy = 0, delta = 0;
        for (int i = 0; i < a.Pixels.Length; i++)
        {
            double x = a.Pixels[i] - ma, y = b.Pixels[i] - mb;
            xy += x * y; xx += x * x; yy += y * y;
            delta += Math.Abs(a.Pixels[i] - b.Pixels[i]);
        }
        double color = 1 - delta / (a.Pixels.Length * 255);
        double corr = xx * yy > 0 ? xy / Math.Sqrt(xx * yy) : color;
        return .75 * corr + .25 * color;
    }

    private void ResolveIdentity(FieldObservation o, Dictionary<FieldCell, FieldUnit> before)
    {
        if (o.Sprites is null || _matchSprite is null) return;
        var gone = before.Values.Where(u => o.Cells.GetValueOrDefault(u.Cell, -1) == 0).ToList();
        var used = new HashSet<FieldCell>();
        foreach (var (cell, patch) in o.Sprites)
        {
            if (!_units.TryGetValue(cell, out var unit)) continue;
            // 异阶尚未确认时不学习这一帧，避免把换入者的图块记到旧身份下。
            if (_tierChanges.ContainsKey(cell)) continue;
            before.TryGetValue(cell, out var old);
            _pictures[cell] = patch;
            bool arrival = old is null;
            bool unexplainedGain = _untrackedGains.Contains(cell);
            var context = _gainContexts.GetValueOrDefault(cell);
            if (arrival && !_gainContexts.ContainsKey(cell)) _gainSources[cell] = o.GainSource ?? "manual";
            // 2. 本次事件已由商店卡或拖动面板确定身份。
            bool direct = unit.Operator is not null && (unit.Source == "purchase" || unit.Source == "drag")
                && (old?.Operator != unit.Operator || old.Source != unit.Source || arrival);
            if (direct)
            {
                SetIdentity(cell, unit with { Status = "sure", Candidates = [] }, patch);
                AddRoster(unit.Operator!, unit.Source == "purchase" ? "purchase" : "manual", o.At);
                continue;
            }
            // 两次空扫描或三秒内回到原格，连待确认候选也原样保留。
            if (_returned.Contains(cell) && old is not null && unit.Operator == old.Operator && unit.Source == old.Source)
            { SetIdentity(cell, old, patch); continue; }
            // 同格连续占用且没有事件改写身份时，动画和镜头变化不使确定身份降级。
            // 延迟发现的无卡新增仍需推翻“本阶唯一”的推断。
            if (old is not null && old.Tier == unit.Tier && unit.Operator == old.Operator && unit.Source == old.Source
                && old.Status is "sure" or "auto" && !(_untrackedGains.Contains(cell)
                    && (old.Source == "only" || !_anchors.ContainsKey(cell))))
            { SetIdentity(cell, old, patch); continue; }
            // 待确认仍使用固定锚点，避免候选顺序随待机动画抖动。
            if (old is not null && old.Tier == unit.Tier && unit.Operator == old.Operator && unit.Source == old.Source
                && _anchors.TryGetValue(cell, out var anchor) && SpriteSimilarity(anchor, patch) >= SameSpriteScore)
            { SetIdentity(cell, old, patch, false); continue; }
            // 3. 一次扫描内的空出格与新格必须同阶、图像相似，且只能消费一次。
            var moves = gone.Where(u => !used.Contains(u.Cell) && u.Operator is not null && u.Tier == unit.Tier
                && _anchors.TryGetValue(u.Cell, out var image) && SpriteSimilarity(image, patch) >= SameSpriteScore).ToArray();
            if (arrival && moves.Length == 1)
            {
                var from = moves[0]; used.Add(from.Cell);
                // 自动和待确认身份移动后仍保留不确定程度，不能凭挪格变成事实。
                SetIdentity(cell, unit with { Operator = from.Operator, Source = "carry", Status = from.Status,
                    Candidates = from.Candidates, Elite = from.Elite }, patch);
                _units[cell] = _units[cell] with { Equipment = from.Equipment };
                _log.RemoveAll(e => e.Kind == "lost" && e.From == from.Cell && e.At == o.At.ToString("O"));
                Add(o.At, "move", from.Operator, from.Cell, cell);
                _gains.RemoveAll(g => g.Cell == cell);
                continue;
            }
            // 4. 原格升阶且同名同阶的两份同时消失。
            if (old?.Operator is not null && old.Status == "sure" && unit.Tier == old.Tier + 1
                && gone.Count(u => u.Operator == old.Operator && u.Status == "sure" && u.Tier == old.Tier) == 2)
            {
                SetIdentity(cell, unit with { Operator = old.Operator, Source = "merge", Status = "sure", Candidates = [], Elite = true }, patch);
                continue;
            }
            // 跨扫描挪格、短暂漏读及换镜头后，先找本局已确认或自动认出的个人图块。
            var remembered = _learned.Where(p => p.Unit.Tier == unit.Tier
                && (!unexplainedGain || context?.Names is null || context.Names.Contains(p.Unit.Operator!))
                && SpriteMatcher.MatchPersonal(p.Patch, patch) >= SameSpriteScore).ToArray();
            if (remembered.Select(p => p.Unit.Operator).Distinct().Count() == 1)
            {
                var known = remembered.OrderByDescending(p => p.Unit.Status == "sure").First();
                SetIdentity(cell, known.Unit with { Source = "carry" }, patch);
                _untrackedGains.Remove(cell); _gains.RemoveAll(g => g.Cell == cell);
                continue;
            }
            if (old is null && _history.TryGetValue(cell, out var previous) && previous.Unit.Status == "pending"
                && previous.Unit.Tier == unit.Tier && SpriteSimilarity(previous.Patch, patch) >= SameSpriteScore)
            { SetIdentity(cell, previous.Unit, previous.Patch); continue; }
            var names = _roster.Values.OrderBy(r => r.Sold).Where(r => HasTier(r.Operator, unit.Tier)).Select(r => r.Operator).ToArray();
            // 无卡赠送新格不能被“本阶唯一”强填；它可能根本不在本局名单里。
            var active = names.Where(n => !_roster[n].Sold).ToArray();
            // 5. 本阶唯一。
            if (!unexplainedGain && active.Length == 1)
            {
                SetIdentity(cell, unit with { Operator = active[0], Source = "only", Status = "sure", Candidates = [],
                    Elite = unit.Operator == active[0] ? unit.Elite : null }, patch);
                continue;
            }
            // 6. 本局同阶；低分或无卡新增时放开到该阶全集，必须确认。
            var ranked = unexplainedGain ? Rank(patch, unit.Tier, context?.Names is {} pool
                ? pool.Where(n => HasTier(n, unit.Tier)).ToArray()
                : names.Concat(_identityOperators.Where(p => p.Tier == unit.Tier).Select(p => p.Name)).Distinct().ToArray()) : Rank(patch, unit.Tier, names);
            bool open = unexplainedGain || ranked.Length == 0 || ranked.Max(c => c.Score) < MinimumRosterScore;
            if (open && !unexplainedGain) ranked = Rank(patch, unit.Tier, _identityOperators.Where(p => p.Tier == unit.Tier).Select(p => p.Name).ToArray());
            if (unexplainedGain) ranked = ranked.OrderByDescending(c => c.Score).ToArray();
            var preferred = unexplainedGain ? null : ranked.Take(3).FirstOrDefault(c => _confirmed.Contains((c.Name, unit.Tier))
                && !_units.Values.Any(u => u.Cell != cell && u.Operator == c.Name));
            if (preferred is not null) ranked = new[] { preferred }.Concat(ranked.Where(c => c.Name != preferred.Name)).ToArray();
            var candidates = ranked.Take(3).Select(c => new FieldCandidate(c.Name, c.Score)).ToArray();
            bool confirmedFirst = ranked.Length > 0 && _playerConfirmed.Contains(ranked[0].Name)
                && !_units.Values.Any(u => u.Cell != cell && u.Operator == ranked[0].Name);
            // Rank 已逐名字取个人模板与冻结模板的高分，个人模板领先也用同一条分差判断。
            double gap = confirmedFirst ? SpriteMatcher.AutoFillGap / 2 : SpriteMatcher.AutoFillGap;
            bool sourceKnown = unexplainedGain && context?.Names is not null;
            string status = candidates.Length == 0 ? "unknown" : (!open || sourceKnown || preferred is not null || confirmedFirst) && ranked.Length > 1
                && ranked[0].Score - ranked[1].Score >= gap ? "auto" : "pending";
            SetIdentity(cell, unit with { Operator = candidates.FirstOrDefault()?.Operator,
                Source = candidates.Length == 0 ? null : "sprite", Status = status, Candidates = candidates,
                Elite = unit.Operator == candidates.FirstOrDefault()?.Operator ? unit.Elite : null }, patch);
        }
        foreach (var u in gone)
        {
            bool consumed = _log.Any(e => e.At == o.At.ToString("O") && e.From == u.Cell && e.Kind is "move" or "sell")
                || before.Values.Any(v => v.Operator == u.Operator && v.Tier == u.Tier
                    && _units.TryGetValue(v.Cell, out var merged) && merged.Source == "merge" && merged.Tier == u.Tier + 1);
            if (!consumed && _pictures.TryGetValue(u.Cell, out var picture)) _missing[u.Cell] = (u, picture, o.At, 1);
            _pictures.Remove(u.Cell); _anchors.Remove(u.Cell); _gainSources.Remove(u.Cell); _gainContexts.Remove(u.Cell); _untrackedGains.Remove(u.Cell);
        }
    }

    private void RestoreMissing(FieldObservation o)
    {
        _returned.Clear();
        foreach (var (cell, missing) in _missing.ToArray())
        {
            if (!o.Cells.TryGetValue(cell, out int tier)) continue;
            bool within = missing.Scans <= 2 || (o.At - missing.At).TotalSeconds <= 3;
            if (tier > 0 && tier == missing.Unit.Tier && within)
            {
                _units[cell] = missing.Unit;
                _pictures[cell] = missing.Patch; _anchors[cell] = missing.Patch;
                _returned.Add(cell);
            }
            if (tier > 0 || !within) _missing.Remove(cell);
            else _missing[cell] = missing with { Scans = missing.Scans + 1 };
        }
    }

    private bool HasTier(string name, int tier) => _identityOperators.Any(o => o.Name == name && o.Tier == tier)
        || _units.Values.Any(u => u.Operator == name && u.Tier == tier && u.Status == "sure")
        || _learned.Any(p => p.Unit.Operator == name && p.Unit.Tier == tier);

    private SpriteCandidate[] Rank(FieldSpritePatch patch, int tier, string[] names)
    {
        if (names.Length == 0) return [];
        // 先比个人模板，再逐名字与冻结模板取高分，沿用原分差线。
        var personal = _learned.Where(p => p.Unit.Tier == tier && names.Contains(p.Unit.Operator!))
            .GroupBy(p => p.Unit.Operator!).ToDictionary(g => g.Key,
                g => g.Max(p => SpriteMatcher.MatchPersonal(p.Patch, patch)));
        return _matchSprite!(patch, tier, names).Candidates
            .Concat(personal.Select(p => new SpriteCandidate(p.Key, p.Value)))
            .GroupBy(c => c.Name).Select(g => g.MaxBy(c => c.Score)!)
            .OrderBy(c => _roster.GetValueOrDefault(c.Name)?.Sold ?? false).ThenByDescending(c => c.Score).ToArray();
    }

    private void Remember(FieldUnit unit, FieldSpritePatch patch)
    {
        if (unit.Operator is null || !(unit.Status == "auto" || unit.Status == "sure" && unit.Source != "only")) return;
        if (unit.Status == "sure") _confirmed.Add((unit.Operator, unit.Tier));
        if (_learned.Any(p => p.Unit.Operator == unit.Operator && p.Unit.Tier == unit.Tier
            && SpriteSimilarity(p.Patch, patch) >= SameSpriteScore)) return;
        var same = _learned.Where(p => p.Unit.Operator == unit.Operator).ToArray();
        if (same.Length == 6) _learned.Remove(same[0]);
        _learned.Add((unit, patch));
    }

    private void SetIdentity(FieldCell cell, FieldUnit identity, FieldSpritePatch patch, bool save = true)
    {
        // 装备字段归独立追踪器管理。
        var current = _units[cell];
        _units[cell] = current with { Operator = identity.Operator, Source = identity.Source,
            Status = identity.Status, Candidates = identity.Candidates, Elite = identity.Elite };
        if (save)
        {
            _anchors[cell] = patch;
            _history[cell] = (_units[cell], patch);
            Remember(_units[cell], patch);
        }
    }
}
