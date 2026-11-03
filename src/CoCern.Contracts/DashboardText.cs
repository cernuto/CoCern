using System.Globalization;
using System.Text;

namespace CoCern.Contracts.Dashboard;

/// <summary>
/// The text a dashboard record carries from stored state. Readers that build those records from stored JSON (storage's observation,
/// the Domain's standing projection) apply it, so a stored value cannot reorder, hide or flood what the page shows.
/// </summary>
public static class DashboardText
{
    /// <summary>The most UTF-16 code units a dashboard text keeps.</summary>
    public const int MaxLength = 256;

    /// <summary>
    /// <paramref name="value"/> without control or format characters (bidirectional overrides and zero-width marks among them), cut at
    /// <see cref="MaxLength"/> UTF-16 code units without splitting a character.
    /// </summary>
    public static string Safe(string value)
    {
        var result = new StringBuilder(Math.Min(value.Length, MaxLength));
        foreach (var rune in value.EnumerateRunes())
        {
            if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format) continue;
            if (result.Length + rune.Utf16SequenceLength > MaxLength) break;
            result.Append(rune.ToString());
        }
        return result.ToString();
    }
}
