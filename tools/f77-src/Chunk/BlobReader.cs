using System.Text.RegularExpressions;
using deobfuscator.Peel;

namespace deobfuscator.Chunk;

/// <summary>
/// Layer 3: deserialize the XORed bytecode blob into the Proto IR.
///
/// Ground truth: 77main/Bytecode Library/Bytecode/Serializer.cs (writer) and the
/// runtime reader emitted in 77main/Obfuscator/VM Generation/Generator.cs.
///
/// Every blob byte is XORed with the 1-byte PrimaryXorKey. Each chunk (and all
/// nested chunks, in the same per-build shuffled order) consists of 4 real
/// sections: ParameterCount, Constants, Instructions, Functions. The section
/// order and the constant-type tag mapping are per-build random permutations,
/// so both are brute-forced (24 x 24 combos, x 256 keys if layer 2 failed);
/// the winning combination must consume the whole blob exactly.
/// </summary>
public static class BlobReader
{
    public enum Section
    {
        ParameterCount,
        Constants,
        Instructions,
        Functions,
    }

    public sealed class Result
    {
        public required Proto Root;
        public required byte XorKey;
        public required Section[] Order;
        /// <summary>Mapping from ConstantKind (0-3) to written tag byte.</summary>
        public required int[] ConstantMapping;
        /// <summary>How many (key, order, mapping) combos parsed cleanly.</summary>
        public required int CandidateCount;
        public required bool KeyWasBruteForced;
    }

    private sealed class ParseFail : Exception
    {
        public ParseFail(string what) : base(what) { }
    }

    /// <param name="preferredKeys">
    /// Keys to try first (e.g. recovered by layer 2). Remaining keys 0-255 are
    /// still tried afterwards unless <paramref name="onlyPreferred"/> is set.
    /// </param>
    /// <param name="vmScript">
    /// The VM script (layer-1 output). Only used when no v1 combination parses:
    /// the v2 instruction-record layout is extracted from the script's embedded
    /// runtime deserializer.
    /// </param>
    public static Result Deserialize(byte[] blob, IReadOnlyList<int>? preferredKeys,
        bool onlyPreferred = false, string? vmScript = null)
    {
        var preferred = preferredKeys ?? Array.Empty<int>();
        var keys = onlyPreferred
            ? preferred
            : preferred.Concat(Enumerable.Range(0, 256).Where(k => !preferred.Contains(k)));

        var orders = Permutations(
            new[] { Section.ParameterCount, Section.Constants, Section.Instructions, Section.Functions }).ToList();
        var mappings = Permutations(new[] { 0, 1, 2, 3 }).ToList();

        var successes = new List<(Result Res, long Score)>();

        var keyList = keys.ToList();
        for (var ki = 0; ki < keyList.Count; ki++)
        {
            // Once a layer-2 key parses cleanly, don't bother brute-forcing the rest.
            if (ki >= preferred.Count && successes.Count > 0)
                break;

            var key = keyList[ki];
            var data = new byte[blob.Length];
            for (var i = 0; i < blob.Length; i++)
                data[i] = (byte)(blob[i] ^ key);

            foreach (var order in orders)
            {
                foreach (var mapping in mappings)
                {
                    try
                    {
                        var reader = new Reader(data, (byte)key);
                        // mapping[type] = tag; invert to tag -> type
                        var typeForTag = new int[4];
                        for (var t = 0; t < 4; t++)
                            typeForTag[mapping[t]] = t;

                        var root = reader.ReadChunk(order, typeForTag, depth: 0);
                        if (reader.Position != data.Length)
                            throw new ParseFail("trailing bytes");

                        var res = new Result
                        {
                            Root = root,
                            XorKey = (byte)key,
                            Order = order,
                            ConstantMapping = mapping,
                            CandidateCount = 0,
                            KeyWasBruteForced = ki >= preferred.Count,
                        };
                        successes.Add((res, Score(root)));
                    }
                    catch (ParseFail)
                    {
                        // wrong combination
                    }
                }
            }
        }

        // ---- format v2 fallback ------------------------------------------------
        if (successes.Count == 0 && vmScript != null
            && V2Layout.Extract(vmScript) is { } layout)
        {
            // v2 constant tags seen hardcoded as 0=bool,1=number,2=string; the tag
            // assignment is still brute-forced over the 6 permutations of the three
            // payload kinds (ConstantKind.Nil is never written by this build).
            // The on-disk order of the 5 payload fields is brute-forced too
            // (the flattener's state machines obscure it in the reader source).
            var v2Mappings = Permutations(new[]
                { (int)ConstantKind.Boolean, (int)ConstantKind.Number, (int)ConstantKind.String }).ToList();
            var fieldOrders = Permutations(layout.Fields.ToArray()).ToList();
            for (var ki = 0; ki < keyList.Count; ki++)
            {
                if (ki >= preferred.Count && successes.Count > 0)
                    break;
                var key = keyList[ki];
                var data = new byte[blob.Length];
                for (var i = 0; i < blob.Length; i++)
                    data[i] = (byte)(blob[i] ^ key);

                foreach (var fieldOrder in fieldOrders)
                foreach (var order in orders)
                {
                    foreach (var mapping in v2Mappings)
                    {
                        try
                        {
                            var reader = new Reader(data, (byte)key);
                            // mapping[tag] = ConstantKind, used directly
                            var root = reader.ReadChunkV2(order, mapping, fieldOrder.ToList(), depth: 0);
                            if (reader.Position != data.Length)
                                throw new ParseFail("trailing bytes");
                            successes.Add((new Result
                            {
                                Root = root,
                                XorKey = (byte)key,
                                Order = order,
                                ConstantMapping = mapping,
                                CandidateCount = 0,
                                KeyWasBruteForced = ki >= preferred.Count,
                            }, Score(root)));
                        }
                        catch (ParseFail)
                        {
                            // wrong combination
                        }
                    }
                }
            }
        }

        if (successes.Count == 0)
            throw new InvalidDataException("No (key, section order, constant mapping) combination parsed the blob.");

        foreach (var s in successes)
            s.Res.CandidateCount = successes.Count;

        Candidates = successes
            .OrderByDescending(s => s.Score)
            .Select(s => (s.Res.XorKey, Order: s.Res.Order, Mapping: s.Res.ConstantMapping, s.Score))
            .ToList();

        var best = successes.OrderByDescending(s => s.Score).First();
        // v2 field orders cannot be distinguished by consumption (all records have
        // the same size); if several v2 candidates tie on score, keep them for
        // semantic disambiguation after handler classification.
        PendingTies = best.Res.ConstantMapping.Length == 3
                      && successes.Count(s => s.Score == best.Score) > 1
            ? successes.Where(s => s.Score == best.Score).Select(s => s.Res).ToList()
            : null;
        return best.Res;
    }

    /// <summary>
    /// Tied top-scoring candidates from the last Deserialize (v2 field-order
    /// ambiguity), or null when the winner was clear.
    /// </summary>
    public static IReadOnlyList<Result>? PendingTies { get; private set; }

    /// <summary>Re-pick among <see cref="PendingTies"/> using a semantic scorer.</summary>
    public static Result Disambiguate(Func<Proto, long> scorer)
    {
        if (PendingTies is not { Count: > 1 } ties)
            throw new InvalidOperationException("no tied candidates");
        var best = ties.OrderByDescending(r => scorer(r.Root)).First();
        PendingTies = null;
        return best;
    }

    /// <summary>All cleanly-parsing combinations from the last Deserialize call, best score first.</summary>
    public static IReadOnlyList<(byte Key, Section[] Order, int[] Mapping, long Score)> Candidates { get; private set; }
        = Array.Empty<(byte, Section[], int[], long)>();

    /// <summary>
    /// Wire-format v2, seen in the wild (watermark "77fuscator 0.6.1 EARLY BUILD",
    /// prefix-less LZW+base36 raw blob). Derived from the variant's own runtime
    /// deserializer embedded in the VM script:
    ///   - every byte is XORed with the primary key (readers XOR on read);
    ///   - chunk sections (this build: Functions -> Instructions -> ParameterCount
    ///     -> Constants; brute-forced like v1);
    ///   - instruction records are UNIFORM (no type branching): descriptor u8
    ///     (bit0 set = data slot, no payload), then enum = ULEB128, then five
    ///     payload fields — four i32 and one i16 — stored under per-build numeric
    ///     instruction-table keys (OP_A/OP_B/OP_C/OP_E/OP_F);
    ///   - constants: i32 count, tags 0=bool(u8) 1=number(f64) 2=string(flag u8;
    ///     1 = tampered crash table, else ULEB128 byte length + XORed bytes);
    ///     no Nil tag in this build;
    ///   - ParameterCount: u16 ^ key (same as v1);
    ///   - Functions: ULEB128 count, recursive, 0-based.
    /// The payload field (key,width) set is recovered from the VM script's
    /// instruction-constructor expression; the on-disk ORDER of the fields is
    /// obscured by the flattener's state machines, so it is brute-forced
    /// (5! = 120 permutations) during parsing.
    /// </summary>
    public sealed class V2Layout
    {
        public required List<(int Key, bool Is16)> Fields;

        /// <summary>
        /// Finds the instruction constructor in the (flattened) runtime
        /// deserializer (`<var>={[kEnum]=<leb>(),[k0]=<i32>()}` plus nearby
        /// `<var>[k]=<reader>()` assignments) and returns the unordered payload
        /// field set. The i16 field is the one whose reader name differs from
        /// the i32 reader used inside the constructor.
        /// </summary>
        public static V2Layout? Extract(string vmScript)
        {
            var ctor = Regex.Match(vmScript,
                @"([A-Za-z_]\w*)\s*=\s*\{\s*\[(\d+)\]\s*=\s*([A-Za-z_]\w*)\(\)\s*,\s*\[(\d+)\]\s*=\s*([A-Za-z_]\w*)\(\)\s*,?\s*\}");
            if (!ctor.Success) return null;
            var tableVar = ctor.Groups[1].Value;
            var i32Reader = ctor.Groups[5].Value;

            var fields = new Dictionary<int, bool> { [int.Parse(ctor.Groups[4].Value)] = false };
            // field assignments may precede or follow the constructor textually
            var from = Math.Max(0, ctor.Index - 300);
            var window = vmScript.Substring(from,
                Math.Min(ctor.Index + ctor.Length + 300, vmScript.Length) - from);
            foreach (Match m in Regex.Matches(window,
                Regex.Escape(tableVar) + @"\s*\[(\d+)\]\s*=\s*([A-Za-z_]\w*)\(\)"))
            {
                var key = int.Parse(m.Groups[1].Value);
                var reader = m.Groups[2].Value;
                if (reader == i32Reader)
                    fields[key] = false;
                else
                    fields[key] = true; // the odd reader out: i16
            }
            return fields.Count == 5
                ? new V2Layout { Fields = fields.Select(kv => (kv.Key, kv.Value)).ToList() }
                : null;
        }
    }

    /// <summary>
    /// Heuristic score used when more than one combination consumes the blob
    /// exactly: prefer parses with more instructions and printable string
    /// constants (a real program), and small parameter counts.
    /// </summary>
    private static long Score(Proto p)
    {
        long score = p.Instructions.Count(i => !i.IsData) * 4L;
        foreach (var c in p.Constants)
        {
            if (c.Type == ConstantKind.String && !c.Tampered && c.Data is string s
                && s.All(ch => ch is >= ' ' and <= '~'))
                score += 20 + s.Length;
            if (c.Type == ConstantKind.Number)
                score += 2;
        }
        if (p.ParamCount > 32)
            score -= 50;
        foreach (var child in p.Children)
            score += Score(child);
        return score;
    }

    private static IEnumerable<T[]> Permutations<T>(T[] items)
    {
        if (items.Length <= 1)
        {
            yield return items;
            yield break;
        }
        for (var i = 0; i < items.Length; i++)
        {
            var rest = items.Where((_, idx) => idx != i).ToArray();
            foreach (var perm in Permutations(rest))
                yield return new[] { items[i] }.Concat(perm).ToArray();
        }
    }

    private sealed class Reader
    {
        private readonly byte[] _data;
        private readonly byte _key;
        public int Position;

        public Reader(byte[] data, byte key)
        {
            _data = data;
            _key = key;
        }

        private int Remaining => _data.Length - Position;

        private byte U8()
        {
            if (Position >= _data.Length) throw new ParseFail("u8 overrun");
            return _data[Position++];
        }

        private ushort U16()
        {
            if (Remaining < 2) throw new ParseFail("u16 overrun");
            var v = (ushort)(_data[Position] | (_data[Position + 1] << 8));
            Position += 2;
            return v;
        }

        private short I16() => unchecked((short)U16());

        private int I32()
        {
            if (Remaining < 4) throw new ParseFail("i32 overrun");
            var v = _data[Position]
                    | (_data[Position + 1] << 8)
                    | (_data[Position + 2] << 16)
                    | (_data[Position + 3] << 24);
            Position += 4;
            return v;
        }

        private double F64()
        {
            if (Remaining < 8) throw new ParseFail("f64 overrun");
            var v = BitConverter.Int64BitsToDouble(
                _data[Position]
                | ((long)_data[Position + 1] << 8)
                | ((long)_data[Position + 2] << 16)
                | ((long)_data[Position + 3] << 24)
                | ((long)_data[Position + 4] << 32)
                | ((long)_data[Position + 5] << 40)
                | ((long)_data[Position + 6] << 48)
                | ((long)_data[Position + 7] << 56));
            Position += 8;
            return v;
        }

        private int U24()
        {
            if (Remaining < 3) throw new ParseFail("u24 overrun");
            var v = _data[Position] | (_data[Position + 1] << 8) | (_data[Position + 2] << 16);
            Position += 3;
            return v;
        }

        private long ULEB128()
        {
            long result = 0;
            var shift = 0;
            for (var i = 0; i < 10; i++)
            {
                var b = U8();
                result |= (long)(b & 0x7F) << shift;
                if ((b & 0x80) == 0)
                    return result;
                shift += 7;
            }
            throw new ParseFail("ULEB128 too long");
        }

        public Proto ReadChunk(Section[] order, int[] typeForTag, int depth)
        {
            if (depth > 32) throw new ParseFail("recursion too deep");

            var proto = new Proto();

            foreach (var section in order)
            {
                switch (section)
                {
                    case Section.ParameterCount:
                        // Written as i16 of (paramCount ^ key); reader XORs again.
                        proto.ParamCount = U16() ^ _key;
                        if (proto.ParamCount > 1024) throw new ParseFail("param count insane");
                        break;

                    case Section.Constants:
                        {
                            var count = I32();
                            if (count < 0 || count > Remaining) throw new ParseFail("constant count insane");
                            for (var i = 0; i < count; i++)
                            {
                                var tag = U8();
                                if (tag > 3) throw new ParseFail("bad constant tag");
                                var constant = new Constant { Type = (ConstantKind)typeForTag[tag] };
                                switch (constant.Type)
                                {
                                    case ConstantKind.Nil:
                                        break;
                                    case ConstantKind.Boolean:
                                        constant.Data = U8() != 0;
                                        break;
                                    case ConstantKind.Number:
                                        constant.Data = F64();
                                        break;
                                    case ConstantKind.String:
                                        {
                                            var flag = U8();
                                            if (flag == 1)
                                            {
                                                constant.Tampered = true;
                                                break;
                                            }
                                            if (flag != 0) throw new ParseFail("bad string flag");
                                            var len = I32();
                                            if (len < 0 || len > Remaining) throw new ParseFail("string length insane");
                                            constant.Data = Layers.Latin1.GetString(_data, Position, len);
                                            Position += len;
                                            break;
                                        }
                                }
                                proto.Constants.Add(constant);
                            }
                            break;
                        }

                    case Section.Instructions:
                        {
                            var count = ULEB128();
                            if (count > Remaining / 4) throw new ParseFail("instruction count insane");
                            for (var i = 0; i < count; i++)
                            {
                                var d = U8();
                                var inst = new Instruction { PC = i };
                                if ((d & 1) != 0)
                                {
                                    inst.IsData = true;
                                    proto.Instructions.Add(inst);
                                    continue;
                                }

                                var type = (d >> 1) & 7;
                                if (type > 3) throw new ParseFail("bad instruction type");
                                inst.Type = (InstructionKind)type;

                                var enumValue = ULEB128();
                                if (enumValue > 4096) throw new ParseFail("enum insane");
                                inst.Enum = (int)enumValue;

                                inst.A = I16();
                                switch (inst.Type)
                                {
                                    case InstructionKind.ABC:
                                        inst.B = U24();
                                        inst.C = U24();
                                        inst.E = I16();
                                        inst.F = I32();
                                        break;
                                    case InstructionKind.ABx:
                                        inst.B = I32();
                                        break;
                                    case InstructionKind.AsBx:
                                        inst.B = I32() - 65536;
                                        break;
                                    case InstructionKind.AsBxC:
                                        inst.B = I32() - 65536;
                                        inst.C = U24();
                                        inst.E = I16();
                                        inst.F = I32();
                                        break;
                                }
                                proto.Instructions.Add(inst);
                            }
                            break;
                        }

                    case Section.Functions:
                        {
                            var count = ULEB128();
                            if (count > Remaining / 8) throw new ParseFail("function count insane");
                            for (var i = 0; i < count; i++)
                                proto.Children.Add(ReadChunk(order, typeForTag, depth + 1));
                            break;
                        }
                }
            }

            return proto;
        }

        /// <summary>
        /// Format v2 chunk reader (see <see cref="V2Layout"/>). mapping[tag] =
        /// ConstantKind for the three written tags; tag 2 strings carry a flag
        /// byte (1 = tampered decoy, else ULEB128 length + bytes).
        /// </summary>
        public Proto ReadChunkV2(Section[] order, int[] tagToKind, List<(int Key, bool Is16)> fieldOrder, int depth)
        {
            if (depth > 32) throw new ParseFail("recursion too deep");

            var proto = new Proto();

            foreach (var section in order)
            {
                switch (section)
                {
                    case Section.ParameterCount:
                        proto.ParamCount = U16() ^ _key;
                        if (proto.ParamCount > 1024) throw new ParseFail("param count insane");
                        break;

                    case Section.Constants:
                        {
                            var count = I32();
                            if (count < 0 || count > Remaining) throw new ParseFail("constant count insane");
                            for (var i = 0; i < count; i++)
                            {
                                var tag = U8();
                                if (tag >= tagToKind.Length) throw new ParseFail("bad constant tag");
                                var constant = new Constant { Type = (ConstantKind)tagToKind[tag] };
                                switch (constant.Type)
                                {
                                    case ConstantKind.Boolean:
                                        constant.Data = U8() != 0;
                                        break;
                                    case ConstantKind.Number:
                                        constant.Data = F64();
                                        break;
                                    case ConstantKind.String:
                                        {
                                            var flag = U8();
                                            if (flag == 1)
                                            {
                                                constant.Tampered = true;
                                                break;
                                            }
                                            if (flag != 0) throw new ParseFail("bad string flag");
                                            var len = ULEB128();
                                            if (len > Remaining) throw new ParseFail("string length insane");
                                            constant.Data = Layers.Latin1.GetString(_data, Position, (int)len);
                                            Position += (int)len;
                                            break;
                                        }
                                    default:
                                        throw new ParseFail("nil tag in v2 blob");
                                }
                                proto.Constants.Add(constant);
                            }
                            break;
                        }

                    case Section.Instructions:
                        {
                            var count = ULEB128();
                            if (count > Remaining / 8) throw new ParseFail("instruction count insane");
                            for (var i = 0; i < count; i++)
                            {
                                var d = U8();
                                var inst = new Instruction { PC = i, Type = InstructionKind.ABC };
                                if ((d & 1) != 0)
                                {
                                    inst.IsData = true;
                                    proto.Instructions.Add(inst);
                                    continue;
                                }

                                var enumValue = ULEB128();
                                if (enumValue > 4096) throw new ParseFail("enum insane");
                                inst.Enum = (int)enumValue;
                                inst.Fields = new Dictionary<int, int>(fieldOrder.Count);
                                foreach (var (fieldKey, is16) in fieldOrder)
                                    inst.Fields[fieldKey] = is16 ? I16() : I32();
                                proto.Instructions.Add(inst);
                            }
                            break;
                        }

                    case Section.Functions:
                        {
                            var count = ULEB128();
                            if (count > Remaining / 8) throw new ParseFail("function count insane");
                            for (var i = 0; i < count; i++)
                                proto.Children.Add(ReadChunkV2(order, tagToKind, fieldOrder, depth + 1));
                            break;
                        }
                }
            }

            return proto;
        }
    }
}
