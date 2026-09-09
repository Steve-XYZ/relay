namespace Relay.Core;

/// <summary>
/// Human-friendly identifiers used in URLs, CLI output and incident reports.
/// Crockford-style alphabet: no I, L, O or U, so ids survive being read aloud.
/// </summary>
public static class ShortId
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string Generate(int length = 4)
    {
        var chars = new char[length];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = Alphabet[Random.Shared.Next(Alphabet.Length)];
        return new string(chars);
    }
}
