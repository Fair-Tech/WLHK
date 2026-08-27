using System.Reflection;
using Wlhk.Core;
using Xunit;

namespace Wlhk.Tests;

/// <summary>
/// Left/right modifier support (issue #4). The hook's combo building and
/// resolution are exercised directly: installing a real low-level hook in a test
/// run is not viable, so the modifier state fields are set the same way the
/// hook callback sets them.
/// </summary>
public sealed class ModifierSideTests
{
    private static void SetModifiers(KeyboardHook hook, params string[] fields)
    {
        foreach (string field in fields)
            Field(field).SetValue(hook, true);

        static FieldInfo Field(string name) =>
            typeof(KeyboardHook).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field {name} not found");
    }

    private static string Build(KeyboardHook hook, string baseName, bool sideSpecific) =>
        (string)typeof(KeyboardHook)
            .GetMethod("BuildCombo", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(hook, [baseName, sideSpecific])!;

    private static string Resolve(KeyboardHook hook, string baseName) =>
        (string)typeof(KeyboardHook)
            .GetMethod("ResolveCombo", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(hook, [baseName])!;

    [Fact]
    public void SideAgnosticFormMatchesLegacyNaming()
    {
        using var hook = new KeyboardHook();
        SetModifiers(hook, "_rShift", "_lCtrl");
        Assert.Equal("CTRL+SHIFT+H", Build(hook, "H", sideSpecific: false));
    }

    [Fact]
    public void SideSpecificFormNamesEachHeldSide()
    {
        using var hook = new KeyboardHook();
        SetModifiers(hook, "_rShift", "_rCtrl");
        Assert.Equal("RCTRL+RSHIFT+H", Build(hook, "H", sideSpecific: true));
    }

    [Fact]
    public void BothSidesOfOneModifierAreNamedLeftFirst()
    {
        using var hook = new KeyboardHook();
        SetModifiers(hook, "_lShift", "_rShift");
        Assert.Equal("LSHIFT+RSHIFT+H", Build(hook, "H", sideSpecific: true));
    }

    [Fact]
    public void UnmodifiedKeyIsIdenticalInBothForms()
    {
        using var hook = new KeyboardHook();
        Assert.Equal("VOLUME_MUTE", Build(hook, "VOLUME_MUTE", sideSpecific: true));
        Assert.Equal("VOLUME_MUTE", Build(hook, "VOLUME_MUTE", sideSpecific: false));
    }

    [Fact]
    public void SideSpecificBindingWinsWhenMapped()
    {
        using var hook = new KeyboardHook();
        hook.SetMappedCombos(["RSHIFT+H", "SHIFT+H"]);
        SetModifiers(hook, "_rShift");
        Assert.Equal("RSHIFT+H", Resolve(hook, "H"));
    }

    [Fact]
    public void SideAgnosticBindingStillMatchesEitherSide()
    {
        // Only the legacy binding exists: both shifts must keep working.
        using var left = new KeyboardHook();
        left.SetMappedCombos(["SHIFT+H"]);
        SetModifiers(left, "_lShift");
        Assert.Equal("SHIFT+H", Resolve(left, "H"));

        using var right = new KeyboardHook();
        right.SetMappedCombos(["SHIFT+H"]);
        SetModifiers(right, "_rShift");
        Assert.Equal("SHIFT+H", Resolve(right, "H"));
    }

    [Fact]
    public void OtherSideFallsBackToSideAgnosticBinding()
    {
        // "RSHIFT+H" bound, left shift pressed: must not match the right-side
        // binding, and falls back to the side-agnostic form (unmapped here).
        using var hook = new KeyboardHook();
        hook.SetMappedCombos(["RSHIFT+H"]);
        SetModifiers(hook, "_lShift");
        Assert.Equal("SHIFT+H", Resolve(hook, "H"));
    }

    [Fact]
    public void SidedAndUnsidedBindingsCoexist()
    {
        // The issue's example: Shift+H and RShift+H as different hotkeys.
        using var hook = new KeyboardHook();
        hook.SetMappedCombos(["SHIFT+H", "RSHIFT+H"]);

        using var rightHook = new KeyboardHook();
        rightHook.SetMappedCombos(["SHIFT+H", "RSHIFT+H"]);

        SetModifiers(hook, "_lShift");
        SetModifiers(rightHook, "_rShift");

        Assert.Equal("SHIFT+H", Resolve(hook, "H"));
        Assert.Equal("RSHIFT+H", Resolve(rightHook, "H"));
    }
}
