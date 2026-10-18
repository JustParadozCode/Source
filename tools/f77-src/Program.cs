using deobfuscator.Chunk;
using deobfuscator.Emit;
using deobfuscator.Lift;
using deobfuscator.Lua;
using deobfuscator.Peel;
using deobfuscator.Vm;

namespace deobfuscator;

/// <summary>
/// 77fuscator static deobfuscator.
///
///   deobfuscator &lt;input.lua&gt; [-o output.luac] [--dump-protos]
///
/// Pipeline:
///   Layer 1 (Peel.Layers)      - undo ExtraCompression (LZW+base36) and extract
///                                the hex bytecode blob ("77FUS|...").
///   Layer 2 (Lua.XorKey)       - statically evaluate `local XOR_KEY = ...` to
///                                recover PrimaryXorKey (falls back to brute force).
///   Layer 3 (Chunk.BlobReader) - brute-force section order / constant mapping and
///                                deserialize the blob into the Proto IR.
/// </summary>
public static class Program
{
    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: deobfuscator <input.lua> [-o output.luac] [--strings] [--dump-protos] [--dump-handlers] [--dump-lifted]");
            return 2;
        }

        string? input = null;
        string? output = null;
        var dumpProtos = false;
        var dumpHandlers = false;
        var dumpLifted = false;
        var strings = false;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "-o" or "--output":
                    if (++i >= args.Length)
                    {
                        Console.Error.WriteLine("missing value for -o");
                        return 2;
                    }
                    output = args[i];
                    break;
                case "--dump-protos":
                    dumpProtos = true;
                    break;
                case "--dump-handlers":
                    dumpHandlers = true;
                    break;
                case "--dump-lifted":
                    dumpLifted = true;
                    break;
                case "--strings":
                    strings = true;
                    break;
                default:
                    input ??= args[i];
                    break;
            }
        }

        if (input == null || !File.Exists(input))
        {
            Console.Error.WriteLine($"input file not found: {input ?? "(none)"}");
            return 2;
        }

        try
        {
            return Run(input, output, dumpProtos, dumpHandlers, dumpLifted, strings);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"error: {ex.Message}");
            return 1;
        }
    }

    private static int Run(string input, string? output, bool dumpProtos, bool dumpHandlers,
        bool dumpLifted, bool strings)
    {
        // Read as Latin-1 so byte values survive the round trip into strings.
        var script = Layers.Latin1.GetString(File.ReadAllBytes(input));

        // ---- Layer 1: unwrap -------------------------------------------------
        var vmScript = Layers.DecompressIfNeeded(script, out var wasCompressed);
        if (Environment.GetEnvironmentVariable("DEOB_DUMP_VM") is { } dumpPath)
            File.WriteAllText(dumpPath, vmScript);
        byte[] blob;
        var rawCompressedBlob = false;
        try
        {
            blob = Layers.ExtractBlob(vmScript);
        }
        catch (InvalidDataException) when
            (Layers.TryExtractRawCompressedBlob(vmScript) is { } raw)
        {
            // Variant: uncompressed VM wrapper, blob slot holds LZW+base36 of
            // the raw (still XOR-encrypted) blob instead of prefixed hex.
            blob = raw;
            rawCompressedBlob = true;
        }

        Console.WriteLine($"Input:            {input}");
        Console.WriteLine($"Compressed:       {(wasCompressed ? "yes (LZW+base36, decoded)" : rawCompressedBlob ? "blob only (LZW+base36 raw blob variant)" : "no")}");
        Console.WriteLine($"VM script length: {vmScript.Length} chars");
        Console.WriteLine($"Blob length:      {blob.Length} bytes");

        // ---- Layer 2: XOR key -------------------------------------------------
        var key = XorKey.TryRecover(vmScript);
        IReadOnlyList<int> candidateKeys;
        if (key is { } k)
        {
            Console.WriteLine($"XOR key:          {k} (0x{k:X2}, recovered statically from local XOR_KEY)");
            candidateKeys = new[] { k };
        }
        else
        {
            // The Flattener renames all locals, so `XOR_KEY` usually does not
            // survive; evaluate every NumberObfuscation-shaped IIFE instead.
            candidateKeys = XorKey.FindCandidateKeys(vmScript);
            Console.WriteLine(candidateKeys.Count > 0
                ? $"XOR key:          no literal XOR_KEY local (renamed); {candidateKeys.Count} candidate(s) " +
                  $"from static IIFE evaluation: {string.Join(", ", candidateKeys)}"
                : "XOR key:          not recovered; brute-forcing all 256");
        }

        // ---- Layer 3: deserialize ---------------------------------------------
        var result = BlobReader.Deserialize(blob, candidateKeys, vmScript: vmScript);

        Console.WriteLine($"XOR key used:     {result.XorKey} (0x{result.XorKey:X2})" +
                          (result.KeyWasBruteForced ? " (brute-forced)" : ""));
        Console.WriteLine($"Section order:    {string.Join(" -> ", result.Order)}");
        Console.WriteLine(result.ConstantMapping.Length == 4
            ? $"Constant mapping: Nil={result.ConstantMapping[0]} " +
              $"Boolean={result.ConstantMapping[1]} " +
              $"Number={result.ConstantMapping[2]} " +
              $"String={result.ConstantMapping[3]}"
            : $"Constant mapping (v2 tags): bool={Array.IndexOf(result.ConstantMapping, 1)} " +
              $"number={Array.IndexOf(result.ConstantMapping, 2)} " +
              $"string={Array.IndexOf(result.ConstantMapping, 3)}");
        Console.WriteLine($"Clean parses:     {result.CandidateCount} candidate(s); best scored parse shown");
        foreach (var (cKey, cOrder, cMapping, cScore) in BlobReader.Candidates)
            Console.WriteLine($"  candidate: key={cKey} order=[{string.Join(",", cOrder)}] " +
                              $"mapping=[{string.Join(",", cMapping)}] score={cScore}");

        // ---- Report ------------------------------------------------------------
        Console.WriteLine();
        DumpProto(result.Root, 0, verbose: dumpProtos);

        // ---- Layer 4: dispatch tree + handler classification --------------------
        DispatchInfo? dispatch = null;
        HandlerDb? db = null;
        if (dumpHandlers || dumpLifted || output != null)
        {
            dispatch = DispatchTree.Extract(vmScript);
            db = HandlerDb.Classify(dispatch);

            // v2 blobs: all field orders consume the blob identically, so the
            // correct one is chosen semantically now that handlers are classified.
            if (BlobReader.PendingTies != null)
            {
                var before = result;
                result = BlobReader.Disambiguate(p => Lifter.PlausibilityScore(p, db));
                Console.WriteLine($"v2 field order:   disambiguated semantically " +
                                  $"(score {Lifter.PlausibilityScore(result.Root, db)} vs {Lifter.PlausibilityScore(before.Root, db)} first-seen)");
                Console.WriteLine();
                DumpProto(result.Root, 0, verbose: dumpProtos);
            }
        }
        if (dumpHandlers)
        {
            Console.WriteLine();
            DumpHandlers(dispatch!, db!, result.Root);
        }

        // ---- Layer 5/6: lift + .luac writer -------------------------------------
        if (output != null || dumpLifted)
        {
            var lift = Lifter.Lift(result.Root, db!, preDump: dumpLifted);

            // optional: recover encrypted string literals (see Lift/StringRecovery.cs)
            if (strings)
                StringRecovery.Run(lift.Root, lift.Report);

            Console.WriteLine();
            Console.WriteLine("==== Lift report ====");
            foreach (var line in lift.Report)
                Console.WriteLine($"  {line}");
            var totalUnliftable = CountUnliftable(lift.Root);
            Console.WriteLine($"Unliftable instructions (NOP'd): {totalUnliftable}");
            if (lift.Root.UnliftableCount > 0)
                Console.WriteLine($"WARNING: main proto contains {lift.Root.UnliftableCount} unliftable instruction(s)!");

            if (dumpLifted)
            {
                Console.WriteLine();
                DumpLifted(lift.Root, 0);
            }

            if (output != null)
            {
                var bytes = LuacWriter.Write(lift.Root);
                File.WriteAllBytes(output, bytes);
                Console.WriteLine();
                Console.WriteLine($"Wrote {bytes.Length} bytes to {output}");
            }
        }

        return 0;
    }

    private static int CountUnliftable(LiftedProto p) =>
        p.UnliftableCount + p.Children.Sum(CountUnliftable);

    private static void DumpLifted(LiftedProto p, int depth)
    {
        var indent = new string(' ', depth * 2);
        Console.WriteLine($"{indent}Proto: params={p.ParamCount} nups={p.NumUpvalues} " +
                          $"vararg={p.IsVararg} maxstack={p.MaxStack} consts={p.Constants.Count} " +
                          $"children={p.Children.Count} unliftable={p.UnliftableCount}");
        if (p.PreDump != null)
        {
            Console.WriteLine($"{indent}-- pre-linearization nodes:");
            foreach (var line in p.PreDump.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                Console.WriteLine($"{indent}  {line}");
        }
        for (var i = 0; i < p.Code.Count; i++)
        {
            var ci = p.Code[i];
            var extra = "";
            if (ci.Op is LuaOp.LoadK or LuaOp.GetGlobal or LuaOp.SetGlobal or LuaOp.Closure
                && ci.Bx >= 0 && ci.Bx < p.Constants.Count
                && ci.Op != LuaOp.Closure)
                extra = $"  ; {p.Constants[ci.Bx]}";
            Console.WriteLine($"{indent}[{i,4}] (old {ci.OldPC,4}) {ci}{extra}");
        }
        foreach (var child in p.Children)
            DumpLifted(child, depth + 1);
    }

    private static void DumpHandlers(DispatchInfo dispatch, HandlerDb db, Proto root)
    {
        Console.WriteLine("==== Dispatch / handler analysis ====");
        Console.WriteLine($"Handlers in tree: {dispatch.Leaves.Count} (enum ids 0..{dispatch.Leaves.Keys.Max()})");
        Console.WriteLine($"Renamed roles:    Instr={dispatch.InstrName} Inst={dispatch.InstName} " +
                          $"InstrPoint={dispatch.InstrPointName} EnumTemp={dispatch.EnumName}");
        Console.WriteLine($"Field keys:       OP_ENUM={db.KeyEnum} OP_A={db.KeyA} OP_B={db.KeyB} OP_C={db.KeyC}");
        Console.WriteLine($"Roles:            {string.Join(", ", db.Roles.OrderBy(kv => kv.Key).Select(kv => $"{kv.Key}={kv.Value}"))}");

        foreach (var line in db.Log)
            Console.WriteLine(line);

        // enum density check
        var dense = Enumerable.Range(0, dispatch.Leaves.Count).All(dispatch.Leaves.ContainsKey);
        Console.WriteLine($"check {(dense ? "OK" : "FAIL")}: enum ids dense 0..{dispatch.Leaves.Count - 1}");

        // used-enum coverage check
        var used = new HashSet<int>();
        CollectUsedEnums(root, used);
        var uncovered = used.Where(e =>
            !db.Handlers.TryGetValue(e, out var h) || h.Kind == HandlerKind.Unknown).ToList();
        Console.WriteLine(uncovered.Count == 0
            ? $"check OK: all {used.Count} enum ids used by the blob are classified"
            : $"check FAIL: used but unclassified enums: {string.Join(", ", uncovered)}");
        foreach (var e in uncovered)
            foreach (var inst in EnumerateInstructions(root).Where(i => i.Enum == e).Take(5))
                Console.WriteLine($"    usage: {inst}");

        // stats per kind
        Console.WriteLine();
        Console.WriteLine("Handler kind stats (all tree handlers / blob-used only):");
        var usedByKind = db.Handlers.Values
            .GroupBy(h => h.Kind)
            .OrderByDescending(g => g.Count())
            .Select(g => $"  {g.Key,-20} {g.Count(),3} / {g.Count(h => used.Contains(h.EnumId))}");
        foreach (var line in usedByKind)
            Console.WriteLine(line);

        Console.WriteLine();
        Console.WriteLine("Enum -> HandlerInfo:");
        foreach (var id in db.Handlers.Keys.OrderBy(x => x))
            Console.WriteLine($"  {db.Handlers[id]}");
    }

    private static void CollectUsedEnums(Proto proto, HashSet<int> used)
    {
        foreach (var inst in proto.Instructions)
            if (!inst.IsData)
                used.Add(inst.Enum);
        foreach (var child in proto.Children)
            CollectUsedEnums(child, used);
    }

    private static IEnumerable<Instruction> EnumerateInstructions(Proto proto)
    {
        foreach (var inst in proto.Instructions)
            if (!inst.IsData)
                yield return inst;
        foreach (var child in proto.Children)
            foreach (var inst in EnumerateInstructions(child))
                yield return inst;
    }

    private static void DumpProto(Proto proto, int depth, bool verbose)
    {
        var indent = new string(' ', depth * 2);
        var strings = proto.Constants
            .Where(c => c.Type == ConstantKind.String && !c.Tampered)
            .Select(c => (string)c.Data!)
            .Take(5)
            .Select(s => s.Length > 60 ? s[..60] + "..." : s)
            .ToList();
        var tampered = proto.Constants.Count(c => c.Tampered);

        Console.WriteLine($"{indent}Proto: params={proto.ParamCount} " +
                          $"constants={proto.Constants.Count} " +
                          $"instructions={proto.Instructions.Count} " +
                          $"(data slots: {proto.Instructions.Count(i => i.IsData)}) " +
                          $"children={proto.Children.Count}" +
                          (tampered > 0 ? $" tamperedStrings={tampered}" : ""));
        if (strings.Count > 0)
            Console.WriteLine($"{indent}  first strings: {string.Join(", ", strings.Select(s => $"\"{s}\""))}");

        if (verbose)
        {
            var allStrings = proto.Constants
                .Where(c => c.Type == ConstantKind.String && !c.Tampered)
                .Select(c => (string)c.Data!)
                .ToList();
            if (allStrings.Count > 0)
                Console.WriteLine($"{indent}  strings: {string.Join(", ", allStrings.Select(s => $"\"{s}\""))}");
            foreach (var inst in proto.Instructions.Take(40))
                Console.WriteLine($"{indent}  {inst}");
            if (proto.Instructions.Count > 40)
                Console.WriteLine($"{indent}  ... ({proto.Instructions.Count - 40} more instructions)");
        }

        foreach (var child in proto.Children)
            DumpProto(child, depth + 1, verbose);
    }
}
