using System.Security.Cryptography;
using System.Text;
using CadTranslation.Contracts;

namespace CadTranslation.Core;

public static class Hashing
{
    public static string Sha256File(string path)
    {
        // Core Console holds the active drawing open while scan verifies the job hash.
        // The scanner is read-only, so sharing read/write access only permits the host's
        // existing handle; no writer is introduced by this method.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Sha256Text(string value) =>
        Convert.ToHexString(SHA256.HashData(JsonDefaults.Utf8NoBom.GetBytes(value))).ToLowerInvariant();
}
