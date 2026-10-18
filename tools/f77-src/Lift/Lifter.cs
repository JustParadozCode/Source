using deobfuscator.Chunk;
using deobfuscator.Vm;

namespace deobfuscator.Lift;

/// <summary>Stock Lua 5.1 opcode numbers (lopcodes.h order).</summary>
public static class LuaOp
{
    public const int Move = 0, LoadK = 1, LoadBool = 2, LoadNil = 3, GetUpval = 4,
        GetGlobal = 5, GetTable = 6, SetGlobal = 7, SetUpval = 8, SetTable = 9,
        NewTable = 10, Self = 11, Add = 12, Sub = 13, Mul = 14, Div = 15, Mod = 16,
        Pow = 17, Unm = 18, Not = 19, Len = 20, Concat = 21, Jmp = 22, Eq = 23,
        Lt = 24, Le = 25, Test = 26, TestSet = 27, Call = 28, TailCall = 29,
        Return = 30, ForLoop = 31, ForPrep = 32, TForLoop = 33, SetList = 34,
        Close = 35, Closure = 36, VarArg = 37;

    public static readonly string[] Names =
    {
        "MOVE", "LOADK", "LOADBOOL", "LOADNIL", "GETUPVAL", "GETGLOBAL", "GETTABLE",
        "SETGLOBAL", "SETUPVAL", "SETTABLE", "NEWTABLE", "SELF", "ADD", "SUB", "MUL",
        "DIV", "MOD", "POW", "UNM", "NOT", "LEN", "CONCAT", "JMP", "EQ", "LT", "LE",
        "TEST", "TESTSET", "CALL", "TAILCALL", "RETURN", "FORLOOP", "FORPREP",
        "TFORLOOP", "SETLIST", "CLOSE", "CLOSURE", "VARARG",
    };

    /// <summary>ABx-form opcodes (Bx field instead of B/C).</summary>
    public static bool IsABx(int op) => op is LoadK or GetGlobal or SetGlobal or Closure;

    /// <summary>AsBx-form opcodes (jump-target field).</summary>
    public static bool IsAsBx(int op) => op is Jmp or ForLoop or ForPrep;
}

/// <summary>A lifted instruction in stock Lua 5.1 form (post-linearization).</summary>
public sealed class LiftedIns
{
    public int Op;
    public int A, B, C;
    public int Bx = -1;
    /// <summary>Output-space absolute 0-based target for JMP/FORLOOP/FORPREP; -1 otherwise.</summary>
    public int Target = -1;
    public int OldPC = -1;

    public override string ToString()
    {
        var s = LuaOp.Names[Op];
        if (LuaOp.IsABx(Op)) return $"{s} {A} {Bx}";
        if (LuaOp.IsAsBx(Op)) return $"{s} {A} -> {Target}";
        return $"{s} {A} {B} {C}";
    }
}

public sealed class LiftedProto
{
    public int ParamCount;
    public int NumUpvalues;
    public int MaxStack;
    public int IsVararg;
    public List<LiftedIns> Code = new();
    public List<Constant> Constants = new();
    public List<LiftedProto> Children = new();
    public int UnliftableCount;
    /// <summary>Text dump of the pre-linearization node graph (only when requested).</summary>
    public string? PreDump;
}

public sealed class LiftResult
{
    public required LiftedProto Root;
    public List<string> Report = new();
}

/// <summary>
/// Layer 5: lift a blob <see cref="Proto"/> back to stock Lua 5.1 bytecode.
///
/// Pipeline per proto (ground truth: 77main/Obfuscator/VM Generation/BSGenerator.cs,
/// Generator.cs, 77main/Bytecode Library/Bytecode/Serializer.cs):
///   1. Polymorphic fixup: on-disk enum is the OpPolymorphic wrapper VIndex; the
///      real enum is PolyRealEnum, and operand fields are offset per PolyFields
///      (real = ondisk + sign*offset; the serializer stored real - sign*offset:
///      IsXSubtract => on-disk = real + offset, handler computes Inst[k] - offset).
///   2. GetInstruction-led fixup patterns (Redirect / RegisterReload /
///      ResetOpcodeData A|B|C+Enum): repair the target instruction, delete the
///      pattern, redirect jumps landing on the pattern start to the target.
///   3. Fake-global recovery (EnhancedSecurity): real-code GetGlobal constants for
///      the 10 library globals were renamed to random fake names; the anti-tamper
///      block builds { [fake] = _G[real] }, so the mapping is recovered from there.
///   4. Prologue strip: [11 shuffled Prep*][PrepVarargSize][anti-tamper block]
///      [LoadNil A=0 marker][PrepStackArgs] are all deleted.
///   5. Operand decode (invert the Serializer Mutate functions) to stock semantics.
///   6. CFG recovery: thread junk JMP connectors, reachability, trace-scheduling
///      linearization, recompute sBx targets.
/// </summary>
public sealed class Lifter
{
    private readonly HandlerDb _db;
    private readonly List<string> _report = new();
    private readonly bool _preDump;

    private static readonly HashSet<string> KnownGlobals = new()
    {
        "math", "string", "table", "coroutine", "debug", "os", "task", "buffer", "bit", "bit32",
    };

    private static readonly HashSet<HandlerKind> PrepKinds = new()
    {
        HandlerKind.PrepArgs, HandlerKind.PrepConsts, HandlerKind.PrepEnv,
        HandlerKind.PrepLUpvals, HandlerKind.PrepParams, HandlerKind.PrepPCount,
        HandlerKind.PrepProtos, HandlerKind.PrepStack, HandlerKind.PrepStackArgs,
        HandlerKind.PrepTop, HandlerKind.PrepVararg, HandlerKind.PrepVarargSize,
        HandlerKind.NewStack,
    };

    private Lifter(HandlerDb db, bool preDump) => (_db, _preDump) = (db, preDump);

    public static LiftResult Lift(Proto root, HandlerDb db, bool preDump = false)
    {
        var lifter = new Lifter(db, preDump);
        var lifted = lifter.LiftProto(root, isMain: true, path: "main");
        return new LiftResult { Root = lifted, Report = lifter._report };
    }

    // ------------------------------------------------------------------ roles

    /// <summary>Value of the instruction field playing the given role, reading
    /// through the handler's operand permutation (shuffle clones): h.KeyX is the
    /// numeric field key role X is read from; that key corresponds to one of the
    /// global OP_A/OP_B/OP_C slots, which is where the blob stored the value.</summary>
    private int RoleVal(Instruction ins, HandlerInfo h, char role)
    {
        var key = role switch { 'A' => h.KeyA, 'B' => h.KeyB, 'C' => h.KeyC, _ => -1 };
        if (key < 0) return -1;
        // wire-format v2: fields are keyed by their numeric instruction-table keys
        if (ins.Fields != null)
            return ins.Fields.TryGetValue(key, out var v) ? v : -1;
        if (key == _db.KeyA) return ins.A;
        if (key == _db.KeyB) return ins.B;
        if (key == _db.KeyC) return ins.C;
        return -1; // OP_E/OP_F keys: unused by any liftable handler
    }

    /// <summary>Write the canonical (unshuffled) physical field for a role —
    /// used by fixup-pattern repair, whose targets are never shuffle clones.</summary>
    private void SetField(Instruction ins, int key, int value)
    {
        if (ins.Fields != null)
        {
            ins.Fields[key] = value;
            return;
        }
        if (key == _db.KeyA) ins.A = value;
        else if (key == _db.KeyB) ins.B = value;
        else if (key == _db.KeyC) ins.C = value;
    }

    private void AddToField(Instruction ins, int key, int delta)
    {
        if (ins.Fields != null)
        {
            ins.Fields[key] = ins.Fields.GetValueOrDefault(key) + delta;
            return;
        }
        if (key == _db.KeyA) ins.A += delta;
        else if (key == _db.KeyB) ins.B += delta;
        else if (key == _db.KeyC) ins.C += delta;
    }

    private HandlerInfo InfoFor(Instruction ins) =>
        _db.Handlers.TryGetValue(ins.Enum, out var h)
            ? h
            : new HandlerInfo { Kind = HandlerKind.Unknown, EnumId = ins.Enum };

    /// <summary>
    /// Semantic plausibility of a parsed proto tree given the handler database:
    /// jump targets must land in range, constant references must be in the
    /// constant table. Used to disambiguate v2 field orders, which all consume
    /// the blob identically.
    /// </summary>
    public static long PlausibilityScore(Proto proto, HandlerDb db)
    {
        long score = 0;
        var cnt = proto.Instructions.Count;
        foreach (var ins in proto.Instructions)
        {
            if (ins.IsData || !db.Handlers.TryGetValue(ins.Enum, out var h)) continue;
            int Rv(char role)
            {
                var key = role switch { 'A' => h.KeyA, 'B' => h.KeyB, 'C' => h.KeyC, _ => -1 };
                if (key < 0) return -1;
                if (ins.Fields != null) return ins.Fields.GetValueOrDefault(key, int.MinValue);
                if (key == db.KeyA) return ins.A;
                if (key == db.KeyB) return ins.B;
                if (key == db.KeyC) return ins.C;
                return int.MinValue;
            }
            switch (h.Kind)
            {
                case HandlerKind.Jmp or HandlerKind.ForLoop or HandlerKind.ForPrep
                    or HandlerKind.Lt or HandlerKind.Le or HandlerKind.Test or HandlerKind.TestSet:
                    {
                        var t = Rv('B');
                        score += t >= 0 && t < cnt ? 3 : -3;
                        break;
                    }
                case HandlerKind.LoadK or HandlerKind.GetGlobal or HandlerKind.SetGlobal
                    or HandlerKind.LoadN:
                    {
                        var b = Rv('B');
                        score += b >= 1 && b <= proto.Constants.Count ? 3 : -3;
                        break;
                    }
                case HandlerKind.Call or HandlerKind.TailCall or HandlerKind.Return:
                    {
                        var a = Rv('A');
                        score += a >= 0 && a < 260 ? 1 : -3;
                        break;
                    }
                case HandlerKind.Closure:
                    {
                        var b = Rv('B');
                        score += b >= 0 && b < proto.Children.Count ? 3 : -3;
                        break;
                    }
            }
        }
        foreach (var child in proto.Children)
            score += PlausibilityScore(child, db);
        return score;
    }

    /// <summary>
    /// Literal operand value for a role the handler body does not read (KeyX &lt; 0),
    /// recovered from the variant name: Call/TailCall/Return/VarArg/SetList variants
    /// embed the literal, e.g. Call "B0C2" => B=0, C=2. These fields were serialized
    /// unmutated, but shuffle clones may have moved them, so the variant name is the
    /// only reliable source. Returns 0 when no token is present.
    /// </summary>
    private static int VariantLit(string variant, char role)
    {
        var idx = variant.IndexOf(role);
        return idx >= 0 && idx + 1 < variant.Length && char.IsDigit(variant[idx + 1])
            ? variant[idx + 1] - '0'
            : 0;
    }

    // ------------------------------------------------------------------ nodes

    private sealed class Node
    {
        public int OldPC;
        public int Op;
        public int A, B, C;
        public int Bx = -1;
        public int JumpTarget = -1; // oldPC: JMP/FORLOOP/FORPREP target; cond jump edge
        public int SkipTarget = -1; // oldPC: cond skip edge
        public int Flow = -1;       // oldPC: fallthrough
        public bool Transparent;    // junk connector JMP: thread through, don't emit
        public bool Flippable;      // cond supports flag inversion (EQ/LT/LE/TEST/TESTSET)
        public bool NoFlow;         // RETURN/TAILCALL
        public bool IsNop;          // substituted NOP (transparent flow node)
        public bool Emitted;
        public bool Pinned;
        public Node? Pair;          // fused LoadBoolSkip successor (LOADBOOL C=1 pairs)
        public List<Node> Pseudos = new(); // closure pseudo-instructions

        public override string ToString()
        {
            var s = $"[{OldPC}] {LuaOp.Names[Op]} A={A} B={B} C={C}";
            if (Bx >= 0) s += $" Bx={Bx}";
            if (JumpTarget >= 0) s += $" jmp->{JumpTarget}";
            if (SkipTarget >= 0) s += $" skip->{SkipTarget}";
            if (Flow >= 0) s += $" flow->{Flow}";
            if (Transparent) s += " (connector)";
            if (Pinned) s += " (pinned)";
            return s;
        }
    }

    // ------------------------------------------------------------------ proto

    private LiftedProto LiftProto(Proto proto, bool isMain, string path)
    {
        void Warn(string msg) => _report.Add($"{path}: {msg}");

        var ins = proto.Instructions;
        var n = ins.Count;
        var infos = new HandlerInfo[n];
        var deleted = new bool[n];
        var redirect = new Dictionary<int, int>(); // pattern start PC -> repaired target PC

        // ---- Step 1: polymorphic resolution -----------------------------------
        for (var i = 0; i < n; i++)
        {
            var h = InfoFor(ins[i]);
            if (h.Kind == HandlerKind.Polymorphic)
            {
                if (h.PolyRealEnum >= 0 && _db.Handlers.ContainsKey(h.PolyRealEnum))
                {
                    if (h.PolyFields != null)
                        foreach (var (key, (sign, offset)) in h.PolyFields)
                        {
                            if (sign == 0) continue;
                            AddToField(ins[i], key, sign * offset);
                        }
                    ins[i].Enum = h.PolyRealEnum;
                    h = _db.Handlers[h.PolyRealEnum];
                }
                else
                {
                    Warn($"pc {i}: polymorphic wrapper with unknown real enum {h.PolyRealEnum}");
                }
            }
            infos[i] = h;
        }

        // ---- Step 2: GetInstruction fixup patterns -----------------------------
        for (var i = 0; i < n; i++)
        {
            if (deleted[i] || ins[i].IsData || infos[i].Kind != HandlerKind.GetInstruction) continue;
            var span = RoleVal(ins[i], infos[i], 'B'); // serializer fixed this to 4 or 6
            var target = i + span; // GetInstruction: Stk[A] = Instr[InstrPoint + B] (target0 = PC0 + B)
            if (span is not (4 or 6) || target >= n || deleted[target])
            {
                Warn($"pc {i}: GetInstruction with unexpected span {span}; left as-is");
                continue;
            }

            HandlerKind KindAt(int pc) => pc < n && !deleted[pc] ? infos[pc].Kind : HandlerKind.Unknown;
            var repaired = false;

            if (span == 4)
            {
                // [GetInstruction, PushXKey, SetTableNC, LoadNil, target]
                var setTableC = RoleVal(ins[i + 2], infos[i + 2], 'C');
                switch (KindAt(i + 1))
                {
                    case HandlerKind.PushEnumKey: // Redirect: real enum was stashed
                        ins[target].Enum = setTableC;
                        repaired = true;
                        break;
                    case HandlerKind.PushAKey: // ResetOpcodeData-A
                        SetField(ins[target], _db.KeyA, setTableC);
                        repaired = true;
                        break;
                    case HandlerKind.PushBKey: // ResetOpcodeData-B
                        SetField(ins[target], _db.KeyB, setTableC);
                        repaired = true;
                        break;
                }
            }
            else if (KindAt(i + 1) == HandlerKind.PushAKey && KindAt(i + 2) == HandlerKind.PushBKey)
            {
                // RegisterReload: [GetI, PushAKey, PushBKey, SetTableNC, SetTableNC, LoadNil, target]
                SetField(ins[target], _db.KeyA, RoleVal(ins[i + 3], infos[i + 3], 'C'));
                SetField(ins[target], _db.KeyB, RoleVal(ins[i + 4], infos[i + 4], 'C'));
                repaired = true;
            }
            else if (KindAt(i + 1) == HandlerKind.PushCKey && KindAt(i + 3) == HandlerKind.PushEnumKey)
            {
                // ResetOpcodeData-C+Enum: [GetI, PushCKey, SetTC, PushEnumKey, SetTE, LoadNil, target]
                SetField(ins[target], _db.KeyC, RoleVal(ins[i + 2], infos[i + 2], 'C'));
                ins[target].Enum = RoleVal(ins[i + 4], infos[i + 4], 'C');
                repaired = true;
            }

            if (!repaired)
            {
                Warn($"pc {i}: unrecognized GetInstruction pattern (span {span}); left as-is");
                continue;
            }

            for (var j = i; j < target; j++) deleted[j] = true;
            redirect[i] = target;
            infos[target] = InfoFor(ins[target]); // re-derive from repaired enum
        }

        // ---- Step 3: fake-global name recovery (anti-tamper region scan) --------
        var envMap = new Dictionary<string, string>();
        {
            var regState = new Dictionary<int, (string Tag, string Val)>();
            for (var i = 0; i < n; i++)
            {
                if (deleted[i] || ins[i].IsData) continue;
                var h = infos[i];
                if (h.Kind == HandlerKind.PrepStackArgs) break; // end of prologue region
                var a = RoleVal(ins[i], h, 'A');
                switch (h.Kind)
                {
                    case HandlerKind.LoadK:
                        {
                            var ci = RoleVal(ins[i], h, 'B') - 1;
                            if (a >= 0 && ci >= 0 && ci < proto.Constants.Count
                                && proto.Constants[ci] is { Type: ConstantKind.String, Tampered: false } c)
                                regState[a] = ("str", (string)c.Data!);
                            break;
                        }
                    case HandlerKind.GetGlobal:
                        {
                            var ci = RoleVal(ins[i], h, 'B') - 1;
                            if (a >= 0 && ci >= 0 && ci < proto.Constants.Count
                                && proto.Constants[ci] is { Type: ConstantKind.String, Tampered: false } c)
                                regState[a] = ("global", (string)c.Data!);
                            break;
                        }
                    case HandlerKind.SetTable:
                        {
                            var b = RoleVal(ins[i], h, 'B');
                            var cv = RoleVal(ins[i], h, 'C');
                            if (regState.TryGetValue(b, out var ks) && regState.TryGetValue(cv, out var vs)
                                && ks.Tag == "str" && vs.Tag == "global"
                                && KnownGlobals.Contains(vs.Val)
                                && ks.Val.Length is >= 2 and <= 12
                                && ks.Val.All(ch => ch is (>= 'A' and <= 'Z') or (>= '0' and <= '9')))
                                envMap[ks.Val] = vs.Val;
                            break;
                        }
                    default:
                        if (a >= 0) regState.Remove(a); // clobbered
                        break;
                }
            }
        }
        if (envMap.Count > 0)
            _report.Add($"{path}: recovered {envMap.Count} fake-global mapping(s): " +
                        string.Join(", ", envMap.Select(kv => $"{kv.Key}={kv.Value}")));

        // ---- Step 4: prologue strip ---------------------------------------------
        var stripEnd = -1; // delete [0, stripEnd]
        for (var i = 0; i + 1 < n; i++)
        {
            if (deleted[i] || ins[i].IsData) continue;
            if (infos[i].Kind == HandlerKind.LoadNil && RoleVal(ins[i], infos[i], 'A') == 0
                && !deleted[i + 1] && infos[i + 1].Kind == HandlerKind.PrepStackArgs)
            {
                stripEnd = i + 1;
                break;
            }
        }
        if (stripEnd < 0)
        {
            var j = 0;
            while (j < n && (deleted[j] || ins[j].IsData || PrepKinds.Contains(infos[j].Kind))) j++;
            stripEnd = j - 1;
            Warn($"no LoadNil/PrepStackArgs marker; stripped leading Prep* run of {j}");
        }
        for (var i = 0; i <= stripEnd && i < n; i++) deleted[i] = true;

        // ---- Step 5: operand decode -> nodes -------------------------------------
        var lifted = new LiftedProto { ParamCount = proto.ParamCount };
        foreach (var cst in proto.Constants)
            lifted.Constants.Add(new Constant { Type = cst.Type, Data = cst.Data, Tampered = cst.Tampered });

        var nodes = new Node?[n];
        var childNups = new Dictionary<int, int>(); // original child index -> nups
        var fusedMembers = new HashSet<int>(); // pair/pseudo PCs: legal jump targets, no node
        var unliftable = 0;

        int AddNumberConstant(double v)
        {
            lifted.Constants.Add(new Constant { Type = ConstantKind.Number, Data = v });
            return lifted.Constants.Count - 1;
        }

        string? ConstString(int idx0) =>
            idx0 >= 0 && idx0 < lifted.Constants.Count
            && lifted.Constants[idx0] is { Type: ConstantKind.String, Tampered: false } c
                ? (string)c.Data!
                : null;

        Node? DecodeNode(int i)
        {
            var h = infos[i];
            var a = RoleVal(ins[i], h, 'A');
            var b = RoleVal(ins[i], h, 'B');
            var c = RoleVal(ins[i], h, 'C');
            static int Rk(int val, bool isConst) => isConst ? val + 255 : val;

            Node Simple(int op, int aa, int bb, int cc) =>
                new() { Op = op, OldPC = i, A = aa, B = bb, C = cc };

            Node Nop(string what)
            {
                unliftable++;
                Warn($"pc {i}: unliftable {what}; substituted NOP");
                return new() { Op = LuaOp.Jmp, OldPC = i, Flow = i + 1, IsNop = true };
            }

            switch (h.Kind)
            {
                case HandlerKind.Move: return Simple(LuaOp.Move, a, b, 0);
                case HandlerKind.LoadK:
                    return new() { Op = LuaOp.LoadK, OldPC = i, A = a, Bx = b - 1 };
                case HandlerKind.LoadBool: return Simple(LuaOp.LoadBool, a, b, 0);
                case HandlerKind.LoadBoolSkip:
                    {
                        var node = Simple(LuaOp.LoadBool, a, b, 1);
                        node.Flow = i + 2; // skips the adjacent instruction
                        // fuse the adjacent instruction (must stay physically next)
                        if (i + 1 < n && !deleted[i + 1] && !ins[i + 1].IsData)
                        {
                            var pair = DecodeNode(i + 1);
                            if (pair is { JumpTarget: < 0, SkipTarget: < 0, NoFlow: false, IsNop: false }
                                && pair.Pseudos.Count == 0)
                            {
                                node.Pair = pair;
                                deleted[i + 1] = true; // emitted via the pair
                                fusedMembers.Add(i + 1);
                            }
                            else
                            {
                                Warn($"pc {i}: LoadBoolSkip followed by control instruction; layout broken");
                            }
                        }
                        return node;
                    }
                case HandlerKind.LoadNil: return Simple(LuaOp.LoadNil, a, b, 0);
                case HandlerKind.LoadNilSingle: return Simple(LuaOp.LoadNil, a, a, 0);
                case HandlerKind.GetUpval: return Simple(LuaOp.GetUpval, a, b, 0);
                case HandlerKind.SetUpval: return Simple(LuaOp.SetUpval, a, b, 0);
                case HandlerKind.GetGlobal:
                case HandlerKind.SetGlobal:
                    {
                        var idx0 = b - 1;
                        var name = ConstString(idx0);
                        if (name == null)
                            return Nop($"{h.Kind} with non-string constant");
                        if (envMap.TryGetValue(name, out var real))
                            lifted.Constants[idx0] = new Constant { Type = ConstantKind.String, Data = real };
                        return new()
                        {
                            Op = h.Kind == HandlerKind.GetGlobal ? LuaOp.GetGlobal : LuaOp.SetGlobal,
                            OldPC = i, A = a, Bx = idx0,
                        };
                    }
                case HandlerKind.GetTable: return Simple(LuaOp.GetTable, a, b, Rk(c, h.CIsConst));
                case HandlerKind.GetTableN:
                    return Simple(LuaOp.GetTable, a, b, AddNumberConstant(c) | 256);
                case HandlerKind.SetTable:
                    return Simple(LuaOp.SetTable, a, Rk(b, h.BIsConst), Rk(c, h.CIsConst));
                case HandlerKind.NewTable: return Simple(LuaOp.NewTable, a, 0, 0);
                case HandlerKind.Self: return Simple(LuaOp.Self, a, b, Rk(c, h.CIsConst));
                case HandlerKind.Add or HandlerKind.Sub or HandlerKind.Mul or HandlerKind.Div
                    or HandlerKind.Mod or HandlerKind.Pow:
                    {
                        var cIsConst = h.CIsConst;
                        // OpModB's handler body is copy-pasted from OpModBC in the
                        // obfuscator source, so both classify as Mod/BC. Disambiguate
                        // via the constant table: a true ModBC has C = constIdx+1 with
                        // a numeric constant; a true ModB has C = register (and the
                        // obfuscated program would read Const[C] — if that is not a
                        // number it could never have run, so C must be a register;
                        // if it IS numeric, ModBC reproduces the exact behavior).
                        if (h.Kind == HandlerKind.Mod && h.BIsConst && h.CIsConst
                            && (c == 0 || c - 1 >= lifted.Constants.Count
                                || lifted.Constants[c - 1].Type != ConstantKind.Number))
                            cIsConst = false;
                        var op = h.Kind switch
                        {
                            HandlerKind.Add => LuaOp.Add, HandlerKind.Sub => LuaOp.Sub,
                            HandlerKind.Mul => LuaOp.Mul, HandlerKind.Div => LuaOp.Div,
                            HandlerKind.Mod => LuaOp.Mod, _ => LuaOp.Pow,
                        };
                        return Simple(op, a, Rk(b, h.BIsConst), Rk(c, cIsConst));
                    }
                case HandlerKind.Unm: return Simple(LuaOp.Unm, a, b, 0);
                case HandlerKind.Not: return Simple(LuaOp.Not, a, b, 0);
                case HandlerKind.Len: return Simple(LuaOp.Len, a, b, 0);
                case HandlerKind.Concat:
                    // Concat/Single handlers don't read C (it is implied B+1)
                    return Simple(LuaOp.Concat, a, b, h.KeyC >= 0 ? c : b + 1);
                case HandlerKind.Jmp:
                    return new() { Op = LuaOp.Jmp, OldPC = i, JumpTarget = b, Transparent = true };
                case HandlerKind.ForPrep when a < 0:
                    // SubstituteOpcode: a Jmp rewritten as ForPrep with A in [-200,-150)
                    return new() { Op = LuaOp.Jmp, OldPC = i, JumpTarget = b, Transparent = true };
                case HandlerKind.ForPrep:
                    return new() { Op = LuaOp.ForPrep, OldPC = i, A = a, JumpTarget = b, Flow = i + 1 };
                case HandlerKind.ForLoop:
                    return new() { Op = LuaOp.ForLoop, OldPC = i, A = a, JumpTarget = b, Flow = i + 1 };
                case HandlerKind.Lt or HandlerKind.Le:
                    {
                        // Map by the handler BODY's operator (verified against
                        // OpLt/OpLe/OpGt/OpGe.cs), not by the obfuscator's enum
                        // name: body "<"  => stock LT A=0;  body ">"  (their
                        // Le&A!=0, "Gt" clone) => stock LE A=1;
                        //       body "<=" => stock LE A=0;  body ">=" (their
                        // Lt&A!=0, "Ge" clone) => stock LT A=1.
                        var isG = h.Variant.StartsWith("G");
                        var op = (h.Kind == HandlerKind.Lt) != isG ? LuaOp.Lt : LuaOp.Le;
                        return new()
                        {
                            Op = op,
                            OldPC = i, A = isG ? 1 : 0,
                            B = Rk(a, h.BIsConst), // old B operand lives in the A field
                            C = Rk(c, h.CIsConst),
                            JumpTarget = b, SkipTarget = i + 2, Flippable = true,
                        };
                    }
                case HandlerKind.Eq:
                    {
                        var jmpTarget = ConsumeFollowingJump(ins, infos, deleted, i);
                        if (jmpTarget < 0)
                            Warn($"pc {i}: Eq not followed by a jump; edges may be wrong");
                        return new()
                        {
                            Op = LuaOp.Eq, OldPC = i,
                            A = h.Variant.StartsWith("Ne") ? 1 : 0,
                            B = Rk(b, h.BIsConst), C = Rk(c, h.CIsConst),
                            JumpTarget = jmpTarget, SkipTarget = i + 2, Flippable = true,
                        };
                    }
                case HandlerKind.Test:
                    return new()
                    {
                        Op = LuaOp.Test, OldPC = i, A = a,
                        C = h.Variant == "C" ? 1 : 0,
                        JumpTarget = b, SkipTarget = i + 2, Flippable = true,
                    };
                case HandlerKind.TestSet:
                    return new()
                    {
                        Op = LuaOp.TestSet, OldPC = i, A = a,
                        B = c, // the register lives in the C field after Mutate
                        C = h.Variant == "C" ? 1 : 0,
                        JumpTarget = b, SkipTarget = i + 2, Flippable = true,
                    };
                case HandlerKind.TForLoop:
                    {
                        var jmpTarget = ConsumeFollowingJump(ins, infos, deleted, i);
                        if (jmpTarget < 0)
                            Warn($"pc {i}: TForLoop not followed by a jump");
                        return new()
                        {
                            Op = LuaOp.TForLoop, OldPC = i, A = a, C = c,
                            JumpTarget = jmpTarget, SkipTarget = i + 2, Flippable = false,
                        };
                    }
                case HandlerKind.Call:
                    return Simple(LuaOp.Call, a,
                        h.KeyB >= 0 ? b - a + 1 : VariantLit(h.Variant, 'B'),
                        h.KeyC >= 0 ? c - a + 2 : VariantLit(h.Variant, 'C'));
                case HandlerKind.TailCall:
                    {
                        var node = Simple(LuaOp.TailCall, a,
                            h.KeyB >= 0 ? b - a + 1 : VariantLit(h.Variant, 'B'), 0);
                        node.NoFlow = true;
                        // Stock 5.1 always emits a dead `RETURN A 0` right after a
                        // TAILCALL, and this build's loader verifier enforces it
                        // (TAILCALL not followed by RETURN B=0 => "bad code").
                        node.Pair = new Node
                        {
                            Op = LuaOp.Return, OldPC = -1, A = a, B = 0, C = 0, NoFlow = true,
                        };
                        return node;
                    }
                case HandlerKind.Return:
                    {
                        // Return/B1 ("do return end") reads neither A nor B
                        var node = Simple(LuaOp.Return,
                            h.KeyA >= 0 ? a : 0,
                            h.KeyB >= 0 ? b + 2 : VariantLit(h.Variant, 'B'), 0);
                        node.NoFlow = true;
                        return node;
                    }
                case HandlerKind.SetList:
                    return h.Variant.Contains("C0")
                        ? Nop("SetList C0 (Data payload not serialized)")
                        : Simple(LuaOp.SetList, a,
                            h.KeyB >= 0 ? b : VariantLit(h.Variant, 'B'),
                            h.KeyC >= 0 ? c : VariantLit(h.Variant, 'C'));
                case HandlerKind.Close: return Simple(LuaOp.Close, a, 0, 0);
                case HandlerKind.Closure:
                    {
                        var nups = h.Variant == "NU" ? 0 : c;
                        childNups[b] = Math.Max(childNups.GetValueOrDefault(b), nups);
                        var node = new Node { Op = LuaOp.Closure, OldPC = i, A = a, Bx = b };
                        for (var k = 1; k <= nups; k++)
                        {
                            if (i + k >= n || deleted[i + k]) break;
                            var ph = infos[i + k];
                            var pa = RoleVal(ins[i + k], ph, 'A');
                            var pb = RoleVal(ins[i + k], ph, 'B');
                            if (ph.Kind == HandlerKind.Move)
                                node.Pseudos.Add(new Node { Op = LuaOp.Move, OldPC = i + k, A = 0, B = pb });
                            else if (ph.Kind == HandlerKind.GetUpval)
                                node.Pseudos.Add(new Node { Op = LuaOp.GetUpval, OldPC = i + k, A = 0, B = pb });
                            else
                                Warn($"pc {i + k}: unexpected closure pseudo {ph.Kind}");
                            deleted[i + k] = true; // consumed by the closure
                            fusedMembers.Add(i + k);
                        }
                        node.Flow = i + 1 + nups;
                        return node;
                    }
                case HandlerKind.VarArg:
                    return Simple(LuaOp.VarArg, a,
                        h.KeyB >= 0 ? b - a + 2 : VariantLit(h.Variant, 'B'), 0);
                case HandlerKind.LoadN:
                    return new() { Op = LuaOp.LoadK, OldPC = i, A = a, Bx = AddNumberConstant(b - 1) };
                case HandlerKind.Nop:
                    return new() { Op = LuaOp.Jmp, OldPC = i, Flow = i + 1, IsNop = true };
                case HandlerKind.PushCache:
                    // In live (post-strip) code this only occurs in the compiled
                    // DecryptStringForm: `local cache = <the VM string cache>`.
                    // A fresh table per call is semantically correct (the cache is
                    // a pure memoization of decrypt(input)); sharing is not needed.
                    // (Classifier may mark it ambiguous with PushStackPersistent/
                    // PushConstCache/PushXor — those never survive the prologue strip.)
                    if (h.Ambiguous)
                        Warn($"pc {i}: ambiguous push-family handler treated as PushCache (fresh table)");
                    return Simple(LuaOp.NewTable, a, 0, 0);
                default:
                    return Nop($"{h.Kind}/{h.Variant}");
            }
        }

        for (var i = stripEnd + 1; i < n; i++)
        {
            if (deleted[i] || ins[i].IsData) continue;
            var node = DecodeNode(i);
            if (node != null) nodes[node.OldPC] = node;
        }
        lifted.UnliftableCount = unliftable;

        // ---- edge mapping ---------------------------------------------------------
        int MapFlow(int pc) // first live node with OldPC >= pc
        {
            for (var j = pc; j < n; j++)
                if (!deleted[j] && nodes[j] != null) return j;
            return -1;
        }

        int MapTarget(int pc)
        {
            var guard = 0;
            while (pc < n && redirect.TryGetValue(pc, out var r) && guard++ < 8) pc = r;
            if (pc < n && nodes[pc] != null) return pc;
            // fused pair/pseudo members have no node but are legal jump targets;
            // their output PC is registered when the leader is emitted
            if (pc < n && fusedMembers.Contains(pc)) return pc;
            var f = MapFlow(pc);
            Warn(f >= 0
                ? $"jump target pc {pc} vanished; retargeted to {f}"
                : $"jump target pc {pc} vanished; no live successor");
            return f;
        }

        foreach (var node in nodes)
        {
            if (node == null) continue;
            if (node.JumpTarget >= 0) node.JumpTarget = MapTarget(node.JumpTarget);
            if (node.SkipTarget >= 0) node.SkipTarget = MapFlow(node.SkipTarget);
            if (node.Flow >= 0)
                node.Flow = MapFlow(node.Flow);
            else if (!node.NoFlow && node.JumpTarget < 0 && node.SkipTarget < 0
                     && node.Pseudos.Count == 0 && !node.IsNop)
                node.Flow = MapFlow(node.OldPC + 1);
            if (node.IsNop && node.Flow >= 0) node.Flow = MapFlow(node.Flow);
        }
        // fused pair members: pair flows where the leader flows
        foreach (var node in nodes)
            if (node?.Pair != null)
                node.Pair.Flow = node.Flow;

        var entry = MapFlow(stripEnd + 1);
        if (entry < 0)
        {
            Warn("no real code after strip; emitting bare RETURN");
            lifted.Code.Add(new LiftedIns { Op = LuaOp.Return, A = 0, B = 1, C = 0 });
            FinalizeHeader(lifted, isMain);
            return lifted;
        }

        // ---- Step 6: CFG threading + linearization --------------------------------
        Linearize(nodes, entry, lifted, Warn);

        // Stock 5.1 protos always end in the parser-appended implicit
        // `RETURN 0 1` (zero values); the loader verifier rejects a proto
        // whose last instruction is a jump, and decompilers (unluac) require
        // the final RETURN to carry zero values. Append it unless already
        // there — e.g. after a real `return expr` (B>1) or the dead
        // post-TAILCALL return (B=0).
        if (lifted.Code.Count == 0 || lifted.Code[^1] is not { Op: LuaOp.Return, B: 1 })
            lifted.Code.Add(new LiftedIns { Op = LuaOp.Return, A = 0, B = 1, C = 0 });

        // ---- children: prune unreferenced, recurse, remap Closure Bx ---------------
        var refOrder = new List<int>();
        var refSet = new HashSet<int>();
        foreach (var ci in lifted.Code)
            if (ci.Op == LuaOp.Closure && ci.Bx >= 0 && refSet.Add(ci.Bx))
                refOrder.Add(ci.Bx);
        var remap = new Dictionary<int, int>();
        for (var k = 0; k < refOrder.Count; k++) remap[refOrder[k]] = k;
        foreach (var ci in lifted.Code)
            if (ci.Op == LuaOp.Closure && ci.Bx >= 0) ci.Bx = remap[ci.Bx];
        foreach (var oldIdx in refOrder)
        {
            var child = LiftProto(proto.Children[oldIdx], isMain: false, $"{path}/{oldIdx}");
            child.NumUpvalues = childNups.GetValueOrDefault(oldIdx);
            lifted.Children.Add(child);
        }

        FinalizeHeader(lifted, isMain);
        return lifted;
    }

    /// <summary>
    /// For Eq/TForLoop (not folded): the following physical instruction is the
    /// jump. It may have been rewritten to a ForPrep-with-negative-A by
    /// SubstituteOpcode. Marks it consumed and returns its absolute target.
    /// </summary>
    private int ConsumeFollowingJump(List<Instruction> ins, HandlerInfo[] infos, bool[] deleted, int i)
    {
        var j = i + 1;
        if (j >= ins.Count || deleted[j] || ins[j].IsData) return -1;
        var h = infos[j];
        if (h.Kind == HandlerKind.Jmp
            || h.Kind == HandlerKind.ForPrep && RoleVal(ins[j], h, 'A') < 0)
        {
            deleted[j] = true;
            return RoleVal(ins[j], h, 'B');
        }
        return -1;
    }

    private void FinalizeHeader(LiftedProto lifted, bool isMain)
    {
        var need = 1;
        var openTop = false;
        var hasVararg = false;
        foreach (var ci in lifted.Code)
        {
            static int Reg(int rk) => rk < 256 ? rk : 0;
            var m = ci.Op switch
            {
                LuaOp.Move => Math.Max(ci.A, ci.B),
                LuaOp.LoadK or LuaOp.LoadBool or LuaOp.GetUpval or LuaOp.GetGlobal
                    or LuaOp.NewTable or LuaOp.Closure => ci.A,
                LuaOp.LoadNil => ci.B,
                LuaOp.GetTable => Math.Max(ci.A, Math.Max(ci.B, Reg(ci.C))),
                LuaOp.SetGlobal or LuaOp.SetUpval => ci.A,
                LuaOp.SetTable => Math.Max(ci.A, Math.Max(Reg(ci.B), Reg(ci.C))),
                LuaOp.Self => ci.A + 1,
                >= LuaOp.Add and <= LuaOp.Pow => Math.Max(ci.A, Math.Max(Reg(ci.B), Reg(ci.C))),
                LuaOp.Unm or LuaOp.Not or LuaOp.Len => Math.Max(ci.A, ci.B),
                LuaOp.Concat => ci.C,
                LuaOp.Eq or LuaOp.Lt or LuaOp.Le => Math.Max(Reg(ci.B), Reg(ci.C)),
                LuaOp.Test => ci.A,
                LuaOp.TestSet => Math.Max(ci.A, ci.B),
                LuaOp.Call or LuaOp.TailCall => ci.A + Math.Max(
                    ci.B > 0 ? ci.B : 8, ci.C > 0 ? ci.C - 1 : 8),
                LuaOp.Return => ci.A + Math.Max(ci.B - 1, 0),
                LuaOp.ForLoop or LuaOp.ForPrep => ci.A + 3,
                LuaOp.TForLoop => ci.A + 2 + ci.C,
                LuaOp.SetList => ci.A + (ci.B > 0 ? ci.B : 8),
                LuaOp.VarArg => ci.A + (ci.B > 0 ? ci.B - 1 : 8),
                _ => 0,
            };
            if (ci.Op is LuaOp.Call or LuaOp.TailCall or LuaOp.SetList or LuaOp.VarArg
                && (ci.B == 0 || ci.C == 0))
                openTop = true;
            if (ci.Op == LuaOp.VarArg) hasVararg = true;
            need = Math.Max(need, m + 1);
        }
        if (openTop) need += 8;
        need += 2; // safety margin
        lifted.MaxStack = Math.Min(255, Math.Max(need, lifted.ParamCount + 1));
        // Stock 5.1: main chunks are is_vararg=2 (empirically checked against the
        // bundled lua5.1.exe). Nested vararg functions normally get 7
        // (HASARG|ISVARARG|NEEDSARG), but this build's loader rejects 7
        // ("bad code in precompiled chunk"); 2 (plain ISVARARG) loads and runs.
        lifted.IsVararg = isMain ? 2 : hasVararg ? 2 : 0;
    }

    // ------------------------------------------------------------------ linearize

    private void Linearize(Node?[] nodes, int entry, LiftedProto lifted, Action<string> warn)
    {
        var n = nodes.Length;

        IEnumerable<int> RawSucc(Node node)
        {
            if (node.SkipTarget >= 0) // conditional pair
            {
                yield return node.JumpTarget;
                yield return node.SkipTarget;
                yield break;
            }
            if (node.JumpTarget >= 0) yield return node.JumpTarget;
            if (node.NoFlow) yield break;
            if (node.Flow >= 0) yield return node.Flow;
        }

        // Thread transparent connectors (junk JMPs and NOPs): single-out-edge
        // nodes that need not exist in the output. Cycle-safe: a connector cycle
        // head gets pinned and emitted.
        int Thread(int pc)
        {
            var seen = new HashSet<int>();
            while (pc >= 0 && pc < n)
            {
                var node = nodes[pc];
                if (node == null) return pc; // fused pair member: maps via newPc
                if (!node.Transparent && !node.IsNop) return pc;
                var next = node.IsNop ? node.Flow : node.JumpTarget;
                if (next < 0) return pc;
                if (!seen.Add(next))
                {
                    var cycleHead = nodes[next];
                    if (cycleHead != null)
                    {
                        cycleHead.Pinned = true;
                        cycleHead.Transparent = false;
                    }
                    return next;
                }
                pc = next;
            }
            return pc;
        }

        foreach (var node in nodes)
        {
            if (node == null) continue;
            if (node.JumpTarget >= 0) node.JumpTarget = Thread(node.JumpTarget);
            if (node.SkipTarget >= 0) node.SkipTarget = Thread(node.SkipTarget);
            if (node.Flow >= 0) node.Flow = Thread(node.Flow);
        }

        // reachability
        var reachable = new bool[n];
        var stack = new Stack<int>();
        void Seed(int pc)
        {
            if (pc >= 0 && pc < n && !reachable[pc])
            {
                reachable[pc] = true;
                stack.Push(pc);
            }
        }
        Seed(Thread(entry));
        while (stack.Count > 0)
        {
            var node = nodes[stack.Pop()];
            if (node == null) continue;
            foreach (var s in RawSucc(node)) Seed(s);
        }
        // pinned connectors must survive even if nothing reachable points at them
        foreach (var node in nodes)
            if (node is { Pinned: true })
            {
                Seed(node.OldPC);
                while (stack.Count > 0)
                {
                    var m = nodes[stack.Pop()];
                    if (m == null) continue;
                    foreach (var s in RawSucc(m)) Seed(s);
                }
            }

        if (_preDump)
        {
            var sb = new System.Text.StringBuilder();
            for (var i = 0; i < n; i++)
                if (nodes[i] != null)
                    sb.AppendLine($"{nodes[i]}{(reachable[i] ? "" : "  (unreachable)")}");
            lifted.PreDump = sb.ToString();
        }

        // trace scheduling
        var order = new List<Node>();
        var worklist = new Queue<int>();
        worklist.Enqueue(Thread(entry));

        void Enqueue(int pc)
        {
            if (pc >= 0 && pc < n && nodes[pc] is { Emitted: false } && reachable[pc])
                worklist.Enqueue(pc);
        }

        void Emit(Node node)
        {
            node.Emitted = true;
            order.Add(node);
        }

        void EmitJmp(int targetPc) // synthetic JMP node (OldPC = -1)
        {
            order.Add(new Node { Op = LuaOp.Jmp, OldPC = -1, JumpTarget = targetPc, Emitted = true });
        }

        while (worklist.Count > 0)
        {
            var head = worklist.Dequeue();
            if (head < 0 || head >= n) continue;
            var node = nodes[head];
            if (node == null || node.Emitted || !reachable[head]) continue;

            while (true)
            {
                Emit(node);
                // fused members are emitted in materialization right after their
                // leader; just mark them so they are never scheduled standalone
                foreach (var p in node.Pseudos) p.Emitted = true;
                if (node.Pair != null) node.Pair.Emitted = true;

                if (node.SkipTarget >= 0)
                {
                    // conditional: cond; JMP <one edge>; the other edge falls through
                    var jump = node.JumpTarget;
                    var skip = node.SkipTarget;
                    var skipNode = skip >= 0 && skip < n ? nodes[skip] : null;
                    var jumpNode = jump >= 0 && jump < n ? nodes[jump] : null;
                    if (skipNode is { Emitted: false })
                    {
                        EmitJmp(jump);
                        Enqueue(jump);
                        node = skipNode;
                        continue;
                    }
                    if (node.Flippable && jumpNode is { Emitted: false })
                    {
                        if (node.Op is LuaOp.Eq or LuaOp.Lt or LuaOp.Le) node.A ^= 1;
                        else node.C ^= 1; // TEST/TESTSET
                        EmitJmp(skip);
                        Enqueue(skip);
                        node = jumpNode;
                        continue;
                    }
                    EmitJmp(jump);
                    if (skip >= 0) EmitJmp(skip);
                    Enqueue(jump);
                    Enqueue(skip);
                    break;
                }

                if (node.Op == LuaOp.ForPrep)
                {
                    // FORPREP always jumps; the loop body follows physically
                    Enqueue(node.JumpTarget);
                    var f = node.Flow;
                    if (f >= 0 && nodes[f] is { Emitted: false })
                    {
                        node = nodes[f]!;
                        continue;
                    }
                    break;
                }

                if (node.JumpTarget >= 0)
                {
                    // FORLOOP (flow = loop exit) or a pinned connector JMP
                    Enqueue(node.JumpTarget);
                    var f = node.Flow;
                    if (f >= 0 && f < n && nodes[f] is { Emitted: false } fn)
                    {
                        node = fn;
                        continue;
                    }
                    if (f >= 0 && node.Op == LuaOp.ForLoop) EmitJmp(f);
                    break;
                }

                if (node.NoFlow) break;

                {
                    var f = node.Flow;
                    if (f >= 0 && f < n && nodes[f] is { Emitted: false } fn2)
                    {
                        node = fn2;
                        continue;
                    }
                    if (f >= 0) EmitJmp(f);
                    break;
                }
            }
        }

        foreach (var nd in nodes)
            if (nd is { Emitted: false } && reachable[nd.OldPC])
            {
                warn($"pc {nd.OldPC}: reachable but unscheduled; appending");
                Emit(nd);
            }

        // materialize: assign output PCs (fused pair member = leader + 1)
        var newPc = new Dictionary<int, int>();
        var outIdx = 0;
        foreach (var node in order)
        {
            if (node.OldPC >= 0) newPc[node.OldPC] = outIdx;
            outIdx++;
            if (node.Pair != null)
            {
                if (node.Pair.OldPC >= 0) newPc[node.Pair.OldPC] = outIdx;
                outIdx++;
            }
            foreach (var p in node.Pseudos)
            {
                if (p.OldPC >= 0) newPc[p.OldPC] = outIdx;
                outIdx++;
            }
        }

        int OutTarget(int oldPc)
        {
            if (newPc.TryGetValue(oldPc, out var t)) return t;
            warn($"internal: edge to unemitted pc {oldPc}");
            return 0;
        }

        LiftedIns ToIns(Node node)
        {
            var li = new LiftedIns
            {
                Op = node.Op, A = node.A, B = node.B, C = node.C, Bx = node.Bx, OldPC = node.OldPC,
            };
            if (LuaOp.IsAsBx(node.Op))
                li.Target = node.IsNop
                    ? (node.Flow >= 0 ? OutTarget(node.Flow) : -1)
                    : OutTarget(node.JumpTarget);
            return li;
        }

        foreach (var node in order)
        {
            lifted.Code.Add(ToIns(node));
            if (node.Pair != null) lifted.Code.Add(ToIns(node.Pair));
            foreach (var p in node.Pseudos) lifted.Code.Add(ToIns(p));
        }

        // verify sBx range and targets
        for (var i = 0; i < lifted.Code.Count; i++)
        {
            var ci = lifted.Code[i];
            if (!LuaOp.IsAsBx(ci.Op)) continue;
            if (ci.Target < 0 || ci.Target > lifted.Code.Count)
                warn($"output pc {i}: {ci} target out of range");
            var sBx = ci.Target - (i + 1);
            if (sBx is > 131071 or < -131071)
                warn($"output pc {i}: sBx {sBx} does not fit 18 bits");
        }
    }
}
