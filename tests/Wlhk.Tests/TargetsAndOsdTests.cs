using System.Reflection;
using Wlhk.Core;
using Wlhk.WaveLink;
using Xunit;
using static Wlhk.Tests.Targets;

namespace Wlhk.Tests;

/// <summary>
/// Windows volume targets, hold-key chords, the focused-app fallbacks, and what
/// the OSD shows (app names and icons). v2.6.
/// </summary>
public sealed class TargetsAndOsdTests
{
    // ─── Hold-key chords ────────────────────────────────────────────────────────

    [Fact]
    public void AnOrdinaryKeyCanBeAChord()
    {
        // Hold F13, press a volume key: the press aims at F13's entry.
        var (hook, _) = Hook(volumeKeys: true, ("mic", ["F13"]));
        HoldKey(hook, "F13");

        Assert.Equal(("VOLUME_UP", "mic"), hook.ResolveWithTargets("VOLUME_UP"));
    }

    [Fact]
    public void MixedModifiersAreStrippedAndTheMostSpecificChordWins()
    {
        var (hook, _) = Hook(volumeKeys: false, ("shift", ["SHIFT"]), ("ctrlshift", ["CTRL", "SHIFT"]));
        HoldModifiers(hook, "_lCtrl", "_rShift");

        Assert.Equal(("F13", "ctrlshift"), hook.ResolveWithTargets("F13"));
    }

    [Fact]
    public void AKeyAndAModifierTogether()
    {
        var (hook, _) = Hook(volumeKeys: false, ("mic", ["SHIFT", "CAPS LOCK"]));
        HoldModifiers(hook, "_lShift");
        Assert.Equal(("SHIFT+F13", null), hook.ResolveWithTargets("F13")); // Caps Lock not held yet

        HoldKey(hook, "CAPS LOCK");
        Assert.Equal(("F13", "mic"), hook.ResolveWithTargets("F13"));
    }

    [Fact]
    public void LeftAndRightModifiersAreSeparateChords()
    {
        var (hook, _) = Hook(volumeKeys: false, ("left", ["LSHIFT"]), ("right", ["RSHIFT"]));

        HoldModifiers(hook, "_lShift");
        Assert.Equal(("F13", "left"), hook.ResolveWithTargets("F13"));

        // Both down: taking out either chord still leaves the other Shift, so neither applies.
        HoldModifiers(hook, "_rShift");
        Assert.Equal(("SHIFT+F13", null), hook.ResolveWithTargets("F13"));
    }

    [Fact]
    public void ARightModifierChordIgnoresTheLeftKey()
    {
        var (hook, _) = Hook(volumeKeys: false, ("right", ["RCTRL"]));
        HoldModifiers(hook, "_lCtrl");

        Assert.Equal(("CTRL+F13", null), hook.ResolveWithTargets("F13"));
    }

    [Fact]
    public void ASideAgnosticChordStillMatchesEitherSide()
    {
        // Chords recorded before sides were tracked.
        var (hook, _) = Hook(volumeKeys: false, ("mic", ["SHIFT"]));
        HoldModifiers(hook, "_rShift");

        Assert.Equal(("F13", "mic"), hook.ResolveWithTargets("F13"));
    }

    [Fact]
    public void ChordKeysOnlyRedirectActiveTargetHotkeys()
    {
        var config = new AppConfig();
        config.Hotkeys["F14"] = new HotkeyBinding { NormalAction = new HotkeyAction { Type = "mute", ChannelId = "music" } };
        config.Targets.Add(new TargetEntry { Id = "mic", ChannelId = "mic", HoldKeys = ["F13"] });
        using var hook = new KeyboardHook();
        Publish(hook, config);
        HoldKey(hook, "F13");

        Assert.Equal(("F14", null), hook.ResolveWithTargets("F14"));
    }

    [Fact]
    public void ChordKeysArePublishedSoTheHookHoldsThemInsteadOfTypingThem()
    {
        var config = new AppConfig();
        config.Targets.Add(new TargetEntry { Id = "a", ChannelId = "mic", HoldKeys = ["CTRL", "F13"] });
        config.Targets.Add(new TargetEntry { Id = "b", ChannelId = "music", HoldKeys = ["SHIFT"] });

        var effective = EffectiveBindings.Build(config);

        Assert.Equal(["F13"], effective.ChordKeys.ToArray()); // modifiers still type normally
        Assert.Equal("a", effective.TargetChords[0].TargetId); // bigger chord first
    }

    [Fact]
    public void LegacySingleModifiersBecomeChordsAndJunkIsDropped()
    {
        string dir = Path.Combine(Path.GetTempPath(), "WLHK.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "config.json");
            File.WriteAllText(path, """
                {"targets":[{"id":"a","channelId":"mic","modifier":"shift"},{"id":"b","channelId":"music","modifier":"nonsense"}]}
                """);
            var store = new ConfigStore(path);
            store.Load();

            Assert.Equal(["SHIFT"], store.Current.Targets[0].HoldKeys);
            Assert.Null(store.Current.Targets[1].HoldKeys);
            Assert.All(store.Current.Targets, t => Assert.Null(t.Modifier));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(null, "None")]
    [InlineData(new[] { "CTRL", "SHIFT" }, "Ctrl + Shift")]
    [InlineData(new[] { "LCTRL", "RSHIFT" }, "L Ctrl + R Shift")]
    [InlineData(new[] { "RALT", "F13" }, "R Alt + F13")]
    [InlineData(new[] { "F13" }, "F13")]
    [InlineData(new[] { "SHIFT", "CAPS LOCK" }, "Shift + Caps Lock")]
    public void ChordsDescribeThemselves(string[]? keys, string expected) =>
        Assert.Equal(expected, EffectiveBindings.DescribeChord(keys));

    // ─── Windows scopes ─────────────────────────────────────────────────────────

    [Fact]
    public void NativeVolumeControlsGoToTheDefaultDevice()
    {
        var audio = new FakeAppAudio(new AppVolumeResult("Speakers", 40, null, null, null, Previous: 50));
        var (exec, _, osd) = Executor(audio, connected: false); // works without Wave Link

        exec.Execute(new HotkeyAction { Type = "volume", Mode = "down", Scope = "system", Step = 10 });

        Assert.Equal("system", Assert.Single(audio.Scopes));
        Assert.Equal(("down", 10), (audio.Requests[0].Mode, audio.Requests[0].Step));
        Assert.Equal(new OsdMessage("Speakers", null, 40), Assert.Single(osd));
    }

    [Fact]
    public void FocusedAppsVolumeShowsTheAppAndItsIcon()
    {
        var audio = new FakeAppAudio(new AppVolumeResult("Spotify", null, true, null, @"C:\Spotify\Spotify.exe"));
        var (exec, _, osd) = Executor(audio);

        exec.Execute(new HotkeyAction { Type = "mute", Mode = "toggle", Scope = "app_volume" });

        Assert.Equal("toggle_mute", audio.Requests[0].Mode);
        Assert.Equal(new OsdMessage("Spotify", "Muted", null, @"C:\Spotify\Spotify.exe"), Assert.Single(osd));
    }

    [Fact]
    public void ActiveTargetCanBeWindowsVolume()
    {
        var audio = new FakeAppAudio(new AppVolumeResult("Speakers", 60, null, null, Previous: 50));
        var state = Of(new TargetEntry { Id = "a", Scope = "app_volume" }, new TargetEntry { Id = "b", Scope = "system" });
        var (exec, _, _) = Executor(audio, state: state, connected: false);
        var up = new HotkeyAction { Type = "volume", Mode = "up", Scope = "active" };

        exec.Execute(up);
        state.Next();
        exec.Execute(up);

        Assert.Equal(["app", "system"], audio.Scopes);
    }

    [Fact]
    public void LegacyFocusedAppVolumeActionsBecomeVolumeAndMuteScopes()
    {
        var volume = new HotkeyAction { Type = "app_volume", Mode = "down", Step = 4 };
        var mute = new HotkeyAction { Type = "app_volume", Mode = "toggle_mute" };

        ActionCatalog.Upgrade(volume);
        ActionCatalog.Upgrade(mute);

        Assert.Equal(("volume", "down", "app_volume", 4), (volume.Type, volume.Mode, volume.Scope, volume.Step));
        Assert.Equal(("mute", "toggle", "app_volume"), (mute.Type, mute.Mode, mute.Scope));
    }

    // ─── Focused app's channel and its fallback ─────────────────────────────────

    [Fact]
    public void FocusedAppsChannelNamesTheAppWithTheChannelOnTheRight()
    {
        var (exec, wl, osd) = Executor();
        wl.Focused = new FocusedApp { Id = "Spotify", Name = "Spotify", ChannelId = "music" };

        exec.Execute(new HotkeyAction { Type = "volume", Mode = "up", Scope = "focused_app", Step = 10 });

        Assert.Equal(new OsdMessage("Spotify", null, 60, IconForApp: "Spotify", TitleRight: "Music"), Assert.Single(osd));
    }

    [Theory]
    [InlineData("app_volume", "app")]
    [InlineData("system", "system")]
    public void FallbackToWindowsVolumeWhenTheAppIsntInAChannel(string fallbackScope, string expected)
    {
        var audio = new FakeAppAudio(new AppVolumeResult("Notepad", 20, null, null, Previous: 25));
        var config = new AppConfig { FocusedAppFallback = new TargetEntry { Scope = fallbackScope } };
        var (exec, wl, _) = Executor(audio, config: config);
        wl.Focused = new FocusedApp { Id = "Notepad", Name = "Notepad", ChannelId = null };

        exec.Execute(new HotkeyAction { Type = "volume", Mode = "down", Scope = "focused_app" });

        Assert.Equal(expected, Assert.Single(audio.Scopes));
    }

    [Fact]
    public void FallbackToAChannel()
    {
        var config = new AppConfig { FocusedAppFallback = new TargetEntry { ChannelId = "music" } };
        var (exec, wl, _) = Executor(config: config);
        wl.Focused = new FocusedApp { Id = "Notepad", Name = "Notepad", ChannelId = null };

        exec.Execute(new HotkeyAction { Type = "mute", Mode = "mute", Scope = "focused_app" });

        Assert.Equal("music", Assert.Single(wl.ChannelWrites).Id);
    }

    [Fact]
    public void WithoutAFallbackItStillSaysSo()
    {
        var (exec, wl, osd) = Executor();
        wl.Focused = new FocusedApp { Id = "Notepad", Name = "Notepad", ChannelId = null };

        exec.Execute(new HotkeyAction { Type = "mute", Mode = "mute", Scope = "focused_app" });

        Assert.Empty(wl.ChannelWrites);
        Assert.Equal("Notepad isn't in a channel", Assert.Single(osd).Text);
    }

    // ─── No-audio fallback ──────────────────────────────────────────────────────

    private static readonly AppVolumeResult Silent =
        new("Notepad", null, null, AppVolumeResult.NoAudio, @"C:\Windows\notepad.exe");

    [Fact]
    public void NoAudioFallsBackToTheDefaultDevice()
    {
        var audio = new FakeAppAudio(Silent) { SystemReply = new AppVolumeResult("Speakers", 45, null, null, Previous: 50) };
        var config = new AppConfig { FocusedAppNoAudioFallback = new TargetEntry { Scope = "system" } };
        var (exec, _, osd) = Executor(audio, config: config, connected: false);

        exec.Execute(new HotkeyAction { Type = "volume", Mode = "down", Scope = "app_volume", Step = 5 });

        Assert.Equal(["app", "system"], audio.Scopes);
        Assert.Equal(("down", 5), (audio.Requests[1].Mode, audio.Requests[1].Step));
        Assert.Equal(new OsdMessage("Speakers", null, 45), Assert.Single(osd)); // no "No audio" toast first
    }

    [Fact]
    public void NoAudioFallsBackToAChannelKeepingTheMode()
    {
        var config = new AppConfig { FocusedAppNoAudioFallback = new TargetEntry { ChannelId = "music" } };
        var (exec, wl, _) = Executor(new FakeAppAudio(Silent), config: config);

        exec.Execute(new HotkeyAction { Type = "mute", Mode = "toggle", Scope = "app_volume" });

        Assert.True(Assert.Single(wl.ChannelWrites).Muted);
    }

    [Fact]
    public void NoAudioFallbackAlsoCoversTheChannelFallback()
    {
        // Not in a channel -> Focused App's Volume -> no audio -> Music.
        var config = new AppConfig
        {
            FocusedAppFallback = new TargetEntry { Scope = "app_volume" },
            FocusedAppNoAudioFallback = new TargetEntry { ChannelId = "music" }
        };
        var (exec, wl, _) = Executor(new FakeAppAudio(Silent), config: config);
        wl.Focused = new FocusedApp { Id = "Notepad", Name = "Notepad", ChannelId = null };

        exec.Execute(new HotkeyAction { Type = "volume", Mode = "up", Scope = "focused_app" });

        Assert.Equal("music", Assert.Single(wl.ChannelWrites).Id);
    }

    [Fact]
    public void NoAudioFallbackToTheFocusedAppsChannelDoesntLoop()
    {
        // The two fallbacks point at each other; the second one gives up with a message.
        var audio = new FakeAppAudio(Silent);
        var config = new AppConfig
        {
            FocusedAppFallback = new TargetEntry { Scope = "app_volume" },
            FocusedAppNoAudioFallback = new TargetEntry { Scope = "focused_app" }
        };
        var (exec, wl, osd) = Executor(audio, config: config);
        wl.Focused = new FocusedApp { Id = "Notepad", Name = "Notepad", ChannelId = null };

        exec.Execute(new HotkeyAction { Type = "volume", Mode = "up", Scope = "app_volume" });

        Assert.Equal(["app"], audio.Scopes);
        Assert.Equal("Notepad isn't in a channel", Assert.Single(osd).Text);
    }

    [Fact]
    public void WithoutANoAudioFallbackItStillSaysSo()
    {
        var (exec, _, osd) = Executor(new FakeAppAudio(Silent));

        exec.Execute(new HotkeyAction { Type = "volume", Mode = "up", Scope = "app_volume" });

        Assert.Equal(AppVolumeResult.NoAudio, Assert.Single(osd).Text);
    }

    [Fact]
    public void OtherWindowsProblemsDontFallBack()
    {
        var audio = new FakeAppAudio(new AppVolumeResult("App Volume", null, null, "Couldn't reach Windows audio"));
        var config = new AppConfig { FocusedAppNoAudioFallback = new TargetEntry { Scope = "system" } };
        var (exec, _, _) = Executor(audio, config: config);

        exec.Execute(new HotkeyAction { Type = "volume", Mode = "up", Scope = "app_volume" });

        Assert.Equal(["app"], audio.Scopes);
    }

    // ─── Plain titles ───────────────────────────────────────────────────────────

    [Fact]
    public void AChannelInAllMixesIsJustTheChannelName()
    {
        var state = Of(new TargetEntry { Id = "a", ChannelId = "music" });
        var (exec, _, osd) = Executor(state: state);

        exec.Execute(new HotkeyAction { Type = "target", Mode = "select", TargetId = "a" });

        Assert.Equal("Music", Assert.Single(osd).Text);
    }

    [Theory]
    [InlineData("volume", "up", null, 60)]
    [InlineData("mute", "mute", "Muted", null)]
    public void TitlesAreJustTheName(string type, string mode, string? text, int? level)
    {
        var (exec, _, osd) = Executor();
        exec.Execute(new HotkeyAction { Type = type, Mode = mode, ChannelId = "music", Step = 10 });
        Assert.Equal(new OsdMessage("Music", text, level), Assert.Single(osd));
    }

    // ─── Target While Held availability ─────────────────────────────────────────

    [Theory]
    [InlineData("normal", false)]
    [InlineData("hold", true)]
    [InlineData("double", true)]
    public void TargetWhileHeldIsOfferedOnHoldAndDoublePressOnly(string trigger, bool offered) =>
        Assert.Equal(offered, ActionCatalog.ModesFor("target", trigger).Any(m => m.Mode == "hold"));

    // ─── Helpers ────────────────────────────────────────────────────────────────

    private static (HotkeyActionExecutor, FakeWaveLink, List<OsdMessage>) Executor(
        FakeAppAudio? audio = null, TargetState? state = null, bool connected = true, AppConfig? config = null)
    {
        var wl = new FakeWaveLink { IsConnected = connected }.WithChannel("music", "Music", level: 0.5);
        var osd = new List<OsdMessage>();
        var exec = new HotkeyActionExecutor(() => config ?? new AppConfig(), wl, (_, _, _) => { },
            new RecordingMediaSender(), state ?? new TargetState(), audio ?? new FakeAppAudio(default), osd.Add);
        return (exec, wl, osd);
    }

    private static (KeyboardHook, AppConfig) Hook(bool volumeKeys, params (string Id, string[] Keys)[] chords)
    {
        var config = new AppConfig { VolumeKeys = volumeKeys ? "wavelink" : "windows" };
        config.Hotkeys["F13"] = new HotkeyBinding
        {
            NormalAction = new HotkeyAction { Type = "volume", Mode = "up", Scope = "active" }
        };
        foreach (var (id, keys) in chords)
            config.Targets.Add(new TargetEntry { Id = id, ChannelId = id, HoldKeys = [.. keys] });
        var hook = new KeyboardHook();
        Publish(hook, config);
        return (hook, config);
    }

    private static void Publish(KeyboardHook hook, AppConfig config)
    {
        var effective = EffectiveBindings.Build(config);
        hook.SetMappedCombos(effective.Mapped);
        hook.SetResolutionInfo(effective);
    }

    private static void HoldModifiers(KeyboardHook hook, params string[] fields)
    {
        foreach (string field in fields)
            typeof(KeyboardHook).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(hook, true);
    }

    private static void HoldKey(KeyboardHook hook, string key) =>
        ((HashSet<string>)typeof(KeyboardHook).GetField("_heldKeys", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(hook)!).Add(key);
}
