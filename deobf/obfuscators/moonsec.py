"""MoonSec V3 plugin: C# devirt (tools/moonsec) + lua5.1 decom step."""
import os
import subprocess
import sys

import external
import leakd
from obfuscators.base import Obfuscator


class Moonsec(Obfuscator):
    name = "moonsec"
    label = "MoonSec V3"
    doc = ""

    def detect(self, source):
        head = source[:500]
        if "MoonSec V3" in head or "Protected_by_MoonSecV" in head:
            return 0.95
        return 0.0

    def deobfuscate(self, job):
        key = leakd.key_or_none(job.args)
        if key:
            code = leakd.try_post("/moonsec", job.source_path, key,
                                  timeout=job.args.timeout)
            if code:
                return job.write(job.path(".moonsec.lua"), code)
            print("[*] MoonSec API failed; using local tool", file=sys.stderr)
        dotnet = external.need_dotnet()
        dll = external.tool_path("moonsec", "publish", "MoonsecDeobfuscator.dll")
        lua_dir = os.path.join(external.TOOLS, "moonsec-lua")
        lua51 = external.find_lua51(lua_dir)
        decom = external.tool_path("moonsec-lua", "decom.lua")
        mid = job.path(".moonsec.luac")
        out = job.path(".moonsec.lua")
        t = job.args.timeout
        try:
            r = subprocess.run([dotnet, dll, "-dev", "-i", job.source_path, "-o", mid],
                               capture_output=True, text=True, timeout=t)
        except FileNotFoundError:
            sys.exit("[!] dotnet not found (MoonSec needs .NET 8). "
                     "Windows: winget install Microsoft.DotNet.SDK.8.")
        except subprocess.TimeoutExpired:
            sys.exit("[!] moonsec timed out after %ds" % t)
        if r.returncode != 0 or not os.path.exists(mid):
            err = (r.stdout or r.stderr or "").strip().splitlines()
            tail = err[-1][:300] if err else "exit %d" % r.returncode
            sys.exit("[!] moonsec failed: %s" % tail)
        try:
            r2 = subprocess.run([lua51, decom, mid, out],
                                capture_output=True, text=True, timeout=t)
        except FileNotFoundError:
            sys.exit("[!] lua5.1 not found (MoonSec decom step needs it). "
                     "Linux/VPS: apt install lua5.1.")
        except subprocess.TimeoutExpired:
            sys.exit("[!] moonsec decom step timed out after %ds" % t)
        if r2.returncode != 0 or not os.path.exists(out) or os.path.getsize(out) == 0:
            err = (r2.stdout or r2.stderr or "").strip().splitlines()
            tail = err[-1][:300] if err else "exit %d" % r2.returncode
            sys.exit("[!] moonsec decom step failed: %s" % tail)
        return out
