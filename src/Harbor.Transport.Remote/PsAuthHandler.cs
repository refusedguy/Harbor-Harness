using System.Security.Cryptography;

namespace Harbor.Transport.Remote;

public static class PsAuthHandler
{
    public static string GeneratePsk()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes);
    }

    public static bool Validate(string? provided, string expected)
        => !string.IsNullOrEmpty(provided) && CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(provided), Encoding.UTF8.GetBytes(expected));
}
