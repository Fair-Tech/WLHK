using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using Wlhk.Core;
using Wlhk.UI;
using Xunit;
using static Wlhk.Tests.Targets;

namespace Wlhk.Tests;

/// <summary>Hold-repeat, key-repeat ramping, momentary targets (engine) and the scroll-wheel fix (v2.6).</summary>
public sealed class EngineAndInputTests
{
    // ─── Hold repeat ────────────────────────────────────────────────────────────

    [Fact]
    public async Task HoldRepeatKeepsFiringWhileHeldAndStopsOnRelease()
    {
        var wl = new FakeWaveLink().WithChannel("music", "Music", level: 0.0);
        var engine = EngineWith(wl, out _, new HotkeyBinding
        {
            HoldAction = new HotkeyAction
            {
                Type = "volume", Mode = "up", ChannelId = "music", Step = 1,
                Duration = 100, Repeat = true, RepeatIntervalMs = 40
            }
        });

        engine.OnKey(new KeyInput("F13", true, false, null));
        await Task.Delay(450);
        engine.OnKey(new KeyInput("F13", false, false, null));

        int whileHeld = wl.ChannelWriteCount;
        // Hold fires at ~100 ms, then every ~40 ms: ~8 expected. Loose bound for slow CI.
        Assert.True(whileHeld >= 4, $"expected repeated writes, got {whileHeld}");

        await Task.Delay(250);
        Assert.Equal(whileHeld, wl.ChannelWriteCount);
    }

    [Fact]
    public async Task HoldWithoutRepeatFiresOnce()
    {
        var wl = new FakeWaveLink().WithChannel("music", "Music");
        var engine = EngineWith(wl, out _, new HotkeyBinding
        {
            HoldAction = new HotkeyAction { Type = "volume", Mode = "up", ChannelId = "music", Duration = 80 }
        });

        engine.OnKey(new KeyInput("F13", true, false, null));
        await Task.Delay(350);
        engine.OnKey(new KeyInput("F13", false, false, null));

        Assert.Equal(1, wl.ChannelWriteCount);
    }

    [Fact]
    public async Task DisablingHotkeysStopsARunningRepeat()
    {
        var wl = new FakeWaveLink().WithChannel("music", "Music", level: 0.0);
        var engine = EngineWith(wl, out _, new HotkeyBinding
        {
            HoldAction = new HotkeyAction
            {
                Type = "volume", Mode = "up", ChannelId = "music", Step = 1,
                Duration = 50, Repeat = true, RepeatIntervalMs = 30
            }
        });

        engine.OnKey(new KeyInput("F13", true, false, null));
        await Task.Delay(200);
        engine.Enabled = false;
        int atDisable = wl.ChannelWriteCount;
        await Task.Delay(200);

        Assert.Equal(atDisable, wl.ChannelWriteCount);
    }

    // ─── Key-repeat ramping (Global Features volume keys) ───────────────────────

    [Fact]
    public void GlobalVolumeKeysRampOnTheOsKeyRepeat()
    {
        var wl = new FakeWaveLink().WithChannel("music", "Music", level: 0.50);
        var config = new AppConfig();
        config.VolumeKeys = "wavelink";
        config.Targets.Add(Entry("a", "music"));
        var engine = Engine(wl, Of(Entry("a", "music")), config);

        engine.OnKey(new KeyInput("VOLUME_UP", true, false, null));
        engine.OnKey(new KeyInput("VOLUME_UP", true, true, null));
        engine.OnKey(new KeyInput("VOLUME_UP", true, true, null));
        engine.OnKey(new KeyInput("VOLUME_UP", false, false, null));

        Assert.Equal(3, wl.ChannelWriteCount);
        Assert.Equal(0.65, wl.Channels[0].Level, 6); // 3 x default 5% step
    }

    [Fact]
    public void RecordedHotkeysStillIgnoreTheOsKeyRepeat()
    {
        var wl = new FakeWaveLink().WithChannel("music", "Music");
        var engine = EngineWith(wl, out _, new HotkeyBinding
        {
            NormalAction = new HotkeyAction { Type = "volume", Mode = "up", ChannelId = "music" }
        });

        engine.OnKey(new KeyInput("F13", true, false, null));
        engine.OnKey(new KeyInput("F13", true, true, null));
        engine.OnKey(new KeyInput("F13", true, true, null));
        engine.OnKey(new KeyInput("F13", false, false, null));

        Assert.Equal(1, wl.ChannelWriteCount);
    }

    // ─── Momentary targets ──────────────────────────────────────────────────────

    [Fact]
    public void TargetWhileHeldRevertsOnRelease()
    {
        var state = Of(Entry("a", "browser"), Entry("b", "mic"));
        var engine = EngineWith(new FakeWaveLink(), out _, new HotkeyBinding
        {
            NormalAction = new HotkeyAction { Type = "target", Mode = "hold", TargetId = "b" }
        }, state);

        engine.OnKey(new KeyInput("F14", true, false, null));
        Assert.Equal("b", state.Current()!.Id);

        engine.OnKey(new KeyInput("F14", false, false, null));
        Assert.Equal("a", state.Current()!.Id);
    }

    [Fact]
    public void ModifierOverrideFromTheHookReachesTheAction()
    {
        var wl = new FakeWaveLink().WithChannel("browser", "Browser").WithChannel("mic", "Mic");
        var state = Of(Entry("a", "browser"), Entry("b", "mic", modifier: "shift"));
        var engine = EngineWith(wl, out _, new HotkeyBinding
        {
            NormalAction = new HotkeyAction { Type = "mute", Mode = "toggle", Scope = "active" }
        }, state);

        engine.OnKey(new KeyInput("F13", true, false, "b"));
        engine.OnKey(new KeyInput("F13", false, false, "b"));

        Assert.Equal("mic", Assert.Single(wl.ChannelWrites).Id);
    }

    private static HotkeyEngine EngineWith(FakeWaveLink wl, out AppConfig config, HotkeyBinding binding,
        TargetState? state = null)
    {
        config = new AppConfig();
        config.Hotkeys[binding.NormalAction?.Type == "target" ? "F14" : "F13"] = binding;
        return Engine(wl, state ?? new TargetState(), config);
    }

    private static HotkeyEngine Engine(FakeWaveLink wl, TargetState state, AppConfig config)
    {
        var store = new ConfigStore(Path.Combine(Path.GetTempPath(), "WLHK.Tests", "unused.json"));
        var engine = new HotkeyEngine(store, wl, (_, _, _) => { }, state, new RecordingMediaSender());
        engine.SetBindings(EffectiveBindings.Build(config).Bindings);
        return engine;
    }

    // ─── Scroll wheel ───────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern nint SendMessageW(nint hWnd, int msg, nint wParam, nint lParam);

    [Fact]
    public void WheelOverAClosedDropdownDoesNotChangeIt()
    {
        RunInSta(() =>
        {
            using var form = new Form();
            var page = new Panel { AutoScroll = true, Dock = DockStyle.Fill };
            var combo = new NoWheelComboBox { DropDownStyle = ComboBoxStyle.DropDownList };
            combo.Items.AddRange(["One", "Two", "Three"]);
            page.Controls.Add(combo);
            form.Controls.Add(page);
            form.CreateControl();
            combo.SelectedIndex = 1;

            SendMessageW(combo.Handle, 0x020A, (nint)(-120 << 16), 0);
            SendMessageW(combo.Handle, 0x020A, (nint)(120 << 16), 0);

            Assert.Equal(1, combo.SelectedIndex);
        });
    }

    [Fact]
    public void WheelOverANumberBoxDoesNotChangeIt()
    {
        RunInSta(() =>
        {
            using var form = new Form();
            var page = new Panel { AutoScroll = true, Dock = DockStyle.Fill };
            var number = new NoWheelNumericUpDown { Minimum = 0, Maximum = 100, Value = 50 };
            page.Controls.Add(number);
            form.Controls.Add(page);
            form.CreateControl();

            SendMessageW(number.Handle, 0x020A, (nint)(120 << 16), 0);
            SendMessageW(number.Controls[1].Handle, 0x020A, (nint)(-120 << 16), 0); // the inner edit box

            Assert.Equal(50, number.Value);
        });
    }

    [Fact]
    public void WheelStillChangesAPlainNumberBox()
    {
        // Control case: proves the test above would catch the original bug.
        RunInSta(() =>
        {
            using var form = new Form();
            var number = new NumericUpDown { Minimum = 0, Maximum = 100, Value = 50 };
            form.Controls.Add(number);
            form.CreateControl();

            SendMessageW(number.Handle, 0x020A, (nint)(120 << 16), 0);

            Assert.NotEqual(50, number.Value);
        });
    }

    private static void RunInSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)), "STA test thread timed out.");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
