using System.Text;

namespace Modexa.Core.Licensing;

/// <summary>
/// Holds the FSLM product API key in an obfuscated form so it is not a plaintext literal in the
/// shipped binary. Obfuscation is XOR-over-Base64 keyed by a string that is itself rebuilt from
/// character codes (so neither the key nor the ciphertext appears as readable text). This is the
/// same scheme the Backup Manager uses.
///
/// <see cref="EncryptedApiKey"/> is EMPTY in source (the public repo never carries the real key).
/// The release build injects the obfuscated key from secret\fslm-key.txt (or the MODEXA_FSLM_KEY
/// env var) and restores the empty default afterwards — see build\publish.ps1. Produce the value
/// with <see cref="Obfuscate"/>("&lt;FSLM key&gt;").
/// </summary>
internal static class Secrets
{
    private const string EncryptedApiKey = ""; /*AUTO_FSLM_KEY*/

    public static string ApiKey => string.IsNullOrEmpty(EncryptedApiKey)
        ? "MODEXA_API_KEY_PLACEHOLDER"
        : Deobfuscate(EncryptedApiKey);

    /// <summary>Rebuilt from char codes so the obfuscation key is not a readable literal.</summary>
    private static string ObfuscationKey()
    {
        // "Modexa#Rsg.Ir*2026" — adjust freely; must match whatever produced EncryptedApiKey.
        var codes = new byte[]
        {
            77, 111, 100, 101, 120, 97, 35, 82, 115, 103, 46, 73, 114, 42, 50, 48, 50, 54
        };
        var sb = new StringBuilder(codes.Length);
        foreach (var c in codes) sb.Append((char)c);
        return sb.ToString();
    }

    public static string Obfuscate(string plain)
    {
        byte[] key = Encoding.UTF8.GetBytes(ObfuscationKey());
        byte[] data = Encoding.UTF8.GetBytes(plain);
        var outBytes = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
            outBytes[i] = (byte)(data[i] ^ key[i % key.Length]);
        return Convert.ToBase64String(outBytes);
    }

    private static string Deobfuscate(string encrypted)
    {
        byte[] key = Encoding.UTF8.GetBytes(ObfuscationKey());
        byte[] data = Convert.FromBase64String(encrypted);
        var outBytes = new byte[data.Length];
        for (int i = 0; i < data.Length; i++)
            outBytes[i] = (byte)(data[i] ^ key[i % key.Length]);
        return Encoding.UTF8.GetString(outBytes);
    }
}
