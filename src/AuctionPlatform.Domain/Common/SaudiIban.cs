using System.Numerics;
using System.Text;

namespace AuctionPlatform.Domain.Common;

/// <summary>
/// Saudi IBANs: "SA", two check digits and 20 more digits, 24 characters in all.
///
/// Withdrawals are paid by bank transfer, so the destination has to be an account a bank will
/// accept. The ISO 13616 check digits catch a mistyped or swapped digit before the money leaves
/// the wallet, instead of after the transfer bounces.
/// </summary>
public static class SaudiIban
{
    public const int Length = 24;

    /// <summary>Strips spaces and dashes, upper-cases, and checks the shape and check digits.</summary>
    public static bool TryNormalise(string? raw, out string iban)
    {
        iban = string.Empty;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        var compact = new string(raw.Where(c => !char.IsWhiteSpace(c) && c != '-').ToArray())
            .ToUpperInvariant();

        if (compact.Length != Length || !compact.StartsWith("SA", StringComparison.Ordinal)) return false;
        if (!compact.Skip(2).All(char.IsAsciiDigit)) return false;
        if (!HasValidCheckDigits(compact)) return false;

        iban = compact;
        return true;
    }

    public static string Normalise(string? raw) =>
        TryNormalise(raw, out var iban)
            ? iban
            : throw new DomainException(
                ErrorCodes.IbanInvalid, "Enter a valid Saudi IBAN: SA followed by 22 digits.");

    /// <summary>ISO 13616: move the first four characters to the end, letters to numbers, mod 97 is 1.</summary>
    private static bool HasValidCheckDigits(string iban)
    {
        var rearranged = iban[4..] + iban[..4];
        var digits = new StringBuilder();
        foreach (var c in rearranged)
        {
            digits.Append(char.IsAsciiLetter(c) ? (c - 'A' + 10).ToString() : c.ToString());
        }

        return BigInteger.Parse(digits.ToString()) % 97 == 1;
    }
}
