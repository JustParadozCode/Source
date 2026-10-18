# Luraph v14.7 / v14.8 / v14.9 (`deobf/obfuscators/luraph_v14_*/`)

> Obfuscator notes for the Luraph v14 plugins. Same rules as CLAUDE.md: a
> reference, not a changelog; **hard limit 500 lines**.

The v14 family VM is a flattened-Lua state machine, not the v15 bytecode
interpreter. The plugins trace it (trap/chunk reruns, spin watchdog) and
lift nested v15-shaped payloads when present; a pure v14 state-machine VM
yields the behaviour trace (lifting it needs its own unflattening walk).

- `luraph_v14_7/__init__.py`: `LuraphV14_7` (detect: exact `v14.7` header =
  1.0, other headers = 0.3, header-stripped v14 shapes = 0.8). `v14_8` /
  `v14_9` differ only in their exact version match; their
  `driver.py`/`vmmap.py`/`devirt.py` re-export `luraph_v14_7` (identical
  family structure, documented here instead of triplicated).
- `driver.py`: the v15 pipeline with v14-aware instrumentation (VM-table
  method hooks, state-loop spin counting). Options are v15's
  (`--no-hooks`, `--max-runs`, `--devirt-rounds`); v14 adds none.
- `vmmap.py`: re-exports v15's map (nested v15 payloads map unchanged) +
  `find_vm_tables`, `find_state_loops`, `v14_method_entries`,
  `is_v14_direct`.
- `devirt.py`: gates on v15-shaped dispatchers (raises Unsupported for
  pure v14 inputs -> trace fallback), otherwise delegates to the v15
  machinery by import. v15's JIT/LPH_ENCFUNC/LPH_ENCSTR paths stay live
  through that route, so they are shared, not deleted here.

## Samples (regression reference)

From `terrorlua/obfuscator-samples` (`Luraph/v14.7`, `/v14.8`, `/v14.9`,
`/v15.0`); local copies under `C:\Users\THU\work\Deobfuscator\samples14\`
(`v14.7-a.lua`, `v14.7-b-*.lua`, `v14.8-a.lua`, `v14.8-b-*.lua`,
`v14.9-*.lua`, `v15.0-*.lua`). No sources are published for these.

| Sample | Shape | Trace | Devirtualized |
|---|---|---|---|
| `v14.7-a.lua` (1.2 MB, v14.7 header) | Compression wrapper | Not run yet (needs `bin/luau.exe`; VM Compression is out of scope, same as v15) | Out of scope (double-obfuscated) |
| `v14.7-b-0014079c.lua` (288 KB, v14.7 header; also tested as
`C:\Users\THU\Downloads\0014079c227766b6.lua`) | Direct `return((TABLE):G()(...))` table | Finished, 1 run, 1077 statements: Fluent UI loader (3 HttpGet URLs), 30+ remotes fired, 54 probe lines stripped | Trace fallback by design (state-machine VM) |
| `v14.7-b-00a98a1f.lua` (1.2 MB, v14.7 header) | Compression wrapper | Not run yet | Out of scope (double-obfuscated) |
| `v14.8-a.lua` (147 KB, v14.8 header) | Direct `return({` table | Not run yet | Trace fallback by design |
| `v14.8-b-2b8eea00.lua` (325 KB, v14.8 header) | Direct `return({` table | Not run yet | Trace fallback by design |
| `v14.8-b-2f30b13f.lua` (1.3 MB, v14.8 header) | Direct `return({` table | Not run yet | Trace fallback by design |
| `v14.9-a.lua`, `v14.9-b-*.lua` (1.7-2.4 MB, v14.9 headers) | Compression wrappers (all seen) | Not run yet | Out of scope (double-obfuscated) |
| `v15.0-a.lua` (171 KB, v15.0 header) | `return setmetatable({` | Reference (v15 plugin) | v15 bytecode lift |
| `v15.0-b-9eccab05.lua` (222 KB, v15.0 header) | Compression wrapper | Reference (generic trace; out of scope for v15 too) | Out of scope |
| `printsample (5).lua` (118 KB, v14.7 header; user file, not terrorlua) | Direct table + 1 dispatcher-like loop, 0 makers | Finished, 1 run, 48 statements: `print("hii!! luraph 14.7")`, 57 probe lines stripped | Trace fallback by design |

## VM structure vs v15

- Object: v14 direct = `return((TABLE):G()(...))`, a plain table of
  named methods applied to its entry method `G` with the chunk varargs
  (no setmetatable, no numeric library slots). v15 = `return
  setmetatable({[N]=bit32.x,...})`. Wrappers (any version, VM Compression
  option) = `return(function()local ...` + tail loader (`buffer.fromstring`
  + loadstring + "Luraph decompression error"); the real VM is the
  decompressed chunk, handled as a `\0CHUNK` rerun.
- Dispatch: v14 methods are `local M=<n>; while true do if M<../==..
  then ... else M[,..]=self:METHOD(...); continue/break end` state
  machines over sibling VM-object methods (`z:OR`, `z:YR`, `v:H2`), plus
  `repeat/until false` and `while TBL[k] do` outers. A v14 method can
  occasionally match the v15 head pattern (printsample (5).lua has one
  `while true do local E=u[V]` loop), but with zero closure makers there
  is still nothing to lift: the gate requires dispatchers *and* makers.
  v15's LPH_CRASH expansion (`[0]=function... return function()
  local R,g,t=4; while true do if R<=...` with `%268435456` scrambling)
  has no v14-direct counterpart.
- Constant pool: v14 operands are inline table indexes (`R[48]()`,
  `P[0x2E]`, `u[0X3]`); no buffer-held pool/offsets. `buffer.*` library
  slots are absent (only an opaque `buffer` value: `r=buffer`, plus an
  obfuscated `"rea\x64f\x33..."` name). No `return function(` nested
  makers in direct v14 code (0 hits), so no LPH_JIT-shaped makers there.
- Closures: no `S,h=n,function(...)...end` maker assignment. Method
  signatures repeat the self name (`function(z,z,P,M,R)`,
  `function(v,u,m,f,K)`), the repeated-name family LURAPH.md records,
  but construction differs (plain method table, no proto parameter), so
  there is no `__PA`/`__PK` capture for v14 methods: hooks number methods
  (`__PID[name]`) for trap attribution only.
- Spin: one state-loop iteration ≈ one flattened block (coarser than
  v15's per-instruction step); `SPIN_CHECKS = 24` is kept (same
  endless-loop cutoff order).

## Per-version differences (14.7 vs 14.8 vs 14.9)

None structural found: 14.7 and 14.8 direct VMs are the same family
(method-key names and numeric styles differ per build only:
`n0/HR/H/T0/A0...` vs `RP/p2/S2/CP/Q2...`; 14.7-direct lacks `bit32`
slots, 14.8-direct has them, but that varies by build options, not by
version). All 14.9 samples seen are compression wrappers; their inner
VMs are expected to be the same family (the wrapper tail code is
identical across 14.7/14.9/v15.0). Hence one shared implementation with
per-version detection. If a future 14.8/14.9 sample shows a new shape,
fork only its `vmmap.py` finders and note it here.

## Known limitations

- Pure v14 state-machine VMs are traced, not lifted (no unflattening
  walk yet). Untaken branches are missing from the output, same as any
  trace (CLAUDE.md, Limitations).
- Compression wrappers (VM Compression, any version incl. v15) are out
  of scope: the inner chunk is instrumented and traced when the runtime
  reports it, but not devirtualized.
- Header-stripped files: the three v14 plugins share fallback shapes
  (0.8 each); ties resolve by plugin order (v14.7 first). A
  header-stripped v15 compression wrapper also matches the wrapper
  fallback (v15 scores 0.0 there: its fallback needs the setmetatable
  head), so it lands on a v14 plugin and gets the trace pipeline.
- v15's JIT/ENCFUNC/ENCSTR code is reached only for v15-shaped nested
  payloads; a v15-shaped chunk inside a v14 script lifts, a v14-shaped
  chunk traces.

## Checks

- Detection (no binaries needed, pure regex):
  `python deobf/deob.py <sample> --detect` on every downloaded sample:
  v14.7 files -> `luraph_v14_7  1.0`, v14.8 -> `luraph_v14_8  1.0`,
  v14.9 -> `luraph_v14_9  1.0`, v15.0 -> `luraph_v15  1.0`; no other
  plugin above 0.5 on any of them.
- Imports: `python -c "import obfuscators.luraph_v14_7.driver,
  obfuscators.luraph_v14_8.driver, obfuscators.luraph_v14_9.driver"` from
  `deobf/` (catches registry/wiring breakage without a runtime).
- Full runs need `bin/luau.exe` + `bin/luau-ast.exe` (downloaded/built on
  first use, same as v15); none have been run yet for v14 (see table).
  When they are: `research/compile_check.py` the lifted file (only for
  nested v15 payloads), else compare the trace against a `--no-devirt`
  run (`harness.same_trace`).
