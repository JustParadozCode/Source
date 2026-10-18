"""
Luraph v14 front end (see LURAPH_V14.md).

A pure v14 direct VM is a flattened-Lua state machine, not a bytecode
interpreter: there are no operand arrays, no dispatch `while true do
local op=ARR[PC]` loops, no proto closures, and no buffer-held constant
pool for the v15 stepper to walk. Lifting that shape needs its own
unflattening walk, which is future work (LURAPH_V14.md, Limitations).

What this module does instead, with no forked logic:

- Gate: `lift_program` / `collect_requests` first check the main source
  and every loadstring'd chunk for v15-shaped dispatchers. When none
  exist the input is a pure v14 state-machine VM (or a compression
  wrapper around one) and they raise Unsupported with a clear message;
  the driver's `lift()` catches it and keeps the behaviour trace, exactly
  like any other lifter failure. Nothing is written, nothing is lost.
- Delegate: when a v15-shaped dispatcher IS present (nested v15 payloads
  inside chunks, layered protection), the call goes straight to the v15
  machinery, which is reused by import rather than copied. Its JIT /
  LPH_ENCFUNC / LPH_ENCSTR paths stay live through that route, which is
  why they are shared and not deleted here.
- Re-export the driver-facing names (`WalkCache`, `same_patches`,
  `finish_text`, `run_big_stack`) so the v14 driver needs no fork.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.dirname(os.path.dirname(HERE))     # deobf/: the shared modules
if ROOT not in sys.path:
    sys.path.insert(0, ROOT)

from obfuscators.luraph_v15 import vmmap as _v15map  # noqa: E402
from obfuscators.luraph_v15 import devirt as _v15  # noqa: E402
from obfuscators.luraph_v14_7 import vmmap as _v14map  # noqa: E402
from luasym import Unsupported  # noqa: E402

# driver-facing names, shared (not forked) with v15
WalkCache = _v15.WalkCache
same_patches = _v15.same_patches
finish_text = _v15.finish_text
run_big_stack = _v15.run_big_stack


def _has_v15_shape(path):
    """Whether one source file holds a v15-style bytecode dispatcher."""
    try:
        root = _v15map.load_ast(path)
    except SyntaxError:
        return False
    return bool(_v15map.find_dispatchers(root))


def _has_liftable_shape(path):
    """Whether one source file holds a liftable v15-style VM: a bytecode
    dispatcher *and* a closure maker. Dispatcher-like loops alone are not
    enough (v14 methods occasionally match the head pattern, e.g.
    printsample (5).lua has one dispatcher but zero makers: the v15 walk
    would lift 0 functions and yield an empty file)."""
    try:
        root = _v15map.load_ast(path)
    except SyntaxError:
        return False
    return bool(_v15map.find_dispatchers(root)) and bool(_v15map.maker_info(root))


def _describe_shape(path):
    """One-line shape name for messages: v15-dispatch / v14-table /
    wrapper / unknown."""
    try:
        with open(path, encoding="latin-1", newline="") as f:
            head = f.read(2000)
    except OSError:
        return "unreadable"
    try:
        root = _v15map.load_ast(path)
    except SyntaxError:
        return "unparseable"
    if _v15map.find_dispatchers(root):
        if _v15map.maker_info(root):
            return "v15-dispatch"
        return "v15-dispatch-no-makers"
    if _v14map.find_vm_tables(root):
        return "v14-table"
    if head.lstrip().startswith("return(function()"):
        return "wrapper"
    return "unknown"


def gate(source_path, chunk_paths=()):
    """Raise Unsupported when no liftable v15-style VM exists in the main
    source or any chunk (pure v14 input: the trace is the result)."""
    paths = [source_path] + list(chunk_paths)
    if any(_has_liftable_shape(p) for p in paths):
        return
    shapes = ", ".join("%s=%s" % (os.path.basename(p), _describe_shape(p)) for p in paths)
    raise Unsupported("no v15-style VM to lift (%s): the v14 state-machine VM "
                      "lifts to the behaviour trace instead" % shapes)


def lift_program(source, protos_path, chunk_paths=(), fetch=None):
    """The root proto of every VM -> Luau text (plus stats and constant requests)."""
    gate(source, chunk_paths)
    return _v15.lift_program(source, protos_path, chunk_paths, fetch)


def collect_requests(source, protos_path, chunk_paths=(), cache=None, fetch=None):
    """What an intermediate constant round needs from lift_program, without
    the structuring/codegen/naming. -> (stats, requests, buffer patches)."""
    gate(source, chunk_paths)
    if cache is None:
        cache = WalkCache()
    return _v15.collect_requests(source, protos_path, chunk_paths, cache, fetch)


def main():
    """Devirt debug CLI: `python obfuscators/luraph_v14_7/devirt.py <src>
    <protos.json> [--chunk FILE ...]` (other v15 devirt flags apply after
    the gate, when a v15-shaped payload is present)."""
    args = sys.argv[1:]
    chunks = []
    for i, a in enumerate(args):
        if a == "--chunk" and i + 1 < len(args):
            chunks.append(args[i + 1])
    if len(args) >= 1:
        gate(args[0], chunks)
    _v15.main()


if __name__ == "__main__":
    run_big_stack(main)
