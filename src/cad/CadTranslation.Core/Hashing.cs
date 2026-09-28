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
        return ToHexString(Sha256(stream)).ToLowerInvariant();
    }

    public static string Sha256Text(string value) =>
        ToHexString(Sha256(JsonDefaults.Utf8NoBom.GetBytes(value))).ToLowerInvariant();

    // SHA256.HashData is .NET 5+/.NET 7+ (stream); the AutoCAD 2020-2024 hosts only have ComputeHash.
    private static byte[] Sha256(Stream stream)
    {
#if NETFRAMEWORK
        using var algorithm = SHA256.Create();
        return algorithm.ComputeHash(stream);
#else
        return SHA256.HashData(stream);
#endif
    }

    private static byte[] Sha256(byte[] data)
    {
#if NETFRAMEWORK
        using var algorithm = SHA256.Create();
        return algorithm.ComputeHash(data);
#else
        return SHA256.HashData(data);
#endif
    }
}
