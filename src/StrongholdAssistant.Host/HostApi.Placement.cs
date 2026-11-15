using System.Text.Json;
using ASPAssistant.Core.Placement;

namespace StrongholdAssistant.Host;

public sealed partial class HostApi
{
    private PlacementAdapter _placementAdapter = null!;
    private readonly object _placementGate = new();
    private CancellationTokenSource? _placementStop;
    private string? _placementSignature;
    private int _placementGeneration;
    private PlacementResult? _placement;
    public PlacementResult? Placement { get { lock (_placementGate) return _placement; } }
    public event Action<OverlayPlacementPlan?>? PlacementOverlayChanged;

    public static OverlayPlacementPlan? ToOverlayPlacement(PlacementResult? result) => result is null ? null
        : new(result.BasedOn, result.Moves.Select(move =>
            new OverlayPlacementMove(move.Operator, move.From, move.To, move.Facing)).ToArray());

    private void NotifyPlacement(PlacementResult? result)
    {
        Changed?.Invoke("placement", result!);
        PlacementOverlayChanged?.Invoke(ToOverlayPlacement(result));
    }

    private async Task InitializePlacementAsync(string directory)
    {
        _placementAdapter = await PlacementAdapter.LoadAsync(directory);
        Changed += (kind, _) =>
        {
            if (kind is "field" or "match" or "roster") SchedulePlacement();
        };
    }

    private void ResetPlacement(bool force = true)
    {
        lock (_placementGate)
        {
            if (!force && _placementSignature == "") return;
            _placementStop?.Cancel();
            _placementGeneration++;
            _placementSignature = "";
            _placement = null;
        }
        NotifyPlacement(null);
    }

    private void SchedulePlacement()
    {
        var field = Field;
        if (field is null || field.MapId is null || Match.Economy?.Round is not > 0 || !field.Units.Any(u => u.Zone == "board"))
        {
            ResetPlacement(force: false);
            return;
        }
        // 按协议读可选字段；168/171 的宿主实现合入后自动带入，不在这里补识别。
        var snapshot = JsonSerializer.SerializeToElement(field, JsonOptions).Deserialize<PlacementField>(JsonOptions)!;
        var match = Match;
        var matchJson = JsonSerializer.SerializeToElement(match, JsonOptions);
        var bar = matchJson.TryGetProperty("covenantBar", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.Deserialize<PlacementLayers[]>(JsonOptions) : null;
        var input = new PlacementInput(snapshot, match.Economy!.Round, match.Mode?.Id, match.Solo,
            match.BossId, match.EnemyTypes, match.RoundEnemies?.Round == match.Economy.Round
                ? match.RoundEnemies.Enemies.Select(e => e.Name).ToArray() : null,
            bar, match.StrategyId, ReadRoster(), Settings.OperatorDefaults.ToDictionary(p => p.Key, p => p.Value.Skill), Settings.PlacementGoal)
        {
            Modules = Settings.OperatorDefaults.ToDictionary(p => p.Key, p => p.Value.Module),
        };
        var signature = JsonSerializer.Serialize(input with { Field = snapshot with { UpdatedAt = "" } }, JsonOptions);
        lock (_placementGate)
        {
            if (signature == _placementSignature) return;
            _placementSignature = signature;
            int generation = ++_placementGeneration;
            _placementStop?.Cancel();
            var stop = new CancellationTokenSource();
            _placementStop = stop;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(1000, stop.Token);
                    var result = _placementAdapter.Compute(input, stop.Token);
                    PublishPlacement(result, generation);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                finally
                {
                    lock (_placementGate)
                    {
                        if (_placementStop == stop) _placementStop = null;
                        stop.Dispose();
                    }
                }
            });
        }
    }

    private void PublishPlacement(PlacementResult? result, int generation)
    {
        // 消息桥会同步切到窗口线程；不能持有计算锁等待它，否则 placement() 可能互锁。
        if (DialogOwner?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(() => PublishPlacement(result, generation));
            return;
        }
        lock (_placementGate)
        {
            if (generation != _placementGeneration) return;
            _placement = result;
        }
        NotifyPlacement(result);
    }
}
