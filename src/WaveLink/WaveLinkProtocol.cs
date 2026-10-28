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
            channel.Effects = ParseEffects(item["effects"]);
            channel.Apps = ParseApps(item["apps"]);
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
        if (patch["apps"] is JsonArray) target.Apps = ParseApps(patch["apps"]);
        if (patch["effects"] is JsonArray effects)
        {
            foreach (var fx in effects)
            {
                if (fx is null) continue;
                string fxId = fx["id"]?.GetValue<string>() ?? "";
                var existing = target.Effects.FirstOrDefault(e => e.Id == fxId);
                if (existing is null)
                {
                    target.Effects.Add(new ChannelEffect
                    {
                        Id = fxId,
                        Name = fx["name"]?.GetValue<string>() ?? "",
                        IsEnabled = fx["isEnabled"]?.GetValue<bool>() ?? false
                    });
                    continue;
                }
                if (fx["name"] is JsonNode fxName) existing.Name = fxName.GetValue<string>();
                if (fx["isEnabled"] is JsonNode on) existing.IsEnabled = on.GetValue<bool>();
            }
        }
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

    internal static List<ChannelEffect> ParseEffects(JsonNode? node)
    {
        var result = new List<ChannelEffect>();
        if (node is JsonArray effects)
            foreach (var fx in effects)
                if (fx is not null)
                    result.Add(new ChannelEffect
                    {
                        Id = fx["id"]?.GetValue<string>() ?? "",
                        Name = fx["name"]?.GetValue<string>() ?? "",
                        IsEnabled = fx["isEnabled"]?.GetValue<bool>() ?? false
                    });
        return result;
    }

    internal static List<ChannelApp> ParseApps(JsonNode? node)
    {
        var result = new List<ChannelApp>();
        if (node is JsonArray apps)
            foreach (var app in apps)
                if (app is not null)
                    result.Add(new ChannelApp
                    {
                        Id = app["id"]?.GetValue<string>() ?? "",
                        Name = app["name"]?.GetValue<string>() ?? ""
                    });
        return result;
    }

    internal static FocusedApp? ParseFocusedApp(JsonNode? node)
    {
        string id = node?["id"]?.GetValue<string>() ?? "";
        if (id.Length == 0) return null;
        return new FocusedApp
        {
            Id = id,
            Name = node?["name"]?.GetValue<string>() is { Length: > 0 } name ? name : id,
            ChannelId = node?["channel"]?["id"]?.GetValue<string>()
        };
    }

    internal static List<OutputDevice> ParseOutputDevices(JsonNode? node)
    {
        var result = new List<OutputDevice>();
        if (node is not JsonArray devices) return result;
        foreach (var d in devices)
        {
            if (d is null) continue;
            var device = new OutputDevice
            {
                Id = d["id"]?.GetValue<string>() ?? "",
                Name = d["name"]?.GetValue<string>() ?? ""
            };
            if (d["outputs"] is JsonArray outputs)
                foreach (var o in outputs)
                    if (o is not null)
                        device.Outputs.Add(new OutputPort
                        {
                            Id = o["id"]?.GetValue<string>() ?? "",
                            Name = o["name"]?.GetValue<string>() ?? "",
                            Level = o["level"]?.GetValue<double>() ?? 1.0,
                            IsMuted = o["isMuted"]?.GetValue<bool>() ?? false
                        });
            result.Add(device);
        }
        return result;
    }

    internal static void ApplyOutputDevicePatch(OutputDevice target, JsonNode patch)
    {
        if (patch["name"] is JsonNode name) target.Name = name.GetValue<string>();
        if (patch["outputs"] is not JsonArray outputs) return;
        foreach (var o in outputs)
        {
            var port = target.Outputs.FirstOrDefault(p => p.Id == o?["id"]?.GetValue<string>());
            if (port is null || o is null) continue;
            if (o["level"] is JsonNode level) port.Level = level.GetValue<double>();
            if (o["isMuted"] is JsonNode muted) port.IsMuted = muted.GetValue<bool>();
        }
    }

    internal static List<InputDevice> ParseInputDevices(JsonNode? node)
    {
        var result = new List<InputDevice>();
        if (node is not JsonArray devices) return result;
        foreach (var d in devices)
        {
            if (d is null) continue;
            var device = new InputDevice
            {
                Id = d["id"]?.GetValue<string>() ?? "",
                Name = d["name"]?.GetValue<string>() ?? ""
            };
            if (d["inputs"] is JsonArray inputs)
                foreach (var i in inputs)
                {
                    if (i is null) continue;
                    var port = new InputPort
                    {
                        Id = i["id"]?.GetValue<string>() ?? "",
                        Name = i["name"]?.GetValue<string>() ?? ""
                    };
                    ApplyInputPatch(port, i);
                    device.Inputs.Add(port);
                }
            result.Add(device);
        }
        return result;
    }

    /// <summary>Apply a full or partial input object (initial load and inputDeviceChanged).</summary>
    internal static void ApplyInputPatch(InputPort port, JsonNode patch)
    {
        if (patch["isMuted"] is JsonNode muted) port.IsMuted = muted.GetValue<bool>();
        if (patch["isGainLockOn"] is JsonNode locked) port.IsGainLockOn = locked.GetValue<bool>();
        if (patch["micPcMix"]?["value"] is JsonNode mix) port.MicPcMix = mix.GetValue<double>();
        if (patch["gain"] is JsonNode gain)
        {
            if (gain["value"] is JsonNode value) port.Gain = value.GetValue<double>();
            if (gain["min"] is JsonNode min) port.GainMinDb = min.GetValue<double>();
            if (gain["max"] is JsonNode max) port.GainMaxDb = max.GetValue<double>();
            if (gain["lookUpTable"] is JsonArray table)
            {
                var rows = new List<(double, double)>();
                foreach (var row in table)
                    if (row is JsonArray { Count: >= 2 } pair && pair[0] is not null && pair[1] is not null)
                        rows.Add((pair[0]!.GetValue<double>(), pair[1]!.GetValue<double>()));
                port.GainTable = rows.OrderBy(r => r.Item1).ToList();
            }
        }
    }

    /// <summary>
    /// Wave Link gain is a normalized 0..1 value, mapped to dB by a per-device
    /// lookup table (linear for the Wave, but not guaranteed to be). Interpolate
    /// through the table; fall back to a straight min..max mapping without one.
    /// </summary>
    internal static double GainToDb(InputPort port, double value)
    {
        var t = port.GainTable;
        if (t.Count < 2) return port.GainMinDb + Math.Clamp(value, 0, 1) * (port.GainMaxDb - port.GainMinDb);
        if (value <= t[0].Value) return t[0].Db;
        for (int i = 1; i < t.Count; i++)
            if (value <= t[i].Value)
                return Lerp(t[i - 1].Value, t[i - 1].Db, t[i].Value, t[i].Db, value);
        return t[^1].Db;
    }

    internal static double DbToGain(InputPort port, double db)
    {
        var t = port.GainTable;
        if (t.Count < 2)
        {
            double span = port.GainMaxDb - port.GainMinDb;
            return span <= 0 ? 0 : Math.Clamp((db - port.GainMinDb) / span, 0, 1);
        }
        if (db <= t[0].Db) return t[0].Value;
        for (int i = 1; i < t.Count; i++)
            if (db <= t[i].Db)
                return Lerp(t[i - 1].Db, t[i - 1].Value, t[i].Db, t[i].Value, db);
        return t[^1].Value;
    }

    private static double Lerp(double x0, double y0, double x1, double y1, double x) =>
        x1 == x0 ? y0 : y0 + (y1 - y0) * (x - x0) / (x1 - x0);

    private static ChannelMix ParseChannelMix(JsonNode item) => new()
    {
        Id = item["id"]?.GetValue<string>() ?? "",
        IsMuted = item["isMuted"]?.GetValue<bool>() ?? false,
        Level = item["level"]?.GetValue<double>() ?? 1.0
    };
}
