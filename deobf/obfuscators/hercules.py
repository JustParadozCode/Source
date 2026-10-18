"""Hercules plugin: bundled pure-Python static decompiler (tools/hercules)."""
import os
import re
import subprocess
import sys

import external
import leakd
from obfuscators.base import Obfuscator


class Hercules(Obfuscator):
    name = "hercules"
    label = "Hercules"
    doc = ""

    def detect(self, source):
        head = source[:500]
        if "Obfuscated by Hercules" in head:
            return 1.0
        if "WrapState(BcToState(" in source[:3000]:
            return 0.8
        return 0.0

    def deobfuscate(self, job):
        key = leakd.key_or_none(job.args)
        if key:
            code = leakd.try_post("/hercules", job.source_path, key,
                                  timeout=job.args.timeout)
            if code:
                return job.write(job.path(".hercules.lua"), code)
            print("[*] Hercules API failed; using local tool", file=sys.stderr)
        cli = external.tool_path("hercules", "deobfhercules.py")
        out = job.path(".hercules.lua")
        t = job.args.timeout
        try:
            r = subprocess.run([sys.executable, cli, job.source_path, out,
                                "--timeout", str(t)],
                               capture_output=True, text=True, timeout=t + 10)
        except subprocess.TimeoutExpired:
            sys.exit("[!] hercules timed out after %ds" % t)
        if r.returncode != 0 or not os.path.exists(out) or os.path.getsize(out) == 0:
            err = (r.stderr or r.stdout or "").strip().splitlines()
            tail = err[-1][:300] if err else "exit %d" % r.returncode
            sys.exit("[!] hercules failed: %s" % tail)
        return out
