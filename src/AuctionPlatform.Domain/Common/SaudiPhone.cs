using System.Text;

namespace AuctionPlatform.Domain.Common;

/// <summary>
/// Saudi mobile numbers, the only phone numbers the platform accepts.
///
/// Every way people write one — "05X XXX XXXX", "5XXXXXXXX", "+966 5X XXX XXXX", "009665XXXXXXXX",
/// with spaces, dashes or brackets, in Western or Arabic-Indic digits — reduces to one canonical
/// form, +9665XXXXXXXX. Numbers are stored and compared only in that form, so one number is always
/// one identity.
/// </summary>
public static class SaudiPhone
{
    public const string CountryCode = "+966";

    /// <summary>Shown to people who type something else.</summary>
    public const string FormatHint = "Enter a Saudi mobile number, for example 05XXXXXXXX.";

    public static bool TryNormalise(string? raw, out string normalised)
    {
        normalised = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var digits = new StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            if (char.IsDigit(c))
            {
                // Arabic-Indic and Eastern Arabic-Indic digits become their Western equivalents.
                digits.Append((char)('0' + (int)char.GetNumericValue(c)));
            }
            else if (c is not (' ' or '-' or '(' or ')' or '.' or '+' or '‎' or '‏' or ' '))
            {
                // Letters or other symbols mean this is not a phone number at all.
                return false;
            }
        }

        var national = digits.ToString();
        if (national.StartsWith("00966", StringComparison.Ordinal)) national = national[5..];
        else if (national.StartsWith("966", StringComparison.Ordinal)) national = national[3..];
        else if (national.StartsWith('0')) national = national[1..];

        // A Saudi mobile number is nine digits after the country code, starting with 5.
        if (national.Length != 9 || national[0] != '5') return false;

        normalised = CountryCode + national;
        return true;
    }

    /// <summary>The canonical form, or a validation error explaining the expected format.</summary>
    public static string Normalise(string? raw) =>
        TryNormalise(raw, out var normalised)
            ? normalised
            : throw new DomainException(ErrorCodes.PhoneNumberInvalid, FormatHint);
}
