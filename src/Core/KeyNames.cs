namespace Wlhk.Core;

/// <summary>
/// Virtual-key code to key-name mapping, byte-compatible with v1's combo strings.
///
/// v1 used node-global-key-listener, whose Windows lookup table gives every VK a
/// "standardName" (e.g. "SPACE", "LEFT ARROW", "NUMPAD 0") and a raw "name"
/// (e.g. "VOLUME_MUTE"). v1's getComboString used the standardName and fell back
/// to the raw name when the standardName was empty (media keys, PAUSE, APPS, ...).
/// Existing user configs are keyed on those exact strings, so this table mirrors
/// the NGKL table verbatim — including its idiosyncratic labels (OEM_3 = "SECTION").
/// </summary>
public static class KeyNames
{
    /// <summary>VK codes treated as pure modifiers: they never trigger actions and are never suppressed.</summary>
    public static bool IsModifier(int vk) => vk is 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or (>= 0xA0 and <= 0xA5);

    /// <summary>
    /// Returns the v1-compatible base key name for a VK code, or null for
    /// unknown keys and pure modifiers (which never form a combo on their own).
    /// </summary>
    public static string? BaseName(int vk)
    {
        if (IsModifier(vk)) return null;

        // Letters and digits
        if (vk is >= 0x30 and <= 0x39) return ((char)vk).ToString();          // 0-9
        if (vk is >= 0x41 and <= 0x5A) return ((char)vk).ToString();          // A-Z
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);          // F1-F24
        if (vk is >= 0x60 and <= 0x69) return "NUMPAD " + (vk - 0x60);        // NUMPAD 0-9

        return vk switch
        {
            0x08 => "BACKSPACE",
            0x09 => "TAB",
            0x0C => "NUMPAD CLEAR",
            0x0D => "RETURN",
            0x13 => "PAUSE",                 // raw fallback (empty standardName)
            0x14 => "CAPS LOCK",
            0x1B => "ESCAPE",
            0x20 => "SPACE",
            0x21 => "PAGE UP",
            0x22 => "PAGE DOWN",
            0x23 => "END",
            0x24 => "HOME",
            0x25 => "LEFT ARROW",
            0x26 => "UP ARROW",
            0x27 => "RIGHT ARROW",
            0x28 => "DOWN ARROW",
            0x2C => "PRINT SCREEN",
            0x2D => "INS",
            0x2E => "DELETE",
            0x5D => "APPS",                  // raw fallback
            0x5F => "SLEEP",                 // raw fallback
            0x6A => "NUMPAD MULTIPLY",
            0x6B => "NUMPAD PLUS",
            0x6D => "NUMPAD MINUS",
            0x6E => "NUMPAD DOT",
            0x6F => "NUMPAD DIVIDE",
            0x90 => "NUM LOCK",
            0x91 => "SCROLL LOCK",
            // Browser / launcher / media keys: NGKL standardName is empty,
            // so v1 stored the raw platform name.
            0xA6 => "BROWSER_BACK",
            0xA7 => "BROWSER_FORWARD",
            0xA8 => "BROWSER_REFRESH",
            0xA9 => "BROWSER_STOP",
            0xAA => "BROWSER_SEARCH",
            0xAB => "BROWSER_FAVORITES",
            0xAC => "BROWSER_HOME",
            0xAD => "VOLUME_MUTE",
            0xAE => "VOLUME_DOWN",
            0xAF => "VOLUME_UP",
            0xB0 => "MEDIA_NEXT_TRACK",
            0xB1 => "MEDIA_PREV_TRACK",
            0xB2 => "MEDIA_STOP",
            0xB3 => "MEDIA_PLAY_PAUSE",
            0xB4 => "LAUNCH_MAIL",
            0xB5 => "LAUNCH_MEDIA_SELECT",
            0xB6 => "LAUNCH_APP1",
            0xB7 => "LAUNCH_APP2",
            // OEM punctuation (NGKL's US-layout labels, kept verbatim for config compat)
            0xBA => "SEMICOLON",
            0xBB => "EQUALS",
            0xBC => "COMMA",
            0xBD => "MINUS",
            0xBE => "DOT",
            0xBF => "FORWARD SLASH",
            0xC0 => "SECTION",
            0xDB => "SQUARE BRACKET OPEN",
            0xDC => "BACKSLASH",
            0xDD => "SQUARE BRACKET CLOSE",
            0xDE => "QUOTE",
            0xDF => "OEM_8",
            0xE2 => "BACKTICK",
            _ => null
        };
    }
}
