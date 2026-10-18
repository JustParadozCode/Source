using Loretta.CodeAnalysis;
using Loretta.CodeAnalysis.Lua;
using Loretta.CodeAnalysis.Lua.Syntax;

namespace deobfuscator.Vm;

public enum HandlerKind
{
    Move, LoadK, LoadBool, LoadBoolSkip, LoadNil, GetUpval, SetUpval,
    GetGlobal, SetGlobal, GetTable, SetTable, NewTable, Self, Add, Sub, Mul, Div, Mod, Pow,
    Unm, Not, Len, Concat, Jmp, Eq, Lt, Le, Test, TestSet, Call, TailCall, Return,
    ForLoop, ForPrep, TForLoop, SetList, Close, Closure, VarArg,
    // VM-internal / fixup / prologue:
    Nop, GetInstruction, PushAKey, PushBKey, PushCKey, PushEnumKey, SetTableNC, SetTableNB,
    SetTableNBC, GetTableN, PushCache, PushStackPersistent, PushInsts, PushConsts, PushStack,
    NewStack, GetFEnv, SetFenv, LoadN, LoadNilSingle, CallSpec, LoadKList, MoveList,
    CopyTable, UnpackTable, SetTop, PushPC, PushXor, PushXorKey, PushConstCache,
    PrepArgs, PrepConsts, PrepEnv, PrepLUpvals, PrepParams, PrepPCount, PrepProtos,
    PrepStack, PrepStackArgs, PrepTop, PrepVararg, PrepVarargSize, FuncDecl,
    Replay, ReplayJmp, ReverseConstB, ReverseConstC, ReverseStkB, ReverseStkC,
    // Luau bitwise extension ops (rare; injected BXor is used by constant encryption):
    BAnd, BOr, BXor, BLShift, BRShift, IntDiv, BNot,
    Polymorphic, Unknown
}

public sealed class HandlerInfo
{
    public HandlerKind Kind;
    public int EnumId;
    /// <summary>Sub-variant name, e.g. "CallB0C2", "Ne", "Gt", "ShuffleAB-clone" notes.</summary>
    public string Variant = "";
    public bool BIsConst;
    public bool CIsConst;
    /// <summary>Numeric instruction-field key playing operand role A/B/C in this body (-1 = absent).</summary>
    public int KeyA = -1, KeyB = -1, KeyC = -1;
    /// <summary>Operand permutation, e.g. "ABC" (normal) or "BAC" (ShuffleAB clone). Filled in Finalize.</summary>
    public string Permutation = "";
    /// <summary>True when the body shape is shared by several decoy handlers that cannot be told apart.</summary>
    public bool Ambiguous;
    // Polymorphic extras: field-key -> (sign, offset); sign 0 = plain copy.
    public Dictionary<int, (int Sign, int Offset)>? PolyFields;
    public int PolyRealEnum = -1;
    // Closure anchor: the embedded OpMove enum id.
    public int ClosureMoveEnum = -1;
    // PushAKey/BKey/CKey/EnumKey: the pushed numeric literal.
    public int PushedValue = -1;
    /// <summary>Canonicalized body, kept for deferred fixups in Finalize.</summary>
    public List<PStmt>? IR;

    public override string ToString()
    {
        var s = $"enum {EnumId,3}: {Kind}{(Variant.Length > 0 ? $"/{Variant}" : "")}";
        var keys = $"A={KeyA} B={KeyB} C={KeyC}";
        s += $"  [{keys}]";
        if (BIsConst) s += " B=const";
        if (CIsConst) s += " C=const";
        if (Permutation.Length > 0 && Permutation != "ABC") s += $" perm={Permutation}";
        if (Ambiguous) s += " (ambiguous)";
        if (PushedValue >= 0) s += $" pushed={PushedValue}";
        if (ClosureMoveEnum >= 0) s += $" moveEnum={ClosureMoveEnum}";
        if (PolyRealEnum >= 0) s += $" realEnum={PolyRealEnum}";
        if (PolyFields is { Count: > 0 } pf)
            s += $" fields=[{string.Join(", ", pf.OrderBy(kv => kv.Key).Select(kv =>
                $"k{kv.Key}:{(kv.Value.Sign switch { 1 => "+", -1 => "-", _ => "" })}{kv.Value.Offset}"))}]";
        return s;
    }
}

/// <summary>
/// Layer 4b: classify every handler body recovered by <see cref="DispatchTree"/>
/// into a Lua 5.1 opcode semantics, recovering the per-build instruction field
/// keys (OP_A/OP_B/OP_C) on the way.
///
/// Method: structural template matching over the canonicalized pattern-IR
/// (LuaIR). Templates are the handler bodies from
/// 77main/Obfuscator/Opcodes/Op*.cs written with role identifiers
/// (Stk, Inst, Const, ...) and key placeholders (KEY_A, KEY_B, KEY_C, ...).
/// Identifiers in the obfuscated script are matched by unification: role names
/// bind globally and consistently across all handlers, template locals are
/// wildcards with per-body consistency, and KEY_* placeholders capture the
/// numeric field-key literals (consistent per capture name, pairwise distinct).
///
/// A few families are structurally ambiguous even so and are matched by
/// dedicated deferred matchers once the distinguishing roles are known:
///   `X = {}`          PrepStack / PrepLupvals / PrepVararg
///   `X = Y[lit]`      PrepConsts / PrepProtos / PrepParams
///   `S[I[k]] = name`  Push* / GetFEnv / PushPC family
///   `S[I[k]] = num`   PushAKey / PushBKey / PushCKey / PushEnumKey
///   (empty)           Nop / PushXorKey / SuperOperator / Dynamic / Mutated
///   `S[I[k]] = f(x,y)` Luau bitwise family (BAND/BOR/BXOR/shifts/INTDIV/BNOT)
/// </summary>
public sealed class HandlerDb
{
    public Dictionary<int, HandlerInfo> Handlers = new();
    public List<string> Log = new();
    public Dictionary<string, string> Roles = new();       // role -> actual identifier
    private readonly Dictionary<string, string> _roleOf = new(); // actual -> role
    public Dictionary<string, string> LibAliases = new();  // actual identifier -> bit helper identity
    public int KeyEnum = -1, KeyA = -1, KeyB = -1, KeyC = -1;

    private static readonly HashSet<string> RoleNames = new()
    {
        "Stk", "Inst", "Const", "Instr", "InstrPoint", "Env", "Upvalues", "Lupvals",
        "Proto", "Chunk", "Top", "Args", "PCount", "Params", "Vararg", "Varargsz",
        "Cache", "PersistentStacks", "ConstantsCache", "BitXOR", "Wrap", "Unpack",
        "Select", "Abs", "Next", "GetFEnv", "_R",
    };

    private sealed class Template
    {
        public required HandlerKind Kind;
        public required string Variant;
        public bool BIsConst;
        public bool CIsConst;
        public required string Src;
        public List<PStmt>? Body;
    }

    private static readonly List<Template> Templates = BuildTemplates();

    private static List<Template> BuildTemplates()
    {
        var t = new List<Template>();
        void Add(HandlerKind kind, string variant, string src, bool bc = false, bool cc = false) =>
            t.Add(new Template { Kind = kind, Variant = variant, Src = src, BIsConst = bc, CIsConst = cc });

        // ---- moves / loads -------------------------------------------------
        Add(HandlerKind.Move, "", "Stk[Inst[KEY_A]]=Stk[Inst[KEY_B]]");
        Add(HandlerKind.LoadK, "", "Stk[Inst[KEY_A]]=Const[Inst[KEY_B]]", bc: true);
        Add(HandlerKind.LoadN, "", "Stk[Inst[KEY_A]]=Inst[KEY_B]");
        Add(HandlerKind.LoadBool, "", "Stk[Inst[KEY_A]]=(Inst[KEY_B]~=0)");
        Add(HandlerKind.LoadBoolSkip, "", "Stk[Inst[KEY_A]]=(Inst[KEY_B]~=0) InstrPoint=InstrPoint+1");
        Add(HandlerKind.LoadNil, "Multi", "for Idx=Inst[KEY_A],Inst[KEY_B] do Stk[Idx]=nil end");
        Add(HandlerKind.LoadNilSingle, "", "Stk[Inst[KEY_A]]=nil");
        Add(HandlerKind.LoadKList, "",
            "local off = Inst[KEY_A] for i = off, Inst[KEY_C] do Stk[i] = Const[i - off + 1] end");
        Add(HandlerKind.MoveList, "",
            "local A = Inst[KEY_A] local B = Inst[KEY_B] local diff = Inst[KEY_C] " +
            "for i = A, B do Stk[i] = Stk[i - diff] end");

        // ---- upvalues / globals --------------------------------------------
        Add(HandlerKind.GetUpval, "", "local UV=Upvalues[Inst[KEY_B]] Stk[Inst[KEY_A]]=UV[1][UV[2]]");
        Add(HandlerKind.SetUpval, "", "local UV=Upvalues[Inst[KEY_B]] UV[1][UV[2]]=Stk[Inst[KEY_A]]");
        Add(HandlerKind.GetGlobal, "", "Stk[Inst[KEY_A]]=Env[Const[Inst[KEY_B]]]", bc: true);
        Add(HandlerKind.SetGlobal, "", "Env[Const[Inst[KEY_B]]]=Stk[Inst[KEY_A]]", bc: true);

        // ---- tables ---------------------------------------------------------
        Add(HandlerKind.GetTable, "", "Stk[Inst[KEY_A]]=Stk[Inst[KEY_B]][Stk[Inst[KEY_C]]]");
        Add(HandlerKind.GetTable, "ConstC", "Stk[Inst[KEY_A]]=Stk[Inst[KEY_B]][Const[Inst[KEY_C]]]", cc: true);
        Add(HandlerKind.GetTableN, "", "Stk[Inst[KEY_A]]=Stk[Inst[KEY_B]][Inst[KEY_C]]");
        Add(HandlerKind.SetTable, "", "Stk[Inst[KEY_A]][Stk[Inst[KEY_B]]]=Stk[Inst[KEY_C]]");
        Add(HandlerKind.SetTable, "B", "Stk[Inst[KEY_A]][Const[Inst[KEY_B]]]=Stk[Inst[KEY_C]]", bc: true);
        Add(HandlerKind.SetTable, "C", "Stk[Inst[KEY_A]][Stk[Inst[KEY_B]]]=Const[Inst[KEY_C]]", cc: true);
        Add(HandlerKind.SetTable, "BC", "Stk[Inst[KEY_A]][Const[Inst[KEY_B]]]=Const[Inst[KEY_C]]", bc: true, cc: true);
        Add(HandlerKind.SetTableNC, "", "Stk[Inst[KEY_A]][Stk[Inst[KEY_B]]]=Inst[KEY_C]");
        Add(HandlerKind.SetTableNB, "", "Stk[Inst[KEY_A]][Inst[KEY_B]]=Stk[Inst[KEY_C]]");
        Add(HandlerKind.SetTableNBC, "", "Stk[Inst[KEY_A]][Inst[KEY_B]]=Inst[KEY_C]");
        Add(HandlerKind.NewTable, "", "Stk[Inst[KEY_A]]={}");
        Add(HandlerKind.NewTable, "BElse", "Stk[Inst[KEY_A]]={Unpack({},1,Inst[KEY_B])}");
        Add(HandlerKind.Self, "",
            "local A=Inst[KEY_A] local B=Stk[Inst[KEY_B]] Stk[A+1]=B Stk[A]=B[Stk[Inst[KEY_C]]]");
        Add(HandlerKind.Self, "C",
            "local A=Inst[KEY_A] local B=Stk[Inst[KEY_B]] Stk[A+1]=B Stk[A]=B[Const[Inst[KEY_C]]]", cc: true);
        Add(HandlerKind.CopyTable, "",
            "local t = Stk[Inst[KEY_B]] for i,v in Next,Stk[Inst[KEY_A]] do t[#t+1] = v end");
        Add(HandlerKind.UnpackTable, "", "return Unpack(Stk[Inst[KEY_A]])");

        // ---- arithmetic ------------------------------------------------------
        foreach (var (kind, op) in new[]
        {
            (HandlerKind.Add, "+"), (HandlerKind.Sub, "-"), (HandlerKind.Mul, "*"),
            (HandlerKind.Div, "/"), (HandlerKind.Mod, "%"), (HandlerKind.Pow, "^"),
        })
        {
            const string sb = "Stk[Inst[KEY_B]]";
            const string sc = "Stk[Inst[KEY_C]]";
            const string cb = "Const[Inst[KEY_B]]";
            const string cc2 = "Const[Inst[KEY_C]]";
            Add(kind, "", $"Stk[Inst[KEY_A]]={sb}{op}{sc}");
            Add(kind, "B", $"Stk[Inst[KEY_A]]={cb}{op}{sc}", bc: true);
            Add(kind, "C", $"Stk[Inst[KEY_A]]={sb}{op}{cc2}", cc: true);
            // NOTE: OpModB's body is identical to OpModBC in the obfuscator
            // source (bug). Both classify as Mod/BC; the lifter must
            // disambiguate via the on-disk C field (C <= 255 => ModB).
            Add(kind, "BC", $"Stk[Inst[KEY_A]]={cb}{op}{cc2}", bc: true, cc: true);
        }

        // ---- unary -----------------------------------------------------------
        Add(HandlerKind.Unm, "", "Stk[Inst[KEY_A]]=-Stk[Inst[KEY_B]]");
        Add(HandlerKind.Not, "", "Stk[Inst[KEY_A]]=(not Stk[Inst[KEY_B]])");
        Add(HandlerKind.Len, "", "Stk[Inst[KEY_A]]=#Stk[Inst[KEY_B]]");
        Add(HandlerKind.Concat, "Multi",
            "local B=Inst[KEY_B] local K=Stk[B] for Idx=B+1,Inst[KEY_C] do K=K..Stk[Idx] end Stk[Inst[KEY_A]]=K");
        Add(HandlerKind.Concat, "Single", "local B=Inst[KEY_B] Stk[Inst[KEY_A]] = Stk[B] .. Stk[B+1]");

        // ---- jumps / comparisons ---------------------------------------------
        Add(HandlerKind.Jmp, "", "InstrPoint=Inst[KEY_B]");
        Add(HandlerKind.SetTop, "", "Top=Inst[KEY_A]");
        foreach (var (kind, op) in new[] { (HandlerKind.Eq, "=="), (HandlerKind.Eq, "~=") })
        {
            var variant = op == "==" ? "" : "Ne";
            const string sb = "Stk[Inst[KEY_B]]";
            const string sc = "Stk[Inst[KEY_C]]";
            const string cb = "Const[Inst[KEY_B]]";
            const string cc2 = "Const[Inst[KEY_C]]";
            Add(kind, variant, $"if ({sb} {op} {sc}) then InstrPoint = InstrPoint + 1 end");
            Add(kind, variant + "B", $"if ({cb} {op} {sc}) then InstrPoint = InstrPoint + 1 end", bc: true);
            Add(kind, variant + "C", $"if ({sb} {op} {cc2}) then InstrPoint = InstrPoint + 1 end", cc: true);
            Add(kind, variant + "BC", $"if ({cb} {op} {cc2}) then InstrPoint = InstrPoint + 1 end", bc: true, cc: true);
        }
        // folded-jump comparisons: operands are the A and C fields; B is the jump target.
        foreach (var (kind, variant, op) in new[]
        {
            (HandlerKind.Lt, "", "<"), (HandlerKind.Le, "", "<="),
            (HandlerKind.Lt, "Gt", ">"), (HandlerKind.Le, "Ge", ">="),
        })
        {
            const string sa = "Stk[Inst[KEY_A]]";
            const string sc = "Stk[Inst[KEY_C]]";
            const string ca = "Const[Inst[KEY_A]]";
            const string cc2 = "Const[Inst[KEY_C]]";
            string Body(string x, string y) =>
                $"if ({x} {op} {y}) then InstrPoint=InstrPoint+1 else InstrPoint=Inst[KEY_B] end";
            Add(kind, variant, Body(sa, sc));
            Add(kind, variant + "B", Body(ca, sc), bc: true);
            Add(kind, variant + "C", Body(sa, cc2), cc: true);
            Add(kind, variant + "BC", Body(ca, cc2), bc: true, cc: true);
        }
        Add(HandlerKind.Test, "",
            "if Stk[Inst[KEY_A]] then InstrPoint=InstrPoint+1 else InstrPoint=Inst[KEY_B] end");
        Add(HandlerKind.Test, "C",
            "if not Stk[Inst[KEY_A]] then InstrPoint=InstrPoint+1 else InstrPoint=Inst[KEY_B] end");
        Add(HandlerKind.TestSet, "",
            "local B=Stk[Inst[KEY_C]] if B then InstrPoint=InstrPoint+1 " +
            "else Stk[Inst[KEY_A]]=B InstrPoint=Inst[KEY_B] end");
        Add(HandlerKind.TestSet, "C",
            "local B=Stk[Inst[KEY_C]] if not B then InstrPoint=InstrPoint+1 " +
            "else Stk[Inst[KEY_A]]=B InstrPoint=Inst[KEY_B] end");

        // ---- calls / returns ---------------------------------------------------
        const string resLoopC = "local Edx = 0 for Idx = A, Inst[KEY_C] do Edx = Edx + 1 Stk[Idx] = Results[Edx] end";
        const string resLoopTop = "local Edx = 0 for Idx = A, Top do Edx = Edx + 1 Stk[Idx] = Results[Edx] end";
        Add(HandlerKind.Call, "", // B>2 C>2
            $"local A = Inst[KEY_A] local Results = {{ Stk[A](Unpack(Stk, A + 1, Inst[KEY_B])) }} {resLoopC}");
        Add(HandlerKind.Call, "B2", // B==2 C>2
            $"local A = Inst[KEY_A] local Results = {{ Stk[A](Stk[A + 1]) }} {resLoopC}");
        Add(HandlerKind.Call, "B0", // B==0 C>2
            $"local A = Inst[KEY_A] local Results = {{ Stk[A](Unpack(Stk, A + 1, Top)) }} {resLoopC}");
        Add(HandlerKind.Call, "B1", // B==1 C>2
            "local A = Inst[KEY_A] local Results = { Stk[A]() } local Limit = Inst[KEY_C] " +
            "local Edx = 0 for Idx = A, Limit do Edx = Edx + 1 Stk[Idx] = Results[Edx] end");
        Add(HandlerKind.Call, "C0", // B>2 C==0
            $"local A = Inst[KEY_A] local Results, Limit = _R(Stk[A](Unpack(Stk, A + 1, Inst[KEY_B]))) " +
            $"Top = Limit + A - 1 {resLoopTop}");
        Add(HandlerKind.Call, "C0B2",
            $"local A = Inst[KEY_A] local Results, Limit = _R(Stk[A](Stk[A + 1])) " +
            $"Top = Limit + A - 1 {resLoopTop}");
        Add(HandlerKind.Call, "C1", "local A = Inst[KEY_A] Stk[A](Unpack(Stk, A + 1, Inst[KEY_B]))");
        Add(HandlerKind.Call, "C1B2", "local A = Inst[KEY_A] Stk[A](Stk[A + 1])");
        Add(HandlerKind.Call, "B0C0",
            $"local A = Inst[KEY_A] local Results, Limit = _R(Stk[A](Unpack(Stk, A + 1, Top))) " +
            $"Top = Limit + A - 1 {resLoopTop}");
        Add(HandlerKind.Call, "B0C1", "local A = Inst[KEY_A] Stk[A](Unpack(Stk, A + 1, Top))");
        Add(HandlerKind.Call, "B1C0",
            $"local A = Inst[KEY_A] local Results, Limit = _R(Stk[A]()) " +
            $"Top = Limit + A - 1 {resLoopTop}");
        Add(HandlerKind.Call, "B1C1", "Stk[Inst[KEY_A]]()");
        Add(HandlerKind.Call, "C2", "local A = Inst[KEY_A] Stk[A] = Stk[A](Unpack(Stk, A + 1, Inst[KEY_B]))");
        Add(HandlerKind.Call, "C2B2", "local A = Inst[KEY_A] Stk[A] = Stk[A](Stk[A + 1])");
        Add(HandlerKind.Call, "B0C2", "local A = Inst[KEY_A] Stk[A] = Stk[A](Unpack(Stk, A + 1, Top))");
        Add(HandlerKind.Call, "B1C2", "local A = Inst[KEY_A] Stk[A] = Stk[A]()");

        Add(HandlerKind.TailCall, "", // B>1
            "local A = Inst[KEY_A] do return Stk[A](Unpack(Stk, A + 1, Inst[KEY_B])) end");
        Add(HandlerKind.TailCall, "B0",
            "local A = Inst[KEY_A] do return Stk[A](Unpack(Stk, A + 1, Top)) end");
        Add(HandlerKind.TailCall, "B1", "do return Stk[Inst[KEY_A]]() end");

        Add(HandlerKind.Return, "", // B>3
            "local A = Inst[KEY_A] do return Unpack(Stk, A, A + Inst[KEY_B]) end");
        Add(HandlerKind.Return, "B2", "do return Stk[Inst[KEY_A]] end");
        Add(HandlerKind.Return, "B3", "local A = Inst[KEY_A] do return Stk[A], Stk[A + 1] end");
        Add(HandlerKind.Return, "B0", "local A = Inst[KEY_A] do return Unpack(Stk, A, Top) end");
        Add(HandlerKind.Return, "B1", "do return end");

        // ---- loops -------------------------------------------------------------
        Add(HandlerKind.ForLoop, "",
            "local A = Inst[KEY_A] local step = Stk[A + 2] local index = Stk[A] + step " +
            "local limit = Stk[A + 1] local loops " +
            "if step == Abs(step) then loops = index <= limit else loops = index >= limit end " +
            "if loops then Stk[A] = index Stk[A + 3] = index InstrPoint = Inst[KEY_B] end");
        Add(HandlerKind.ForPrep, "",
            "local A=Inst[KEY_A] Stk[A]=(Stk[A] or 0)-(Stk[A+2] or 0) InstrPoint=Inst[KEY_B]");
        Add(HandlerKind.TForLoop, "",
            "local A = Inst[KEY_A] local C = Inst[KEY_C] local Offset = A + 2 " +
            "local Result = { Stk[A](Stk[A + 1], Stk[Offset]) } " +
            "for Idx = 1, C do Stk[Offset + Idx] = Result[Idx] end " +
            "local R = Stk[A + 3] " +
            "if R then Stk[Offset] = R else InstrPoint = InstrPoint + 1 end");

        // ---- SetList -------------------------------------------------------------
        Add(HandlerKind.SetList, "",
            "local A=Inst[KEY_A] local Offset=(Inst[KEY_C]-1)*50 local T=Stk[A] local B=Inst[KEY_B] " +
            "for Idx=1,B do T[Offset+Idx]=Stk[A+Idx] end");
        Add(HandlerKind.SetList, "C1",
            "local A=Inst[KEY_A] local T=Stk[A] local B=Inst[KEY_B] for Idx=1,B do T[Idx]=Stk[A+Idx] end");
        Add(HandlerKind.SetList, "B0",
            "local A=Inst[KEY_A] local Offset=(Inst[KEY_C]-1)*50 local T=Stk[A] local X=Top-A " +
            "for Idx=1,X do T[Offset+Idx]=Stk[A+Idx] end");
        Add(HandlerKind.SetList, "C0",
            "local A=Inst[KEY_A] InstrPoint=InstrPoint+1 local Offset=(Instr[InstrPoint][KEY_DATA]-1)*50 " +
            "local T=Stk[A] local B=Inst[KEY_B] for Idx=1,B do T[Offset+Idx]=Stk[A+Idx] end");
        Add(HandlerKind.SetList, "B0C0",
            "local A=Inst[KEY_A] InstrPoint=InstrPoint+1 local Offset=(Instr[InstrPoint][KEY_DATA]-1)*50 " +
            "local T=Stk[A] local X=Top-A for Idx=1,X do T[Offset+Idx]=Stk[A+Idx] end");
        Add(HandlerKind.SetList, "B0C1",
            "local A=Inst[KEY_A] local T=Stk[A] local X=Top-A for Idx=1,X do T[Idx]=Stk[A+Idx] end");

        // ---- upvalue closing / closures -------------------------------------------
        Add(HandlerKind.Close, "",
            "local A=Inst[KEY_A] local Cls={} " +
            "for Idx=1,#Lupvals do local List=Lupvals[Idx] " +
            "for Idz=0,#List do local Upv=List[Idz] local NStk=Upv[1] local Pos=Upv[2] " +
            "if NStk==Stk and Pos>=A then Cls[Pos]=NStk[Pos] Upv[1]=Cls end end end");
        Add(HandlerKind.Closure, "",
            "local NewProto=Proto[Inst[KEY_B]] local uvlist = {} local C = Inst[KEY_C] " +
            "for i = 1, C, 1 do InstrPoint = InstrPoint + 1 local pseudo = Instr[InstrPoint] " +
            "if pseudo[KEY_ENUM] == MOVEENUM then uvlist[i - 1] = { Stk, pseudo[KEY_B] } " +
            "else uvlist[i - 1] = Upvalues[pseudo[KEY_B]] end " +
            "Lupvals[(#Lupvals) + 1] = uvlist end " +
            "Stk[Inst[KEY_A]] = Wrap(NewProto, uvlist)");
        Add(HandlerKind.Closure, "NU", "Stk[Inst[KEY_A]]=Wrap(Proto[Inst[KEY_B]],nil)");

        // ---- vararg -----------------------------------------------------------------
        Add(HandlerKind.VarArg, "",
            "local A = Inst[KEY_A] local B = Inst[KEY_B] for Idx = A, B do Stk[Idx] = Vararg[Idx - A] end");
        Add(HandlerKind.VarArg, "B0",
            "local A = Inst[KEY_A] Top = A + Varargsz - 1 " +
            "for Idx = A, Top do local VA = Vararg[Idx - A] Stk[Idx] = VA end");

        // ---- fixups / misc ------------------------------------------------------------
        Add(HandlerKind.GetInstruction, "", "Stk[Inst[KEY_A]]=Instr[InstrPoint + Inst[KEY_B]]");
        Add(HandlerKind.SetFenv, "", "Env = Stk[Inst[KEY_A]]");
        Add(HandlerKind.CallSpec, "", "Stk[Inst[KEY_C]] = Stk[Inst[KEY_A]](Stk[Inst[KEY_B]])");
        Add(HandlerKind.FuncDecl, "",
            "local CurrentInst = Inst Stk[Inst[KEY_A]]=function() return CurrentInst[KEY_A] end");
        Add(HandlerKind.Replay, "",
            "if Inst[KEY_A] ~= 0 then InstrPoint = InstrPoint + Inst[KEY_B] Inst[KEY_B] = 0 end");
        Add(HandlerKind.ReplayJmp, "",
            "if Inst[KEY_A] == 0 then InstrPoint = InstrPoint + Inst[KEY_B] " +
            "local instrPointer = Inst[InstrPoint + Inst[KEY_C]] instrPointer[KEY_A] = 1 Inst[KEY_A] = 1 end");
        Add(HandlerKind.ReverseConstB, "", "Const[Inst[KEY_B]] = -Const[Inst[KEY_B]]");
        Add(HandlerKind.ReverseConstC, "", "Const[Inst[KEY_C]] = -Const[Inst[KEY_C]]");
        Add(HandlerKind.ReverseStkB, "", "Stk[Inst[KEY_B]] = -Stk[Inst[KEY_B]]");
        Add(HandlerKind.ReverseStkC, "", "Stk[Inst[KEY_C]] = -Stk[Inst[KEY_C]]");

        // ---- prologue (self-anchoring shapes) ------------------------------------------
        Add(HandlerKind.NewStack, "",
            "Stk = {} for Idx = 0, PCount do if Idx < Params then Stk[Idx] = Args[Idx + 1] else break end end");
        Add(HandlerKind.PrepStackArgs, "",
            "for Idx = 0, PCount do if Idx >= Params then Vararg[Idx - Params] = Args[Idx + 1] " +
            "else Stk[Idx] = Args[Idx + 1] end end");
        Add(HandlerKind.PrepArgs, "", "Args = {...}");
        Add(HandlerKind.PrepEnv, "", "Env = GetFEnv()");
        Add(HandlerKind.PrepPCount, "", "PCount = Select('#', ...) - 1");
        Add(HandlerKind.PrepTop, "", "Top = -1");
        Add(HandlerKind.PrepVarargSize, "", "Varargsz = PCount - Params + 1");

        return t;
    }

    // ===========================================================================

    public static HandlerDb Classify(DispatchInfo dispatch)
    {
        var db = new HandlerDb { KeyEnum = dispatch.EnumKey, LibAliases = dispatch.LibAliases };
        db.BindRoleGlobal("Inst", dispatch.InstName);
        db.BindRoleGlobal("Instr", dispatch.InstrName);
        db.BindRoleGlobal("InstrPoint", dispatch.InstrPointName);
        // the BitXOR helper alias doubles as the PushXor role
        foreach (var (actual, helper) in dispatch.LibAliases)
            if (helper == "BitXOR")
                db.BindRoleGlobal("BitXOR", actual);

        // parse templates once
        foreach (var tpl in Templates)
        {
            if (tpl.Body != null) continue;
            var root = LuaSyntaxTree.ParseText(tpl.Src, new LuaParseOptions(LuaSyntaxOptions.Lua51)).GetRoot();
            tpl.Body = LuaIR.Convert(((CompilationUnitSyntax)root).Statements);
        }

        var remaining = new SortedSet<int>(dispatch.Leaves.Keys);
        var converted = new Dictionary<int, List<PStmt>>();
        foreach (var id in remaining)
        {
            try
            {
                converted[id] = LuaIR.Convert(dispatch.Leaves[id]);
            }
            catch (Exception ex)
            {
                db.Log.Add($"enum {id}: body conversion failed ({ex.Message}); will stay Unknown");
            }
        }

        // fixpoint passes: roles discovered by early handlers unlock deferred families
        var progress = true;
        while (progress && remaining.Count > 0)
        {
            progress = false;
            foreach (var id in remaining.ToList())
            {
                if (!converted.TryGetValue(id, out var body)) continue;
                if (db.TryClassify(id, body, out var info))
                {
                    info.IR = body;
                    db.Handlers[id] = info;
                    remaining.Remove(id);
                    progress = true;
                }
            }
        }

        // anything left is Unknown
        foreach (var id in remaining)
        {
            db.Handlers[id] = new HandlerInfo { Kind = HandlerKind.Unknown, EnumId = id };
            var text = string.Join(" ", dispatch.Leaves[id].Select(s => s.ToString()));
            db.Log.Add($"enum {id}: UNKNOWN handler body: {(text.Length > 300 ? text[..300] + "..." : text)}");
        }

        db.FinalizeDb();
        return db;
    }

    private void BindRoleGlobal(string role, string actual)
    {
        Roles[role] = actual;
        _roleOf[actual] = role;
    }

    private bool TryClassify(int enumId, List<PStmt> body, out HandlerInfo info)
    {
        // 1. empty body: Nop / PushXorKey / SuperOperator / Dynamic / Mutated collision
        if (body.Count == 0)
        {
            info = new HandlerInfo
            {
                Kind = HandlerKind.Nop, EnumId = enumId, Variant = "empty",
                Ambiguous = true, // PushXorKey et al. share the empty body
            };
            return true;
        }

        // 2. polymorphic fixup shape
        if (TryPolymorphic(enumId, body, out info)) return true;

        // 3. templates
        var debugEnum = Environment.GetEnvironmentVariable("DEOB_DEBUG_ENUM");
        if (debugEnum != null && int.TryParse(debugEnum, out var de0) && de0 == enumId)
            foreach (var st in body)
                Console.Error.WriteLine($"[dbg] enum {enumId} ir: {IRDump.Stmt(st)}");

        // Collect ALL matching templates. A match that needs to create new global
        // role bindings is a guess (e.g. `Const[A] >= Stk[C]` also structurally
        // matches a `Stk[A] >= Const[C]` body with the roles swapped); such guesses
        // poison every later classification. Prefer the match needing the fewest
        // new bindings; if several tie, defer this handler to a later fixpoint
        // pass, after self-anchoring handlers (NewStack etc.) have bound the roles.
        Template? bestTpl = null;
        MatchState? bestState = null;
        var bestScore = int.MaxValue;
        var bestCount = 0;
        foreach (var tpl in Templates)
        {
            var state = new MatchState();
            if (!MatchList(tpl.Body!, body, state))
            {
                if (debugEnum != null && int.TryParse(debugEnum, out var de) && de == enumId)
                                {
                                    var idx = -1;
                                    if (tpl.Body!.Count == body.Count)
                                        for (var si = 0; si < body.Count; si++)
                                            if (!MatchStmt(tpl.Body![si], body[si], new MatchState())) { idx = si; break; }
                                    Console.Error.WriteLine($"[dbg] enum {enumId} vs {tpl.Kind}/{tpl.Variant}: " +
                                        (idx < 0 ? $"stmt count {tpl.Body!.Count} != {body.Count}"
                                            : $"stmt {idx}:\n  tpl : {tpl.Body![idx]}\n  body: {body[idx]}"));
                                }
                continue;
            }
            if (!state.KeyDistinct()) continue;

            var score = state.NewRoles.Count;
            if (score < bestScore)
            {
                bestScore = score;
                bestCount = 1;
                bestTpl = tpl;
                bestState = state;
            }
            else if (score == bestScore)
            {
                bestCount++;
            }
        }

        if (bestTpl != null && bestCount == 1)
        {
            var tpl = bestTpl;
            var state = bestState!;
            foreach (var (role, actual) in state.NewRoles)
                BindRoleGlobal(role, actual);

            info = new HandlerInfo
            {
                Kind = tpl.Kind,
                Variant = tpl.Variant,
                BIsConst = tpl.BIsConst,
                CIsConst = tpl.CIsConst,
                EnumId = enumId,
                KeyA = state.Key("KEY_A"),
                KeyB = state.Key("KEY_B"),
                KeyC = state.Key("KEY_C"),
            };
            if (tpl.Kind == HandlerKind.Closure && tpl.Variant == "")
                info.ClosureMoveEnum = (int)state.Num("MOVEENUM");
            return true;
        }
        if (bestTpl != null && debugEnum != null && int.TryParse(debugEnum, out var de2) && de2 == enumId)
            Console.Error.WriteLine($"[dbg] enum {enumId}: {bestCount} templates tie with {bestScore} new bindings; deferred");

        // 4. deferred families
        if (TryPushFamilies(enumId, body, out info)) return true;
        if (TryAssignFamilies(enumId, body, out info)) return true;
        if (TryBitwiseFamily(enumId, body, out info)) return true;

        info = null!;
        return false;
    }

    // ---------------------------------------------------------------- families

    /// <summary>
    /// `Instr[InstrPoint] = { [k]=Inst[k][+-off], ..., [K_ENUM]=realEnum }; InstrPoint = InstrPoint - 1`
    /// </summary>
    private bool TryPolymorphic(int enumId, List<PStmt> body, out HandlerInfo info)
    {
        info = null!;
        if (body.Count != 2) return false;
        if (body[0] is not PStmt.Assign { Targets.Count: 1, Values.Count: 1 } assign) return false;
        if (body[1] is not PStmt.Assign { Targets.Count: 1, Values.Count: 1 } back) return false;
        var instr = Roles["Instr"];
        var ip = Roles["InstrPoint"];
        var inst = Roles["Inst"];
        if (assign.Targets[0] is not PExpr.Index
            {
                Target: PExpr.Name t0, Key: PExpr.Name k0
            } || t0.Id != instr || k0.Id != ip) return false;
        if (back.Targets[0] is not PExpr.Name bt || bt.Id != ip
            || back.Values[0] is not PExpr.Bin { Op: "-", L: PExpr.Name bl, R: PExpr.Num { V: 1 } }
            || bl.Id != ip) return false;
        if (assign.Values[0] is not PExpr.Table { Items.Count: 0 } table) return false;

        var fields = new Dictionary<int, (int, int)>();
        var realEnum = -1;
        foreach (var (k, v) in table.Keyed)
        {
            if (k is not PExpr.Num keyNum) return false;
            var key = (int)keyNum.V;
            switch (v)
            {
                case PExpr.Num value: // the enum field
                    if (key != KeyEnum) return false;
                    realEnum = (int)value.V;
                    break;
                case PExpr.Index { Target: PExpr.Name it, Key: PExpr.Num ik } when it.Id == inst:
                    if ((int)ik.V != key) return false;
                    fields[key] = (0, 0);
                    break;
                case PExpr.Bin { Op: "+" or "-", L: PExpr.Index { Target: PExpr.Name it2, Key: PExpr.Num ik2 }, R: PExpr.Num off }
                    when it2.Id == inst:
                    if ((int)ik2.V != key) return false;
                    fields[key] = (v is PExpr.Bin { Op: "+" } ? 1 : -1, (int)off.V);
                    break;
                default:
                    return false;
            }
        }
        if (realEnum < 0) return false;

        info = new HandlerInfo
        {
            Kind = HandlerKind.Polymorphic,
            EnumId = enumId,
            PolyFields = fields,
            PolyRealEnum = realEnum,
        };
        return true;
    }

    /// <summary>
    /// `S[I[k]] = name`  (Push* / GetFEnv / PushPC) and `S[I[k]] = num` (Push*Key).
    /// </summary>
    private bool TryPushFamilies(int enumId, List<PStmt> body, out HandlerInfo info)
    {
        info = null!;
        if (body.Count != 1) return false;
        if (body[0] is not PStmt.Assign { Targets.Count: 1, Values.Count: 1 } assign) return false;
        if (assign.Targets[0] is not PExpr.Index
        {
            Target: PExpr.Name t, Key: PExpr.Index { Target: PExpr.Name i, Key: PExpr.Num keyLit },
        }) return false;
        if (i.Id != Roles["Inst"]) return false;
        if (assign.Values[0] is not (PExpr.Num or PExpr.Name)) return false;
        // the shape is unique to Stk-indexed pushes; bind Stk if new
        if (Roles.TryGetValue("Stk", out var stk))
        {
            if (t.Id != stk) return false;
        }
        else
        {
            BindRoleGlobal("Stk", t.Id);
        }

        switch (assign.Values[0])
        {
            case PExpr.Num pushed:
                // kind resolved in Finalize once the global field keys are voted
                info = new HandlerInfo
                {
                    Kind = HandlerKind.Unknown,
                    Variant = "pushnum",
                    EnumId = enumId,
                    KeyA = (int)keyLit.V,
                    PushedValue = (int)pushed.V,
                };
                return true;

            case PExpr.Name rhs:
                var kind = _roleOf.TryGetValue(rhs.Id, out var role)
                    ? role switch
                    {
                        "Stk" => HandlerKind.PushStack,
                        "Const" => HandlerKind.PushConsts,
                        "Instr" => HandlerKind.PushInsts,
                        "InstrPoint" => HandlerKind.PushPC,
                        "Env" => HandlerKind.GetFEnv,
                        "Cache" => HandlerKind.PushCache,
                        "PersistentStacks" => HandlerKind.PushStackPersistent,
                        "ConstantsCache" => HandlerKind.PushConstCache,
                        "BitXOR" => HandlerKind.PushXor,
                        _ => (HandlerKind?)null,
                    }
                    : null;
                // some builds leave these locals unrenamed; use the literal name
                if (kind == null)
                    kind = rhs.Id switch
                    {
                        "Cache" => HandlerKind.PushCache,
                        "PersistentStacks" => HandlerKind.PushStackPersistent,
                        "ConstantsCache" => HandlerKind.PushConstCache,
                        _ => null,
                    };
                if (kind == null)
                {
                    // unbound identifier: Cache / PersistentStacks / ConstantsCache /
                    // BitXOR pushes cannot be told apart structurally
                    info = new HandlerInfo
                    {
                        Kind = HandlerKind.PushCache,
                        Variant = $"unresolved-ident:{rhs.Id}",
                        EnumId = enumId,
                        KeyA = (int)keyLit.V,
                        Ambiguous = true,
                    };
                    Log.Add($"enum {enumId}: push-family handler with unresolvable identifier '{rhs.Id}' " +
                            "(one of PushCache/PushStackPersistent/PushConstCache/PushXor); classified PushCache");
                    return true;
                }
                info = new HandlerInfo { Kind = kind.Value, EnumId = enumId, KeyA = (int)keyLit.V };
                return true;
        }
        return false;
    }

    /// <summary>
    /// `X = {}` (PrepStack/PrepLupvals/PrepVararg) and `X = Y[lit]`
    /// (PrepConsts/PrepProtos/PrepParams). Deferred until the LHS identifier is
    /// role-bound by another handler; never binds roles itself.
    /// </summary>
    private bool TryAssignFamilies(int enumId, List<PStmt> body, out HandlerInfo info)
    {
        info = null!;
        if (body.Count != 1) return false;
        if (body[0] is not PStmt.Assign { Targets.Count: 1, Values.Count: 1 } assign) return false;
        if (assign.Targets[0] is not PExpr.Name lhs) return false;
        if (!_roleOf.TryGetValue(lhs.Id, out var role)) return false; // deferred

        switch (assign.Values[0])
        {
            case PExpr.Table { Items.Count: 0, Keyed.Count: 0 }:
                var kind = role switch
                {
                    "Stk" => HandlerKind.PrepStack,
                    "Lupvals" => HandlerKind.PrepLUpvals,
                    "Vararg" => HandlerKind.PrepVararg,
                    _ => (HandlerKind?)null,
                };
                if (kind == null) return false;
                info = new HandlerInfo { Kind = kind.Value, EnumId = enumId };
                return true;

            case PExpr.Index { Target: PExpr.Name chunk, Key: PExpr.Num }:
                var kind2 = role switch
                {
                    "Const" => HandlerKind.PrepConsts,
                    "Proto" => HandlerKind.PrepProtos,
                    "Params" => HandlerKind.PrepParams,
                    _ => (HandlerKind?)null,
                };
                if (kind2 == null) return false;
                if (!Roles.ContainsKey("Chunk")) BindRoleGlobal("Chunk", chunk.Id);
                else if (Roles["Chunk"] != chunk.Id) return false;
                info = new HandlerInfo { Kind = kind2.Value, EnumId = enumId };
                return true;
        }
        return false;
    }

    /// <summary>
    /// Luau bitwise extension handlers: `S[I[ka]] = f(x, y)` / `S[I[ka]] = f(x)`.
    /// The callee is a renamed VMP1 helper alias (BitBAND/BitBOR/BitXOR/
    /// BitLSHIFT/BitRSHIFT) recovered by DispatchTree.RecoverLibAliases, or
    /// __INTDIV__/BitNOT (bare local functions, identified by elimination:
    /// BitNOT is the only 1-argument handler).
    /// </summary>
    private bool TryBitwiseFamily(int enumId, List<PStmt> body, out HandlerInfo info)
    {
        info = null!;
        if (body.Count != 1) return false;
        if (body[0] is not PStmt.Assign { Targets.Count: 1, Values.Count: 1 } assign) return false;
        if (assign.Targets[0] is not PExpr.Index
        {
            Target: PExpr.Name t, Key: PExpr.Index { Target: PExpr.Name i, Key: PExpr.Num keyLit },
        }) return false;
        if (i.Id != Roles["Inst"]) return false;
        if (Roles.TryGetValue("Stk", out var stk) && t.Id != stk) return false;
        if (assign.Values[0] is not PExpr.Call { Func: PExpr.Name callee } call) return false;
        if (call.Args.Count is < 1 or > 2) return false;

        HandlerKind kind;
        var ambiguous = false;
        if (LibAliases.TryGetValue(callee.Id, out var helper))
        {
            kind = helper switch
            {
                "BitBAND" => HandlerKind.BAnd,
                "BitBOR" => HandlerKind.BOr,
                "BitXOR" => HandlerKind.BXor,
                "BitLSHIFT" => HandlerKind.BLShift,
                "BitRSHIFT" => HandlerKind.BRShift,
                _ => HandlerKind.Unknown,
            };
        }
        else
        {
            // no alias guard: __INTDIV__ (2 args) or BitNOT (1 arg), by elimination
            kind = call.Args.Count == 1 ? HandlerKind.BNot : HandlerKind.IntDiv;
            ambiguous = call.Args.Count != 1;
        }

        var constTarget = Roles.TryGetValue("Const", out var cst) ? cst : null;
        bool IsConstArg(PExpr arg) =>
            arg is PExpr.Index { Target: PExpr.Name at } && constTarget != null && at.Id == constTarget;
        static int ArgKey(PExpr arg) =>
            arg is PExpr.Index { Key: PExpr.Index { Key: PExpr.Num k } } ? (int)k.V : -1;

        info = new HandlerInfo
        {
            Kind = kind,
            Variant = call.Args.Count == 1 ? "" :
                (IsConstArg(call.Args[0]) ? "B" : "") + (IsConstArg(call.Args[1]) ? "C" : ""),
            EnumId = enumId,
            KeyA = (int)keyLit.V,
            KeyB = call.Args.Count > 0 ? ArgKey(call.Args[0]) : -1,
            KeyC = call.Args.Count > 1 ? ArgKey(call.Args[1]) : -1,
            BIsConst = call.Args.Count > 1 && IsConstArg(call.Args[0]),
            CIsConst = call.Args.Count > 1 && IsConstArg(call.Args[1]),
            Ambiguous = ambiguous,
        };
        if (ambiguous)
            Log.Add($"enum {enumId}: bitwise-family callee '{callee.Id}' has no alias guard; " +
                    "classified IntDiv by elimination");
        return true;
    }

    // ---------------------------------------------------------------- finalize

    private void FinalizeDb()
    {
        // vote the global field keys from captured role keys
        KeyA = Vote(Handlers.Values.Select(h => h.KeyA));
        KeyB = Vote(Handlers.Values.Select(h => h.KeyB));
        KeyC = Vote(Handlers.Values.Select(h => h.KeyC));

        // bitwise handlers may have been classified before the Const role was
        // bound; recompute their const flags now that roles are complete
        if (Roles.TryGetValue("Const", out var constActual))
            foreach (var h in Handlers.Values.Where(h => h.Kind is >= HandlerKind.BAnd and <= HandlerKind.BNot))
            {
                if (h.IR is not [PStmt.Assign { Values: [PExpr.Call call] }]) continue;
                bool IsConstArg(PExpr arg) =>
                    arg is PExpr.Index { Target: PExpr.Name at } && at.Id == constActual;
                if (call.Args.Count > 1)
                {
                    h.BIsConst = IsConstArg(call.Args[0]);
                    h.CIsConst = IsConstArg(call.Args[1]);
                    h.Variant = (h.BIsConst ? "B" : "") + (h.CIsConst ? "C" : "");
                }
            }

        // resolve pushnum kinds now that keys are known
        foreach (var h in Handlers.Values.Where(h => h.Variant == "pushnum"))
        {
            h.Kind = h.PushedValue == KeyA ? HandlerKind.PushAKey
                : h.PushedValue == KeyB ? HandlerKind.PushBKey
                : h.PushedValue == KeyC ? HandlerKind.PushCKey
                : h.PushedValue == KeyEnum ? HandlerKind.PushEnumKey
                : HandlerKind.Unknown;
            h.Variant = "";
            if (h.Kind == HandlerKind.Unknown)
                Log.Add($"enum {h.EnumId}: pushnum value {h.PushedValue} matches no field key");
        }

        // operand permutations
        foreach (var h in Handlers.Values)
        {
            char Letter(int key) =>
                key < 0 ? '_' : key == KeyA ? 'A' : key == KeyB ? 'B' : key == KeyC ? 'C' : '?';
            if (h.KeyA < 0 && h.KeyB < 0 && h.KeyC < 0) continue;
            h.Permutation = $"{Letter(h.KeyA)}{Letter(h.KeyB)}{Letter(h.KeyC)}".TrimEnd('_');
        }

        // cross-checks
        var pushEnum = Handlers.Values.FirstOrDefault(h => h.Kind == HandlerKind.PushEnumKey);
        if (pushEnum != null)
            Log.Add(pushEnum.PushedValue == KeyEnum
                ? $"check OK: PushEnumKey literal {pushEnum.PushedValue} == OP_ENUM key {KeyEnum}"
                : $"check FAIL: PushEnumKey literal {pushEnum.PushedValue} != OP_ENUM key {KeyEnum}");
        else
            Log.Add("check SKIP: no PushEnumKey handler present");

        foreach (var (kind, key, name) in new[]
        {
            (HandlerKind.PushAKey, KeyA, "OP_A"), (HandlerKind.PushBKey, KeyB, "OP_B"),
            (HandlerKind.PushCKey, KeyC, "OP_C"),
        })
        {
            var push = Handlers.Values.FirstOrDefault(h => h.Kind == kind);
            if (push != null)
                Log.Add(push.PushedValue == key
                    ? $"check OK: {kind} literal {push.PushedValue} == {name} key {key}"
                    : $"check FAIL: {kind} literal {push.PushedValue} != {name} key {key}");
        }

        var moveEnum = Handlers.Values.FirstOrDefault(h => h.Kind == HandlerKind.Move)?.EnumId ?? -1;
        var closure = Handlers.Values.FirstOrDefault(h => h.Kind == HandlerKind.Closure && h.Variant == "");
        if (closure != null && moveEnum >= 0)
            Log.Add(closure.ClosureMoveEnum == moveEnum
                ? $"check OK: Closure embeds Move enum {moveEnum}"
                : $"check FAIL: Closure embeds enum {closure.ClosureMoveEnum}, Move is {moveEnum}");
        else
            Log.Add("check SKIP: Closure-with-upvalues or Move handler missing");

        // key agreement across independent handler families
        foreach (var (name, voted, pick) in new[]
        {
            ("OP_A", KeyA, (Func<HandlerInfo, int>)(h => h.KeyA)),
            ("OP_B", KeyB, h => h.KeyB),
            ("OP_C", KeyC, h => h.KeyC),
        })
        {
            var families = Handlers.Values
                .Where(h => pick(h) >= 0)
                .GroupBy(h => h.Kind)
                .Select(g => (Kind: g.Key, Values: g.Select(pick).Distinct().ToList()))
                .ToList();
            // shuffle clones legitimately capture a permuted key; a kind only
            // dissents when NO handler of that kind captured the global key
            var dissent = families.Where(f => !f.Values.Contains(voted)).ToList();
            Log.Add(dissent.Count == 0
                ? $"check OK: {name}={voted} agrees across {families.Count} handler kinds"
                : $"check FAIL: {name} vote {voted}; dissenting kinds: {string.Join(", ", dissent.Select(d => $"{d.Kind}=[{string.Join("|", d.Values)}]"))}");
        }

        static int Mode(IEnumerable<int> values)
        {
            var list = values.Where(v => v >= 0).ToList();
            return list.Count == 0 ? -1 : list.GroupBy(v => v).OrderByDescending(g => g.Count()).First().Key;
        }

        static int Vote(IEnumerable<int> values) => Mode(values);
    }

    // ---------------------------------------------------------------- matching engine

    private sealed class MatchState
    {
        public Dictionary<string, string> NewRoles = new();
        public Dictionary<string, string> Wildcards = new();
        public Dictionary<string, double> Nums = new();
        /// <summary>Actual identifiers that played a role in this attempt.</summary>
        public HashSet<string> RoleActuals = new();

        public int Key(string name) => Nums.TryGetValue(name, out var v) ? (int)v : -1;
        public double Num(string name) => Nums.TryGetValue(name, out var v) ? v : -1;

        public bool KeyDistinct()
        {
            var keys = Nums.Where(kv => kv.Key.StartsWith("KEY_")).Select(kv => kv.Value).ToList();
            return keys.Count == keys.Distinct().Count();
        }
    }

    private bool BindRole(MatchState state, string role, string actual)
    {
        if (Roles.TryGetValue(role, out var bound))
        {
            if (bound != actual) return false;
            state.RoleActuals.Add(actual);
            return true;
        }
        if (state.NewRoles.TryGetValue(role, out var pending)) return pending == actual;
        if (_roleOf.ContainsKey(actual)) return false; // actual already plays another role
        if (state.NewRoles.ContainsValue(actual)) return false;
        if (state.Wildcards.ContainsValue(actual)) return false; // it's a local in this body
        state.NewRoles[role] = actual;
        state.RoleActuals.Add(actual);
        return true;
    }

    private bool BindWildcard(MatchState state, string name, string actual)
    {
        // Shadowing semantics: a handler local declared *after* a role usage may
        // reuse the role's identifier (the minifier renames per-variable and
        // reuses names). Matching is sequential, so a wildcard may bind an
        // identifier that played a role earlier in this body, but BindRole
        // still rejects identifiers already bound as locals.
        if (state.Wildcards.TryGetValue(name, out var prev)) return prev == actual;
        // NOTE: no injectivity requirement — shadowed handler locals reuse names,
        // so two template wildcards may map to the same actual identifier.
        state.Wildcards[name] = actual;
        return true;
    }

    private bool MatchExpr(PExpr t, PExpr b, MatchState state)
    {
        if (t is PExpr.Name tn)
        {
            var name = tn.Id;
            if (name is "NUMANY" or "MOVEENUM" || name.StartsWith("KEY_"))
            {
                if (b is not PExpr.Num bn) return false;
                if (state.Nums.TryGetValue(name, out var prev)) return prev == bn.V;
                state.Nums[name] = bn.V;
                return true;
            }
            if (RoleNames.Contains(name))
                return b is PExpr.Name rn && BindRole(state, name, rn.Id);
            // wildcard: any identifier, consistent within the body, never a role variable
            return b is PExpr.Name wn && BindWildcard(state, name, wn.Id);
        }

        if (t is PExpr.Num en) return b is PExpr.Num ben && en.V == ben.V;
        if (t is PExpr.Str es) return b is PExpr.Str bes && es.V == bes.V;
        if (t is PExpr.Bool eb) return b is PExpr.Bool beb && eb.V == beb.V;
        if (t is PExpr.Nil) return b is PExpr.Nil;
        if (t is PExpr.Varargs) return b is PExpr.Varargs;
        if (t is PExpr.Index ei)
            return b is PExpr.Index bei && MatchExpr(ei.Target, bei.Target, state) && MatchExpr(ei.Key, bei.Key, state);
        if (t is PExpr.Call ec)
            return b is PExpr.Call bec && MatchExpr(ec.Func, bec.Func, state) && MatchExprs(ec.Args, bec.Args, state);
        if (t is PExpr.Bin ebn)
            return b is PExpr.Bin bebn && ebn.Op == bebn.Op && MatchExpr(ebn.L, bebn.L, state) && MatchExpr(ebn.R, bebn.R, state);
        if (t is PExpr.Un eu)
            return b is PExpr.Un beu && eu.Op == beu.Op && MatchExpr(eu.X, beu.X, state);
        if (t is PExpr.Table et)
            return b is PExpr.Table bet && et.Keyed.Count == bet.Keyed.Count
                   && MatchExprs(et.Items, bet.Items, state)
                   && et.Keyed.Zip(bet.Keyed).All(kv =>
                       MatchExpr(kv.First.K, kv.Second.K, state) && MatchExpr(kv.First.V, kv.Second.V, state));
        if (t is PExpr.Fn ef)
            return b is PExpr.Fn bef && ef.Params.SequenceEqual(bef.Params) && MatchList(ef.Body, bef.Body, state);
        return false;
    }

    private bool MatchExprs(List<PExpr> t, List<PExpr> b, MatchState state) =>
        t.Count == b.Count && t.Zip(b).All(p => MatchExpr(p.First, p.Second, state));

    private bool MatchList(List<PStmt> t, List<PStmt> b, MatchState state) =>
        t.Count == b.Count && t.Zip(b).All(p => MatchStmt(p.First, p.Second, state));

    private bool MatchStmt(PStmt t, PStmt b, MatchState state)
    {
        if (t is PStmt.Local tl)
            return b is PStmt.Local bl && tl.Names.Count == bl.Names.Count
                   && MatchExprs(tl.Values, bl.Values, state)
                   && tl.Names.Zip(bl.Names).All(p => BindWildcard(state, p.First, p.Second));
        if (t is PStmt.Assign ta)
            return b is PStmt.Assign ba && MatchExprs(ta.Targets, ba.Targets, state)
                   && MatchExprs(ta.Values, ba.Values, state);
        if (t is PStmt.Expr te)
            return b is PStmt.Expr be && MatchExpr(te.E, be.E, state);
        if (t is PStmt.If ti)
            return b is PStmt.If bi && MatchExpr(ti.Cond, bi.Cond, state)
                   && MatchList(ti.Then, bi.Then, state)
                   && ti.Elifs.Count == bi.Elifs.Count
                   && ti.Elifs.Zip(bi.Elifs).All(e =>
                       MatchExpr(e.First.Cond, e.Second.Cond, state) && MatchList(e.First.Body, e.Second.Body, state))
                   && (ti.Else == null ? bi.Else == null
                       : bi.Else != null && MatchList(ti.Else, bi.Else, state));
        if (t is PStmt.ForNum tf)
            return b is PStmt.ForNum bf && BindWildcard(state, tf.Var, bf.Var)
                   && MatchExpr(tf.Init, bf.Init, state)
                   && MatchExpr(tf.Final, bf.Final, state) && MatchList(tf.Body, bf.Body, state);
        if (t is PStmt.ForGen tg)
            return b is PStmt.ForGen bg && tg.Vars.Count == bg.Vars.Count
                   && MatchExprs(tg.Exprs, bg.Exprs, state)
                   && tg.Vars.Zip(bg.Vars).All(p => BindWildcard(state, p.First, p.Second))
                   && MatchList(tg.Body, bg.Body, state);
        if (t is PStmt.While tw)
            return b is PStmt.While bw && MatchExpr(tw.Cond, bw.Cond, state) && MatchList(tw.Body, bw.Body, state);
        if (t is PStmt.Do td)
            return b is PStmt.Do bd && MatchList(td.Body, bd.Body, state);
        if (t is PStmt.Return tr)
            return b is PStmt.Return br && MatchExprs(tr.Exprs, br.Exprs, state);
        if (t is PStmt.Break)
            return b is PStmt.Break;
        return false;
    }
}
