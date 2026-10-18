"""Trace-delegating detect plugins for obfuscators with no local engine.

Boronide, IronBrew v3, SynapseXen and wYnFuscate are identified by strong
markers and routed straight to the generic behaviour trace (the same output
`.l` produces). This keeps `.deobf` fully automatic: detect, then run —
no engine names for users to type.
"""
import re

from obfuscators.base import Obfuscator
from obfuscators.generic import Generic


class _TraceDelegate(Obfuscator):
    """Detect-only plugin: names the obfuscator, runs the generic trace."""

    def deobfuscate(self, job):
        return Generic().deobfuscate(job)


class Boronide(_TraceDelegate):
    name = "boronide"
    label = "Boronide (trace)"

    def detect(self, source):
        head = source[:500]
        if "herrtt" in head.lower():
            return 0.9
        return 0.0


class Ironbrew3(_TraceDelegate):
    name = "ironbrew3"
    label = "IronBrew v3 (trace)"

    def detect(self, source):
        if re.search(r"--ironbrew3:", source[:300]):
            return 0.95
        return 0.0


class SynapseXen(_TraceDelegate):
    name = "synapsexen"
    label = "SynapseXen (trace)"

    def detect(self, source):
        if "SynapseXen_" in source[:2000]:
            return 0.9
        return 0.0


class Wynfuscate(_TraceDelegate):
    name = "wynfuscate"
    label = "wYnFuscate (trace)"

    def detect(self, source):
        if "wYnFuscate" in source[:500]:
            return 0.9
        return 0.0
