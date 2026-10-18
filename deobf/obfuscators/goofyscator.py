"""Goofyscator plugin: leakd API (needs LEAKD_API_KEY).

No reliable local marker exists for Goofyscator payloads, so detect()
stays silent. Reachable via the operator CLI:
`python deobf/deob.py FILE --obfuscator goofyscator`.
"""
import sys

import leakd
from obfuscators.base import Obfuscator


class Goofyscator(Obfuscator):
    name = "goofyscator"
    label = "Goofyscator"
    doc = ""

    def detect(self, source):
        return 0.0

    def deobfuscate(self, job):
        key = leakd.api_key(job.args)
        code = leakd.post_file("/goofyscator", job.source_path, key,
                               timeout=job.args.timeout)
        if not code.strip():
            sys.exit("[!] goofyscator API returned an empty result")
        return job.write(job.path(".goofyscator.lua"), code)
