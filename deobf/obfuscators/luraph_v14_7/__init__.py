"""Luraph v14.7 plugin: behaviour trace + devirtualizer entry (notes: LURAPH_V14.md).

The v14 VM is a flattened-Lua state machine (`return({...})` of named
methods, `local M=..; while true do if M<.. then ...` dispatch, no
`while true do local op=ARR[PC]` loops, no buffer-based constant pool),
so the v15 bytecode front end does not apply to it directly. This plugin
reuses the version-agnostic parts of the v15 pipeline (trap/chunk reruns,
long-lived harness, trace rendering) with v14-aware instrumentation
(vmmap), and lifts nested v15-shaped payloads when they appear
(compressed wrappers can hide any inner VM). A pure v14 state-machine VM
raises Unsupported in devirt and the driver keeps the behaviour trace.
"""
import re
import sys

from obfuscators.base import Obfuscator

HEADER = re.compile(r"This file was protected using Luraph Obfuscator v(\d+)(?:\.(\d+))?")

# header-stripped fallback shapes (see LURAPH_V14.md, Sample analysis):
#  - direct v14 VM: a plain constructor table  `return({name=function...})`
#  - VM Compression wrapper: `return(function()local ...` + the tail loader
#    (`buffer.fromstring` + "Luraph decompression error"); the tail sits at
#    the end of the file, so it is searched in the last 60 KB.
TABLE_SHAPE = re.compile(r"return\(\{[A-Za-z_]")
WRAPPER_HEAD = re.compile(r"return\(function\(\)local [A-Za-z_,]+=")


class LuraphV14_7(Obfuscator):
    name = "luraph_v14_7"
    label = "Luraph v14.7"
    doc = "LURAPH_V14.md"

    def detect(self, source):
        m = HEADER.search(source[:500])
        if m:
            major, minor = m.group(1), m.group(2) or "0"
            if (major, minor) == ("14", "7"):
                return 1.0
            return 0.3
        # header-stripped fallback: v14 shapes are disjoint from v15's
        # `return setmetatable({[n]=bit32.x,...`, so 0.8 never steals v15.
        head = source.lstrip()[:2000]
        if TABLE_SHAPE.match(head):
            return 0.8
        if WRAPPER_HEAD.match(head) and "Luraph decompression error" in source[-60000:]:
            # the wrapper is version-agnostic (v14.7/v14.9/v15.0 all emit it):
            # every v14 plugin claims it; plugin order decides ties.
            return 0.8
        return 0.0

    def add_arguments(self, ap):
        # Intentionally empty: the v14 pipeline honors the same runtime flags
        # as v15 (--no-hooks, --max-runs, --devirt-rounds, --budget, ...),
        # which luraph_v15 already registers. Re-registering them would clash.
        pass

    def deobfuscate(self, job):
        from obfuscators.luraph_v14_7 import driver
        from obfuscators.luraph_v15 import restore_header_newline
        fixed = restore_header_newline(job.source)
        if fixed != job.source:
            # the copy keeps the input's name (Path2D cache, messages use job.input)
            print("[*] header comment ran into the code (lost newline): split it", file=sys.stderr)
            job.source = fixed
            job.source_path = job.write(job.path(".src.lua"), fixed, encoding="latin-1")
        return driver.run(job)
