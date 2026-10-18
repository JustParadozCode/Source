"""Xhider plugin: C# deobfuscator (tools/xhider), Lua source out."""
import os
import subprocess
import sys

import external
import leakd
from obfuscators.base import Obfuscator


class Xhider(Obfuscator):
    name = "xhider"
    label = "Xhider"
    doc = ""

    def detect(self, source):
        if "XHD:" in source[:3000]:
            return 0.9
        return 0.0

    def deobfuscate(self, job):
        key = leakd.key_or_none(job.args)
        if key:
            code = leakd.try_post("/xhider", job.source_path, key,
                                  timeout=job.args.timeout)
            if code:
                return job.write(job.path(".xhider.lua"), code)
            print("[*] Xhider API failed; using local tool", file=sys.stderr)
        dotnet = external.need_dotnet()
        dll = external.tool_path("xhider", "publish", "XHiderDeobfuscator.dll")
        out = job.path(".xhider.lua")
        t = job.args.timeout
        try:
            # rc 3 = success with some unknown instructions (file still written)
            r = subprocess.run([dotnet, dll, job.source_path, out],
                               capture_output=True, text=True, timeout=t)
        except FileNotFoundError:
            sys.exit("[!] dotnet not found (Xhider needs .NET 8). "
                     "Windows: winget install Microsoft.DotNet.SDK.8.")
        except subprocess.TimeoutExpired:
            sys.exit("[!] xhider timed out after %ds" % t)
        if r.returncode not in (0, 3) or not os.path.exists(out) \
                or os.path.getsize(out) == 0:
            err = (r.stderr or r.stdout or "").strip().splitlines()
            tail = err[-1][:300] if err else "exit %d" % r.returncode
            sys.exit("[!] xhider failed: %s" % tail)
        return out
