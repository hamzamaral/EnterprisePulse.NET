using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EnterprisePulse.Core.Common;
using Konscious.Security.Cryptography;

namespace EnterprisePulse.Core.Modules.Identity;

/// <summary>Argon2id hashes in the PHC string format ($argon2id$v=19$m=...,t=...,p=...$salt$hash).</summary>
public static partial class PasswordHasher
{
    private const int MemoryKb = 19456;
    private const int Iterations = 2;
    private const int Parallelism = 1;

    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var hash = Compute(password, salt, MemoryKb, Iterations, Parallelism, 32);
        return $"$argon2id$v=19$m={MemoryKb},t={Iterations},p={Parallelism}${B64(salt)}${B64(hash)}";
    }

    public static bool Verify(string password, string encoded)
    {
        var m = Phc().Match(encoded);
        if (!m.Success) return false;
        var salt = FromB64(m.Groups["salt"].Value);
        var expected = FromB64(m.Groups["hash"].Value);
        var actual = Compute(password, salt, int.Parse(m.Groups["m"].Value), int.Parse(m.Groups["t"].Value), int.Parse(m.Groups["p"].Value), expected.Length);
        return CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    /// <summary>At least 8 characters with an upper case letter, a lower case letter and a digit.</summary>
    public static void AssertStrong(string password)
    {
        var ok = password.Length >= 8 && password.Any(char.IsUpper) && password.Any(char.IsLower) && password.Any(char.IsDigit);
        if (!ok) throw new BadRequestAppException("Şifre en az 8 karakter olmalı; büyük harf, küçük harf ve rakam içermeli");
    }

    private static byte[] Compute(string password, byte[] salt, int memory, int iterations, int parallelism, int length)
    {
        using var argon = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            MemorySize = memory,
            Iterations = iterations,
            DegreeOfParallelism = parallelism,
        };
        return argon.GetBytes(length);
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=');

    private static byte[] FromB64(string s) => Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));

    [GeneratedRegex(@"^\$argon2id\$v=19\$m=(?<m>\d+),t=(?<t>\d+),p=(?<p>\d+)\$(?<salt>[^$]+)\$(?<hash>[^$]+)$")]
    private static partial Regex Phc();
}
