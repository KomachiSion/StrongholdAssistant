using ASPAssistant.Core.Models;
using ASPAssistant.Core.Services;

namespace StrongholdAssistant.Host;

public sealed partial class HostApi
{
    private readonly object _fieldGate = new();
    private readonly FieldTracker _fieldTracker = new();
    private FieldReader? _fieldReader;
    private int _fieldGeneration;
    private int _fieldReaderGeneration;
    private Task _fieldIdentityWork = Task.CompletedTask;
    private SpriteMatcher? _fieldSprites;
    public event Action<FieldState?, FieldOverlayGeometry?>? FieldOverlayChanged;
    private FieldOverlayGeometry? _fieldOverlayGeometry;

    internal void HideFieldOverlay()
    {
        _fieldOverlayGeometry = null;
        FieldOverlayChanged?.Invoke(Field, null);
    }

    public Func<System.Drawing.RectangleF?> FieldCursorRegion { get; set; } = () => null;

    public FieldState? Field { get { lock (_fieldGate) return _fieldTracker.State; } }

    public void InitializeField(string directory, IOcrEngine ocr)
    {
        _fieldReader = new(directory, ocr, _operators.Select(o => o.Name));
        _fieldSprites?.Dispose();
        _fieldSprites = new(Path.Combine(directory, "sprite-templates.bin.gz"));
        _fieldTracker.ConfigureIdentity(_operators, (patch, tier, names) =>
        {
            using var crop = OpenCvSharp.Cv2.ImDecode(patch.Image, OpenCvSharp.ImreadModes.Color);
            return _fieldSprites.MatchCrop(crop, patch.Bench, tier, names);
        });
        _shopReader!.ReadUntracked = true;
    }

    public async Task ObserveFieldAsync(byte[] frame, DateTimeOffset at, EconomyReading? economy,
        bool mainScreen, bool shopVisible, IReadOnlyList<ShopItem> shop)
    {
        // 小人比对单独串行执行，忙时不积压旧截图；本轮资金、商店继续扫描。
        if (!_fieldIdentityWork.IsCompleted) return;
        await _fieldIdentityWork;
        int generation;
        lock (_fieldGate) generation = _fieldGeneration;
        if (_fieldReaderGeneration != generation) { _fieldReader!.Reset(); _fieldReaderGeneration = generation; }
        _fieldReader!.Theme = Theme(Settings.GameTheme);
        _fieldReader.KnownMap = Field?.MapId;
        _fieldReader.BossRound = economy?.Round is { } round
            && _boardRouteStore?.IsBossRound(round, Match.Solo, Match.Mode?.Id) == true;
        var cursor = FieldCursorRegion();
        var observation = await _fieldReader!.ReadAsync(frame, at, economy, mainScreen, shopVisible, shop, cursor);
        var match = Match;
        string? strategy = Catalog.GetProperty("strategies").EnumerateArray()
            .FirstOrDefault(s => s.GetProperty("id").GetString() == match.StrategyId) is var selected
            && selected.ValueKind == System.Text.Json.JsonValueKind.Object ? selected.GetProperty("name").GetString() : null;
        var custom = match.CustomPicks?.Values.SelectMany(v => v).ToArray() ?? [];
        // 169：问号和推荐框的锚点用这一帧读到的角标和投影，随定人结果一起推给覆盖层
        var geometry = _fieldReader.OverlayGeometry;
        _fieldIdentityWork = Task.Run(() =>
        {
            FieldState? changed = null;
            lock (_fieldGate)
            {
                if (generation != _fieldGeneration) return;
                _fieldTracker.SeedRoster(strategy, custom, observation.Round, at);
                if (_fieldTracker.Observe(observation)) changed = _fieldTracker.State;
                _fieldOverlayGeometry = geometry;
            }
            FieldOverlayChanged?.Invoke(Field, geometry);
            if (changed is not null) Changed?.Invoke("field", changed);
        });
    }

    public FieldState SetFieldOperator(FieldCell cell, string? name)
    {
        if (!(cell.Zone == "bench" && cell.Col is >= 1 and <= 10 && cell.Row == 0
            || cell.Zone == "board" && cell.Col is >= 0 and <= 6 && cell.Row is >= 0 and <= 4))
            throw new ArgumentException("格坐标无效。");
        if (name is not null && !_operators.Any(o => o.Name == name)) throw new ArgumentException("干员名无效。");
        FieldState state; bool changed;
        lock (_fieldGate)
        {
            changed = _fieldTracker.SetOperator(cell, name, DateTimeOffset.UtcNow);
            state = _fieldTracker.State!;
        }
        if (changed)
        {
            FieldOverlayChanged?.Invoke(state, _fieldOverlayGeometry);
            Changed?.Invoke("field", state);
        }
        return state;
    }

    private void ResetField()
    {
        ResetPlacement();
        bool hadState;
        lock (_fieldGate)
        {
            _fieldGeneration++;
            hadState = _fieldTracker.State is not null;
            _fieldTracker.Reset();
        }
        HideFieldOverlay();
        // 接口事件不允许 null，用空状态通知清除；field() 仍返回未识别的 null。
        if (hadState) Changed?.Invoke("field", new FieldState(null, null, DateTimeOffset.UtcNow.ToString("O"), [], []));
    }
}
