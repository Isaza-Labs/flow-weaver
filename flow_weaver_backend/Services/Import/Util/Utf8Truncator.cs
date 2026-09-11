using System.Text;

namespace flow_weaver_backend.Services.Import.Util;

// Truncate strings by UTF-8 byte budget rather than UTF-16 char count.
// String.Length and string-slicing operate on UTF-16 code units, which
// gives unpredictable results when the limit is meant to be a prompt
// byte budget for an LLM (1 char ≠ 1 byte for anything non-ASCII, and
// surrogate pairs span two code units).
//
// Two-step strategy:
//   1. Walk runes (code points) from the start, accumulating UTF-8
//      bytes until adding the next rune would exceed maxBytes.
//   2. Return the substring up to that cut, never mid-codepoint.
public static class Utf8Truncator
{
    public static bool ExceedsByteBudget(string value, int maxBytes) =>
        Encoding.UTF8.GetByteCount(value) > maxBytes;

    // Returns the longest prefix whose UTF-8 encoding fits in maxBytes.
    // Cut happens on a rune boundary, so surrogate pairs are never split
    // and the result decodes cleanly back to UTF-8.
    public static string TruncateToBytes(string value, int maxBytes)
    {
        if (maxBytes <= 0) return string.Empty;
        if (Encoding.UTF8.GetByteCount(value) <= maxBytes) return value;

        var bytesUsed = 0;
        var charsUsed = 0;
        var enumerator = value.EnumerateRunes();
        while (enumerator.MoveNext())
        {
            var rune = enumerator.Current;
            var runeBytes = rune.Utf8SequenceLength;
            if (bytesUsed + runeBytes > maxBytes) break;
            bytesUsed += runeBytes;
            charsUsed += rune.Utf16SequenceLength;
        }
        return value[..charsUsed];
    }
}
