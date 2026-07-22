using System.Text.Json.Nodes;

namespace Wlhk.WaveLink;

internal static class WaveLinkProtocol
{
    internal static List<Channel> ParseChannels(JsonNode? node)
    {
        var result = new List<Channel>();
        if (node is not JsonArray channels) return result;
        foreach (var item in channels)
        {
            if (item is null) continue;
            var channel = new Channel
            {
                Id = item["id"]?.GetValue<string>() ?? "",
                Name = item["name"]?.GetValue<string>() ?? "",
                IsMuted = item["isMuted"]?.GetValue<bool>() ?? false,
                Level = item["level"]?.GetValue<double>() ?? 1.0
            };
            if (item["mixes"] is JsonArray mixes)
                foreach (var mix in mixes)
                    if (mix is not null)
                        channel.Mixes.Add(ParseChannelMix(mix));
            result.Add(channel);
        }
        return result;
    }

    internal static List<Mix> ParseMixes(JsonNode? node)
    {
        var result = new List<Mix>();
        if (node is not JsonArray mixes) return result;
        foreach (var item in mixes)
            if (item is not null)
                result.Add(new Mix
                {
                    Id = item["id"]?.GetValue<string>() ?? "",
                    Name = item["name"]?.GetValue<string>() ?? "",
                    IsMuted = item["isMuted"]?.GetValue<bool>() ?? false,
                    Level = item["level"]?.GetValue<double>() ?? 1.0
                });
        return result;
    }

    internal static void ApplyChannelPatch(Channel target, JsonNode patch)
    {
        if (patch["name"] is JsonNode name) target.Name = name.GetValue<string>();
        if (patch["isMuted"] is JsonNode muted) target.IsMuted = muted.GetValue<bool>();
        if (patch["level"] is JsonNode level) target.Level = level.GetValue<double>();
        if (patch["mixes"] is not JsonArray mixes) return;
        foreach (var patchItem in mixes)
        {
            if (patchItem is null) continue;
            string id = patchItem["id"]?.GetValue<string>() ?? "";
            if (id.Length == 0) continue;
            var targetMix = target.Mixes.FirstOrDefault(m => m.Id == id);
            if (targetMix is null)
            {
                target.Mixes.Add(ParseChannelMix(patchItem));
                continue;
            }
            if (patchItem["isMuted"] is JsonNode mixMuted)
                targetMix.IsMuted = mixMuted.GetValue<bool>();
            if (patchItem["level"] is JsonNode mixLevel)
                targetMix.Level = mixLevel.GetValue<double>();
        }
    }

    internal static void ApplyMixPatch(Mix target, JsonNode patch)
    {
        if (patch["name"] is JsonNode name) target.Name = name.GetValue<string>();
        if (patch["isMuted"] is JsonNode muted) target.IsMuted = muted.GetValue<bool>();
        if (patch["level"] is JsonNode level) target.Level = level.GetValue<double>();
    }

    internal static JsonObject BuildSetChannelMixParams(
        string channelId, string mixId, double? level = null, bool? isMuted = null)
    {
        var mix = new JsonObject { ["id"] = mixId };
        if (level is double l) mix["level"] = l;
        if (isMuted is bool m) mix["isMuted"] = m;
        return new JsonObject
        {
            ["id"] = channelId,
            ["mixes"] = new JsonArray(mix)
        };
    }

    private static ChannelMix ParseChannelMix(JsonNode item) => new()
    {
        Id = item["id"]?.GetValue<string>() ?? "",
        IsMuted = item["isMuted"]?.GetValue<bool>() ?? false,
        Level = item["level"]?.GetValue<double>() ?? 1.0
    };
}
