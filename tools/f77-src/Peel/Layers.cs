using System.Text;
using System.Text.RegularExpressions;

namespace deobfuscator.Peel;

/// <summary>
/// Layer 1: undo the obfuscator's outer wrapping.
///
/// Two possible shapes (see 77main/Obfuscator/VM Generation/Compression.cs and
/// 77main/Obfuscator/VM Generation/Generator.cs):
///  - Uncompressed: the script text itself IS the VM script and contains a
///    string literal "77FUS|&lt;UPPERCASE HEX&gt;".
///  - Compressed (ExtraCompression, default): the WHOLE VM script text was
///    LZW-compressed and base36-encoded; the outer script contains
///    decompress('77FUS|&lt;base36 payload&gt;'). After decoding, the inner VM
///    script contains the hex "77FUS|" blob.
/// </summary>
public static class Layers
{
    private const string Magic = "77FUS|";
    private const string Base36Digits = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    public static readonly Encoding Latin1 = Encoding.GetEncoding(28591);

    private static readonly Regex PayloadRegex =
        new Regex(@"77FUS\|([0-9A-Z]+)", RegexOptions.Compiled);

    private static bool IsPureHex(string s) =>
        s.Length % 2 == 0 && s.All(c => c is (>= '0' and <= '9') or (>= 'A' and <= 'F'));

    /// <summary>
    /// Finds the first 77FUS| payload. Returns (payload, isHexBlob).
    /// A pure-uppercase-hex, even-length payload is the bytecode blob itself;
    /// anything else is the LZW/base36-compressed inner VM script.
    /// </summary>
    private static (string Payload, bool IsHex)? FindPayload(string script)
    {
        foreach (Match m in PayloadRegex.Matches(script))
        {
            var payload = m.Groups[1].Value;
            if (payload.Length == 0) continue;
            // A compressed payload that happens to be all-hex chars of even
            // length is ambiguous; the hex blob is always long, so prefer the
            // longest match overall (regex is greedy per match anyway).
            return (payload, IsPureHex(payload));
        }
        return null;
    }

    /// <summary>
    /// If the script is in the compressed outer form, decode it and return the
    /// inner VM script; otherwise return the script unchanged.
    /// </summary>
    public static string DecompressIfNeeded(string script, out bool wasCompressed)
    {
        wasCompressed = false;
        var found = FindPayload(script);
        if (found is not { } f || f.IsHex)
            return script;

        // Trust seed 256 first (that is what the compressor in Compression.cs
        // actually uses). The obfuscator's own runtime decompressor seeds its
        // dictionary 0-207 (first new code 208) due to an arithmetic quirk on
        // _VERSION; keep that as a fallback if the 256 decode doesn't yield a
        // script containing the hex blob.
        foreach (var seed in new[] { 256, 208 })
        {
            string? decoded = TryDecode(f.Payload, seed);
            if (decoded != null && FindPayload(decoded) is { IsHex: true })
            {
                wasCompressed = true;
                return decoded;
            }
        }

        // Last resort: return the 256 decode even if it looks wrong.
        var fallback = TryDecode(f.Payload, 256);
        if (fallback != null)
        {
            wasCompressed = true;
            return fallback;
        }

        throw new InvalidDataException("Found a compressed 77FUS| payload but LZW decoding failed.");
    }

    private static readonly Regex LongBase36StringRegex =
        new Regex(@"'([0-9A-Z]{1000,})'", RegexOptions.Compiled);

    /// <summary>
    /// Variant shape seen in the wild: the uncompressed VM wrapper
    /// (Generator.cs:602 argument list) but with the blob slot holding
    /// LZW+base36 of the RAW bytecode blob instead of "77FUS|&lt;hex&gt;" — no
    /// magic prefix anywhere. Detect long quoted base36 string arguments, LZW
    /// decode, and return the raw bytes as the blob. Returns null when the
    /// script doesn't match this variant.
    /// </summary>
    public static byte[]? TryExtractRawCompressedBlob(string script)
    {
        if (script.Contains(Magic)) return null;
        foreach (Match m in LongBase36StringRegex.Matches(script))
        {
            var payload = m.Groups[1].Value;
            var decoded = TryDecode(payload, 256) ?? TryDecode(payload, 208);
            if (decoded == null || decoded.Length == 0) continue;
            // TryDecode emits chars whose values are the original bytes.
            return Latin1.GetBytes(decoded);
        }
        return null;
    }

    /// <summary>
    /// Finds the "77FUS|&lt;HEX&gt;" blob in the (possibly decompressed) script
    /// and hex-decodes it.
    /// </summary>
    public static byte[] ExtractBlob(string script)
    {
        foreach (Match m in PayloadRegex.Matches(script))
        {
            var payload = m.Groups[1].Value;
            if (!IsPureHex(payload)) continue;
            var bytes = new byte[payload.Length / 2];
            for (var i = 0; i < bytes.Length; i++)
                bytes[i] = Convert.ToByte(payload.Substring(i * 2, 2), 16);
            return bytes;
        }
        throw new InvalidDataException("No hex 77FUS| blob found in script.");
    }

    private static int Base36Value(char c)
    {
        var v = Base36Digits.IndexOf(c);
        if (v < 0) throw new InvalidDataException($"Invalid base36 character '{c}'.");
        return v;
    }

    /// <summary>
    /// Token stream per CompressedToString: repeat { read 1 char L (base36),
    /// read next L chars as a base36 number = one LZW code }.
    /// </summary>
    private static List<int> Tokenize(string payload)
    {
        var codes = new List<int>();
        var i = 0;
        while (i < payload.Length)
        {
            var len = Base36Value(payload[i]);
            i++;
            if (len == 0 || i + len > payload.Length)
                throw new InvalidDataException("Corrupt base36 token stream.");
            long value = 0;
            for (var j = 0; j < len; j++)
                value = value * 36 + Base36Value(payload[i + j]);
            i += len;
            if (value > int.MaxValue) throw new InvalidDataException("LZW code out of range.");
            codes.Add((int)value);
        }
        return codes;
    }

    /// <summary>
    /// LZW decode. Dictionary seeded with codes 0..(seed-1) = single-byte
    /// strings; first new dictionary code = seed. Output characters are byte
    /// values (Latin-1), so the decoded string equals the original script text.
    /// </summary>
    private static string? TryDecode(string payload, int seed)
    {
        try
        {
            var codes = Tokenize(payload);
            if (codes.Count == 0) return null;

            var dict = new List<string>(seed + 256);
            for (var i = 0; i < seed; i++)
                dict.Add(((char)i).ToString());

            var sb = new StringBuilder();
            var first = codes[0];
            if (first >= dict.Count) return null;
            var w = dict[first];
            sb.Append(w);

            for (var i = 1; i < codes.Count; i++)
            {
                var code = codes[i];
                string entry;
                if (code < dict.Count)
                    entry = dict[code];
                else if (code == dict.Count) // KwKwK case
                    entry = w + w[0];
                else
                    return null; // desync

                sb.Append(entry);
                dict.Add(w + entry[0]);
                w = entry;
            }

            return sb.ToString();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
