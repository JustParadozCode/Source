"""Prometheus plugin: bundled Node tool (tools/prometheus)."""
import os
import re
import sys

import external
import leakd
from obfuscators.base import Obfuscator


class Prometheus(Obfuscator):
    name = "prometheus"
    label = "Prometheus"
    doc = ""

    def detect(self, source):
        head = source.lstrip()[:3000]
        # Medium preset: return(function(...)local w={"\069\066\081...", ...}
        if (head.startswith("return(function(...)")
                and len(re.findall(r"\\\d{3}", head)) >= 3):
            return 0.85
        if "rometheus" in source[:500]:
            return 0.9
        return 0.0

    def deobfuscate(self, job):
        key = leakd.key_or_none(job.args)
        if key:
            code = leakd.try_post("/prometheus", job.source_path, key,
                                  timeout=job.args.timeout)
            if code:
                return job.write(job.path(".prometheus.lua"), code)
            print("[*] Prometheus API failed; using local tool", file=sys.stderr)
        node = external.need_node()
        cli = external.tool_path("prometheus", "bin", "pdeobf.js")
        out = job.path(".prometheus.lua")
        external.run([node, cli, job.source_path, "-o", out],
                     timeout=job.args.timeout)
        if not os.path.exists(out) or os.path.getsize(out) == 0:
            sys.exit("[!] prometheus produced no output")
        return out
