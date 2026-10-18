"""MoonVeil plugin: leakd API (needs LEAKD_API_KEY)."""
import re
import sys

import leakd
from obfuscators.base import Obfuscator


class Moonveil(Obfuscator):
    name = "moonveil"
    label = "MoonVeil"
    doc = ""

    def detect(self, source):
        if re.search(r"generated using MoonVeil", source[:500]):
            return 0.95
        return 0.0

    def deobfuscate(self, job):
        key = leakd.api_key(job.args)
        code = leakd.post_file("/moonveil", job.source_path, key,
                               timeout=job.args.timeout)
        if not code.strip():
            sys.exit("[!] moonveil API returned an empty result")
        return job.write(job.path(".moonveil.lua"), code)
