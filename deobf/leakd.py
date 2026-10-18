"""
Client for the leakd deobfuscation API (leakd.up.railway.app).

Used by the API-routed plugins (ironbrew2, goofyscator, clyde, moonveil).
Pure stdlib (urllib): no new dependencies. The key comes from the
LEAKD_API_KEY environment variable (bot .env) or --leakd-key.
"""
import json
import os
import sys
import urllib.request

BASE = "https://leakd.up.railway.app"


def api_key(args=None):
    key = key_or_none(args)
    if not key:
        sys.exit("[!] LEAKD_API_KEY not set. Put your leakd key in the bot .env "
                 "(LEAKD_API_KEY=...) or pass --leakd-key ...")
    return key


def key_or_none(args=None):
    """API key or '' (never exits). For API-first chains with local fallback."""
    key = ""
    if args is not None:
        key = getattr(args, "leakd_key", "") or ""
    return (key or os.environ.get("LEAKD_API_KEY", "")).strip().strip('"').strip("'")


def try_post(endpoint, src_path, api_key_value, timeout=120):
    """post_file() that returns None instead of exiting.

    Lets engine plugins try the API first and fall back to their bundled
    local tool when the key is missing, the API errors, or it times out.
    """
    try:
        code = post_file(endpoint, src_path, api_key_value, timeout=timeout)
    except SystemExit:
        return None
    except Exception:
        return None
    if not isinstance(code, str) or not code.strip():
        return None
    return code


def try_post_retry(endpoint, src_path, api_key_value, timeout=120,
                   attempts=3):
    """try_post() with retries: LeakD endpoints occasionally answer a
    transient 502 ('upstream error') on the first hit and succeed on the
    next. Sleeps 3s between attempts. Returns the code or None."""
    import time
    for i in range(max(1, attempts)):
        code = try_post(endpoint, src_path, api_key_value, timeout=timeout)
        if code:
            return code
        if i + 1 < max(1, attempts):
            print("[*] %s attempt %d/%d failed; retrying..."
                  % (endpoint, i + 1, max(1, attempts)), file=sys.stderr)
            time.sleep(3)
    return None


def _norm_conf(x):
    """Confidence to 0..1 whether the API sends 0..1 or 0..100."""
    try:
        f = float(x)
    except (TypeError, ValueError):
        return 0.0
    if f > 1.0:
        f /= 100.0
    return max(0.0, min(1.0, f))


_API_MAP = {
    "luraph": "luraph", "ironbrew1": "ironbrew1", "ib1": "ironbrew1",
    "ironbrew2": "ironbrew2", "ib2": "ironbrew2",
    "prometheus": "prometheus", "moonsec": "moonsec", "hercules": "hercules",
    "77fuscator": "f77", "f77": "f77", "psu": "psu", "xhider": "xhider",
    "luaobfuscator": "luaobfuscator", "goofyscator": "goofyscator",
    "goofy": "goofyscator", "clyde": "clyde", "moonveil": "moonveil",
    "moonveil2": "moonveil", "moonveil2x": "moonveil",
    "luraphv14": "luraph", "luraphv15": "luraph",
    "ironveil": "ironveil",
    "boronide": None, "ironbrew3": None,
    "synapsexen": None, "synapse": None,
    "wynfuscate": None,
    "generic": "generic", "unknown": "generic",
}


def map_name(api_name):
    """Map a leakd /detect name to a plugin name, or None if unknown."""
    import re as _re
    norm = _re.sub(r"[^a-z0-9]", "", (api_name or "").lower())
    if norm in _API_MAP:
        return _API_MAP[norm]
    m = _re.match(r"^(.*?)v?\d+(?:\.\d+)*$", norm)
    if m and m.group(1) in _API_MAP:
        return _API_MAP[m.group(1)]
    return None


_DETECT_CACHE = {}
_DETECT_CACHE_CAP = 64


def _cache_key(src_path):
    import hashlib
    h = hashlib.sha256()
    with open(src_path, "rb") as f:
        for chunk in iter(lambda: f.read(65536), b""):
            h.update(chunk)
    return h.hexdigest()


def detect_file_cached(src_path, api_key_value, timeout=60):
    """detect_file() with a small in-memory cache (same file = 1 API call)."""
    key = _cache_key(src_path)
    if key in _DETECT_CACHE:
        return _DETECT_CACHE[key]
    res = detect_file(src_path, api_key_value, timeout=timeout)
    if len(_DETECT_CACHE) >= _DETECT_CACHE_CAP:
        _DETECT_CACHE.pop(next(iter(_DETECT_CACHE)))
    _DETECT_CACHE[key] = res
    return res


def detect_file(src_path, api_key_value, timeout=60):
    """Ask leakd /detect what a file is.

    Returns (top_name, top_version, confidence_0_1,
             [(name, version, confidence_0_1), ...]).
    Raises SystemExit with a short reason when unavailable.
    """
    with open(src_path, "rb") as f:
        data = f.read()
    boundary = "------------------------det%x" % (len(data) & 0xFFFFFFFF)
    fname = os.path.basename(src_path) or "script.lua"
    body = (("--%s\r\nContent-Disposition: form-data; name=\"file\"; "
             "filename=\"%s\"\r\nContent-Type: application/octet-stream\r\n\r\n"
             % (boundary, fname)).encode("latin-1")
            + data + ("\r\n--%s--\r\n" % boundary).encode("latin-1"))
    req = urllib.request.Request(
        BASE + "/detect", data=body,
        headers={"X-Api-Key": api_key_value,
                 "Content-Type": "multipart/form-data; boundary=%s" % boundary,
                 "User-Agent": "discord-deobf-bot"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = r.read()
    except Exception as ex:
        raise SystemExit("[!] leakd /detect failed (%s: %s)"
                         % (type(ex).__name__, str(ex)[:150]))
    try:
        obj = json.loads(raw.decode("utf-8"))
        dets = [((d.get("name") or "?"), (d.get("version") or ""),
                 _norm_conf(d.get("confidence", 0)))
                for d in obj.get("detections", []) if isinstance(d, dict)]
        top = obj.get("top_result") or {}
        return ((top.get("name", "?"), (top.get("version") or ""),
                 _norm_conf(top.get("confidence", 0)),
                 sorted(dets, key=lambda t: -t[2])))
    except SystemExit:
        raise
    except Exception as ex:
        raise SystemExit("[!] leakd /detect gave an unreadable reply: %s"
                         % str(ex)[:150])


def post_file(endpoint, src_path, api_key_value, timeout=120):
    """POST a file as multipart/form-data, return the deobfuscated source.

    Handles the shapes leakd-style APIs use: raw lua/text body, or JSON
    carrying the code ({code|result|output|data}) or a download_url.
    sys.exit with the server's message on any failure.
    """
    with open(src_path, "rb") as f:
        data = f.read()
    boundary = "------------------------deobfbot%x" % (len(data) & 0xFFFFFFFF)
    fname = os.path.basename(src_path) or "script.lua"
    body = (("--%s\r\nContent-Disposition: form-data; name=\"file\"; "
             "filename=\"%s\"\r\nContent-Type: application/octet-stream\r\n\r\n"
             % (boundary, fname)).encode("latin-1")
            + data + ("\r\n--%s--\r\n" % boundary).encode("latin-1"))
    req = urllib.request.Request(
        BASE + endpoint, data=body,
        headers={"X-Api-Key": api_key_value,
                 "Content-Type": "multipart/form-data; boundary=%s" % boundary,
                 "User-Agent": "discord-deobf-bot"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            raw = r.read()
            ctype = r.headers.get("Content-Type", "")
    except Exception as ex:
        sys.exit("[!] leakd API request failed (%s: %s)"
                 % (type(ex).__name__, str(ex)[:200]))
    if not raw.strip():
        sys.exit("[!] leakd API returned an empty body")
    text = raw.decode("utf-8", "replace")
    if "json" in ctype or (text.lstrip().startswith("{") and text.rstrip().endswith("}")):
        try:
            obj = json.loads(text)
        except Exception:
            obj = None
        if isinstance(obj, dict):
            if isinstance(obj.get("error"), str) and obj["error"]:
                sys.exit("[!] leakd API error: %s" % obj["error"][:300])
            for k in ("deobfuscated_code", "code", "result", "output", "data",
                        "deobfuscated"):
                if isinstance(obj.get(k), str) and obj[k].strip():
                    return obj[k]
            url = obj.get("download_url") or obj.get("url")
            if isinstance(url, str) and url.startswith("http"):
                try:
                    with urllib.request.urlopen(
                            urllib.request.Request(
                                url, headers={"User-Agent": "discord-deobf-bot"}),
                            timeout=timeout) as r2:
                        return r2.read().decode("utf-8", "replace")
                except Exception as ex:
                    sys.exit("[!] leakd download_url fetch failed: %s" % str(ex)[:200])
            sys.exit("[!] leakd API returned JSON without usable code "
                     "(keys: %s)" % ", ".join(sorted(obj.keys()))[:200])
    return text
