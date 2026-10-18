using Loretta.CodeAnalysis;
using Loretta.CodeAnalysis.Lua;
using Loretta.CodeAnalysis.Lua.Syntax;

namespace deobfuscator.Vm;

/// <summary>
/// A small pattern-IR for Lua statement/expression shapes, used for structural
/// matching of VM handler bodies against templates.
///
/// The conversion from Loretta syntax canonicalizes the mutations applied by
/// 77main/Rewriters/CFRewriter.cs:
///   - extra parentheses are stripped;
///   - `not (a == b)`  -> `a ~= b`   and   `not (a ~= b)` -> `a == b`;
///   - `(a > b or a == b)` (either order) -> `a >= b`, same for `<` -> `<=`;
///   - the numeric-for rewrite
///       `do local i,v = a,b; while true do BODY; if i>=v then break end; i=i+1; end; end`
///     is folded back to `for i=a,b do BODY end` (step 1 is also normalized away);
///   - the generic-for rewrite
///       `do local it,t,i,v = E; while true do i,v = it(t,i); if not i then break end; BODY end end`
///     is folded back to `for i,v in E do BODY end`;
///   - trailing `nil` fields appended to table constructors are stripped.
/// Value equality on records gives structural comparison for free.
/// </summary>
public abstract record PExpr
{
    public sealed record Num(double V) : PExpr;
    public sealed record Str(string V) : PExpr;
    public sealed record Bool(bool V) : PExpr;
    public sealed record Nil() : PExpr;
    public sealed record Varargs() : PExpr;
    public sealed record Name(string Id) : PExpr;
    public sealed record Index(PExpr Target, PExpr Key) : PExpr;
    public sealed record Call(PExpr Func, List<PExpr> Args) : PExpr;
    public sealed record Bin(string Op, PExpr L, PExpr R) : PExpr;
    public sealed record Un(string Op, PExpr X) : PExpr;
    public sealed record Table(List<PExpr> Items, List<(PExpr K, PExpr V)> Keyed) : PExpr;
    public sealed record Fn(List<string> Params, List<PStmt> Body) : PExpr;
}

public abstract record PStmt
{
    public sealed record Local(List<string> Names, List<PExpr> Values) : PStmt;
    public sealed record Assign(List<PExpr> Targets, List<PExpr> Values) : PStmt;
    public sealed record Expr(PExpr E) : PStmt;
    public sealed record If(PExpr Cond, List<PStmt> Then, List<(PExpr Cond, List<PStmt> Body)> Elifs,
        List<PStmt>? Else) : PStmt;
    public sealed record ForNum(string Var, PExpr Init, PExpr Final, List<PStmt> Body) : PStmt;
    public sealed record ForGen(List<string> Vars, List<PExpr> Exprs, List<PStmt> Body) : PStmt;
    public sealed record While(PExpr Cond, List<PStmt> Body) : PStmt;
    public sealed record Do(List<PStmt> Body) : PStmt;
    public sealed record Return(List<PExpr> Exprs) : PStmt;
    public sealed record Break() : PStmt;
}

public static class LuaIR
{
    public static List<PStmt> Convert(StatementListSyntax list) =>
        CanonicalizeLoops(list.Statements.Select(Convert).ToList());

    public static List<PStmt> Convert(IEnumerable<StatementSyntax> statements) =>
        CanonicalizeLoops(statements.Select(Convert).ToList());

    // ---------------------------------------------------------------- statements

    private static PStmt Convert(StatementSyntax stmt)
    {
        switch (stmt)
        {
            case LocalVariableDeclarationStatementSyntax local:
                return new PStmt.Local(
                    local.Names.Select(n => n.Name).ToList(),
                    local.EqualsValues?.Values.Select(Convert).ToList() ?? new List<PExpr>());

            case AssignmentStatementSyntax assign:
                return new PStmt.Assign(
                    assign.Variables.Select(Convert).ToList(),
                    assign.EqualsValues.Values.Select(Convert).ToList());

            case ExpressionStatementSyntax exprStmt:
                return new PStmt.Expr(Convert(exprStmt.Expression));

            case IfStatementSyntax ifStmt:
                return new PStmt.If(
                    Convert(ifStmt.Condition),
                    Convert(ifStmt.Body),
                    ifStmt.ElseIfClauses
                        .Select(c => (Convert(c.Condition), Convert(c.Body)))
                        .ToList(),
                    ifStmt.ElseClause is { } els ? Convert(els.ElseBody) : null);

            case NumericForStatementSyntax forNum:
                {
                    var body = Convert(forNum.Body);
                    // Normalize an explicit step of 1 away so the CFRewriter
                    // do/while form (which hardcodes step 1) compares equal.
                    if (forNum.StepValue is { } step && Convert(step) is not PExpr.Num { V: 1 })
                        throw new NotSupportedException("numeric for with step != 1 in handler body");
                    return new PStmt.ForNum(forNum.Identifier.Name, Convert(forNum.InitialValue),
                        Convert(forNum.FinalValue), body);
                }

            case GenericForStatementSyntax forGen:
                return new PStmt.ForGen(
                    forGen.Identifiers.Select(i => i.Name).ToList(),
                    forGen.Expressions.Select(Convert).ToList(),
                    Convert(forGen.Body));

            case WhileStatementSyntax whileStmt:
                return new PStmt.While(Convert(whileStmt.Condition), Convert(whileStmt.Body));

            case DoStatementSyntax doStmt:
                return new PStmt.Do(Convert(doStmt.Body));

            case ReturnStatementSyntax ret:
                return new PStmt.Return(ret.Expressions.Select(Convert).ToList());

            case BreakStatementSyntax:
                return new PStmt.Break();

            default:
                throw new NotSupportedException($"unsupported statement in handler body: {stmt.Kind()}");
        }
    }

    /// <summary>Folds CFRewriter loop rewrites back to plain for loops.</summary>
    private static List<PStmt> CanonicalizeLoops(List<PStmt> stmts)
    {
        var result = new List<PStmt>();
        foreach (var stmt in stmts)
        {
            if (stmt is PStmt.Do { Body.Count: 2 } doStmt)
            {
                // numeric for: do local i,v = init,fin; while true do BODY; if i>=v then break end; i=i+1; end end
                if (doStmt.Body[0] is PStmt.Local { Names.Count: 2 } decl
                    && doStmt.Body[1] is PStmt.While { Cond: PExpr.Bool { V: true } } wh
                    && wh.Body.Count >= 2
                    && wh.Body[^2] is PStmt.If { Elifs.Count: 0, Else: null, Then.Count: 1 } brk
                    && brk.Then[0] is PStmt.Break
                    && wh.Body[^1] is PStmt.Assign { Targets.Count: 1, Values.Count: 1 } inc
                    && inc.Targets[0] is PExpr.Name incVar
                    && incVar.Id == decl.Names[0]
                    && Strip(inc.Values[0]) is PExpr.Bin { Op: "+", L: PExpr.Name incL, R: PExpr.Num { V: 1 } }
                    && incL.Id == decl.Names[0]
                    && IsGEq(brk.Cond, decl.Names[0], decl.Names[1]))
                {
                    var body = wh.Body.Take(wh.Body.Count - 2).ToList();
                    result.Add(new PStmt.ForNum(decl.Names[0], decl.Values[0], decl.Values[1], body));
                    continue;
                }

                // generic for: do local it,t,i,v = E...; while true do i,v = it(t,i); if not i then break end; BODY end end
                if (doStmt.Body[0] is PStmt.Local { Names.Count: 4 } gdecl
                    && doStmt.Body[1] is PStmt.While { Cond: PExpr.Bool { V: true } } gwh
                    && gwh.Body.Count >= 2
                    && gwh.Body[0] is PStmt.Assign { Targets.Count: 2, Values.Count: 1 } step
                    && step.Targets[0] is PExpr.Name g0 && g0.Id == gdecl.Names[2]
                    && step.Targets[1] is PExpr.Name g1 && g1.Id == gdecl.Names[3]
                    && Strip(step.Values[0]) is PExpr.Call
                    {
                        Func: PExpr.Name gIt, Args.Count: 2
                    } gcall
                    && gIt.Id == gdecl.Names[0]
                    && gcall.Args[0] is PExpr.Name gT && gT.Id == gdecl.Names[1]
                    && gcall.Args[1] is PExpr.Name gI && gI.Id == gdecl.Names[2]
                    && gwh.Body[1] is PStmt.If { Elifs.Count: 0, Else: null, Then.Count: 1 } gbrk
                    && gbrk.Then[0] is PStmt.Break
                    && Strip(gbrk.Cond) is PExpr.Un { Op: "not", X: PExpr.Name gN } && gN.Id == gdecl.Names[2])
                {
                    var body = gwh.Body.Skip(2).ToList();
                    result.Add(new PStmt.ForGen(
                        new List<string> { gdecl.Names[2], gdecl.Names[3] }, gdecl.Values, body));
                    continue;
                }
            }
            result.Add(stmt);
        }
        return result;
    }

    /// <summary>True when cond is `name &gt;= bound` in any mutated form.</summary>
    private static bool IsGEq(PExpr cond, string name, string bound)
    {
        cond = Strip(cond);
        return cond is PExpr.Bin { Op: ">=", L: PExpr.Name l, R: PExpr.Name r }
               && l.Id == name && r.Id == bound;
    }

    // ---------------------------------------------------------------- expressions

    public static PExpr Convert(ExpressionSyntax expr)
    {
        switch (expr)
        {
            case ParenthesizedExpressionSyntax paren:
                return Convert(paren.Expression);

            case LiteralExpressionSyntax lit:
                {
                    var v = lit.Token.Value;
                    return v switch
                    {
                        double d => new PExpr.Num(d),
                        string s => new PExpr.Str(s),
                        bool b => new PExpr.Bool(b),
                        _ => lit.Token.Text switch
                        {
                            "nil" => new PExpr.Nil(),
                            "true" => new PExpr.Bool(true),
                            "false" => new PExpr.Bool(false),
                            var t when double.TryParse(t, out var d) => new PExpr.Num(d),
                            var t => throw new NotSupportedException($"literal '{t}'"),
                        },
                    };
                }

            case VarArgExpressionSyntax:
                return new PExpr.Varargs();

            case IdentifierNameSyntax id:
                return new PExpr.Name(id.Name);

            case ElementAccessExpressionSyntax access:
                return new PExpr.Index(Convert(access.Expression), Convert(access.KeyExpression));

            case MemberAccessExpressionSyntax member:
                // handlers do not use member access, but keep it representable
                return new PExpr.Index(Convert(member.Expression), new PExpr.Str(member.MemberName.Text));

            case FunctionCallExpressionSyntax call:
                {
                    if (call.Argument is not ExpressionListFunctionArgumentSyntax argList)
                        throw new NotSupportedException("non-expression-list call argument in handler body");
                    return new PExpr.Call(Convert(call.Expression),
                        argList.Expressions.Select(Convert).ToList());
                }

            case TableConstructorExpressionSyntax table:
                {
                    var items = new List<PExpr>();
                    var keyed = new List<(PExpr, PExpr)>();
                    foreach (var field in table.Fields)
                    {
                        switch (field)
                        {
                            case UnkeyedTableFieldSyntax unkeyed:
                                items.Add(Convert(unkeyed.Value));
                                break;
                            case ExpressionKeyedTableFieldSyntax ek:
                                keyed.Add((Convert(ek.Key), Convert(ek.Value)));
                                break;
                            case IdentifierKeyedTableFieldSyntax ik:
                                keyed.Add((new PExpr.Str(ik.Identifier.Text), Convert(ik.Value)));
                                break;
                        }
                    }
                    // CFRewriter appends 1-4 nil fields to eligible constructors.
                    while (items.Count > 0 && items[^1] is PExpr.Nil)
                        items.RemoveAt(items.Count - 1);
                    return new PExpr.Table(items, keyed);
                }

            case AnonymousFunctionExpressionSyntax func:
                return new PExpr.Fn(
                    func.Parameters.Parameters.Select(p => p switch
                    {
                        NamedParameterSyntax named => named.Name,
                        VarArgParameterSyntax => "...",
                        _ => "?",
                    }).ToList(),
                    Convert(func.Body));

            case UnaryExpressionSyntax un:
                {
                    var operand = Convert(un.Operand);
                    var op = un.OperatorToken.Text;
                    // CFRewriter: a == b  <->  not (a ~= b). Normalize back.
                    if (op == "not" && operand is PExpr.Bin { Op: "==" } eq)
                        return new PExpr.Bin("~=", eq.L, eq.R);
                    if (op == "not" && operand is PExpr.Bin { Op: "~=" } ne)
                        return new PExpr.Bin("==", ne.L, ne.R);
                    return new PExpr.Un(op, operand);
                }

            case BinaryExpressionSyntax bin:
                {
                    var l = Convert(bin.Left);
                    var r = Convert(bin.Right);
                    var op = bin.OperatorToken.Text;
                    // CFRewriter: a >= b -> (a > b or a == b) [either order]; same for <=.
                    if (op == "or" && Strip(l) is PExpr.Bin rel1 && Strip(r) is PExpr.Bin rel2)
                    {
                        var ge = TryFoldRange(rel1, rel2, ">");
                        if (ge != null) return ge;
                        var le = TryFoldRange(rel1, rel2, "<");
                        if (le != null) return le;
                    }
                    return new PExpr.Bin(op, l, r);
                }

            default:
                throw new NotSupportedException($"unsupported expression in handler body: {expr.Kind()}");
        }
    }

    private static PExpr Strip(PExpr e) => e;

    private static PExpr.Bin? TryFoldRange(PExpr.Bin a, PExpr.Bin b, string strictOp)
    {
        // (a <strictOp> b or a == b) -> a <=/>= b, operands in either order
        if (a.Op == strictOp && b.Op == "==" && a.L.Equals(b.L) && a.R.Equals(b.R))
            return new PExpr.Bin(strictOp + "=", a.L, a.R);
        if (b.Op == strictOp && a.Op == "==" && b.L.Equals(a.L) && b.R.Equals(a.R))
            return new PExpr.Bin(strictOp + "=", b.L, b.R);
        return null;
    }
}
