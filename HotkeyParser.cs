using System.Windows.Input;

namespace MDM
{
    public static class HotkeyParser
    {
        public static Key NormalizeKey(KeyEventArgs e)
        {
            Key key = e.Key;
            if (key == Key.System)
                key = e.SystemKey;
            if (key is Key.ImeProcessed or Key.DeadCharProcessed)
            {
                key = e.SystemKey != Key.None ? e.SystemKey : e.Key;
                if (key is Key.ImeProcessed or Key.DeadCharProcessed)
                    key = e.SystemKey;
            }
            return key;
        }

        public static bool Matches(string? gesture, KeyEventArgs e)
            => Matches(gesture, NormalizeKey(e), Keyboard.Modifiers);

        public static bool Matches(string? gesture, Key key, ModifierKeys mods)
        {
            if (string.IsNullOrWhiteSpace(gesture))
                return false;
            if (IsModifierKey(key))
                return false;
            if (!TryParse(gesture, out var wantKey, out var wantMods))
                return false;
            return key == wantKey && mods == wantMods;
        }

        public static bool TryParse(string? gesture, out Key key, out ModifierKeys mods)
        {
            key = Key.None;
            mods = ModifierKeys.None;
            if (string.IsNullOrWhiteSpace(gesture))
                return false;

            foreach (string part in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string p = part.Trim();
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase)
                    || p.Equals("Control", StringComparison.OrdinalIgnoreCase))
                    mods |= ModifierKeys.Control;
                else if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase))
                    mods |= ModifierKeys.Shift;
                else if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase))
                    mods |= ModifierKeys.Alt;
                else if (p.Equals("Win", StringComparison.OrdinalIgnoreCase)
                         || p.Equals("Windows", StringComparison.OrdinalIgnoreCase))
                    mods |= ModifierKeys.Windows;
                else if (TryParseKey(p, out Key k))
                    key = k;
            }
            return key != Key.None;
        }

        private static bool TryParseKey(string p, out Key key)
        {
            key = Key.None;
            if (string.IsNullOrWhiteSpace(p))
                return false;

            if (Enum.TryParse(p, ignoreCase: true, out Key k) && k != Key.None && !IsModifierKey(k))
            {
                key = k;
                return true;
            }

            if (p.Length == 1)
            {
                char c = char.ToUpperInvariant(p[0]);
                if (c is >= 'A' and <= 'Z' && Enum.TryParse(c.ToString(), out Key letter))
                {
                    key = letter;
                    return true;
                }
                if (c is >= '0' and <= '9' && Enum.TryParse("D" + c, out Key digit))
                {
                    key = digit;
                    return true;
                }
            }

            return false;
        }

        public static string Format(Key key, ModifierKeys mods)
        {
            var parts = new List<string>();
            if (mods.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (mods.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (mods.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (mods.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(DisplayKey(key));
            return string.Join("+", parts);
        }

        public static string DisplayKey(Key key)
        {
            if (key >= Key.D0 && key <= Key.D9)
                return ((char)('0' + (key - Key.D0))).ToString();
            if (key >= Key.A && key <= Key.Z)
                return key.ToString();
            if (key >= Key.NumPad0 && key <= Key.NumPad9)
                return "NumPad" + (key - Key.NumPad0);
            return key.ToString();
        }

        public static bool IsModifierKey(Key key) =>
            key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift
                or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin
                or Key.System;
    }
}
