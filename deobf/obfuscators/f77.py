"""77fuscator plugin: C# lifter (tools/f77) + unluac to Lua source."""
import os
import subprocess
import sys

import external
import leakd
from obfuscators.base import Obfuscator


class F77(Obfuscator):
    name = "f77"
    label = "77fuscator"
    doc = ""

    def detect(self, source):
        head = source[:3000]
        if "77fuscator" in head or "77FUS|" in head:
            return 0.9
        return 0.0

    def deobfuscate(self, job):
        key = leakd.key_or_none(job.args)
        if key:
            code = leakd.try_post("/77fuscator", job.source_path, key,
                                  timeout=job.args.timeout)
            if code:
                return job.write(job.path(".f77.lua"), code)
            print("[*] 77fuscator API failed; using local tool", file=sys.stderr)
        dotnet = external.need_dotnet()
        dll = external.tool_path("f77", "publish", "deobfuscator.dll")
        jar = external.tool_path("psu", "tools", "unluac.jar")
        mid = job.path(".f77.luac")
        out = job.path(".f77.lua")
        t = job.args.timeout
        try:
            r = subprocess.run([dotnet, dll, job.source_path, "-o", mid],
                               capture_output=True, text=True, timeout=t)
        except FileNotFoundError:
            sys.exit("[!] dotnet not found (77fuscator needs .NET 8). "
                     "Windows: winget install Microsoft.DotNet.SDK.8.")
        except subprocess.TimeoutExpired:
            sys.exit("[!] 77fuscator timed out after %ds" % t)
        if r.returncode != 0 or not os.path.exists(mid):
            err = (r.stderr or r.stdout or "").strip().splitlines()
            tail = err[-1][:300] if err else "exit %d" % r.returncode
            sys.exit("[!] 77fuscator failed: %s" % tail)
        return external.run_unluac(jar, mid, out, t)
