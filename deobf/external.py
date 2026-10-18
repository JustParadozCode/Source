"""
Shared runners for third-party deobfuscator tools bundled under tools/.

Every runner fails LOUDLY with a one-line actionable message (missing
runtime, missing files) instead of a traceback: deob.py exits 1 and the
Discord bot turns that into a readable embed. Nothing here is imported at
bot startup cost — plugins import this lazily inside deobfuscate().
"""
import os
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
TOOLS = os.path.normpath(os.path.join(HERE, "..", "tools"))


def need(exe, what, hint):
    """Path of an executable or sys.exit with install guidance."""
    p = shutil.which(exe)
    if p:
        return p
    sys.exit("[!] %s not found (%s). %s" % (exe, what, hint))


def need_node():
    return need("node", "Node.js 18+ runs the JS deobfuscators",
                "Windows: winget install OpenJS.NodeJS.LTS. "
                "Replit: add nodejs in the Nix packages.")


def need_dotnet():
    return need("dotnet", ".NET 8 runs the C# deobfuscators",
                "Windows: winget install Microsoft.DotNet.SDK.8. "
                "Linux/VPS: apt install dotnet-runtime-8.0.")


def need_java():
    return need("java", "Java runs unluac (luac -> lua)",
                "Windows: winget install Microsoft.OpenJDK.17. "
                "Linux/VPS: apt install default-jre-headless.")


def tool_path(*parts):
    """Absolute path under tools/, sys.exit if missing."""
    p = os.path.join(TOOLS, *parts)
    if not os.path.exists(p):
        sys.exit("[!] bundled tool missing: tools/%s (re-extract the bot archive)"
                 % "/".join(parts))
    return p


def run(cmd, timeout, cwd=None):
    """Run a tool. Returns stdout text; sys.exit with the tool's stderr tail."""
    try:
        r = subprocess.run(cmd, capture_output=True, text=True,
                           timeout=timeout, cwd=cwd)
    except FileNotFoundError as e:
        sys.exit("[!] cannot start %s (%s)" % (cmd[0], e))
    except subprocess.TimeoutExpired:
        sys.exit("[!] tool timed out after %ds" % timeout)
    if r.returncode != 0:
        err = (r.stderr or r.stdout or "").strip().splitlines()
        tail = err[-1][:300] if err else "exit %d" % r.returncode
        sys.exit("[!] tool failed: %s" % tail)
    return r.stdout


def run_unluac(jar, luac_path, out_lua, timeout):
    """Decompile a .luac binary chunk to Lua source via unluac.jar."""
    need_java()
    try:
        r = subprocess.run(["java", "-jar", jar, luac_path],
                           capture_output=True, text=True, timeout=timeout)
    except FileNotFoundError:
        sys.exit("[!] java not found (unluac needs it). "
                 "Windows: winget install Microsoft.OpenJDK.17.")
    except subprocess.TimeoutExpired:
        sys.exit("[!] unluac timed out after %ds" % timeout)
    if r.returncode != 0 or not (r.stdout or "").strip():
        err = (r.stderr or r.stdout or "").strip().splitlines()
        tail = err[-1][:300] if err else "exit %d" % r.returncode
        sys.exit("[!] unluac failed: %s" % tail)
    with open(out_lua, "w", encoding="utf-8", newline="\n") as f:
        f.write(r.stdout)
    return out_lua


def find_lua51(tool_dir):
    """lua5.1 interpreter for the MoonSec decom step (bundled exe on
    Windows, system binary on Linux)."""
    if os.name == "nt":
        p = os.path.join(tool_dir, "lua5.1.exe")
        if os.path.exists(p):
            return p
        sys.exit("[!] tools/moonsec-lua/lua5.1.exe missing (re-extract the bot archive)")
    p = shutil.which("lua5.1") or shutil.which("lua")
    if p:
        return p
    sys.exit("[!] lua5.1 not found (MoonSec decom step needs it). "
             "Linux/VPS: apt install lua5.1.")
