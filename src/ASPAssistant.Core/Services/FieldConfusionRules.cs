using System.Text.Json;

namespace ASPAssistant.Core.Services;

internal static class FieldConfusionRules
{
    private static readonly double Gap;
    private static readonly HashSet<(string, string)> Pairs = [];

    static FieldConfusionRules()
    {
        using var stream = typeof(FieldConfusionRules).Assembly.GetManifestResourceStream("FieldConfusions")!;
        using var data = JsonDocument.Parse(stream);
        Gap = data.RootElement.GetProperty("gapThreshold").GetDouble();
        foreach (var pair in data.RootElement.GetProperty("pairs").EnumerateArray())
        {
            var names = pair.GetProperty("names");
            string first = names[0].GetString()!, second = names[1].GetString()!;
            Pairs.Add((first, second));
            Pairs.Add((second, first));
        }
    }

    // 只降低自动填入状态，不改首名、候选顺序或确定来源。
    public static bool NeedsConfirmation(SpriteCandidate[] ranked) => ranked.Length > 1
        && ranked[0].Score - ranked[1].Score < Gap
        && Pairs.Contains((ranked[0].Name, ranked[1].Name));
}
