"""Ironveil plugin: leakd API (needs LEAKD_API_KEY).

Marker from real samples: `--[[ Obfuscated using ironveil v1 - ... ]]`.
No local engine is bundled (upstream tool needs npm install), so without
a key this plugin reports it plainly instead of failing obscurely.
"""
import re
import sys

import leakd
from obfuscators.base import Obfuscator


class Ironveil(Obfuscator):
    name = "ironveil"
    label = "Ironveil"
    doc = ""

    def detect(self, source):
        if re.search(r"Obfuscated using ironveil", source[:500], re.I):
            return 0.95
        return 0.0

    def deobfuscate(self, job):
        key = leakd.api_key(job.args)
        code = leakd.post_file("/ironveil", job.source_path, key,
                               timeout=job.args.timeout)
        if not code.strip():
            sys.exit("[!] ironveil API returned an empty result")
        return job.write(job.path(".ironveil.lua"), code)
