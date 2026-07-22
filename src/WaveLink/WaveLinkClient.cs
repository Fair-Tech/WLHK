using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace Wlhk.WaveLink;

public sealed class Channel
{
    public string Id = "";
    public string Name = "";
    public bool IsMuted;
    public double Level = 1.0; // 0..1, protocol-native
    public List<ChannelMix> Mixes = new();
}

public sealed class ChannelMix
{
    public string Id = "";
    public bool IsMuted;
    public double Level = 1.0;
}

public sealed class Mix
{
    public string Id = "";
    public string Name = "";
    public bool IsMuted;
    public double Level = 1.0;
}

public sealed class OutputDevice
{
    public string Id = "";
    public string Name = "";
}

public sealed class InputPort
{
    public string Id = "";
    public bool IsMuted;
}

public sealed class InputDevice
{
    public string Id = "";
    public string Name = "";
    public List<InputPort> Inputs = new();
}

/// <summary>
/// Wave Link WebSocket client: JSON-RPC 2.0 over ws://127.0.0.1:PORT with the
/// mandatory "Origin: streamdeck://" header.
///
/// Port discovery (v1 SDK parity): read
///   %LOCALAPPDATA%\Packages\Elgato.WaveLink_g54w8ztgkx496\LocalState\ws-info.json
/// first, then scan 1884-1893. Each candidate is validated with
/// getApplicationInfo (appID == "EWL", interfaceRevision >= 1) before use.
///
/// Reconnect: one supervisor loop, exponential backoff 1s -> 30s cap, retries
/// forever (v1 gave up after 5 tries). Manual reconnect and power-resume skip
/// the current wait.
/// </summary>
public sealed class WaveLinkClient : IDisposable, IWaveLinkControl
{
    public bool IsConnected { get; private set; }

    /// <summary>Connected and initial state loaded.</summary>
    public event Action? Ready;
    public event Action? Disconnected;
    /// <summary>Channels / output devices / main output changed.</summary>
    public event Action? StateChanged;
    /// <summary>Raised once per disconnect episode after sustained failures (tray balloon).</summary>
    public event Action? ConnectionFailing;

    private volatile List<Channel> _channels = new();
    private volatile List<Mix> _mixes = new();
    private volatile List<OutputDevice> _outputDevices = new();
    private volatile List<InputDevice> _inputDevices = new();
    private volatile string _mainOutputId = "";

    private ClientWebSocket? _ws;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonNode?>> _pending = new();
    private long _lastId;

    private CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wakeSignal = new(0);
    private Task? _supervisor;
    private volatile bool _disposed;

    // ─── Public state accessors (lock-free snapshot reads) ─────────────────────

    public IReadOnlyList<Channel> GetChannels() => _channels;
    public IReadOnlyList<Mix> GetMixes() => _mixes;
    public IReadOnlyList<OutputDevice> GetOutputDevices() => _outputDevices;
    public string MainOutputId => _mainOutputId;

    public Channel? GetChannelById(string? id) =>
        id is null ? null : _channels.FirstOrDefault(c => c.Id == id);
    public Mix? GetMixById(string? id) =>
        id is null ? null : _mixes.FirstOrDefault(m => m.Id == id);
    public OutputDevice? GetOutputDeviceById(string? id) =>
        id is null ? null : _outputDevices.FirstOrDefault(d => d.Id == id);
    public InputDevice? GetInputDeviceById(string? id) =>
        id is null ? null : _inputDevices.FirstOrDefault(d => d.Id == id);

    // ─── Lifecycle ──────────────────────────────────────────────────────────────

    public void Start()
    {
        _supervisor = Task.Run(SupervisorLoop);
    }

    /// <summary>Drop the current connection/wait and retry immediately.</summary>
    public void ManualReconnect()
    {
        try { _ws?.Abort(); } catch { }
        _wakeSignal.Release();
    }

    private async Task SupervisorLoop()
    {
        int failures = 0;
        bool episodeNotified = false;

        while (!_disposed)
        {
            try
            {
                await ConnectAndRunAsync(_cts.Token).ConfigureAwait(false);
                // Clean session ended (socket closed after a successful connect)
                failures = 0;
                episodeNotified = false;
            }
            catch (OperationCanceledException) when (_disposed)
            {
                return;
            }
            catch
            {
                failures++;
            }

            if (IsConnected)
            {
                IsConnected = false;
                Disconnected?.Invoke();
            }
            if (_disposed) return;

            if (failures >= 5 && !episodeNotified)
            {
                episodeNotified = true;
                ConnectionFailing?.Invoke();
            }

            int delayMs = failures switch
            {
                0 => 1000,
                1 => 1000,
                2 => 2000,
                3 => 4000,
                4 => 8000,
                5 => 15000,
                _ => 30000
            };
            // Wait for the backoff delay, but wake early on ManualReconnect().
            try { await _wakeSignal.WaitAsync(delayMs, _cts.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private async Task ConnectAndRunAsync(CancellationToken ct)
    {
        foreach (int port in CandidatePorts())
        {
            ct.ThrowIfCancellationRequested();

            var ws = new ClientWebSocket();
            ws.Options.SetRequestHeader("Origin", "streamdeck://");
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
            try
            {
                using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectTimeout.CancelAfter(1500);
                await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}"), connectTimeout.Token).ConfigureAwait(false);
            }
            catch
            {
                ws.Dispose();
                continue;
            }

            _ws = ws;
            var receiveTask = Task.Run(() => ReceiveLoop(ws, ct), ct);

            try
            {
                // Validate this really is Wave Link (v1 SDK parity).
                var info = await SendRequestAsync("getApplicationInfo", null, timeoutMs: 2500).ConfigureAwait(false);
                string appId = info?["appID"]?.GetValue<string>() ?? "";
                int rev = info?["interfaceRevision"]?.GetValue<int>() ?? 0;
                if (appId != "EWL" || rev < 1)
                    throw new InvalidOperationException("Not a Wave Link endpoint");

                var channelsTask = SendRequestAsync("getChannels", null, 5000);
                var mixesTask = SendRequestAsync("getMixes", null, 5000);
                var outputsTask = SendRequestAsync("getOutputDevices", null, 5000);
                var inputsTask = SendRequestAsync("getInputDevices", null, 5000);
                await Task.WhenAll(channelsTask, mixesTask, outputsTask, inputsTask).ConfigureAwait(false);

                _channels = WaveLinkProtocol.ParseChannels(channelsTask.Result?["channels"]);
                _mixes = WaveLinkProtocol.ParseMixes(mixesTask.Result?["mixes"]);
                var outputsNode = outputsTask.Result;
                _outputDevices = ParseOutputDevices(outputsNode?["outputDevices"]);
                _mainOutputId = outputsNode?["mainOutput"]?["outputDeviceId"]?.GetValue<string>() ?? "";
                _inputDevices = ParseInputDevices(inputsTask.Result?["inputDevices"]);

                IsConnected = true;
                Ready?.Invoke();
                StateChanged?.Invoke();
            }
            catch
            {
                // Wrong port or handshake failed: close and try the next candidate.
                try { ws.Abort(); } catch { }
                try { await receiveTask.ConfigureAwait(false); } catch { }
                FailPending();
                _ws = null;
                ws.Dispose();
                continue;
            }

            // Connected: run until the socket dies.
            try { await receiveTask.ConfigureAwait(false); } catch { }
            FailPending();
            _ws = null;
            ws.Dispose();
            return;
        }

        throw new InvalidOperationException("No Wave Link endpoint found");
    }

    private static IEnumerable<int> CandidatePorts()
    {
        int? filePort = ReadWsInfoPort();
        if (filePort is int p and > 0)
            yield return p;
        for (int port = 1884; port <= 1893; port++)
            if (port != filePort)
                yield return port;
    }

    private static int? ReadWsInfoPort()
    {
        try
        {
            string path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Packages", "Elgato.WaveLink_g54w8ztgkx496", "LocalState", "ws-info.json");
            if (!File.Exists(path)) return null;
            var node = JsonNode.Parse(File.ReadAllText(path));
            return node?["port"]?.GetValue<int>();
        }
        catch { return null; }
    }

    // ─── JSON-RPC plumbing ──────────────────────────────────────────────────────

    private async Task<JsonNode?> SendRequestAsync(string method, JsonObject? @params, int timeoutMs = 3000)
    {
        var ws = _ws ?? throw new InvalidOperationException("Not connected");
        long id = Interlocked.Increment(ref _lastId);
        var tcs = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = tcs;

        var payload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["method"] = method,
            ["params"] = @params ?? new JsonObject(),
            ["id"] = id
        };
        byte[] bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());

        await _sendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }

        using var timeout = new CancellationTokenSource(timeoutMs);
        using (timeout.Token.Register(() => tcs.TrySetException(new TimeoutException(method))))
        {
            try { return await tcs.Task.ConfigureAwait(false); }
            finally { _pending.TryRemove(id, out _); }
        }
    }

    /// <summary>Fire-and-forget setter; errors are swallowed (v1 parity — WL echoes state back via notifications).</summary>
    private void SendNotifySafe(string method, JsonObject @params)
    {
        _ = Task.Run(async () =>
        {
            try { await SendRequestAsync(method, @params).ConfigureAwait(false); }
            catch { }
        });
    }

    private void FailPending()
    {
        foreach (var kv in _pending)
            kv.Value.TrySetException(new WebSocketException("Connection closed"));
        _pending.Clear();
    }

    private async Task ReceiveLoop(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            }
            catch
            {
                break;
            }
            if (result.MessageType == WebSocketMessageType.Close)
                break;

            message.Write(buffer, 0, result.Count);
            if (!result.EndOfMessage)
                continue;

            string text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            message.SetLength(0);

            try { HandleMessage(text); }
            catch { /* malformed frame: ignore */ }
        }
    }

    private void HandleMessage(string raw)
    {
        var node = JsonNode.Parse(raw);
        if (node is null) return;

        if (node["id"] is not null)
        {
            long id = node["id"]!.GetValue<long>();
            if (_pending.TryRemove(id, out var tcs))
            {
                if (node["error"] is JsonNode err)
                    tcs.TrySetException(new InvalidOperationException(err.ToJsonString()));
                else
                    tcs.TrySetResult(node["result"]);
            }
            return;
        }

        string? method = node["method"]?.GetValue<string>();
        var p = node["params"];
        if (method is null || p is null) return;

        switch (method)
        {
            case "channelsChanged":
                _channels = WaveLinkProtocol.ParseChannels(p["channels"]);
                StateChanged?.Invoke();
                break;

            case "channelChanged":
            {
                var target = GetChannelById(p["id"]?.GetValue<string>());
                if (target is not null)
                {
                    WaveLinkProtocol.ApplyChannelPatch(target, p);
                    StateChanged?.Invoke();
                }
                break;
            }

            case "mixesChanged":
                _mixes = WaveLinkProtocol.ParseMixes(p["mixes"]);
                StateChanged?.Invoke();
                break;

            case "mixChanged":
            {
                var target = GetMixById(p["id"]?.GetValue<string>());
                if (target is not null)
                {
                    WaveLinkProtocol.ApplyMixPatch(target, p);
                    StateChanged?.Invoke();
                }
                break;
            }

            case "outputDevicesChanged":
            {
                bool changed = false;
                if (p["mainOutput"]?["outputDeviceId"] is JsonNode mo)
                {
                    _mainOutputId = mo.GetValue<string>();
                    changed = true;
                }
                if (p["outputDevices"] is JsonNode od)
                {
                    _outputDevices = ParseOutputDevices(od);
                    changed = true;
                }
                if (changed) StateChanged?.Invoke();
                break;
            }

            case "outputDeviceChanged":
            {
                var target = GetOutputDeviceById(p["id"]?.GetValue<string>());
                if (target is not null && p["name"] is JsonNode n)
                {
                    target.Name = n.GetValue<string>();
                    StateChanged?.Invoke();
                }
                break;
            }

            case "inputDevicesChanged":
                _inputDevices = ParseInputDevices(p["inputDevices"]);
                StateChanged?.Invoke();
                break;

            case "inputDeviceChanged":
            {
                var target = GetInputDeviceById(p["id"]?.GetValue<string>());
                if (target is not null && p["inputs"] is JsonArray inputs)
                {
                    foreach (var inputPatch in inputs)
                    {
                        var port = target.Inputs.FirstOrDefault(i => i.Id == inputPatch?["id"]?.GetValue<string>());
                        if (port is not null && inputPatch?["isMuted"] is JsonNode m)
                            port.IsMuted = m.GetValue<bool>();
                    }
                    StateChanged?.Invoke();
                }
                break;
            }
        }
    }

    // ─── Parsers (tolerant of extra/missing fields) ─────────────────────────────

    private static List<OutputDevice> ParseOutputDevices(JsonNode? arr)
    {
        var list = new List<OutputDevice>();
        if (arr is JsonArray a)
            foreach (var n in a)
                if (n is not null)
                    list.Add(new OutputDevice
                    {
                        Id = n["id"]?.GetValue<string>() ?? "",
                        Name = n["name"]?.GetValue<string>() ?? ""
                    });
        return list;
    }

    private static List<InputDevice> ParseInputDevices(JsonNode? arr)
    {
        var list = new List<InputDevice>();
        if (arr is JsonArray a)
            foreach (var n in a)
            {
                if (n is null) continue;
                var dev = new InputDevice
                {
                    Id = n["id"]?.GetValue<string>() ?? "",
                    Name = n["name"]?.GetValue<string>() ?? ""
                };
                if (n["inputs"] is JsonArray inputs)
                    foreach (var i in inputs)
                        if (i is not null)
                            dev.Inputs.Add(new InputPort
                            {
                                Id = i["id"]?.GetValue<string>() ?? "",
                                IsMuted = i["isMuted"]?.GetValue<bool>() ?? false
                            });
                list.Add(dev);
            }
        return list;
    }

    // ─── Mutations (protocol shapes match v1 SDK exactly) ──────────────────────

    public void SetChannel(string id, double? level = null, bool? isMuted = null)
    {
        var p = new JsonObject { ["id"] = id };
        if (level is double l) p["level"] = l;
        if (isMuted is bool m) p["isMuted"] = m;
        SendNotifySafe("setChannel", p);

        // Optimistic local update so rapid repeated hotkeys (volume ramp) read fresh state
        // without waiting for the notification round-trip.
        var ch = GetChannelById(id);
        if (ch is not null)
        {
            if (level is double l2) ch.Level = l2;
            if (isMuted is bool m2) ch.IsMuted = m2;
        }
    }

    public void SetChannelMix(string channelId, string mixId, double? level = null, bool? isMuted = null)
    {
        SendNotifySafe("setChannel",
            WaveLinkProtocol.BuildSetChannelMixParams(channelId, mixId, level, isMuted));
        var mix = GetChannelById(channelId)?.Mixes.FirstOrDefault(m => m.Id == mixId);
        if (mix is null) return;
        if (level is double l) mix.Level = l;
        if (isMuted is bool m) mix.IsMuted = m;
    }

    public void SetMainOutput(string outputDeviceId)
    {
        SendNotifySafe("setOutputDevice", new JsonObject
        {
            ["mainOutput"] = new JsonObject { ["outputDeviceId"] = outputDeviceId }
        });
        _mainOutputId = outputDeviceId;
    }

    public void SetInputMute(string deviceId, string inputId, bool isMuted)
    {
        SendNotifySafe("setInputDevice", new JsonObject
        {
            ["id"] = deviceId,
            ["inputs"] = new JsonArray(new JsonObject { ["id"] = inputId, ["isMuted"] = isMuted })
        });
        var port = GetInputDeviceById(deviceId)?.Inputs.FirstOrDefault(i => i.Id == inputId);
        if (port is not null) port.IsMuted = isMuted;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        try { _ws?.Abort(); } catch { }
        _wakeSignal.Release();
    }
}
