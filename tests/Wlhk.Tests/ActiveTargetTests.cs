using System.Reflection;
using Wlhk.Core;
using Xunit;
using static Wlhk.Tests.Targets;

namespace Wlhk.Tests;

/// <summary>Active Target: the list, switching it, aiming actions at it, and target modifiers (v2.6).</summary>
public sealed class ActiveTargetTests
{
    // ─── TargetState ────────────────────────────────────────────────────────────

    [Fact]
    public void StartsOnTheFirstEntryAndCyclesBothWays()
    {
        var state = Of(Entry("a", "browser"), Entry("b", "music"), Entry("c", "mic"));

        Assert.Equal("a", state.Current()!.Id);
        Assert.Equal("b", state.Next()!.Id);
        Assert.Equal("c", state.Next()!.Id);
        Assert.Equal("a", state.Next()!.Id);      // wraps forward
        Assert.Equal("c", state.Previous()!.Id);  // wraps back
    }

    [Fact]
    public void UpdateKeepsTheActiveEntryUnlessItWasRemoved()
    {
        var state = Of(Entry("a", "browser"), Entry("b", "music"));
        state.Select("b");

        state.Update([Entry("x", "mic"), Entry("b", "music")]);
        Assert.Equal("b", state.Current()!.Id);

        state.Update([Entry("x", "mic")]);
        Assert.Equal("x", state.Current()!.Id);
    }

    [Fact]
    public void MomentaryBeatsModifierBeatsActive()
    {
        var state = Of(Entry("a", "browser"), Entry("b", "music"), Entry("c", "mic"));

        Assert.Equal("b", state.Current(modifierOverrideId: "b")!.Id);

        state.PushMomentary("F14", "c");
        Assert.Equal("c", state.Current(modifierOverrideId: "b")!.Id);

        state.ReleaseMomentary("F14");
        Assert.Equal("a", state.Current()!.Id);
    }

    [Fact]
    public void EmptyListResolvesToNothing()
    {
        var state = new TargetState();
        Assert.Null(state.Current());
        Assert.Null(state.Next());
    }

    // ─── Executor ───────────────────────────────────────────────────────────────

    [Fact]
    public void ActionsFollowTheActiveTargetIncludingItsMix()
    {
        var wl = new FakeWaveLink()
            .WithChannel("browser", "Browser", level: 0.5)
            .WithChannel("mic", "Mic", level: 0.5, mixes: new Wlhk.WaveLink.ChannelMix { Id = "stream", Level = 0.5 });
        var state = Of(Entry("a", "browser"), Entry("b", "mic", mixId: "stream"));
        var exec = Executor(wl, state);
        var volumeUp = new HotkeyAction { Type = "volume", Mode = "up", UseActiveTarget = true, Step = 10 };

        exec.Execute(volumeUp);
        Assert.Equal("browser", Assert.Single(wl.ChannelWrites).Id);

        state.Next();
        exec.Execute(volumeUp);
        Assert.Equal(("mic", "stream"), (wl.PairWrites[0].ChannelId, wl.PairWrites[0].MixId));
        Assert.Single(wl.ChannelWrites); // the browser write from before, nothing new
    }

    [Fact]
    public void ModifierOverrideIsUsedForThatPressOnly()
    {
        var wl = new FakeWaveLink().WithChannel("browser", "Browser").WithChannel("mic", "Mic");
        var state = Of(Entry("a", "browser"), Entry("b", "mic", modifier: "shift"));
        var exec = Executor(wl, state);
        var mute = new HotkeyAction { Type = "mute", Mode = "toggle", UseActiveTarget = true };

        exec.Execute(mute, new ActionContext("F13", TargetOverrideId: "b"));
        exec.Execute(mute, new ActionContext("F13", TargetOverrideId: null));

        Assert.Equal(["mic", "browser"], wl.ChannelWrites.Select(w => w.Id).ToArray());
        Assert.Equal("a", state.Current()!.Id); // the override never changed the active entry
    }

    [Fact]
    public void NoTargetsExplainsInsteadOfDoingNothing()
    {
        var osd = new List<(string, string?, int?)>();
        var exec = new HotkeyActionExecutor(() => new AppConfig(), new FakeWaveLink(),
            (a, b, c) => osd.Add((a, b, c)), new RecordingMediaSender(), new TargetState());

        exec.Execute(new HotkeyAction { Type = "mute", Mode = "toggle", UseActiveTarget = true });

        Assert.Equal("No Active Target", Assert.Single(osd).Item1);
    }

    [Fact]
    public void TargetActionsSwitchAndReportTheNewTarget()
    {
        var wl = new FakeWaveLink().WithChannel("browser", "Browser").WithChannel("music", "Music");
        wl.Mixes.Add(new Wlhk.WaveLink.Mix { Id = "stream", Name = "Stream Mix" });
        var state = Of(Entry("a", "browser"), Entry("b", "music", mixId: "stream"));
        var osd = new List<(string, string?, int?)>();
        var exec = new HotkeyActionExecutor(() => new AppConfig(), wl, (a, b, c) => osd.Add((a, b, c)),
            new RecordingMediaSender(), state);

        exec.Execute(new HotkeyAction { Type = "target", Mode = "next" });

        Assert.Equal("b", state.Current()!.Id);
        Assert.Equal(("Target", "Music · Stream Mix", (int?)null), Assert.Single(osd));
    }

    [Fact]
    public void TargetActionsWorkWhileWaveLinkIsDisconnected()
    {
        var state = Of(Entry("a", "browser"), Entry("b", "music"));
        var exec = Executor(new FakeWaveLink { IsConnected = false }, state);

        exec.Execute(new HotkeyAction { Type = "target", Mode = "select", TargetId = "b" });

        Assert.Equal("b", state.Current()!.Id);
    }

    // ─── Global Features → bindings ─────────────────────────────────────────────

    [Fact]
    public void VolumeKeysAreSynthesisedOnlyWhenEnabled()
    {
        var config = new AppConfig();
        Assert.Empty(EffectiveBindings.Build(config).Bindings);

        config.GlobalFeatures.VolumeKeys = "wavelink";
        var effective = EffectiveBindings.Build(config);

        Assert.Equal(["VOLUME_DOWN", "VOLUME_MUTE", "VOLUME_UP"], effective.Mapped.Order().ToArray());
        Assert.Equal(effective.Mapped.Order(), effective.GlobalOnly.Order());
        Assert.True(effective.Bindings["VOLUME_UP"].AutoRepeat);
        Assert.False(effective.Bindings["VOLUME_MUTE"].AutoRepeat);
        Assert.Equal(("volume", "up"), ActionCatalog.Resolve(effective.Bindings["VOLUME_UP"].NormalAction!));
        Assert.True(effective.Bindings["VOLUME_UP"].NormalAction!.UseActiveTarget);
    }

    [Fact]
    public void RecordedHotkeyKeepsItsTapAndOnlyEmptyTriggersAreFilled()
    {
        var config = new AppConfig();
        config.GlobalFeatures.VolumeKeys = "wavelink";
        var recordedTap = new HotkeyAction { Type = "mute", Mode = "toggle", ChannelId = "mic" };
        var recordedHold = new HotkeyAction { Type = "cycle_output", DeviceIds = ["a", "b"] };
        config.Hotkeys["VOLUME_UP"] = new HotkeyBinding { NormalAction = recordedTap };
        config.Hotkeys["VOLUME_MUTE"] = new HotkeyBinding { HoldAction = recordedHold };

        var effective = EffectiveBindings.Build(config);

        Assert.Same(recordedTap, effective.Bindings["VOLUME_UP"].NormalAction);
        var mute = effective.Bindings["VOLUME_MUTE"];
        Assert.Same(recordedHold, mute.HoldAction);
        Assert.Equal("mute", mute.NormalAction!.Type);
        Assert.Null(config.Hotkeys["VOLUME_MUTE"].NormalAction); // stored config untouched
        Assert.DoesNotContain("VOLUME_UP", effective.GlobalOnly);
        Assert.DoesNotContain("VOLUME_MUTE", effective.GlobalOnly);
    }

    [Fact]
    public void TrackAndPlayPauseKeys()
    {
        var config = new AppConfig();
        config.GlobalFeatures.TrackKeys = "target";
        config.GlobalFeatures.PlayPauseKey = "mute";

        var b = EffectiveBindings.Build(config).Bindings;

        Assert.Equal(("target", "next"), ActionCatalog.Resolve(b["MEDIA_NEXT_TRACK"].NormalAction!));
        Assert.Equal(("target", "previous"), ActionCatalog.Resolve(b["MEDIA_PREV_TRACK"].NormalAction!));
        Assert.Equal(("mute", "toggle"), ActionCatalog.Resolve(b["MEDIA_PLAY_PAUSE"].NormalAction!));
    }

    [Fact]
    public void OnlyActiveTargetHotkeysAcceptTargetModifiers()
    {
        var config = new AppConfig();
        config.Hotkeys["F13"] = new HotkeyBinding
        {
            HoldAction = new HotkeyAction { Type = "volume", Mode = "up", UseActiveTarget = true }
        };
        config.Hotkeys["F14"] = new HotkeyBinding
        {
            NormalAction = new HotkeyAction { Type = "mute", Mode = "toggle", ChannelId = "mic" }
        };
        config.Targets.Add(Entry("a", "browser"));
        config.Targets.Add(Entry("b", "mic", modifier: "shift"));
        config.Targets.Add(Entry("c", "music", modifier: "nonsense"));

        var effective = EffectiveBindings.Build(config);

        Assert.Equal(["F13"], effective.Dynamic.ToArray());
        Assert.Equal([(ModifierFamily.Shift, "b")], effective.TargetModifiers.ToArray());
    }

    // ─── Hook: modifier redirect ────────────────────────────────────────────────

    [Fact]
    public void HeldTargetModifierRedirectsToTheUnmodifiedHotkey()
    {
        using var hook = HookFor(out _, "F13");
        Hold(hook, "_lShift");

        Assert.Equal(("F13", "b"), hook.ResolveWithTargets("F13"));
    }

    [Fact]
    public void AComboBoundInItsOwnRightIsNeverRedirected()
    {
        using var hook = HookFor(out var config, "F13");
        config.Hotkeys["SHIFT+F13"] = new HotkeyBinding { NormalAction = new HotkeyAction { Type = "media", Mode = "stop" } };
        Publish(hook, config);
        Hold(hook, "_rShift");

        Assert.Equal(("SHIFT+F13", null), hook.ResolveWithTargets("F13"));
    }

    [Fact]
    public void OtherModifiersAreKeptWhenOneIsAssignedToATarget()
    {
        using var hook = HookFor(out var config, "CTRL+F13");
        Hold(hook, "_lCtrl", "_lShift");

        Assert.Equal(("CTRL+F13", "b"), hook.ResolveWithTargets("F13"));
    }

    [Fact]
    public void HotkeysNotAimedAtTheActiveTargetIgnoreTargetModifiers()
    {
        using var hook = new KeyboardHook();
        var config = new AppConfig();
        config.Hotkeys["F13"] = new HotkeyBinding { NormalAction = new HotkeyAction { Type = "mute", ChannelId = "mic" } };
        config.Targets.Add(Entry("b", "mic", modifier: "shift"));
        Publish(hook, config);
        Hold(hook, "_lShift");

        Assert.Equal(("SHIFT+F13", null), hook.ResolveWithTargets("F13"));
    }

    private static KeyboardHook HookFor(out AppConfig config, string combo)
    {
        var hook = new KeyboardHook();
        config = new AppConfig();
        config.Hotkeys[combo] = new HotkeyBinding
        {
            NormalAction = new HotkeyAction { Type = "volume", Mode = "up", UseActiveTarget = true }
        };
        config.Targets.Add(Entry("a", "browser"));
        config.Targets.Add(Entry("b", "mic", modifier: "shift"));
        Publish(hook, config);
        return hook;
    }

    private static void Publish(KeyboardHook hook, AppConfig config)
    {
        var effective = EffectiveBindings.Build(config);
        hook.SetMappedCombos(effective.Mapped);
        hook.SetResolutionInfo(effective);
    }

    private static void Hold(KeyboardHook hook, params string[] fields)
    {
        foreach (string field in fields)
            typeof(KeyboardHook).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(hook, true);
    }

    private static HotkeyActionExecutor Executor(FakeWaveLink wl, TargetState state) =>
        new(() => new AppConfig(), wl, (_, _, _) => { }, new RecordingMediaSender(), state);
}
