using deobfuscator.Chunk;
using deobfuscator.Lift;
using deobfuscator.Peel;

namespace deobfuscator.Emit;

/// <summary>
/// Stock Lua 5.1 binary chunk writer (luaU_dump compatible): header
/// ESC "Lua" 0x51, format 0, little-endian, int=4, size_t=8, Instruction=4,
/// lua_Number=8 (f64), integral=0; then the root function (source "=deobfuscated",
/// lineDefined=lastlinedefined=0), code, constants, nested protos, empty debug.
/// Tampered/decoy string constants are emitted as nil (their payload was never
/// serialized into the blob).
/// </summary>
public static class LuacWriter
{
    public static byte[] Write(LiftedProto root)
    {
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);

        // header
        w.Write(new byte[] { 0x1B, 0x4C, 0x75, 0x61 }); // ESC "Lua"
        w.Write((byte)0x51); // version 5.1
        w.Write((byte)0);    // official format
        w.Write((byte)1);    // little-endian
        w.Write((byte)4);    // sizeof(int)
        w.Write((byte)8);    // sizeof(size_t)
        w.Write((byte)4);    // sizeof(Instruction)
        w.Write((byte)8);    // sizeof(lua_Number)
        w.Write((byte)0);    // integral flag

        WriteFunction(w, root, isRoot: true);
        w.Flush();
        return ms.ToArray();
    }

    private static void WriteString(BinaryWriter w, string s)
    {
        var bytes = Layers.Latin1.GetBytes(s);
        w.Write((long)bytes.Length + 1); // size_t, includes trailing NUL
        w.Write(bytes);
        w.Write((byte)0);
    }

    private static void WriteFunction(BinaryWriter w, LiftedProto p, bool isRoot)
    {
        WriteString(w, "=deobfuscated");
        w.Write(0); // lineDefined
        w.Write(0); // lastlinedefined
        w.Write((byte)p.NumUpvalues);
        w.Write((byte)p.ParamCount);
        w.Write((byte)p.IsVararg);
        w.Write((byte)p.MaxStack);

        // code
        w.Write(p.Code.Count);
        for (var pc = 0; pc < p.Code.Count; pc++)
            w.Write(Encode(p.Code[pc], pc));

        // constants
        w.Write(p.Constants.Count);
        foreach (var c in p.Constants)
        {
            switch (c.Type)
            {
                case ConstantKind.Boolean:
                    w.Write((byte)1);
                    w.Write((byte)((bool)c.Data! ? 1 : 0));
                    break;
                case ConstantKind.Number:
                    w.Write((byte)3);
                    w.Write((double)c.Data!);
                    break;
                case ConstantKind.String when !c.Tampered:
                    w.Write((byte)4);
                    WriteString(w, (string)c.Data!);
                    break;
                default: // Nil, or a tampered/decoy string: emit as nil
                    w.Write((byte)0);
                    break;
            }
        }

        // nested protos
        w.Write(p.Children.Count);
        foreach (var child in p.Children)
            WriteFunction(w, child, isRoot: false);

        // debug: no line info, no locals, no upvalue names
        w.Write(0);
        w.Write(0);
        w.Write(0);
    }

    /// <summary>Encode with the instruction's output position (needed for sBx).</summary>
    private static uint Encode(LiftedIns ci, int pc)
    {
        var op = (uint)ci.Op;
        if (LuaOp.IsABx(ci.Op))
            return op | ((uint)ci.A << 6) | ((uint)(ci.Bx & 0x3FFFF) << 14);
        if (LuaOp.IsAsBx(ci.Op))
        {
            var sBx = ci.Target - (pc + 1);
            return op | ((uint)ci.A << 6) | ((uint)(sBx + 131071) << 14);
        }
        return op | ((uint)ci.A << 6) | ((uint)ci.C << 14) | ((uint)ci.B << 23);
    }
}
