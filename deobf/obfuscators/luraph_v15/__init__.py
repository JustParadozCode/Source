"""Luraph v14-v15 plugin: devirtualizer + behaviour trace (notes: LURAPH.md).

Unified plugin covering Luraph v14.0 through v15.0. The v15 devirt pipeline
(driver.py, devirt.py, vmmap.py) is handler-agnostic and runs for every
detected Luraph input; when lifting cannot handle a proto (notably the older
v14 `return(function(...))` VM shape, which differs structurally from the v15
`return setmetatable({...})` shape) the driver falls back to the behaviour
trace, exactly as it already does for unliftable v15 protos. No locals are
added to VM instrumentation hooks and getfenv/pcall/setmetatable are never
wrapped (environment fidelity, see CLAUDE.md) — this file adds no hooks.
"""
import os
import re
import sys

import leakd
from obfuscators.base import Obfuscator

# Matches both historical wordings and optional patch part:
#   "-- This file was protected using Luraph Obfuscator v15.0 [https://lura.ph/]"
#   "-- This file was generated using Luraph Obfuscator v14.1 [https://lura.ph/]"
HEADER = re.compile(
    r"This file was (?:protected|generated) using Luraph Obfuscator "
    r"v(\d+)(?:\.(\d+))?(?:\.(\d+))?"
)
HEADER_LINE = re.compile(
    r"\s*--[ \t]*This file was (?:protected|generated) using Luraph Obfuscator "
    r"v[\d.]+[ \t]*\[https?://lura\.ph/?\]"
)


class LuraphV15(Obfuscator):
    name = "luraph"
    label = "Luraph v15"
    doc = "LURAPH.md"

    @staticmethod
    def version_from_source(source):
        """Returns (major, minor, patch) tuple or None. E.g. v14.4.1 -> (14, 4, 1)."""
        m = re.search(
            r"Luraph Obfuscator v(\d+)(?:\.(\d+))?(?:\.(\d+))?",
            source[:500],
        )
        if not m:
            return None
        return (int(m.group(1)), int(m.group(2) or 0), int(m.group(3) or 0))

    @staticmethod
    def version_label(source):
        """Exact label for output credit headers, e.g. 'Luraph v14.4.1'."""
        ver = LuraphV15.version_from_source(source)
        if not ver:
            return "Luraph v15"
        major, minor, patch = ver
        s = "Luraph v%d.%d" % (major, minor)
        if patch:
            s += ".%d" % patch
        return s

    def detect(self, source):
        m = HEADER.search(source[:500])
        if m:
            try:
                major = int(m.group(1))
            except (TypeError, ValueError):
                return 0.3
            if major == 15:
                return 1.0
            # v14 and older: not supported (trace only) — keep confidence
            # below threshold so they fall through to the generic fallback.
            return 0.3
        head = source.lstrip()[:3000]
        # Strip leading comment lines first: faked headers (e.g. a MoonVeil
        # banner pasted over a Luraph VM) must not hide the real VM shape.
        # Body evidence beats header text, so this outranks header-only
        # matches (MoonVeil header = 0.95).
        stripped = re.sub(r"(?m)^\s*--[^\n]*\n", "", head).lstrip()
        # v15: return setmetatable({[n]=bit32.x,...}, ...)
        if stripped.startswith("return setmetatable({") and (
                re.search(r"\[\d+\]=(bit32|buffer|string|table|math)\.\w+", stripped)
                or "LPH" in source[:200000]):
            return 0.96
        return 0.0

    def add_arguments(self, ap):
        g = ap.add_argument_group("Luraph v15")
        g.add_argument("--no-hooks", action="store_true",
                       help="do not instrument VM functions (no anti-tamper trap attribution, no lifting)")
        g.add_argument("--max-runs", type=int, default=12, help="maximum number of trace runs (trap reruns)")
        g.add_argument("--devirt-rounds", type=int, default=200,
                       help="max lift + constant-request rounds (default 200; stops early when no new "
                            "constants turn up)")

    def deobfuscate(self, job):
        from obfuscators.luraph_v15 import driver
        # Exact version in the credit header: "-- Detected obfuscation: Luraph v14.4.1".
        # brand_deobf_file() in the bot strips this before sending, but direct
        # CLI users see the precise source version.
        ver = self.version_from_source(job.source)
        if ver:
            major, minor, patch = ver
            job.obfuscator = "Luraph v%d.%d" % (major, minor)
            if patch:
                job.obfuscator += ".%d" % patch
        else:
            major = 0
        # v15 goes through the leakd API first (keyed); v14 and keyless
        # setups use the local devirt pipeline below (which falls back to
        # the behaviour trace when lifting fails).
        # Fn-stealanegg.lua: fake MoonVeil header + real Luraph v15 VM
        # (return setmetatable...). version is None -> major==0, and forced
        # .luraphDeobf sets obfuscator=luraph. Try API unless known non-v15.
        forced = (getattr(job.args, "obfuscator", "") or "").lower() in (
            "luraph", "luraphv15", "luraph_v15")
        if major == 15 or major == 0 or forced:
            key = leakd.key_or_none(job.args)
            if key:
                code = leakd.try_post_retry("/luraphv15", job.source_path,
                                            key,
                                            timeout=job.args.timeout)
                if code:
                    return job.write(job.path(".luraph.lua"), code)
                print("[*] Luraph API failed; using local devirt",
                      file=sys.stderr)
        fixed = restore_header_newline(job.source)
        if fixed != job.source:
            # the copy keeps the input's name (Path2D cache, messages use job.input)
            print("[*] header comment ran into the code (lost newline): split it", file=sys.stderr)
            job.source = fixed
            job.source_path = job.write(job.path(".src.lua"), fixed, encoding="latin-1")
        result = driver.run(job)
        if result and os.path.exists(result) and has_devirt_stubs(result):
            # The lift surrendered mid-file (emitted error("devirt: ...")
            # stubs instead of code). A behaviour trace is honest output;
            # a half-broken lift is not.
            print("[*] lifted output has devirt error stubs; falling back to behaviour trace",
                  file=sys.stderr)
            from obfuscators.generic import Generic
            return Generic().deobfuscate(job)
        return result


def has_devirt_stubs(path):
    """True when a result file contains engine-emitted devirt error stubs
    (`error("devirt: ...")` as a standalone statement). Conservative: only
    matches the engine's own stub shape, never inline data strings."""
    try:
        with open(path, encoding="utf-8", errors="replace") as f:
            text = f.read()
    except OSError:
        return False
    return bool(re.search(r'(?m)^\s*error\(\s*"devirt:', text))


def restore_header_newline(source):
    """Pasted copies (e.g. Discord's message.txt) can lose the newline after
    the `-- This file was protected ... [https://lura.ph/]` line, which turns
    the whole script into that comment. Put it back."""
    m = HEADER_LINE.match(source)
    if m and source[m.end():m.end() + 1] not in ("", "\n", "\r"):
        return source[:m.end()] + "\n" + source[m.end():].lstrip(" \t")
    return source
