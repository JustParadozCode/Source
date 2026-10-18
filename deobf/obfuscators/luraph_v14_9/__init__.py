"""Luraph v14.9 plugin: behaviour trace + devirtualizer entry (notes: LURAPH_V14.md).

The v14.9 samples seen are VM Compression wrappers around the same v14
family VM as v14.7/v14.8 (see LURAPH_V14.md), so the pipeline, static map
and front end are shared with `luraph_v14_7` rather than duplicated. Only
detection differs (exact version match)."""
import re
import sys

from obfuscators.base import Obfuscator

HEADER = re.compile(r"This file was protected using Luraph Obfuscator v(\d+)(?:\.(\d+))?")

TABLE_SHAPE = re.compile(r"return\(\{[A-Za-z_]")
WRAPPER_HEAD = re.compile(r"return\(function\(\)local [A-Za-z_,]+=")


class LuraphV14_9(Obfuscator):
    name = "luraph_v14_9"
    label = "Luraph v14.9"
    doc = "LURAPH_V14.md"

    def detect(self, source):
        m = HEADER.search(source[:500])
        if m:
            major, minor = m.group(1), m.group(2) or "0"
            if (major, minor) == ("14", "9"):
                return 1.0
            return 0.3
        # header-stripped fallback: shared v14 family shapes (see LURAPH_V14.md);
        # disjoint from v15's `return setmetatable({`, so v15 is never stolen.
        head = source.lstrip()[:2000]
        if TABLE_SHAPE.match(head):
            return 0.8
        if WRAPPER_HEAD.match(head) and "Luraph decompression error" in source[-60000:]:
            return 0.8
        return 0.0

    def add_arguments(self, ap):
        # Shared runtime flags (--no-hooks, --max-runs, ...) are registered
        # by luraph_v15; re-registering them would clash.
        pass

    def deobfuscate(self, job):
        from obfuscators.luraph_v14_7 import driver
        from obfuscators.luraph_v15 import restore_header_newline
        fixed = restore_header_newline(job.source)
        if fixed != job.source:
            print("[*] header comment ran into the code (lost newline): split it", file=sys.stderr)
            job.source = fixed
            job.source_path = job.write(job.path(".src.lua"), fixed, encoding="latin-1")
        return driver.run(job)
