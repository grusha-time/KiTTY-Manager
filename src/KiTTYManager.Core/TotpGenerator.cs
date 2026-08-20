using System.Buffers.Binary;
using System.Security.Cryptography;

namespace KiTTYManager.Core;

public static class TotpGenerator
{
    public static string Generate(string secret, DateTimeOffset time, int digits = 6,
        int periodSeconds = 30, string algorithm = "SHA1")
    {
        if (digits is < 6 or > 8) throw new ArgumentOutOfRangeException(nameof(digits));
        if (periodSeconds < 1) throw new ArgumentOutOfRangeException(nameof(periodSeconds));

        var key = DecodeSecret(secret);
        var counter = time.ToUnixTimeSeconds() / periodSeconds;
        Span<byte> message = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(message, counter);

        byte[] hash = NormalizeAlgorithm(algorithm) switch
        {
            "SHA1" => HMACSHA1.HashData(key, message),
            "SHA256" => HMACSHA256.HashData(key, message),
            "SHA512" => HMACSHA512.HashData(key, message),
            _ => throw new ArgumentException("Поддерживаются SHA1, SHA256 и SHA512.", nameof(algorithm))
        };

        var offset = hash[^1] & 0x0f;
        var binary = BinaryPrimitives.ReadInt32BigEndian(hash.AsSpan(offset, 4)) & 0x7fffffff;
        var modulus = digits == 8 ? 100_000_000 : (int)Math.Pow(10, digits);
        return (binary % modulus).ToString($"D{digits}");
    }

    public static byte[] DecodeSecret(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("TOTP secret не указан.", nameof(value));
        var input = ExtractSecret(value).Where(ch => !char.IsWhiteSpace(ch) && ch != '-').ToArray();
        if (input.Length == 0) throw new FormatException("TOTP secret пуст.");

        var output = new List<byte>(input.Length * 5 / 8);
        var buffer = 0;
        var bits = 0;
        foreach (var character in input)
        {
            if (character == '=') break;
            var index = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567".IndexOf(char.ToUpperInvariant(character));
            if (index < 0) throw new FormatException("TOTP secret должен быть в формате Base32 или otpauth:// URI.");
            buffer = (buffer << 5) | index;
            bits += 5;
            if (bits < 8) continue;
            output.Add((byte)(buffer >> (bits - 8)));
            bits -= 8;
            buffer &= (1 << bits) - 1;
        }
        if (output.Count == 0) throw new FormatException("TOTP secret слишком короткий.");
        return output.ToArray();
    }

    private static string ExtractSecret(string value)
    {
        var trimmed = value.Trim();
        if (!trimmed.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase)) return trimmed;
        var uri = new Uri(trimmed);
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = pair.Split('=', 2);
            if (parts.Length == 2 && Uri.UnescapeDataString(parts[0]).Equals("secret", StringComparison.OrdinalIgnoreCase))
                return Uri.UnescapeDataString(parts[1]);
        }
        throw new FormatException("В otpauth:// URI отсутствует параметр secret.");
    }

    private static string NormalizeAlgorithm(string value) =>
        value.Replace("-", "", StringComparison.Ordinal).Trim().ToUpperInvariant();
}
