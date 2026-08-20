using System.Text;
using System.Security.Cryptography;

namespace KiTTYManager.Core;

internal static class KittyCredentialDecoder
{
    private const string Base64Alphabet = "AZERTYUIOPQSDFGHJKLMWXCVBNazertyuiopqsdfghjklmwxcvbn0123456789+/";
    // Official 0.76.1.13 binaries use 9bis; source/default builds commonly
    // retain ToBeDefined. Accept both file formats without trying either value
    // as an SSH credential.
    private static readonly string[] ScriptKeys = ["9bis", "ToBeDefined"];
    private static readonly byte[] PasswordMask = [0xA4, 0xA5, 0xA9, 0xAA, 0xB3, 0xBC, 0xBD, 0xBE];

    public static string? DecodePassword(string storedValue, string host, string terminalType, int cryptSaltMode)
    {
        if (string.IsNullOrEmpty(storedValue)) return null;
        if (cryptSaltMode > 1) return NormalizePlainPassword(storedValue);

        var mode = cryptSaltMode == 0 ? 0 : 1;
        var key = mode > 0 ? "KiTTY" : host + terminalType + "KiTTY";
        if (!TryDecrypt(storedValue, Encoding.UTF8.GetBytes(key), out var bytes)) return null;

        if (mode != 0)
        {
            var decoded = NormalizePasswordBytes(bytes);
            return IsUsableCredential(decoded) ? decoded : null;
        }

        // Current KiTTY applies MASKPASS around its cipher. Older manager builds
        // wrote the same cipher without that mask, so accept both formats and
        // prefer the candidate that looks like a normal portable credential.
        var legacy = NormalizePasswordBytes(bytes);
        var vendorBytes = bytes.ToArray();
        ApplyPasswordMask(vendorBytes);
        var vendor = NormalizePasswordBytes(vendorBytes);
        var legacyScore = CredentialScore(legacy);
        var vendorScore = CredentialScore(vendor);
        if (legacyScore < 0 && vendorScore < 0) return null;
        return vendorScore >= legacyScore ? vendor : legacy;
    }

    public static string EncodePassword(string password, string host, string terminalType, int cryptSaltMode,
        string? fixedSeed = null)
    {
        if (password.Length == 0) return "";
        if (cryptSaltMode > 1) return password;
        var key = Encoding.UTF8.GetBytes(cryptSaltMode == 0 ? host + terminalType + "KiTTY" : "KiTTY");
        return EncryptBytes(Encoding.Latin1.GetBytes(password), key, fixedSeed);
    }

    internal static bool TryRewriteRootPassword(
        string storedContent, string automaticCommand, string expectedOldPassword, string newPassword,
        out string rewritten)
    {
        rewritten = storedContent;
        if (string.IsNullOrEmpty(storedContent) || string.IsNullOrEmpty(expectedOldPassword)) return false;

        string plaintext;
        string? encryptionKey = null;
        if (LooksLikeLoginScript(storedContent)) plaintext = storedContent;
        else
        {
            plaintext = "";
            foreach (var key in ScriptKeys)
            {
                if (!TryDecrypt(storedContent, Encoding.UTF8.GetBytes(key), out var bytes)) continue;
                var candidate = DecodeText(bytes).Replace('\0', '\n');
                if (!LooksLikeLoginScript(candidate)) continue;
                plaintext = candidate; encryptionKey = key; break;
            }
            if (encryptionKey is null) return false;
        }

        var separator = plaintext.Contains('\0') ? '\0' : '\n';
        var records = plaintext.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split(separator);
        var candidates = new List<int>();
        var rootTransitionSeen = NormalizeRootCommand(automaticCommand) is not null;
        for (var index = 0; index + 1 < records.Length; index++)
        {
            var prompt = records[index].Trim().TrimEnd(':').Trim().ToLowerInvariant();
            var response = records[index + 1].Trim();
            if (NormalizeRootCommand(response) is not null) { rootTransitionSeen = true; continue; }
            if (rootTransitionSeen && IsPasswordPrompt(prompt) && response == expectedOldPassword)
                candidates.Add(index + 1);
        }
        if (candidates.Distinct().Count() != 1) return false;
        records[candidates[0]] = newPassword;
        var updated = string.Join(separator, records);
        rewritten = encryptionKey is null
            ? updated
            : EncryptBytes(EncodeScriptText(updated.Replace('\n', '\0')), Encoding.UTF8.GetBytes(encryptionKey));
        return true;
    }

    private static byte[] EncodeScriptText(string value)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try { return Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback).GetBytes(value); }
        catch (EncoderFallbackException) { return Encoding.UTF8.GetBytes(value); }
    }

    private static string EncryptBytes(byte[] bytes, byte[] key, string? fixedSeed = null)
    {
        var alphabet = Encoding.ASCII.GetBytes(Base64Alphabet);
        var seed = fixedSeed is null ? new byte[5] : Encoding.ASCII.GetBytes(fixedSeed);
        if (fixedSeed is null)
            for (var index = 0; index < seed.Length; index++) seed[index] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        if (seed.Length != 5 || seed.Any(value => Array.IndexOf(alphabet, value) < 0))
            throw new ArgumentException("Seed должен содержать пять символов алфавита KiTTY.", nameof(fixedSeed));
        Scramble(alphabet, seed);
        var output = new List<byte>(seed);
        for (var index = 0; index < bytes.Length; index++)
        {
            var value = (int)bytes[index];
            while (value >= alphabet.Length - 1)
            {
                output.Add(alphabet[^1]);
                value -= alphabet.Length - 1;
                Scramble(alphabet, key);
            }
            output.Add(alphabet[value]);
            if ((index + 1) % alphabet.Length == 0) Scramble(alphabet, key);
        }
        return Encoding.ASCII.GetString(output.ToArray());
    }

    private static void ApplyPasswordMask(byte[] bytes)
    {
        if (bytes.Where((value, index) => (value ^ PasswordMask[index % PasswordMask.Length]) == 0).Any()) return;
        for (var index = 0; index < bytes.Length; index++)
            bytes[index] ^= PasswordMask[index % PasswordMask.Length];
    }

    private static int CredentialScore(string value)
    {
        if (!IsUsableCredential(value)) return -1;
        return value.Count(character => character is >= ' ' and <= '~');
    }

    public static KittyScriptData? DecodeLoginScript(string storedContent, int cryptSaltMode)
    {
        if (string.IsNullOrWhiteSpace(storedContent)) return null;

        // Unlike Password, KiTTY encrypts ScriptfileContent with its own fixed
        // key regardless of cryptsalt. Keep plaintext support for older/manual
        // portable files, but only accept decrypted bytes that look like a
        // real prompt/response script.
        string? script = LooksLikeLoginScript(storedContent) ? storedContent : null;
        foreach (var key in ScriptKeys)
        {
            if (!TryDecrypt(storedContent, Encoding.UTF8.GetBytes(key), out var bytes)) continue;
            var decoded = DecodeText(bytes).Replace('\0', '\n');
            if (!LooksLikeLoginScript(decoded)) continue;
            script = decoded;
            break;
        }

        if (script is null) return null;
        return ExtractChallengeResponses(script);
    }

    private static bool LooksLikeLoginScript(string value)
    {
        return value.Contains("login", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("username", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("password", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("assword", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("пароль", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("sudo", StringComparison.OrdinalIgnoreCase) ||
               value.Contains("su -", StringComparison.OrdinalIgnoreCase);
    }

    private static string? NormalizePlainPassword(string value)
    {
        while (value.Length >= 2 && value[^2] == '\\' && value[^1] is 'n' or 'r')
            value = value[..^2];
        return IsUsableCredential(value) ? value : null;
    }

    private static string NormalizePasswordBytes(byte[] bytes)
    {
        var length = bytes.Length;
        while (length >= 2 && bytes[length - 2] == (byte)'\\' && bytes[length - 1] is (byte)'n' or (byte)'r')
            length -= 2;

        // KiTTY converts the decrypted password from ISO-8859-1 to UTF-8 just
        // before SSH authentication. SSH.NET performs the final UTF-8 encoding.
        return Encoding.Latin1.GetString(bytes, 0, length);
    }

    public static KittyScriptData? ReadLoginScriptFile(string path)
    {
        try
        {
            return ExtractChallengeResponses(DecodeScriptFile(File.ReadAllBytes(path)));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string DecodeScriptFile(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(Encoding.UTF8.GetPreamble()))
            return Encoding.UTF8.GetString(bytes.AsSpan(Encoding.UTF8.GetPreamble().Length));
        if (bytes.AsSpan().StartsWith(Encoding.Unicode.GetPreamble()))
            return Encoding.Unicode.GetString(bytes.AsSpan(Encoding.Unicode.GetPreamble().Length));
        if (bytes.AsSpan().StartsWith(Encoding.BigEndianUnicode.GetPreamble()))
            return Encoding.BigEndianUnicode.GetString(bytes.AsSpan(Encoding.BigEndianUnicode.GetPreamble().Length));

        var sampleLength = Math.Min(bytes.Length, 128);
        var oddNulls = 0;
        for (var index = 1; index < sampleLength; index += 2)
            if (bytes[index] == 0) oddNulls++;
        if (sampleLength >= 4 && oddNulls >= sampleLength / 8)
            return Encoding.Unicode.GetString(bytes);

        return DecodeText(bytes).TrimStart('\uFEFF');
    }

    internal static KittyScriptData ExtractChallengeResponses(string script)
    {
        var lines = script.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

        string? username = null;
        string? password = null;
        string? rootLogin = null;
        string? rootPassword = null;
        var rootTransitionSeen = false;
        // KiTTY scripts are normally prompt/response pairs, but users often
        // insert extra lines or deliberately shorten a prompt. Inspect each
        // adjacent pair instead of losing alignment after such a line.
        for (var index = 0; index + 1 < lines.Length; index++)
        {
            var prompt = lines[index].TrimEnd(':').Trim().ToLowerInvariant();
            var response = lines[index + 1];
            if (!IsUsableCredential(response)) continue;

            if (NormalizeRootCommand(response) is not null)
            {
                rootLogin ??= response;
                rootTransitionSeen = true;
                continue;
            }

            if (IsUsernamePrompt(prompt))
            {
                if (response.Equals("root", StringComparison.OrdinalIgnoreCase) && username is not null)
                {
                    rootLogin ??= response;
                    rootTransitionSeen = true;
                }
                else
                {
                    username ??= response;
                }
            }

            if (IsPasswordPrompt(prompt))
            {
                if (rootTransitionSeen || password is not null) rootPassword ??= response;
                else password = response;
            }
        }
        return new KittyScriptData(script, username, password, rootLogin, rootPassword);
    }

    internal static string? NormalizeRootCommand(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var firstCommand = value.Replace("\\n", "\n", StringComparison.Ordinal)
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(command => command.Trim())
            .FirstOrDefault(command => command.Length > 0);
        if (firstCommand is null) return null;
        var normalized = string.Join(' ', firstCommand.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return normalized.Equals("su", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("su -", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("sudo -i", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("sudo su", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("sudo su -", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : null;
    }

    /// <summary>
    /// Encrypts a plaintext login script for storage in KiTTY's ScriptfileContent
    /// field. KiTTY 0.76.1.13 expects this field to be encrypted with the "9bis" key
    /// and uses null bytes as line separators internally.
    /// Uses Latin1 encoding to ensure byte-exact round-trip for any password.
    /// </summary>
    public static string EncryptScriptContent(string plaintextScript)
    {
        var withNulls = plaintextScript.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Replace('\n', '\0');
        return EncryptBytes(Encoding.Latin1.GetBytes(withNulls), Encoding.UTF8.GetBytes(ScriptKeys[0]));
    }

    private static bool IsUsernamePrompt(string prompt) =>
        prompt.Contains("login", StringComparison.Ordinal) ||
        prompt.Contains("username", StringComparison.Ordinal) ||
        prompt.Contains("user name", StringComparison.Ordinal) ||
        prompt.Contains("имя пользователя", StringComparison.Ordinal);

    private static bool IsPasswordPrompt(string prompt) =>
        prompt.Contains("password", StringComparison.Ordinal) ||
        prompt.EndsWith("assword", StringComparison.Ordinal) ||
        prompt.Contains("passwd", StringComparison.Ordinal) ||
        prompt.Contains("passphrase", StringComparison.Ordinal) ||
        prompt.Contains("пароль", StringComparison.Ordinal);

    private static bool TryDecrypt(string encrypted, byte[] key, out byte[] result)
    {
        result = [];
        if (encrypted.Length <= 5 || key.Length == 0) return false;

        var alphabet = Encoding.ASCII.GetBytes(Base64Alphabet);
        var input = Encoding.ASCII.GetBytes(encrypted.Where(value => value != '\r' && value != '\n').ToArray());
        if (input.Length <= 5 || input.Any(value => Array.IndexOf(alphabet, value) < 0)) return false;

        Scramble(alphabet, input.AsSpan(0, 5));
        var output = new List<byte>(input.Length - 5);
        var decodedCount = 0;
        for (var inputIndex = 5; inputIndex < input.Length;)
        {
            var value = 0;
            while (inputIndex < input.Length && input[inputIndex] == alphabet[^1])
            {
                value += alphabet.Length - 1;
                inputIndex++;
                Scramble(alphabet, key);
            }
            if (inputIndex >= input.Length) return false;

            var digit = Array.IndexOf(alphabet, input[inputIndex++]);
            if (digit < 0 || value + digit > byte.MaxValue) return false;
            output.Add((byte)(value + digit));
            decodedCount++;
            if (decodedCount < alphabet.Length) continue;
            decodedCount = 0;
            Scramble(alphabet, key);
        }

        result = output.ToArray();
        return true;
    }

    private static void Scramble(byte[] alphabet, ReadOnlySpan<byte> key)
    {
        var passes = (key.Length / 2 / alphabet.Length) + 1;
        for (var pass = 0; pass < passes; pass++)
        {
            var keyIndex = 0;
            for (var index = 0; index < alphabet.Length; index++)
            {
                var swapIndex = (key[keyIndex] + index) % alphabet.Length;
                (alphabet[index], alphabet[swapIndex]) = (alphabet[swapIndex], alphabet[index]);
                keyIndex++;
                if (keyIndex == key.Length) keyIndex = 0;
            }
        }
    }

    private static string DecodeText(byte[] bytes)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return new UTF8Encoding(false, true).GetString(bytes).TrimEnd('\0');
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1251).GetString(bytes).TrimEnd('\0');
        }
    }

    private static bool IsUsableCredential(string value) =>
        value.Length > 0 && value.All(character => !char.IsControl(character));
}

internal sealed record KittyScriptData(
    string Content,
    string? Username,
    string? Password,
    string? RootLogin,
    string? RootPassword);
