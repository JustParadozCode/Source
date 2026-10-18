using deobfuscator.Chunk;
using deobfuscator.Peel;

namespace deobfuscator.Lift;

/// <summary>
/// Optional pass: recover original string literals encrypted by the obfuscator's
/// string encryption (77main/Rewriters/StringToLocal.cs +
/// 77main/Obfuscator/Encryption/ConstantEncryption.cs).
///
/// Every source string literal was rewritten to `(Strings_Table[i])`, and the
/// table was built in the main chunk as
///   `(DecryptStringForm("<keybytes>","<encbytes>",L))`
/// per entry, with one shared random key table: `plain[j] = enc[j] ^ key[j % L]`.
/// In the lifted bytecode each entry is a fixed 5-instruction shape:
///
///   MOVE    f, decryptFn        (optional for matching; left in place)
///   LOADK   f+1, keyString
///   LOADK   f+2, encString
///   LOADK   f+3, L
///   CALL    f, 4, 2             (3 args, 1 result)
///
/// The pass emulates the XOR statically and rewrites the 4-instruction argument/
/// call sequence to a single direct LOADK of the recovered literal (plus inert
/// NOPs in the remaining slots so jump targets stay valid). Matching is
/// conservative: L must equal the byte length of the key string, and every match
/// in a proto must use the same key constant. Anything else is left untouched.
/// </summary>
public static class StringRecovery
{
    public static int Run(LiftedProto root, List<string> report)
    {
        var total = RunProto(root, "main", report);
        report.Add($"string recovery: {total} literal(s) recovered");
        return total;
    }

    private static int RunProto(LiftedProto p, string path, List<string> report)
    {
        var recovered = 0;
        var code = p.Code;

        string? Str(int constIdx) =>
            constIdx >= 0 && constIdx < p.Constants.Count
            && p.Constants[constIdx] is { Type: ConstantKind.String, Tampered: false } c
                ? (string)c.Data!
                : null;

        double? Num(int constIdx) =>
            constIdx >= 0 && constIdx < p.Constants.Count
            && p.Constants[constIdx] is { Type: ConstantKind.Number } c
                ? (double)c.Data!
                : null;

        // first pass: collect candidate call sites; all must share one key constant
        var sites = new List<(int Pos, int FnReg, int KeyConst, string Key, string Enc)>();
        for (var i = 0; i + 3 < code.Count; i++)
        {
            if (code[i].Op != LuaOp.LoadK || code[i + 1].Op != LuaOp.LoadK
                || code[i + 2].Op != LuaOp.LoadK || code[i + 3].Op != LuaOp.Call)
                continue;
            var call = code[i + 3];
            if (call.B != 4 || call.C != 2) continue; // exactly 3 args, 1 result
            if (code[i].A != call.A + 1 || code[i + 1].A != call.A + 2 || code[i + 2].A != call.A + 3)
                continue;
            var key = Str(code[i].Bx);
            var enc = Str(code[i + 1].Bx);
            var len = Num(code[i + 2].Bx);
            if (key == null || enc == null || len is not { } l) continue;
            if (l != Math.Floor(l) || l < 1 || l > 4096 || (int)l != key.Length) continue;
            if (enc.Length == 0 || enc.Length > (int)l) continue;
            sites.Add((i, call.A, code[i].Bx, key, enc));
        }

        if (sites.Count > 0 && sites.All(s => s.KeyConst == sites[0].KeyConst))
        {
            var removed = new bool[code.Count];
            foreach (var (pos, fnReg, _, key, enc) in sites)
            {
                var bytes = new byte[enc.Length];
                for (var j = 0; j < bytes.Length; j++)
                    bytes[j] = (byte)(enc[j] ^ key[j % key.Length]);
                var literal = Layers.Latin1.GetString(bytes);

                p.Constants.Add(new Constant { Type = ConstantKind.String, Data = literal });
                code[pos] = new LiftedIns
                {
                    Op = LuaOp.LoadK, A = fnReg, Bx = p.Constants.Count - 1, OldPC = code[pos].OldPC,
                };
                // mark the argument loads + call for removal
                for (var k = 1; k <= 3 && pos + k < code.Count; k++)
                    removed[pos + k] = true;
                // and the leading `MOVE fnReg, decryptFn` if present
                if (pos > 0 && !removed[pos - 1]
                    && code[pos - 1].Op == LuaOp.Move && code[pos - 1].A == fnReg)
                    removed[pos - 1] = true;
                recovered++;
            }
            if (recovered > 0)
            {
                Compact(p, removed, report, path);
                report.Add($"{path}: recovered {recovered} encrypted string literal(s)");
            }
        }
        else if (sites.Count > 0)
        {
            report.Add($"{path}: {sites.Count} decryption-shaped call sites with inconsistent " +
                       "key constants; left untouched");
        }

        foreach (var child in p.Children)
        {
            var idx = p.Children.IndexOf(child);
            recovered += RunProto(child, $"{path}/{idx}", report);
        }
        return recovered;
    }

    /// <summary>
    /// Physically removes the instructions the recovery rewrite made dead (the
    /// decrypt-fn MOVE and the argument/call slots) and compacts the code array,
    /// remapping all AsBx jump targets. Only slots flagged by the rewrite are
    /// removed; everything else (cond+JMP pairs, the dead RETURN after TAILCALL,
    /// the trailing RETURN) is untouched by construction.
    /// </summary>
    private static void Compact(LiftedProto p, bool[] removed, List<string> report, string path)
    {
        var code = p.Code;
        var newIdx = new int[code.Count];
        var w = 0;
        for (var i = 0; i < code.Count; i++)
            if (!removed[i])
                newIdx[i] = w++;

        var retargeted = 0;
        int Resolve(int target)
        {
            // a jump should never point into a removed slot (the decryption
            // entries were straight-line table-constructor code); if it ever
            // does, forward to the next surviving instruction
            var t = target;
            while (t < code.Count && removed[t])
            {
                t++;
                retargeted++;
            }
            return t < code.Count ? newIdx[t] : w;
        }

        var compacted = new List<LiftedIns>(w);
        for (var i = 0; i < code.Count; i++)
        {
            if (removed[i]) continue;
            var ins = code[i];
            if (LuaOp.IsAsBx(ins.Op))
                ins.Target = Resolve(ins.Target);
            compacted.Add(ins);
        }
        if (retargeted > 0)
            report.Add($"{path}: note: {retargeted} jump target(s) pointed into removed decryption slots; forwarded");
        p.Code = compacted;
    }
}
