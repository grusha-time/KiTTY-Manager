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
    // Marker written next to ScriptfileContent for manager-generated mixed
    // scripts. Native KiTTY Save drops unknown session keys, so the durable
    // format marker lives inside the encrypted payload instead; this key is
    // only an informational duplicate. KiTTY stores and ignores the key.
    internal const string ScriptEncodingMarkerKey = "KiTTYManagerScriptEncoding";
    // Appended inside ScriptfileContent after the caller's lines, followed by
    // two empty lines. KiTTY never sees the marker as a prompt after the last
    // answer was sent, and the trailing NULs terminate its NUL-shift loop.
    internal const string ScriptFormatMarker = "__KiTTYManagerScriptEncoding2__";
    // KiTTY 0.76.1.13 compares expected prompt lines with UTF-8 server output
    // (strstr), but sends response bytes through the Windows ANSI path
    // (SendKeyboard/WM_CHAR). New manager-written scripts therefore use mixed
    // role-based records; legacy scripts are decoded as one whole stream.
    private static readonly Encoding ScriptEncoding;
    private static readonly Encoding AnsiDecoding;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    static KittyCredentialDecoder()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        ScriptEncoding = Encoding.GetEncoding(1251, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback);
        AnsiDecoding = Encoding.GetEncoding(1251);
    }

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
        out string rewritten, string? expectedRootPrompt = null)
    {
        rewritten = storedContent;
        if (string.IsNullOrEmpty(storedContent) || string.IsNullOrEmpty(expectedOldPassword)) return false;

        if (LooksLikeLoginScript(storedContent))
        {
            var separator = storedContent.Contains('\0') ? '\0' : '\n';
            var records = storedContent.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split(separator);
            var plaintextCandidates = FindRootPasswordResponses(records, automaticCommand, expectedOldPassword, expectedRootPrompt);
            if (plaintextCandidates.Distinct().Count() != 1) return false;
            records[plaintextCandidates[0]] = newPassword;
            rewritten = string.Join(separator, records);
            return true;
        }

        foreach (var key in ScriptKeys)
        {
            if (!TryDecrypt(storedContent, Encoding.UTF8.GetBytes(key), out var bytes)) continue;
            var separator = Array.IndexOf(bytes, (byte)0) >= 0 ? (byte)0 : (byte)'\n';
            var segments = SplitSegments(bytes, separator).ToList();
            var trailerStart = FindTrailerStart(segments);
            var body = trailerStart >= 0 ? segments.Take(trailerStart).ToArray() : segments.ToArray();

            int index;
            byte[] replacement;
            if (trailerStart >= 0)
            {
                // Marked script: role-based analysis, keep the trailer bytes.
                var records = body.Select((segment, position) => position % 2 == 0
                    ? DecodePromptSegment(segment)
                    : AnsiDecoding.GetString(segment)).ToArray();
                if (!LooksLikeLoginScript(string.Join('\n', records))) continue;
                var candidates = FindRootPasswordResponses(records, automaticCommand, expectedOldPassword, expectedRootPrompt);
                if (candidates.Distinct().Count() != 1) return false;
                index = candidates[0];
                replacement = EncodeResponseText(newPassword);
            }
            else
            {
                // Legacy script: one shared prompt-anchored interpretation for
                // reading and rewriting.
                var legacy = DecodeLegacyRecords(bytes, separator);
                if (!LooksLikeLoginScript(string.Join('\n', legacy.Records))) continue;
                var candidates = FindRootPasswordResponses(
                    legacy.Records, automaticCommand, expectedOldPassword, expectedRootPrompt);
                if (candidates.Distinct().Count() != 1) return false;
                index = candidates[0];
                // KiTTY sends answers through its ANSI input path, so encode the
                // new answer with strict CP1251 first (may throw before any file
                // change). The representation is chosen from the same stream
                // evidence as the reader: CP1251 streams stay raw, while a UTF-8
                // stream uses ASCII \xHH escapes for answers that would otherwise
                // be reinterpreted. Escaped answers always end with "\n" so
                // upstream SendKeyboardPlus submits them with Enter.
                var ansiBytes = EncodeResponseText(newPassword);
                var ansiValidUtf8 = TryDecodeStrictUtf8(ansiBytes, out var reinterpreted);
                var ambiguous = ansiValidUtf8 &&
                                !string.Equals(reinterpreted, newPassword, StringComparison.Ordinal);
                var otherUtf8Answer = false;
                for (var position = 1; position < segments.Count; position += 2)
                {
                    if (position == index) continue;
                    if (!TryDecodeStrictUtf8(segments[position], out _)) continue;
                    if (!segments[position].Any(value => value >= 0x80)) continue;
                    otherUtf8Answer = true;
                    break;
                }
                if (legacy.Cp1251Whole)
                {
                    replacement = ansiBytes;
                }
                else if (ambiguous || (!ansiValidUtf8 && otherUtf8Answer))
                {
                    // The reader must not switch the whole stream to CP1251 and
                    // mangle untouched UTF-8 answers, so store the UTF-8 bytes
                    // as escapes and append the explicit Enter sentinel.
                    replacement = Encoding.ASCII.GetBytes(
                        EscapeBytes(Encoding.UTF8.GetBytes(newPassword)) + "\\n");
                }
                else
                {
                    // Non-empty ANSI buffer makes upstream send Enter itself.
                    replacement = ansiBytes;
                }
            }

            // Replace only the response segment; every other segment keeps its
            // original raw bytes so existing prompt/answer bytes stay untouched.
            segments[index] = replacement;
            rewritten = EncryptBytes(JoinSegments(segments.ToArray(), separator), Encoding.UTF8.GetBytes(key));
            return true;
        }
        return false;
    }

    internal static bool HasScriptFormatTrailer(string? storedContent)
    {
        if (string.IsNullOrWhiteSpace(storedContent) || LooksLikeLoginScript(storedContent)) return false;
        foreach (var key in ScriptKeys)
        {
            if (!TryDecrypt(storedContent, Encoding.UTF8.GetBytes(key), out var bytes)) continue;
            var separator = Array.IndexOf(bytes, (byte)0) >= 0 ? (byte)0 : (byte)'\n';
            if (FindTrailerStart(SplitSegments(bytes, separator).ToList()) >= 0) return true;
        }
        return false;
    }

    private readonly record struct LegacyRecords(string[] Records, bool Cp1251Whole);

    // Legacy encoding is a stream-level property, not a per-prompt guess. An
    // invalid prompt proves the genuine KiTTY script was CP1251, so the whole
    // stream (including answers whose bytes happen to be valid UTF-8) is
    // CP1251. With every prompt valid, an invalid segment plus another answer
    // that looks like non-ASCII UTF-8 also proves a CP1251 stream. Otherwise
    // only the manager's rewritten answer may legitimately be CP1251.
    private static LegacyRecords DecodeLegacyRecords(byte[] bytes, byte separator)
    {
        var segments = SplitSegments(bytes, separator);
        var count = segments.Length;
        while (count > 0 && segments[count - 1].Length == 0) count--;

        var promptInvalidUtf8 = false;
        var anyInvalidUtf8 = false;
        for (var index = 0; index < count; index++)
        {
            if (TryDecodeStrictUtf8(segments[index], out _)) continue;
            anyInvalidUtf8 = true;
            if (index % 2 == 0) promptInvalidUtf8 = true;
        }

        var cp1251Whole = promptInvalidUtf8;
        if (!cp1251Whole && anyInvalidUtf8)
        {
            for (var index = 1; index < count; index += 2)
            {
                if (!TryDecodeStrictUtf8(segments[index], out _)) continue;
                if (!segments[index].Any(value => value >= 0x80)) continue;
                cp1251Whole = true;
                break;
            }
        }

        var records = new string[count];
        for (var index = 0; index < count; index++)
        {
            var text = cp1251Whole || !TryDecodeStrictUtf8(segments[index], out var utf8)
                ? AnsiDecoding.GetString(segments[index])
                : utf8!;
            // KiTTY sends \xHH in a response through SendStrToTerminal as raw
            // bytes, so only responses need unescaping.
            records[index] = index % 2 == 1 ? UnescapeRawBytes(text) : text;
        }
        return new LegacyRecords(records, cp1251Whole);
    }

    private static bool TryDecodeStrictUtf8(byte[] bytes, out string? text)
    {
        try
        {
            text = StrictUtf8.GetString(bytes);
            return true;
        }
        catch (DecoderFallbackException)
        {
            text = null;
            return false;
        }
    }

    private static string UnescapeRawBytes(string text)
    {
        if (!text.Contains('\\')) return text;
        var bytes = new List<byte>(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            if (text[index] == '\\' && index + 1 < text.Length)
            {
                var next = text[index + 1];
                // Mirror SendKeyboardPlus: "\\" is one literal backslash and the
                // following characters are not re-examined as an escape, so
                // "\\x41" means the literal text "\x41", not "A".
                if (next == '\\')
                {
                    bytes.Add((byte)'\\');
                    index++;
                    continue;
                }
                if (next == 'x' && index + 3 < text.Length &&
                    IsHexDigit(text[index + 2]) && IsHexDigit(text[index + 3]))
                {
                    bytes.Add(Convert.ToByte(text.Substring(index + 2, 2), 16));
                    index += 3;
                    continue;
                }
                // KiTTY ends the answer with "\n": SendKeyboardPlus sends the
                // buffer plus Enter and clears it, so the trailing sentinel
                // submits the answer without becoming part of the password.
                if (next == 'n')
                {
                    bytes.Add((byte)'\n');
                    index++;
                    continue;
                }
            }
            if (char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(text.Substring(index, 2)));
                index++;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(text[index].ToString()));
            }
        }
        var raw = bytes.ToArray();
        return TryDecodeStrictUtf8(raw, out var decoded) ? decoded! : AnsiDecoding.GetString(raw);
    }

    private static bool IsHexDigit(char value) =>
        value is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';

    private static string EscapeBytes(byte[] bytes)
    {
        var builder = new StringBuilder(bytes.Length * 4);
        foreach (var value in bytes) builder.Append("\\x").Append(value.ToString("X2"));
        return builder.ToString();
    }

    private static int FindTrailerStart(IReadOnlyList<byte[]> segments)
    {
        // Structural signature only: [empty][MARKER][empty][empty] must be the
        // final four segments. A user answer equal to the marker never matches.
        if (segments.Count < 4) return -1;
        var markerIndex = segments.Count - 3;
        var marker = Encoding.ASCII.GetBytes(ScriptFormatMarker);
        if (!segments[markerIndex].AsSpan().SequenceEqual(marker)) return -1;
        if (segments[markerIndex - 1].Length != 0) return -1;
        if (segments[markerIndex + 1].Length != 0) return -1;
        if (segments[markerIndex + 2].Length != 0) return -1;
        return markerIndex - 1;
    }

    private static List<int> FindRootPasswordResponses(
        IReadOnlyList<string> records, string automaticCommand, string expectedOldPassword,
        string? expectedRootPrompt)
    {
        var candidates = new List<int>();
        var rootTransitionSeen = NormalizeRootCommand(automaticCommand) is not null;
        for (var index = 0; index + 1 < records.Count; index++)
        {
            var prompt = NormalizePrompt(records[index]);
            var response = records[index + 1].Trim();
            if (NormalizeRootCommand(response) is not null) { rootTransitionSeen = true; continue; }
            if (rootTransitionSeen && IsPasswordPrompt(prompt, expectedRootPrompt) && response == expectedOldPassword)
                candidates.Add(index + 1);
        }
        return candidates;
    }

    private static byte[] EncodeResponseText(string value)
    {
        try
        {
            return ScriptEncoding.GetBytes(value);
        }
        catch (EncoderFallbackException exception)
        {
            var character = exception.CharUnknownHigh != '\0'
                ? char.ConvertFromUtf32(char.ConvertToUtf32(exception.CharUnknownHigh, exception.CharUnknownLow))
                : exception.CharUnknown.ToString();
            throw new InvalidOperationException(
                $"Login script не записан: символ «{character}» нельзя представить в кодировке ANSI/1251, которую использует KiTTY. Уберите неподдерживаемые символы.",
                exception);
        }
    }

    private static string DecodePromptSegment(byte[] segment)
    {
        try { return StrictUtf8.GetString(segment); }
        catch (DecoderFallbackException) { return AnsiDecoding.GetString(segment); }
    }

    private static string DecodeScriptBytes(byte[] bytes, bool forceRoleEncoded)
    {
        var separator = Array.IndexOf(bytes, (byte)0) >= 0 ? (byte)0 : (byte)'\n';
        var segments = SplitSegments(bytes, separator).ToList();
        var trailerStart = FindTrailerStart(segments);
        if (trailerStart >= 0)
        {
            return string.Join('\n', segments.Take(trailerStart).Select((segment, index) =>
                index % 2 == 0 ? DecodePromptSegment(segment) : AnsiDecoding.GetString(segment)));
        }
        if (forceRoleEncoded)
        {
            return string.Join('\n', segments.Select((segment, index) =>
                index % 2 == 0 ? DecodePromptSegment(segment) : AnsiDecoding.GetString(segment)));
        }
        return string.Join('\n', DecodeLegacyRecords(bytes, separator).Records);
    }

    private static byte[][] SplitSegments(byte[] bytes, byte separator)
    {
        var segments = new List<byte[]>();
        var start = 0;
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] != separator) continue;
            segments.Add(bytes[start..index]);
            start = index + 1;
        }
        segments.Add(bytes[start..]);
        return segments.ToArray();
    }

    private static byte[] JoinSegments(byte[][] segments, byte separator)
    {
        var length = Math.Max(0, segments.Length - 1);
        foreach (var segment in segments) length += segment.Length;
        var output = new byte[length];
        var offset = 0;
        for (var index = 0; index < segments.Length; index++)
        {
            if (index > 0) output[offset++] = separator;
            segments[index].CopyTo(output, offset);
            offset += segments[index].Length;
        }
        return output;
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

    public static KittyScriptData? DecodeLoginScript(string storedContent, int cryptSaltMode,
        string? expectedRootPrompt = null, bool roleEncoded = false)
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
            var decoded = DecodeScriptBytes(bytes, roleEncoded);
            if (!LooksLikeLoginScript(decoded)) continue;
            script = decoded;
            break;
        }

        if (script is null) return null;
        return ExtractChallengeResponses(script, expectedRootPrompt);
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

    public static KittyScriptData? ReadLoginScriptFile(string path, string? expectedRootPrompt = null)
    {
        try
        {
            return ExtractChallengeResponses(DecodeScriptFile(File.ReadAllBytes(path)), expectedRootPrompt);
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

    internal static KittyScriptData ExtractChallengeResponses(string script, string? expectedRootPrompt = null)
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

            if (IsPasswordPrompt(prompt, rootTransitionSeen ? expectedRootPrompt : null))
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
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
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
    /// Script lines are prompt/response pairs: expected prompts are stored as
    /// UTF-8 (KiTTY compares them with UTF-8 server output), answers as strict
    /// CP1251 (KiTTY sends them through its ANSI input path). Characters outside
    /// CP1251 in an answer are rejected with a clear error.
    /// </summary>
    public static string EncryptScriptContent(string plaintextScript)
    {
        var lines = plaintextScript.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n').Split('\n').ToList();
        // Durable format trailer: an empty prompt terminates KiTTY's executable
        // body after the last answer, then the marker and two empty segments
        // keep the NUL-shift loop inside the buffer. Appended inside the
        // encrypted payload so native Save keeps it; skip only when the full
        // structural trailer is already present (never a marker-valued answer).
        var hasTrailer = lines.Count >= 4 &&
                         lines[^4].Length == 0 &&
                         lines[^3] == ScriptFormatMarker &&
                         lines[^2].Length == 0 &&
                         lines[^1].Length == 0;
        if (!hasTrailer)
        {
            lines.Add("");
            lines.Add(ScriptFormatMarker);
            lines.Add("");
            lines.Add("");
        }
        var segments = new byte[lines.Count][];
        for (var index = 0; index < lines.Count; index++)
            segments[index] = index % 2 == 0 ? Encoding.UTF8.GetBytes(lines[index]) : EncodeResponseText(lines[index]);
        return EncryptBytes(JoinSegments(segments, 0), Encoding.UTF8.GetBytes(ScriptKeys[0]));
    }

    private static bool IsUsernamePrompt(string prompt) =>
        prompt.Contains("login", StringComparison.Ordinal) ||
        prompt.Contains("username", StringComparison.Ordinal) ||
        prompt.Contains("user name", StringComparison.Ordinal) ||
        prompt.Contains("имя пользователя", StringComparison.Ordinal);

    // The configured root prompt is only trusted after the root transition was
    // seen, so an arbitrary user-defined text (e.g. "Введите код:") is never
    // mistaken for the initial SSH password prompt.
    private static bool IsPasswordPrompt(string prompt, string? expectedRootPrompt = null) =>
        prompt.Contains("password", StringComparison.Ordinal) ||
        prompt.EndsWith("assword", StringComparison.Ordinal) ||
        prompt.Contains("passwd", StringComparison.Ordinal) ||
        prompt.Contains("passphrase", StringComparison.Ordinal) ||
        prompt.Contains("пароль", StringComparison.Ordinal) ||
        expectedRootPrompt is not null && NormalizePrompt(expectedRootPrompt) == prompt;

    private static string NormalizePrompt(string value) =>
        value.Trim().TrimEnd(':').Trim().ToLowerInvariant();

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
