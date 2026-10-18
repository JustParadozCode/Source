"""
Obfuscator registry and detection.

To add an obfuscator: write a plugin (a module or package in this folder
with an `Obfuscator` subclass, see base.py and generic.py), then add an
instance to PLUGINS below. deob.py picks the plugin whose detect() is most
confident; `--obfuscator NAME` forces one.
"""
from obfuscators.base import Obfuscator, Job  # noqa: F401
from obfuscators.luraph_v14_7 import LuraphV14_7
from obfuscators.luraph_v14_8 import LuraphV14_8
from obfuscators.luraph_v14_9 import LuraphV14_9
from obfuscators.luraph_v15 import LuraphV15
from obfuscators.ironbrew1 import Ironbrew1
from obfuscators.prometheus import Prometheus
from obfuscators.moonsec import Moonsec
from obfuscators.ironbrew2 import Ironbrew2
from obfuscators.hercules import Hercules
from obfuscators.f77 import F77
from obfuscators.psu import Psu
from obfuscators.xhider import Xhider
from obfuscators.luaobfuscator import LuaObfuscator
from obfuscators.goofyscator import Goofyscator
from obfuscators.clyde import Clyde
from obfuscators.moonveil import Moonveil
from obfuscators.ironveil import Ironveil
from obfuscators.generic import Generic

PLUGINS = [
    LuraphV14_7(),
    LuraphV14_8(),
    LuraphV14_9(),
    LuraphV15(),
    Ironbrew1(),
    Prometheus(),
    Moonsec(),
    Ironbrew2(),
    Hercules(),
    F77(),
    Psu(),
    Xhider(),
    LuaObfuscator(),
    Goofyscator(),
    Clyde(),
    Moonveil(),
    Ironveil(),
    Generic(),      # fallback: behaviour trace only
]

MIN_CONFIDENCE = 0.5    # below this the input counts as unrecognized (the fallback runs)


def by_name(name):
    for p in PLUGINS:
        if p.name == name:
            return p
    # legacy alias: the Luraph plugin lived at luraph_v15/ as "luraph_v15"
    if name in ("luraph_v15", "luraph-v15"):
        for p in PLUGINS:
            if p.name == "luraph":
                return p
    raise KeyError("unknown obfuscator %r (known: %s)" % (name, ", ".join(p.name for p in PLUGINS)))


def scores(source):
    """[(confidence, plugin)], most confident first."""
    out = []
    for p in PLUGINS:
        try:
            c = p.detect(source)
        except Exception:  # noqa: BLE001 - a broken detector must not stop the others
            c = 0.0
        out.append((c, p))
    return sorted(out, key=lambda cp: -cp[0])


def detect(source):
    """(plugin, confidence) for `source`; the generic fallback when no
    plugin is confident enough."""
    best = scores(source)[0]
    if best[0] >= MIN_CONFIDENCE:
        return best[1], best[0]
    return by_name(Generic.name), best[0] if best[1].name == Generic.name else 0.0
