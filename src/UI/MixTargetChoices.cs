using Wlhk.WaveLink;

namespace Wlhk.UI;

internal sealed record MixTargetChoice(string? Id, string Name);

internal static class MixTargetChoices
{
    internal static IReadOnlyList<MixTargetChoice> Build(
        Channel? channel, IReadOnlyList<Mix> mixes, string? selectedMixId)
    {
        var memberIds = channel?.Mixes.Select(m => m.Id).ToHashSet() ?? [];
        var choices = new List<MixTargetChoice> { new(null, "All Mixes") };
        choices.AddRange(mixes.Where(m => memberIds.Contains(m.Id))
            .Select(m => new MixTargetChoice(m.Id, m.Name)));
        if (selectedMixId is not null && choices.All(c => c.Id != selectedMixId))
            choices.Add(new(selectedMixId, $"(unavailable) {selectedMixId}"));
        return choices;
    }
}
