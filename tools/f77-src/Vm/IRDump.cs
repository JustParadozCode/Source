namespace deobfuscator.Vm;

/// <summary>Debug printer for the pattern-IR (used by DEOB_DEBUG_ENUM).</summary>
public static class IRDump
{
    public static string Expr(PExpr e) => e switch
    {
        PExpr.Num n => n.V.ToString("0.###"),
        PExpr.Str s => $"'{s.V}'",
        PExpr.Bool x => x.V ? "true" : "false",
        PExpr.Nil => "nil",
        PExpr.Varargs => "...",
        PExpr.Name n => n.Id,
        PExpr.Index i => $"{Expr(i.Target)}[{Expr(i.Key)}]",
        PExpr.Call c => $"{Expr(c.Func)}({string.Join(",", c.Args.Select(Expr))})",
        PExpr.Bin x => $"({Expr(x.L)}{x.Op}{Expr(x.R)})",
        PExpr.Un u => $"({u.Op}{Expr(u.X)})",
        PExpr.Table t =>
            "{" + string.Join(",", t.Items.Select(Expr).Concat(t.Keyed.Select(kv => $"[{Expr(kv.K)}]={Expr(kv.V)}"))) + "}",
        PExpr.Fn f => $"function({string.Join(",", f.Params)}) ... end",
        _ => "?",
    };

    public static string Stmt(PStmt s) => s switch
    {
        PStmt.Local l => $"local {string.Join(",", l.Names)}={string.Join(",", l.Values.Select(Expr))}",
        PStmt.Assign a => $"{string.Join(",", a.Targets.Select(Expr))}={string.Join(",", a.Values.Select(Expr))}",
        PStmt.Expr x => Expr(x.E),
        PStmt.If i => $"if {Expr(i.Cond)} then [{i.Then.Count} st] " +
                      $"{string.Join(" ", i.Elifs.Select(e => $"elseif {Expr(e.Cond)} [{e.Body.Count} st]"))}" +
                      (i.Else != null ? $"else [{i.Else.Count} st]" : "") + " end",
        PStmt.ForNum f => $"for {f.Var}={Expr(f.Init)},{Expr(f.Final)} do [{f.Body.Count} st] end",
        PStmt.ForGen g => $"for {string.Join(",", g.Vars)} in {string.Join(",", g.Exprs.Select(Expr))} do [{g.Body.Count} st] end",
        PStmt.While w => $"while {Expr(w.Cond)} do [{w.Body.Count} st] end",
        PStmt.Do d => $"do [{d.Body.Count} st] end",
        PStmt.Return r => $"return {string.Join(",", r.Exprs.Select(Expr))}",
        PStmt.Break => "break",
        _ => "?",
    };
}
