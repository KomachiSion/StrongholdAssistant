using System.Text.Json;
using ASPAssistant.Core.Data;

namespace StrongholdAssistant.Host;

public sealed partial class HostApi
{
    // Called only after successful new-match recognition, or an explicit reset.
    // Keep unrelated manual tracking; remove the previous target's stage/group suggestions.
    private void ResetTargetForNewMatch()
    {
        SessionDto session;
        _sessionGate.Wait();
        try
        {
            session = UserJsonFile.Read(_sessionPath,
                text => JsonSerializer.Deserialize<SessionDto>(text, JsonOptions)!) ?? new(null, [], []);
            var target = _trackingTarget;
            var members = (target?.Members ?? []).Concat((target?.Stages ?? []).SelectMany(s => s.Members))
                .Concat((target?.Groups ?? []).Concat((target?.Stages ?? []).SelectMany(s => s.Groups ?? []))
                    .SelectMany(g => g.Members)).ToArray();
            var names = members.Select(m => m.Name).ToHashSet();
            var equipment = (target?.Equipment ?? []).Concat((target?.Stages ?? []).SelectMany(s => s.Equipment))
                .Concat(members.SelectMany(m => m.Equipment).Select(e => e.Name)).ToHashSet();
            session = session with
            {
                TargetLineupId = null, Stage = null, Owned = [],
                Tracked = session.Tracked.Where(n => !names.Contains(n)).ToArray(),
                TrackedEquipment = session.TrackedEquipment.Where(n => !equipment.Contains(n)).ToArray()
            };
            UserJsonFile.Write(_sessionPath, JsonSerializer.Serialize(session, JsonOptions));
            _trackingSession = session;
            _trackingTarget = null;
            _trackingStage = null;
        }
        finally { _sessionGate.Release(); }
        RecomputeTracking();
        Changed?.Invoke("sessionReset", session);
    }
}
