// Script detection adapted from moshi/Lyric Motion 1.4.3 (MIT); no visual overrides.
using System.Text;

namespace Noctis.Features.LyricMotion;

internal enum TextDirection { Neutral, LeftToRight, RightToLeft }

internal static class Script
{
    internal static void Count(string? text, ref int rtl, ref int ltr)
    {
        if (string.IsNullOrEmpty(text)) return;
        foreach (var rune in text.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune)) continue;
            if (IsRightToLeft(rune.Value)) rtl++; else ltr++;
        }
    }

    internal static TextDirection Of(string? text)
    {
        int rtl = 0, ltr = 0; Count(text, ref rtl, ref ltr);
        return rtl + ltr == 0 ? TextDirection.Neutral : rtl >= ltr ? TextDirection.RightToLeft : TextDirection.LeftToRight;
    }

    internal static bool FlowsRightToLeft(string? text, bool songRtl)
    {
        int rtl = 0, ltr = 0; Count(text, ref rtl, ref ltr);
        return rtl > 0 && (songRtl || rtl > ltr);
    }

    private static bool IsRightToLeft(int v) => v is >= 0x0590 and <= 0x08FF
        or >= 0xFB1D and <= 0xFDFF or >= 0xFE70 and <= 0xFEFC
        or >= 0x10800 and <= 0x10FFF or >= 0x1E800 and <= 0x1EFFF;

    internal static bool CanSplit(string text)
    {
        foreach (var rune in text.EnumerateRunes())
            if (Rune.IsLetter(rune) && (IsRightToLeft(rune.Value) || rune.Value is > 0x052F and < 0x2E80)) return false;
        return true;
    }
}
