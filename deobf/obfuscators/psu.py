"""PSU plugin: LeakD API first (needs LEAKD_API_KEY), bundled Node
devirtualizer (tools/psu) + unluac as the local fallback."""
import os
import sys

import external
import leakd
from obfuscators.base import Obfuscator


class Psu(Obfuscator):
    name = "psu"
    label = "PSU"
    doc = ""

    def detect(self, source):
        if "PSU Obfuscator" in source[:500]:
            return 0.95
        return 0.0

    def deobfuscate(self, job):
        key = leakd.key_or_none(job.args)
        if key:
            code = leakd.try_post_retry("/psu", job.source_path, key,
                                        timeout=job.args.timeout)
            if code:
                return job.write(job.path(".psu.lua"), code)
            print("[*] PSU API failed; using local tool", file=sys.stderr)
        node = external.need_node()
        cli = external.tool_path("psu", "src", "cli.js")
        jar = external.tool_path("psu", "tools", "unluac.jar")
        out = job.path(".psu.lua")
        external.run([node, cli, job.source_path, "-o", out,
                      "--unluac", jar],
                     timeout=job.args.timeout)
        if not os.path.exists(out) or os.path.getsize(out) == 0:
            sys.exit("[!] psu produced no output")
        return out
