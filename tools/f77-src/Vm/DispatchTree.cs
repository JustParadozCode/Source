using Loretta.CodeAnalysis;
using Loretta.CodeAnalysis.Lua;
using Loretta.CodeAnalysis.Lua.Syntax;

namespace deobfuscator.Vm;

/// <summary>
/// Layer 4a: locate the VM dispatch loop in the decompressed VM script and
/// recover, for every virtual opcode enum id, the handler body statements.
///
/// The dispatch loop (from 77main/Obfuscator/VM Generation/VMStrings.cs VMP2/3)
/// is, before renaming:
///
///   while true do
///       Inst = Instr[InstrPoint];
///       Enum = Inst[OP_ENUM];
///       &lt;binary-search if/else tree comparing Enum against integer literals&gt;
///       InstrPoint = InstrPoint + 1;
///   end
///
/// The tree (Rewriters/BinaryTreeGenerator.cs) is nested if/else whose
/// conditions compare the enum temp with literals; CFRewriter mutates the
/// comparisons (`==` ↔ `not (~=)`, `&lt;=` → `(a &lt; b or a == b)`, extra parens),
/// which the static evaluator in Lua.XorKey handles transparently.
/// </summary>
public sealed class DispatchInfo
{
    /// <summary>Renamed identifier playing the Instr (instruction list) role.</summary>
    public required string InstrName;
    /// <summary>Renamed identifier playing the Inst (current instruction) role.</summary>
    public required string InstName;
    /// <summary>Renamed identifier playing the InstrPoint role.</summary>
    public required string InstrPointName;
    /// <summary>Renamed identifier playing the Enum temp role.</summary>
    public required string EnumName;
    /// <summary>OP_ENUM instruction-field key for this build.</summary>
    public int EnumKey;
    /// <summary>enum id -> handler body statements (Loretta nodes, as they appear in the tree).</summary>
    public Dictionary<int, List<StatementSyntax>> Leaves = new();
    /// <summary>
    /// Renamed identifier -> semantic library identity for the VMP1 helper
    /// aliases (`BitXOR = BXOR or function...` etc.), recovered from the
    /// positional `...` unpacking of the loader arguments:
    /// (Byte,Char,Sub,Concat,LDExp,GetFEnv,Setmetatable,Select,Unpack,ToNumber,
    ///  next,table.insert,Floor, BitXOR, BOR, BAnd, GSub, Abs, BRSHIFT, BLSHIFT)
    /// — see 77main/Obfuscator/VM Generation/Compression.cs.
    /// </summary>
    public Dictionary<string, string> LibAliases = new();
}

public static class DispatchTree
{
    public static DispatchInfo Extract(string vmScript)
    {
        var root = LuaSyntaxTree.ParseText(vmScript, new LuaParseOptions(LuaSyntaxOptions.Lua51)).GetRoot();

        var libAliases = RecoverLibAliases(root);

        foreach (var node in root.DescendantNodes())
        {
            if (node is not WhileStatementSyntax loop) continue;
            if (TryMatch(loop) is { } info)
            {
                info.LibAliases = libAliases;
                return info;
            }
        }
        throw new InvalidOperationException("dispatch loop not found in VM script");
    }

    // Loader argument order (Compression.cs / Generator.cs FinalVM call).
    private static readonly string[] LoaderArgs =
    {
        "Byte", "Char", "Sub", "Concat", "LDExp", "GetFEnv", "Setmetatable", "Select",
        "Unpack", "ToNumber", "Next", "Insert", "Floor", "BitXOR", "BOR", "BAnd",
        "GSub", "Abs", "BRSHIFT", "BLSHIFT",
    };

    /// <summary>
    /// Maps renamed script identifiers to the VMP1 bit-helper they alias
    /// (BitXOR/BitBOR/BitBAND/BitRSHIFT/BitLSHIFT). The aliases have the shape
    /// `X = Y or function ... end` where Y is one of the loader-argument locals,
    /// whose position in the `<a,b,...> = ...` unpacking gives its identity.
    /// </summary>
    private static Dictionary<string, string> RecoverLibAliases(SyntaxNode root)
    {
        var argVar = new Dictionary<string, string>(); // local name -> loader arg identity

        foreach (var node in root.DescendantNodes())
        {
            IReadOnlyList<string>? names = null;
            SeparatedSyntaxList<ExpressionSyntax>? values = null;
            switch (node)
            {
                case LocalVariableDeclarationStatementSyntax local
                    when local.EqualsValues is { } ev:
                    names = local.Names.Select(n => n.Name).ToList();
                    values = ev.Values;
                    break;
                case AssignmentStatementSyntax assign:
                    names = assign.Variables.OfType<IdentifierNameSyntax>().Select(n => n.Name).ToList();
                    if (names.Count != assign.Variables.Count) names = null;
                    values = assign.EqualsValues?.Values;
                    break;
            }
            if (names is null || values is not { Count: 1 } v) continue;
            if (v[0] is not VarArgExpressionSyntax || names.Count < LoaderArgs.Length) continue;
            for (var i = 0; i < LoaderArgs.Length; i++)
                argVar[names[i]] = LoaderArgs[i];
        }

        var aliases = new Dictionary<string, string>();
        if (argVar.Count == 0) return aliases;

        // VMP1: local BitXOR = BXOR or function... / BitBAND = BAnd or ... /
        //       BitBOR = BOR or ... / BitRSHIFT = BRSHIFT or ... / BitLSHIFT = BLSHIFT or ...
        var guardToHelper = new Dictionary<string, string>
        {
            ["BitXOR"] = "BitXOR", ["BOR"] = "BitBOR", ["BAnd"] = "BitBAND",
            ["BRSHIFT"] = "BitRSHIFT", ["BLSHIFT"] = "BitLSHIFT",
        };

        foreach (var node in root.DescendantNodes())
        {
            string? target = null;
            ExpressionSyntax? value = null;
            switch (node)
            {
                case LocalVariableDeclarationStatementSyntax
                {
                    Names.Count: 1, EqualsValues.Values.Count: 1,
                } local:
                    target = local.Names[0].Name;
                    value = local.EqualsValues.Values[0];
                    break;
                case AssignmentStatementSyntax
                {
                    Variables.Count: 1, EqualsValues.Values.Count: 1,
                } assign when assign.Variables[0] is IdentifierNameSyntax id:
                    target = id.Name;
                    value = assign.EqualsValues.Values[0];
                    break;
            }
            if (target == null || value == null) continue;
            // X = Y or function(...) ... end   (parens tolerated)
            while (value is ParenthesizedExpressionSyntax paren) value = paren.Expression;
            if (value is not BinaryExpressionSyntax { OperatorToken.Text: "or" } orExpr) continue;
            var left = orExpr.Left;
            while (left is ParenthesizedExpressionSyntax lp) left = lp.Expression;
            if (left is not IdentifierNameSyntax guard) continue;
            if (orExpr.Right is not AnonymousFunctionExpressionSyntax) continue;
            if (argVar.TryGetValue(guard.Name, out var identity)
                && guardToHelper.TryGetValue(identity, out var helper))
                aliases[target] = helper;
        }
        return aliases;
    }

    private static DispatchInfo? TryMatch(WhileStatementSyntax loop)
    {
        // condition must be `true`
        if (loop.Condition is not LiteralExpressionSyntax { Token.Text: "true" }) return null;

        var stmts = loop.Body.Statements;
        if (stmts.Count < 4) return null;

        // stmt[0]: Inst = Instr[InstrPoint]   (all identifiers)
        if (stmts[0] is not AssignmentStatementSyntax fetchInst
            || fetchInst.Variables.Count != 1
            || fetchInst.Variables[0] is not IdentifierNameSyntax instVar
            || fetchInst.EqualsValues.Values.Count != 1
            || fetchInst.EqualsValues.Values[0] is not ElementAccessExpressionSyntax instAccess
            || instAccess.Expression is not IdentifierNameSyntax instrName
            || instAccess.KeyExpression is not IdentifierNameSyntax ipName1)
            return null;

        // stmt[1]: Enum = Inst[<int literal>]
        if (stmts[1] is not AssignmentStatementSyntax fetchEnum
            || fetchEnum.Variables.Count != 1
            || fetchEnum.Variables[0] is not IdentifierNameSyntax enumVar
            || fetchEnum.EqualsValues.Values.Count != 1
            || fetchEnum.EqualsValues.Values[0] is not ElementAccessExpressionSyntax enumAccess
            || enumAccess.Expression is not IdentifierNameSyntax instName2
            || instName2.Name != instVar.Name
            || enumAccess.KeyExpression is not LiteralExpressionSyntax keyLit
            || keyLit.Token.Value is not double keyD || keyD != Math.Floor(keyD))
            return null;

        // last stmt: InstrPoint = InstrPoint + 1
        if (stmts[^1] is not AssignmentStatementSyntax advance
            || advance.Variables.Count != 1
            || advance.Variables[0] is not IdentifierNameSyntax ipVar
            || ipVar.Name != ipName1.Name
            || advance.EqualsValues.Values.Count != 1
            || advance.EqualsValues.Values[0] is not BinaryExpressionSyntax add
            || add.OperatorToken.Text != "+"
            || !((add.Left is IdentifierNameSyntax l && l.Name == ipVar.Name
                  && add.Right is LiteralExpressionSyntax { Token.Value: 1.0 })
                 || (add.Right is IdentifierNameSyntax r && r.Name == ipVar.Name
                     && add.Left is LiteralExpressionSyntax { Token.Value: 1.0 })))
            return null;

        // middle: exactly one giant if statement whose conditions only mention the enum temp
        var middle = stmts.Skip(2).Take(stmts.Count - 3).ToList();
        if (middle.Count != 1 || middle[0] is not IfStatementSyntax tree) return null;
        if (!IsTreeCondition(tree.Condition, enumVar.Name)) return null;

        var info = new DispatchInfo
        {
            InstrName = instrName.Name,
            InstName = instVar.Name,
            InstrPointName = ipVar.Name,
            EnumName = enumVar.Name,
            EnumKey = (int)keyD,
        };

        // collect leaves with their path conditions, then resolve enum ids
        var rawLeaves = new List<(List<StatementSyntax> Body, List<(ExpressionSyntax Cond, bool Polarity)> Path)>();
        Collect(tree, new List<(ExpressionSyntax, bool)>(), enumVar.Name, rawLeaves);

        var count = rawLeaves.Count;
        foreach (var (body, path) in rawLeaves)
        {
            var match = -1;
            for (var candidate = 0; candidate < count; candidate++)
            {
                var ok = true;
                foreach (var (cond, polarity) in path)
                {
                    var value = XorKeyEvaluate(cond, enumVar.Name, candidate);
                    if (value == null || (bool)value != polarity)
                    {
                        ok = false;
                        break;
                    }
                }
                if (!ok) continue;
                if (match != -1)
                    throw new InvalidOperationException(
                        $"dispatch tree: ambiguous leaf (candidates {match} and {candidate})");
                match = candidate;
            }
            if (match == -1)
                throw new InvalidOperationException("dispatch tree: leaf with no matching enum candidate");
            if (!info.Leaves.TryAdd(match, body))
                throw new InvalidOperationException($"dispatch tree: enum {match} resolved twice");
        }

        return info;
    }

    private static object? XorKeyEvaluate(ExpressionSyntax cond, string enumName, int candidate) =>
        Lua.XorKey.Evaluate(cond, new Dictionary<string, object?> { [enumName] = (double)candidate });

    /// <summary>
    /// A condition is a tree branch iff it references the enum temp and
    /// otherwise only integer literals (handler bodies never reference the
    /// enum temp, so leaf ifs fail this test).
    /// </summary>
    private static bool IsTreeCondition(ExpressionSyntax cond, string enumName)
    {
        var mentionsEnum = false;
        var clean = true;
        foreach (var node in cond.DescendantNodesAndSelf())
        {
            switch (node)
            {
                case IdentifierNameSyntax id when id.Name == enumName:
                    mentionsEnum = true;
                    break;
                case IdentifierNameSyntax:
                case FunctionCallExpressionSyntax:
                case ElementAccessExpressionSyntax:
                    clean = false;
                    break;
            }
        }
        return mentionsEnum && clean;
    }

    private static void Collect(
        IfStatementSyntax ifStmt,
        List<(ExpressionSyntax Cond, bool Polarity)> path,
        string enumName,
        List<(List<StatementSyntax>, List<(ExpressionSyntax, bool)>)> leaves)
    {
        void Descend(StatementListSyntax body)
        {
            if (body.Statements.Count == 1 && body.Statements[0] is IfStatementSyntax nested
                && IsTreeCondition(nested.Condition, enumName))
            {
                Collect(nested, path, enumName, leaves);
            }
            else
            {
                leaves.Add((body.Statements.ToList(), path.ToList()));
            }
        }

        path.Add((ifStmt.Condition, true));
        Descend(ifStmt.Body);
        path[^1] = (ifStmt.Condition, false);
        foreach (var elif in ifStmt.ElseIfClauses)
        {
            // BinaryTreeGenerator never emits elseif; handle uniformly anyway.
            path.Add((elif.Condition, true));
            Descend(elif.Body);
            path[^1] = (elif.Condition, false);
        }
        if (ifStmt.ElseClause is { } els)
            Descend(els.ElseBody);
        path.RemoveAt(path.Count - 1);
    }
}
