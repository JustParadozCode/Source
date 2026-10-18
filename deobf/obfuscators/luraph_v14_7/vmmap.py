"""
Luraph v14 static map (see LURAPH_V14.md, Sample analysis).

Sample analysis (v14.7/v14.8/v14.9 vs v15.0, terrorlua/obfuscator-samples):
  A. Headers: exact `-- This file was protected using Luraph Obfuscator
     v14.7|v14.8|v14.9 [https://lura.ph/]` (same format as v15).
  B. VM object: direct v14 VMs open with `return({name=function...})` --
     a plain table with named methods, NO setmetatable, NO numeric-keyed
     library slots. v15 opens with `return setmetatable({[N]=bit32.x,...`.
     Compressed samples (VM Compression option, all of v14.9 seen, some of
     v14.7, one of v15.0) open with `return(function()local ...` and end
     with a tail loader (`buffer.fromstring` + loadstring +
     "Luraph decompression error"); the real VM is the decompressed chunk.
  C. Dispatch: v15 loops read `while true do local op=ARR[PC]; if-tree`.
     v14 direct VMs have ZERO such heads. Their methods are flattened Lua
     state machines: `local M=<n>; while true do if M<../==.. then ...
     else M[,..]=z:METHOD(...); continue/break end` (also `repeat/until
     false` and `while TBL[k] do` outer loops), calling other VM-object
     methods (`z:OR(P)`, `z:YR(M,P)`, `v:H2(u)`).
  D. v15-specific macros: no `buffer.*` library slots in direct v14 VMs
     (only an opaque `buffer` value passed around, e.g. `r=buffer`, and an
     obfuscated `"rea\\x64f\\x33..."` name string). `buffer.fromstring`
     appears only as a VM slot in v15 (`[92]=buffer.fromstring`) and in
     compression-wrapper tails (any version). No `return function(` nested
     state-machine makers in direct v14 VMs (0 hits; v15 has them for
     LPH_JIT/LPH_CRASH expansions).
  E. Closure makers: no `S,h=n,function(...)...end` maker assignment in
     v14 direct VMs. Method signatures repeat the self name
     (`function(z,z,P,M,R)`, `function(v,u,m,f,K)`, `function(z,P,M,R,s)`),
     the same repeated-name family LURAPH.md records for v15 makers, but
     closures are built differently (plain table of methods, no proto
     parameter indexed through itself).
  F. Constant pool: table-based, operands inline (`R[48]()`, `P[0x2E]`,
     `u[0X3]`); no buffer-held pool/offsets (`{[5]=buffer,[6]=offsets}`)
     as in v15. String/number decryption is inline arithmetic/bit32
     (e.g. `%268435456` scrambling appears only in v15's LPH_CRASH
     expansion, not in v14 direct code).

Consequence: v15's `find_dispatchers`/`closure_entries`/`maker_info` find
nothing in a pure v14 direct VM (correct: there is no bytecode dispatcher
to map). This module re-exports all of v15's vmmap (nested v15-shaped
payloads, e.g. inside loadstring'd chunks, still map the same way) and
adds v14 finders for the table-of-methods shape: entry-hook sites for the
driver (`v14_method_entries`) and state-loop locations for the spin
watchdog (`find_state_loops`).
"""
import re

from obfuscators.luraph_v15.vmmap import *  # noqa: F401,F403 - v15 payloads map unchanged
from obfuscators.luraph_v15 import vmmap as _v15


def _unwrap_table(expr):
    """The outermost table of a returned expression, through call chains,
    method indexes and groups: v14 invokes its VM as `return((TABLE):G()(...)`
    (the entry method `G` called with the chunk varargs), so the table is
    the base of the call chain, not the yielded node itself."""
    seen = 0
    while isinstance(expr, dict) and seen < 100:
        seen += 1
        t = expr.get("type")
        if t == "AstExprTable":
            return expr
        if t == "AstExprGroup":
            expr = expr.get("expr")
        elif t == "AstExprCall":
            expr = expr.get("func")
        elif t in ("AstExprIndexName", "AstExprIndexExpr"):
            expr = expr.get("expr")
        else:
            return None
    return None


def find_vm_tables(root):
    """The v14 VM's method table: [table node]. A v14 direct VM returns it
    applied to its entry method (`return((TABLE):G()(...))`); wrapper files
    return a function call instead and yield []."""
    found = []

    def visit(n):
        if isinstance(n, dict):
            if n.get("type") == "AstStatReturn" and n.get("list"):
                for e in n["list"]:
                    t = _unwrap_table(e)
                    if t is not None and any(
                            it.get("value", {}).get("type") == "AstExprFunction"
                            for it in t.get("items", [])):
                        found.append(t)
            for v in n.values():
                visit(v)
        elif isinstance(n, list):
            for v in n:
                visit(v)

    visit(root)
    return found


def _is_state_head(body):
    """A dispatch-loop body flattened over a state local: it opens with an
    `if` on the state instead of `local op=ARR[PC]`."""
    return len(body) >= 1 and body[0].get("type") == "AstStatIf"


def find_state_loops(root):
    """Every `while` loop that dispatches on a state local rather than an
    operand array: [{node}]. Covers `while true do if ...` (v14.7/v14.8
    methods) and `while <cond> do` wrappers whose body holds such a loop;
    v15-style array-head loops are excluded (vmmap.find_dispatchers owns
    those) so the two finders never double-patch."""
    disp_ids = {id(d["node"]) for d in _v15.find_dispatchers(root)}
    found = []

    def visit(n):
        if isinstance(n, dict):
            if n.get("type") == "AstStatWhile" and id(n) not in disp_ids:
                body = n.get("body", {}).get("body", [])
                if _is_state_head(body):
                    found.append({"node": n})
            for v in n.values():
                visit(v)
        elif isinstance(n, list):
            for v in n:
                visit(v)

    visit(root)
    return found


def v14_method_entries(root):
    """For each method of the top-level VM table: (line, col of its first
    statement, method key). The driver hangs its entry hooks there (table
    operations only, like v15's patch_entries): it numbers methods in
    first-entry order for trap attribution (`__PID`/`__SKIPP`). No proto
    capture applies (v14 methods are not closures of a proto parameter)."""
    out = []
    for t in find_vm_tables(root):
        for it in t.get("items", []):
            fn = it.get("value")
            if not isinstance(fn, dict) or fn.get("type") != "AstExprFunction":
                continue
            body = fn.get("body", {}).get("body", [])
            if not body:
                continue
            key = it.get("key")
            if it.get("kind") == "record" and isinstance(key, dict):
                name = key.get("value")
            elif isinstance(key, dict) and key.get("type") == "AstExprConstantString":
                name = key.get("value")
            elif isinstance(key, dict) and key.get("type") == "AstExprConstantNumber":
                name = key.get("value")
            else:
                name = None
            l1, c1, _, _ = _v15.loc(body[0])
            out.append((l1, c1, name))
    return out


def is_v14_direct(root):
    """Whether the AST holds a v14 direct VM (a returned method table) as
    opposed to a v15 dispatcher or a compression wrapper."""
    return bool(find_vm_tables(root)) and not _v15.find_dispatchers(root)
