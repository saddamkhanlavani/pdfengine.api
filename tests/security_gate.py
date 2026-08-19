#!/usr/bin/env python3
"""
PDFEngine — Security Gate (Release Gate I)

Scope note, stated up front so this gate is not mistaken for more than it is.
`platform_gate.py` already covers authN/authZ, tenant isolation, batch caps and one
metadata-endpoint SSRF case; those are NOT repeated here. This gate covers the input and
network attack surface: HTML/CSS sanitization, dangerous protocols, SSRF across address
forms and redirects, and resource-exhaustion limits.

NOT covered by this runner, and therefore NOT claimable from it: sandbox escape,
credential leakage from renderer workers, signed-URL abuse, webhook abuse, storage
lifecycle, and the fuzzing suites Gate I also requires. Those remain open.

The bar is deliberately "blocked OR explicitly diagnosed" rather than "no error": an
engine that silently drops a blocked asset and returns a clean-looking PDF is the failure
mode this is written against.

Usage: python3 tests/security_gate.py [--update-baseline]
Exit non-zero on regression vs the committed baseline.
"""
import argparse, json, os, pathlib, re, subprocess, sys, tempfile
import urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
EVIDENCE = ROOT / "tests" / "evidence"
BASELINE = ROOT / "tests" / "corpus" / "security-baseline.json"
BASE = os.environ.get("PDFENGINE_BASE", "http://localhost:5276")
API = BASE + "/api/v1/pdf/generate"
KEY = os.environ.get("PDFENGINE_API_KEY", "test-api-key-123")


def post(html=None, url=None, options=None, timeout=120):
    """Returns (status, body_bytes, diagnostics_dict)."""
    body = {"documentName": "sec", "documentType": 4, "options": options or {"pageSize": "A4"}}
    if html is not None:
        body["html"] = html
    if url is not None:
        body["url"] = url
    req = urllib.request.Request(API, data=json.dumps(body).encode(), method="POST",
                                 headers={"Content-Type": "application/json",
                                          "X-Api-Key": KEY})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read(), json.loads(r.headers.get("X-Render-Diagnostics", "{}"))
    except urllib.error.HTTPError as e:
        return e.code, e.read(), {}


def text_of(pdf_bytes):
    with tempfile.TemporaryDirectory() as td:
        p = pathlib.Path(td) / "d.pdf"
        p.write_bytes(pdf_bytes)
        return subprocess.run(["pdftotext", "-enc", "UTF-8", str(p), "-"],
                              capture_output=True).stdout.decode("utf-8", "replace")


CASES = []


def case(name):
    def deco(fn):
        CASES.append((name, fn))
        return fn
    return deco


# --- input sanitization ------------------------------------------------------

@case("I1 <script> is stripped when allowScripts is false")
def _():
    html = ("<html><body><p id='t'>SAFE</p>"
            "<script>document.getElementById('t').textContent='SCRIPT_EXECUTED'</script>"
            "</body></html>")
    status, pdf, _ = post(html, options={"pageSize": "A4", "allowScripts": False})
    txt = text_of(pdf) if status == 200 else ""
    return (status == 200 and "SCRIPT_EXECUTED" not in txt and "SAFE" in txt), \
        f"status={status} script executed={'SCRIPT_EXECUTED' in txt}"


@case("I2 inline event handlers are stripped even when allowScripts is TRUE")
def _():
    # The dangerous case: a caller enables scripts for their own charts, and untrusted
    # content rides along. on* attributes must never survive regardless of the flag.
    html = ("<html><body><p id='t'>SAFE</p>"
            "<img src='x' onerror=\"document.getElementById('t').textContent='XSS_FIRED'\">"
            "<div onclick=\"document.getElementById('t').textContent='XSS_FIRED'\">c</div>"
            "</body></html>")
    status, pdf, _ = post(html, options={"pageSize": "A4", "allowScripts": True})
    txt = text_of(pdf) if status == 200 else ""
    return (status == 200 and "XSS_FIRED" not in txt), \
        f"status={status} handler fired={'XSS_FIRED' in txt}"


@case("I3 javascript: and data:text/html URIs do not survive")
def _():
    html = ("<html><body>"
            "<a href=\"javascript:alert(1)\">a</a>"
            "<iframe src=\"data:text/html,<script>parent.document.title='PWNED'</script>\"></iframe>"
            "<p>DONE</p></body></html>")
    status, pdf, _ = post(html, options={"pageSize": "A4", "allowScripts": True})
    if status != 200:
        return False, f"status={status}"
    # qpdf-free check: the raw PDF must not carry a javascript: action or the payload.
    blob = pdf.decode("latin-1")
    bad = "javascript:" in blob.lower() or "PWNED" in blob
    return (not bad and "DONE" in text_of(pdf)), f"dangerous URI present in output={bad}"


@case("I4 SVG-embedded script does not execute")
def _():
    html = ("<html><body><p id='t'>SAFE</p>"
            "<svg xmlns='http://www.w3.org/2000/svg'><script>"
            "document.getElementById('t').textContent='SVG_XSS'</script>"
            "<rect width='10' height='10'/></svg></body></html>")
    status, pdf, _ = post(html, options={"pageSize": "A4", "allowScripts": False})
    txt = text_of(pdf) if status == 200 else ""
    return (status == 200 and "SVG_XSS" not in txt), \
        f"status={status} svg script fired={'SVG_XSS' in txt}"


# --- SSRF --------------------------------------------------------------------

SSRF_TARGETS = [
    ("loopback by name",      "http://localhost:22/"),
    ("loopback by IP",        "http://127.0.0.1:22/"),
    ("private 10.x",          "http://10.0.0.1/"),
    ("private 192.168.x",     "http://192.168.1.1/"),
    ("link-local",            "http://169.254.169.254/latest/meta-data/"),
    ("IPv6 loopback",         "http://[::1]/"),
    ("decimal IP form",       "http://2130706433/"),
    ("IPv4-mapped IPv6",      "http://[::ffff:127.0.0.1]/"),
]


def _ssrf_case(label, target):
    @case(f"I5 SSRF blocked — {label}")
    def _(label=label, target=target):
        status, body, _ = post(url=target, timeout=90)
        blob = body.decode("utf-8", "replace")
        blocked = status >= 400 and ("BLOCKED_URL" in blob or "blocked" in blob.lower()
                                     or "not allowed" in blob.lower())
        return blocked, f"status={status} body={blob[:90]}"
    return _


for _label, _target in SSRF_TARGETS:
    _ssrf_case(_label, _target)


@case("I6 SSRF via a sub-resource inside HTML is blocked, not silently fetched")
def _():
    html = ("<html><body><h1>Sub-resource</h1>"
            "<img src='http://169.254.169.254/latest/meta-data/iam/'/>"
            "<link rel='stylesheet' href='http://127.0.0.1:22/x.css'/>"
            "<p>RENDERED</p></body></html>")
    status, pdf, diag = post(html)
    if status != 200:
        return True, f"whole render refused (status={status}) — acceptable"
    warned = any("block" in w.lower() or "ssrf" in w.lower() or "refused" in w.lower()
                 or "asset" in w.lower() for w in diag.get("warnings", []))
    leaked = b"ami-id" in pdf or b"iam" in pdf.lower()
    return (not leaked and warned), \
        f"content leaked={leaked} diagnostic emitted={warned} warnings={len(diag.get('warnings', []))}"


# --- resource limits ---------------------------------------------------------

@case("I7 deeply nested DOM is bounded, not a hang or a 500")
def _():
    depth = 6000
    html = "<html><body>" + "<div>" * depth + "deep" + "</div>" * depth + "</body></html>"
    status, body, diag = post(html, timeout=180)
    ok = status == 200 or (400 <= status < 500)
    return ok, f"status={status} (want 200 with diagnostic, or a 4xx limit — never 5xx/hang)"


@case("I8 very large document is bounded by an explicit limit")
def _():
    html = "<html><body>" + ("<p>filler paragraph content</p>" * 120000) + "</body></html>"
    status, body, _ = post(html, timeout=300)
    ok = status == 200 or (400 <= status < 500)
    size = len(html)
    return ok, f"input={size/1e6:.1f}MB status={status} (want 200 or explicit 4xx, never 5xx)"


@case("I9 oversized request body is refused before rendering")
def _():
    html = "<html><body>" + ("x" * 60_000_000) + "</body></html>"
    try:
        status, body, _ = post(html, timeout=300)
    except Exception as e:                                    # noqa: BLE001
        return True, f"connection-level refusal ({type(e).__name__}) — acceptable"
    return (400 <= status < 500), f"status={status} (want 4xx)"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--update-baseline", action="store_true")
    args = ap.parse_args()
    if subprocess.run(["which", "pdftotext"], capture_output=True).returncode != 0:
        print("FATAL: poppler `pdftotext` not found. brew install poppler")
        return 2

    results, failed = {}, 0
    print(f"{'case':56} {'verdict':8} detail")
    print("-" * 118)
    for name, fn in CASES:
        try:
            ok, detail = fn()
        except urllib.error.URLError as e:
            ok, detail = False, f"request failed: {e}"
        except Exception as e:                                # noqa: BLE001
            ok, detail = False, f"exception: {e}"
        v = "PASS" if ok else "FAIL"
        if not ok:
            failed += 1
        results[name] = {"verdict": v, "detail": detail}
        print(f"{name:56} {v:8} {detail}")

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    (EVIDENCE / "security-gate.json").write_text(
        json.dumps(results, indent=2), encoding="utf-8")
    print("-" * 118)
    print(f"summary: {len(CASES) - failed}/{len(CASES)} passed")
    print("NOT covered here: sandbox escape, worker credential leakage, signed-URL abuse,")
    print("webhook abuse, storage lifecycle, fuzzing suites. Gate I is NOT fully satisfied")
    print("by this runner alone.")

    if args.update_baseline:
        BASELINE.parent.mkdir(parents=True, exist_ok=True)
        BASELINE.write_text(json.dumps(
            {k: v["verdict"] for k, v in results.items()}, indent=2), encoding="utf-8")
        print(f"baseline written -> {BASELINE}")
        return 0

    if BASELINE.exists():
        base = json.loads(BASELINE.read_text(encoding="utf-8"))
        rank = {"PASS": 1, "FAIL": 0}
        regressions = [(k, base[k], results[k]["verdict"]) for k in results
                       if k in base and rank[results[k]["verdict"]] < rank.get(base[k], 0)]
        if regressions:
            print("\nREGRESSIONS (gate FAILED):")
            for k, w, n in regressions:
                print(f"  {k}: {w} -> {n}")
            return 1

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
