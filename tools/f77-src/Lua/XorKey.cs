using Loretta.CodeAnalysis;
using Loretta.CodeAnalysis.Lua;
using Loretta.CodeAnalysis.Lua.Syntax;

namespace deobfuscator.Lua;

/// <summary>
/// Layer 2: recover the PrimaryXorKey from the VM script.
///
/// The key is embedded as `local XOR_KEY = &lt;expr&gt;` where the expression is
/// produced by 77main/Rewriters/NumberObfuscation.cs: typically an IIFE of the
/// shape
///   (function(Enum) local iterations = 0; while true do
///       &lt;if/elseif/else BST comparing Enum to literals; leaves do
///        Enum = Enum +/- offset; iterations = iterations + 1&gt;
///       if iterations == N then break end
///   end; return Enum end)(seed)
/// For key 0 (or non-integral values, which never occur) it is a plain literal.
///
/// The expression is statically evaluated with a small tree-walking evaluator.
/// If anything unsupported is encountered, null is returned and layer 3 falls
/// back to brute-forcing all 256 keys.
/// </summary>
public static class XorKey
{
    private sealed class ReturnSignal : Exception
    {
        public object? Value;
    }

    private sealed class BreakSignal : Exception
    {
    }

    private sealed class Unsupported : Exception
    {
        public Unsupported(string what) : base(what) { }
    }

    /// <summary>
    /// Parses the (decompressed) VM script and tries to statically evaluate the
    /// RHS of `local XOR_KEY = ...`. Returns the key (0-255) or null.
    /// </summary>
    public static int? TryRecover(string vmScript)
    {
        try
        {
            var root = Parse(vmScript);

            foreach (var node in root.DescendantNodes())
            {
                if (node is not LocalVariableDeclarationStatementSyntax local) continue;
                if (local.Names.Count == 0 || local.Names[0].Name != "XOR_KEY") continue;
                if (local.EqualsValues is not { Values.Count: 1 } equalsValues) return null;

                return AsKey(Eval(equalsValues.Values[0], new Dictionary<string, object?>()));
            }

            return null; // no XOR_KEY local found
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Fallback for builds where the Flattener renamed every local (so there is
    /// no `XOR_KEY` identifier left): evaluate EVERY NumberObfuscation-shaped
    /// IIFE in the script and return the distinct values in key range. Layer 3
    /// tries these first before brute-forcing the remaining keys.
    /// </summary>
    public static IReadOnlyList<int> FindCandidateKeys(string vmScript)
    {
        var candidates = new List<int>();
        try
        {
            var root = Parse(vmScript);

            foreach (var node in root.DescendantNodes())
            {
                if (node is not FunctionCallExpressionSyntax
                    {
                        Expression: ParenthesizedExpressionSyntax
                        {
                            Expression: AnonymousFunctionExpressionSyntax func
                        }
                    })
                    continue;
                // NumberObfuscation shape: exactly one parameter, body starts
                // with `local <x> = 0` and contains a `while true` loop.
                if (func.Parameters.Parameters.Count != 1) continue;
                if (func.Body.Statements.Count < 2
                    || func.Body.Statements[0] is not LocalVariableDeclarationStatementSyntax
                    || !func.Body.Statements.Any(s => s is WhileStatementSyntax))
                    continue;

                try
                {
                    if (Eval((ExpressionSyntax)node, new Dictionary<string, object?>()) is { } value
                        && AsKey(value) is { } key
                        && !candidates.Contains(key))
                        candidates.Add(key);
                }
                catch (Exception)
                {
                    // not evaluable; skip
                }
            }
        }
        catch (Exception)
        {
            // fall through with whatever was collected
        }
        return candidates;
    }

    private static SyntaxNode Parse(string vmScript) =>
        LuaSyntaxTree.ParseText(vmScript, new LuaParseOptions(LuaSyntaxOptions.Lua51)).GetRoot();

    /// <summary>
    /// Public wrapper around the tree-walking evaluator, used by the dispatch
    /// tree analysis to evaluate path conditions (comparisons of the enum temp
    /// against integer literals, possibly CFRewriter-mutated). Returns the
    /// Lua value of the expression, or null when it cannot be evaluated.
    /// </summary>
    public static object? Evaluate(ExpressionSyntax expr, Dictionary<string, object?> env)
    {
        try
        {
            return Eval(expr, env);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static int? AsKey(object? value) =>
        value is double d && d == Math.Floor(d) && d is >= 0 and <= 255 ? (int)d : null;

    private static bool Truthy(object? v) => v is not null and not false;

    private static double Num(object? v) =>
        v is double d ? d : throw new Unsupported($"expected number, got {v ?? "nil"}");

    private static object? Eval(ExpressionSyntax expr, Dictionary<string, object?> env)
    {
        switch (expr)
        {
            case LiteralExpressionSyntax lit:
                {
                    var v = lit.Token.Value;
                    if (v is double or bool or string) return v;
                    var text = lit.Token.Text;
                    if (text == "nil") return null;
                    if (text == "true") return true;
                    if (text == "false") return false;
                    if (double.TryParse(text, out var parsed)) return parsed;
                    throw new Unsupported($"literal '{text}'");
                }

            case IdentifierNameSyntax id:
                if (env.TryGetValue(id.Name, out var val)) return val;
                throw new Unsupported($"unknown identifier '{id.Name}'");

            case ParenthesizedExpressionSyntax paren:
                return Eval(paren.Expression, env);

            case UnaryExpressionSyntax un:
                {
                    var op = un.OperatorToken.Text;
                    var operand = Eval(un.Operand, env);
                    return op switch
                    {
                        "-" => -Num(operand),
                        "not" => !Truthy(operand),
                        _ => throw new Unsupported($"unary '{op}'")
                    };
                }

            case BinaryExpressionSyntax bin:
                {
                    var op = bin.OperatorToken.Text;
                    switch (op)
                    {
                        case "and":
                            {
                                var l = Eval(bin.Left, env);
                                return Truthy(l) ? Eval(bin.Right, env) : l;
                            }
                        case "or":
                            {
                                var l = Eval(bin.Left, env);
                                return Truthy(l) ? l : Eval(bin.Right, env);
                            }
                    }

                    var a = Eval(bin.Left, env);
                    var b = Eval(bin.Right, env);
                    return op switch
                    {
                        "+" => Num(a) + Num(b),
                        "-" => Num(a) - Num(b),
                        "*" => Num(a) * Num(b),
                        "/" => Num(a) / Num(b),
                        "%" => Num(a) % Num(b),
                        "^" => Math.Pow(Num(a), Num(b)),
                        "==" => LuaEquals(a, b),
                        "~=" => !LuaEquals(a, b),
                        "<" => Num(a) < Num(b),
                        "<=" => Num(a) <= Num(b),
                        ">" => Num(a) > Num(b),
                        ">=" => Num(a) >= Num(b),
                        _ => throw new Unsupported($"binary '{op}'")
                    };
                }

            case FunctionCallExpressionSyntax call:
                return EvalCall(call, env);

            default:
                throw new Unsupported(expr.Kind().ToString());
        }
    }

    private static bool LuaEquals(object? a, object? b)
    {
        if (a is null || b is null) return a is null && b is null;
        return a.Equals(b);
    }

    private static object? EvalCall(FunctionCallExpressionSyntax call, Dictionary<string, object?> env)
    {
        // Only the IIFE shape is supported: (function(...) ... end)(args)
        if (call.Expression is not ParenthesizedExpressionSyntax
            {
                Expression: AnonymousFunctionExpressionSyntax func
            })
            throw new Unsupported("non-IIFE call");
        if (call.Argument is not ExpressionListFunctionArgumentSyntax argList)
            throw new Unsupported("non-expression-list call argument");

        var childEnv = new Dictionary<string, object?>();
        var parameters = func.Parameters.Parameters;
        for (var i = 0; i < parameters.Count; i++)
        {
            if (parameters[i] is not NamedParameterSyntax named) continue;
            var arg = i < argList.Expressions.Count ? Eval(argList.Expressions[i], env) : null;
            childEnv[named.Name] = arg;
        }

        try
        {
            ExecList(func.Body, childEnv);
        }
        catch (ReturnSignal ret)
        {
            return ret.Value;
        }

        return null; // function fell off the end
    }

    private static void ExecList(StatementListSyntax list, Dictionary<string, object?> env)
    {
        foreach (var statement in list.Statements)
            Exec(statement, env);
    }

    private static void Exec(StatementSyntax stmt, Dictionary<string, object?> env)
    {
        switch (stmt)
        {
            case LocalVariableDeclarationStatementSyntax local:
                for (var i = 0; i < local.Names.Count; i++)
                {
                    var values = local.EqualsValues?.Values;
                    var value = values is { } v && i < v.Count ? Eval(v[i], env) : null;
                    env[local.Names[i].Name] = value;
                }
                break;

            case AssignmentStatementSyntax assign:
                for (var i = 0; i < assign.Variables.Count; i++)
                {
                    if (assign.Variables[i] is not IdentifierNameSyntax id)
                        throw new Unsupported("assignment to non-identifier");
                    var values = assign.EqualsValues?.Values;
                    var value = values is { } v && i < v.Count ? Eval(v[i], env) : null;
                    env[id.Name] = value;
                }
                break;

            case IfStatementSyntax ifStmt:
                if (Truthy(Eval(ifStmt.Condition, env)))
                {
                    ExecList(ifStmt.Body, env);
                    break;
                }
                var taken = false;
                foreach (var clause in ifStmt.ElseIfClauses)
                {
                    if (!Truthy(Eval(clause.Condition, env))) continue;
                    ExecList(clause.Body, env);
                    taken = true;
                    break;
                }
                if (!taken && ifStmt.ElseClause != null)
                    ExecList(ifStmt.ElseClause.ElseBody, env);
                break;

            case WhileStatementSyntax whileStmt:
                {
                    var guard = 0;
                    while (Truthy(Eval(whileStmt.Condition, env)))
                    {
                        if (++guard > 10_000_000)
                            throw new Unsupported("while loop iteration limit");
                        try
                        {
                            ExecList(whileStmt.Body, env);
                        }
                        catch (BreakSignal)
                        {
                            break;
                        }
                    }
                    break;
                }

            case BreakStatementSyntax:
                throw new BreakSignal();

            case ReturnStatementSyntax ret:
                throw new ReturnSignal
                {
                    Value = ret.Expressions.Count > 0 ? Eval(ret.Expressions[0], env) : null
                };

            case ExpressionStatementSyntax exprStmt:
                if (exprStmt.Expression is FunctionCallExpressionSyntax callExpr)
                    EvalCall(callExpr, env);
                else
                    Eval(exprStmt.Expression, env);
                break;

            default:
                throw new Unsupported(stmt.Kind().ToString());
        }
    }
}
