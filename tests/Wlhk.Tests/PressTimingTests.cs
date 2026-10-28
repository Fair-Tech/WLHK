using Wlhk.Core;
using Xunit;
using static Wlhk.Tests.Targets;

namespace Wlhk.Tests;

/// <summary>
/// Tap vs double-press vs hold decisions (v2.6). Includes the regression where a
/// key with both a Normal Press and a Double Press action ran the Normal Press
/// action as well as the double, about 300 ms after every double press.
/// </summary>
public sealed class PressTimingTests
{
    // Normal = volume up (a level write), double = mute (a mute write), so the
    // two are told apart by what they wrote.
    private static HotkeyBinding TapAndDouble() => new()
    {
        NormalAction = new HotkeyAction { Type = "volume", Mode = "up", ChannelId = "music" },
        DoublePressAction = new HotkeyAction { Type = "mute", Mode = "toggle", ChannelId = "music" }
    };

    private static int Taps(FakeWaveLink wl) => wl.ChannelWrites.Count(w => w.Level is not null);
    private static int Doubles(FakeWaveLink wl) => wl.ChannelWrites.Count(w => w.Muted is not null);

    [Fact]
    public async Task DoublePressRunsOnlyTheDoubleAction()
    {
        var (engine, wl) = Engine(TapAndDouble());

        Press(engine);
        Press(engine);
        await Task.Delay(500); // well past the 300 ms window

        Assert.Equal(1, Doubles(wl));
        Assert.Equal(0, Taps(wl)); // the regression: this used to be 1
    }

    [Fact]
    public async Task SingleTapWaitsOutTheWindowThenRunsOnce()
    {
        var (engine, wl) = Engine(TapAndDouble());

        Press(engine);
        Assert.Equal(0, Taps(wl)); // not yet: it might still become a double
        await WaitUntil(() => Taps(wl) == 1);
        await Task.Delay(400); // and it doesn't run twice

        Assert.Equal(1, Taps(wl));
        Assert.Equal(0, Doubles(wl));
    }

    [Fact]
    public async Task TripleTapIsADoubleThenASingle()
    {
        var (engine, wl) = Engine(TapAndDouble());

        Press(engine);
        Press(engine);
        Press(engine);
        await WaitUntil(() => Taps(wl) == 1);
        await Task.Delay(400);

        Assert.Equal(1, Doubles(wl)); // the third press must not start a second double
        Assert.Equal(1, Taps(wl));
    }

    [Fact]
    public async Task TwoSeparateTapsAreTwoTaps()
    {
        var (engine, wl) = Engine(TapAndDouble());

        Press(engine);
        await Task.Delay(450);
        Press(engine);
        await WaitUntil(() => Taps(wl) == 2);

        Assert.Equal(2, Taps(wl));
        Assert.Equal(0, Doubles(wl));
    }

    // ─── Repeat threshold ───────────────────────────────────────────────────────

    [Fact]
    public async Task RepeatStartsAfterOneIntervalIgnoringHoldTime()
    {
        var (engine, wl) = Engine(new HotkeyBinding
        {
            HoldAction = new HotkeyAction
            {
                Type = "volume", Mode = "up", ChannelId = "music", Step = 1,
                Duration = 3000, // disabled in the editor once Repeat is on
                Repeat = true, RepeatIntervalMs = 60
            }
        });

        engine.OnKey(new KeyInput("F13", true, false, null));
        await Task.Delay(300);
        engine.OnKey(new KeyInput("F13", false, false, null));

        Assert.True(Taps(wl) >= 2, $"expected repeats well before 3000 ms, got {Taps(wl)}");
    }

    [Fact]
    public async Task WithATapActionAQuickTapIsStillATap()
    {
        // A 40 ms interval would turn any normal tap into a hold. With a tap
        // action on the same key the threshold is floored, so a ~120 ms tap stays a tap.
        var (engine, wl) = Engine(new HotkeyBinding
        {
            NormalAction = new HotkeyAction { Type = "mute", Mode = "toggle", ChannelId = "music" },
            HoldAction = new HotkeyAction
            {
                Type = "volume", Mode = "up", ChannelId = "music", Repeat = true, RepeatIntervalMs = 40
            }
        });

        engine.OnKey(new KeyInput("F13", true, false, null));
        await Task.Delay(120);
        engine.OnKey(new KeyInput("F13", false, false, null));
        await Task.Delay(100);

        Assert.Equal(1, Doubles(wl)); // the tap (mute) ran
        Assert.Equal(0, Taps(wl));    // the hold (volume) didn't
    }

    // ─── Persistent Target ──────────────────────────────────────────────────────

    [Fact]
    public void PersistentTargetSavesTheChosenEntryNotAMomentaryOne()
    {
        var state = Of(Entry("a", "browser"), Entry("b", "mic"));
        state.Select("b");
        state.PushMomentary("F14", "a");

        Assert.Equal("a", state.Current()!.Id); // what's in effect right now
        Assert.Equal("b", state.ActiveId);      // what would be remembered
    }

    /// <summary>
    /// Wait for something that should happen, up to a generous limit. Fixed sleeps
    /// make "it happened" tests fail on a loaded machine; "it didn't happen" tests
    /// keep fixed waits, since a slow machine can only make those pass.
    /// </summary>
    private static async Task WaitUntil(Func<bool> condition, int timeoutMs = 3000)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && watch.ElapsedMilliseconds < timeoutMs)
            await Task.Delay(20);
    }

    private static void Press(HotkeyEngine engine)
    {
        engine.OnKey(new KeyInput("F13", true, false, null));
        engine.OnKey(new KeyInput("F13", false, false, null));
    }

    private static (HotkeyEngine Engine, FakeWaveLink Wl) Engine(HotkeyBinding binding)
    {
        var wl = new FakeWaveLink().WithChannel("music", "Music", level: 0.5);
        var config = new AppConfig();
        config.Hotkeys["F13"] = binding;
        var store = new ConfigStore(Path.Combine(Path.GetTempPath(), "WLHK.Tests", "unused.json"));
        var engine = new HotkeyEngine(store, wl, (_, _, _) => { }, new TargetState(), new RecordingMediaSender(),
            new FakeAppAudio(default));
        engine.SetBindings(EffectiveBindings.Build(config).Bindings);
        return (engine, wl);
    }
}
