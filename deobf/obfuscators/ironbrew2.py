"""IronBrew v2 plugin: leakd API (needs LEAKD_API_KEY)."""
import sys

import leakd
from obfuscators.base import Obfuscator


class Ironbrew2(Obfuscator):
    name = "ironbrew2"
    label = "IronBrew v2"
    doc = ""

    def detect(self, source):
        head = source.lstrip()[:3000]
        # Full stdlib-alias prologue (Boronide and others reuse the lone
        # getfenv shim, so require the string/setmetatable aliases too).
        if ("getfenv or function()return _ENV end" in head
                and "=string.byte" in head and "=setmetatable" in head):
            return 0.85
        return 0.0

    def deobfuscate(self, job):
        key = leakd.api_key(job.args)
        code = leakd.post_file("/ironbrew2", job.source_path, key,
                               timeout=job.args.timeout)
        if not code.strip():
            sys.exit("[!] ironbrew2 API returned an empty result")
        return job.write(job.path(".ironbrew2.lua"), code)
