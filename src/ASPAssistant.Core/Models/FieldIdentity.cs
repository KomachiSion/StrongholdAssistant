namespace ASPAssistant.Core.Models;

public sealed record FieldCandidate(string Operator, double Score);
public sealed record FieldRosterEntry(string Operator, string Source, string At, bool Sold);

public sealed partial record FieldUnit
{
    public string Status { get; init; } = Operator is null ? "unknown" : "sure";
    public FieldCandidate[] Candidates { get; init; } = [];
    public bool? Elite { get; init; }
}

public sealed partial record FieldState
{
    public FieldRosterEntry[] Roster { get; init; } = [];
}

// 图块只在本局内存中使用，不是消息桥接口的一部分。
public sealed record FieldSpritePatch(byte[] Image, byte[] Pixels, bool Bench);

public sealed partial record FieldObservation
{
    public IReadOnlyDictionary<FieldCell, FieldSpritePatch>? Sprites { get; init; }
    public string? GainSource { get; init; }
}
