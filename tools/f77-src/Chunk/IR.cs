namespace deobfuscator.Chunk;

/// <summary>
/// Intermediate representation of a deserialized 77fuscator bytecode blob.
/// Mirrors 77main/Bytecode Library/IR (Chunk, Instruction, Constant).
/// </summary>
public enum ConstantKind
{
    Nil = 0,
    Boolean = 1,
    Number = 2,
    String = 3,
}

public enum InstructionKind
{
    ABC = 0,
    ABx = 1,
    AsBx = 2,
    AsBxC = 3,
}

public sealed class Constant
{
    public ConstantKind Type;
    /// <summary>bool for Boolean, double for Number, string for String, null for Nil.</summary>
    public object? Data;
    /// <summary>True when the string constant was written as a crash/tamper decoy (flag byte 1, no payload).</summary>
    public bool Tampered;

    public override string ToString() => Type switch
    {
        ConstantKind.String when Tampered => "String<tampered>",
        ConstantKind.String => $"String(\"{Data}\")",
        ConstantKind.Number => $"Number({Data})",
        ConstantKind.Boolean => $"Boolean({Data})",
        _ => "Nil",
    };
}

public sealed class Instruction
{
    public int PC;
    public InstructionKind Type;
    public int Enum;
    public int A;
    public int B;
    public int C;
    public int E;
    public int F;
    /// <summary>
    /// Wire-format v2 (uniform records): payload field values keyed by the
    /// per-build numeric instruction-table keys (OP_A/OP_B/OP_C/OP_E/OP_F).
    /// Null for v1 blobs (fields live in A/B/C/E/F directly).
    /// </summary>
    public Dictionary<int, int>? Fields;
    /// <summary>Descriptor low bit set: a raw data slot, not a real instruction.</summary>
    public bool IsData;

    public override string ToString() =>
        IsData
            ? $"[{PC}] DATA"
            : $"[{PC}] {Type} enum={Enum} A={A} B={B} C={C} E={E} F={F}";
}

public sealed class Proto
{
    public int ParamCount;
    public List<Constant> Constants = new();
    public List<Instruction> Instructions = new();
    public List<Proto> Children = new();
}
