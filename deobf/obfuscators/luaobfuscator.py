"""LuaObfuscator (.com) plugin: bundled Node tool (tools/luaobfuscator)."""
import re
import subprocess
import sys

import external
import leakd
from obfuscators.base import Obfuscator


class LuaObfuscator(Obfuscator):
    name = "luaobfuscator"
    label = "LuaObfuscator"
    doc = ""

    def detect(self, source):
        head = source[:2000]
        if "LuaObfuscator" in head:
            return 0.9
        if "bit32 or bit" in head:
            return 0.7
        # VM preset: numbered stdlib-alias prologue, no bit32 shim
        # (local v0=tonumber;local v1=string.byte;local v2=string.char;...)
        if re.match(r"local v0=[\w.]+\s*;\s*local v1=", head):
            return 0.8
        return 0.0

    def deobfuscate(self, job):
        key = leakd.key_or_none(job.args)
        if key:
            code = leakd.try_post("/luaobfuscator", job.source_path, key,
                                  timeout=job.args.timeout)
            if code:
                return job.write(job.path(".luaobfuscator.lua"), code)
            print("[*] LuaObfuscator API failed; using local tool", file=sys.stderr)
        node = external.need_node()
        cli = external.tool_path("luaobfuscator", "src", "cli.js")
        try:
            r = subprocess.run(
                [node, cli, job.source_path, "--stdout"],
                capture_output=True, text=True, timeout=job.args.timeout)
        except FileNotFoundError:
            sys.exit("[!] node not found (LuaObfuscator needs Node.js 18+). "
                     "Windows: winget install OpenJS.NodeJS.LTS.")
        except subprocess.TimeoutExpired:
            sys.exit("[!] luaobfuscator timed out after %ds" % job.args.timeout)
        if r.returncode != 0 or not (r.stdout or "").strip():
            err = (r.stderr or r.stdout or "").strip().splitlines()
            tail = err[-1][:300] if err else "exit %d" % r.returncode
            sys.exit("[!] luaobfuscator failed: %s" % tail)
        return job.write(job.path(".luaobfuscator.lua"), r.stdout)
