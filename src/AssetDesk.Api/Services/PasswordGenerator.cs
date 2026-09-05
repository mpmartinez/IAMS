using System.Security.Cryptography;

namespace AssetDesk.Api.Services;

/// <summary>
/// Generates the throwaway password a newly invited account is created with. Nobody ever sees it
/// - not even the administrator doing the inviting - because the invitee sets their own through
/// the emailed link. Identity still validates it on creation, though, so it is built to satisfy
/// the configured requirements by construction rather than by chance: one character from each
/// required class, then shuffled so those four do not always sit in the same positions.
/// </summary>
public static class PasswordGenerator
{
    private const string Lower = "abcdefghijklmnopqrstuvwxyz";
    private const string Upper = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private const string Digits = "0123456789";
    private const string Symbols = "!@#$%^&*-_=+";
    private const string All = Lower + Upper + Digits + Symbols;

    /// <summary>Default length is far above the configured minimum of 8 - it costs nothing
    /// when no human has to read or type the result.</summary>
    public static string Generate(int length = 24)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(length, 8);

        var chars = new List<char>(length) { Pick(Lower), Pick(Upper), Pick(Digits), Pick(Symbols) };

        while (chars.Count < length)
            chars.Add(Pick(All));

        // Fisher-Yates over a cryptographic source. Without it the guaranteed-class characters
        // would always occupy the first four positions.
        for (var i = chars.Count - 1; i > 0; i--)
        {
            var j = RandomNumberGenerator.GetInt32(i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string([.. chars]);
    }

    private static char Pick(string set) => set[RandomNumberGenerator.GetInt32(set.Length)];
}
