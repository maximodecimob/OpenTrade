using System.Security.Cryptography;

namespace OpenTrade.Api;

public static class Passwords
{
    const int Iterations = 210_000;
    public const int MinLength = 12;

    static readonly byte[] DummySalt = RandomNumberGenerator.GetBytes(16);
    static readonly byte[] DummyHash = Derive("dummy", DummySalt);

    static byte[] Derive(string p, byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(p, salt, Iterations, HashAlgorithmName.SHA256, 32);

    public static (byte[] Salt, byte[] Hash) Create(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        return (salt, Derive(password, salt));
    }

    public static bool Verify(string password, byte[] salt, byte[] hash) => CryptographicOperations.FixedTimeEquals(Derive(password, salt), hash);

    /// <summary>Burns the same CPU time as a real check, so unknown e-mails are not distinguishable by timing.</summary>
    public static void Burn(string password) => CryptographicOperations.FixedTimeEquals(Derive(password, DummySalt), DummyHash);

    public static string? Validate(string? password) =>
        password is null || password.Length < MinLength ? $"Password must be at least {MinLength} characters"
        : password.Length > 128 ? "Password is too long" : null;
}
