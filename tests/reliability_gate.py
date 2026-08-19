#!/usr/bin/env python3
"""
PDFEngine — Reliability & Chaos Gate (Release Gate L)

Gate L injects: browser crash · Chromium hang · Redis outage · DB outage · storage outage ·
slow assets · broken fonts · DNS failure · full disk · worker termination · network
partition · webhook destination failure.

Scope, stated plainly. This runner injects the faults reachable from outside the process:
browser/worker termination, DNS failure, unreachable and slow assets, broken fonts, and
malformed input. It does NOT inject Redis/DB/storage outages, full disk, or network
partition — those need container-level control this runner does not take. Gate L is
therefore NOT fully satisfied by this runner alone.

The governing rule under test is the project's own graceful-degradation policy: a
recoverable fault must produce a rendered document plus a diagnostic, and an
unrecoverable one must produce a clear categorised error — never a hang, never a 500, and
never a silently truncated document that looks fine.

Usage: python3 tests/reliability_gate.py [--update-baseline]
"""
import argparse, json, os, pathlib, subprocess, sys, tempfile, time
import urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
EVIDENCE = ROOT / "tests" / "evidence"
BASELINE = ROOT / "tests" / "corpus" / "reliability-baseline.json"
BASE = os.environ.get("PDFENGINE_BASE", "http://localhost:5276")
API = BASE + "/api/v1/pdf/generate"
KEY = os.environ.get("PDFENGINE_API_KEY", "test-api-key-123")

CSS = "<style>@page{size:A4;margin:16mm}body{font-family:sans-serif;font-size:12px}</style>"


def post(html, options=None, timeout=240):
    opts = {"pageSize": "A4"}
    opts.update(options or {})
    payload = json.dumps({"documentName": "rel", "documentType": 4,
                          "html": html, "options": opts}).encode()
    req = urllib.request.Request(API, data=payload, method="POST",
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


def healthy(timeout=120):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(BASE + "/health", timeout=5) as r:
                if r.status == 200:
                    return True
        except Exception:                                     # noqa: BLE001
            pass
        time.sleep(2)
    return False


CASES = []


def case(name):
    def deco(fn):
        CASES.append((name, fn))
        return fn
    return deco


@case("L1 unreachable asset degrades gracefully — document still renders")
def _():
    html = (f"<html><head>{CSS}</head><body><h1>Assets</h1>"
            "<img src='https://definitely-not-a-real-host.invalid/a.png' alt='x'/>"
            "<p>CONTENT_SURVIVED</p></body></html>")
    status, pdf, diag = post(html)
    return (status == 200 and "CONTENT_SURVIVED" in text_of(pdf)), \
        f"status={status} content preserved={'CONTENT_SURVIVED' in text_of(pdf) if status==200 else False}"


@case("L2 broken webfont falls back instead of failing the render")
def _():
    html = ("<html><head><style>"
            "@font-face{font-family:'Broken';src:url('https://nope.invalid/f.woff2') format('woff2')}"
            "@page{size:A4;margin:16mm} body{font-family:'Broken',sans-serif;font-size:14px}"
            "</style></head><body><p>FALLBACK_TEXT_RENDERED</p></body></html>")
    status, pdf, diag = post(html)
    return (status == 200 and "FALLBACK_TEXT_RENDERED" in text_of(pdf)), \
        f"status={status}"


@case("L3 DNS failure is categorised, not a generic 500")
def _():
    payload = json.dumps({"documentName": "rel", "documentType": 4,
                          "url": "https://this-host-does-not-resolve-at-all.invalid/",
                          "options": {"pageSize": "A4"}}).encode()
    req = urllib.request.Request(API, data=payload, method="POST",
                                 headers={"Content-Type": "application/json",
                                          "X-Api-Key": KEY})
    try:
        with urllib.request.urlopen(req, timeout=180) as r:
            return False, f"unexpected success status={r.status}"
    except urllib.error.HTTPError as e:
        body = e.read().decode("utf-8", "replace")
        categorised = 400 <= e.code < 500 and ('"code"' in body)
        return categorised, f"status={e.code} body={body[:100]}"


@case("L4 malformed / unbalanced HTML still produces a document")
def _():
    html = ("<html><body><div><p>UNCLOSED_CONTENT<div><span>"
            "<table><tr><td>cell</body>")
    status, pdf, diag = post(html)
    return (status == 200 and "UNCLOSED_CONTENT" in text_of(pdf)), f"status={status}"


@case("L5 a slow asset does not hang the render past its timeout")
def _():
    # httpbin-style delay is not assumed available; a non-routable address gives a
    # deterministic connect stall instead.
    html = (f"<html><head>{CSS}</head><body>"
            "<img src='http://10.255.255.1/slow.png'/><p>TIMELY</p></body></html>")
    t0 = time.perf_counter()
    status, pdf, diag = post(html, timeout=240)
    elapsed = time.perf_counter() - t0
    ok = status in (200, 400, 408, 504) and elapsed < 200
    return ok, f"status={status} elapsed={elapsed:.1f}s (must not hang)"


@case("L6 engine recovers after the browser process is killed")
def _():
    # Worker/browser termination — the fault most likely to take down a render service.
    before = post(f"<html><head>{CSS}</head><body><p>BEFORE</p></body></html>")[0]
    killed = subprocess.run(
        ["pkill", "-f", "chrome.*--headless|chromium.*--headless|ms-playwright.*chrome"],
        capture_output=True)
    time.sleep(3)
    if not healthy(90):
        return False, "API did not return to healthy after browser kill"
    status, pdf, diag = post(f"<html><head>{CSS}</head><body><p>AFTER_RECOVERY</p></body></html>",
                             timeout=300)
    ok = before == 200 and status == 200 and "AFTER_RECOVERY" in text_of(pdf)
    return ok, f"before={before} kill_rc={killed.returncode} after={status}"


@case("L7 repeated failures do not poison later good renders")
def _():
    for _ in range(3):
        post(f"<html><head>{CSS}</head><body>"
             "<img src='https://nope.invalid/x.png'/></body></html>")
    status, pdf, _ = post(f"<html><head>{CSS}</head><body><p>STILL_HEALTHY</p></body></html>")
    return (status == 200 and "STILL_HEALTHY" in text_of(pdf)), f"status={status}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--update-baseline", action="store_true")
    args = ap.parse_args()
    if subprocess.run(["which", "pdftotext"], capture_output=True).returncode != 0:
        print("FATAL: poppler `pdftotext` not found. brew install poppler")
        return 2
    if not healthy(30):
        print("FATAL: API is not healthy before the run — nothing to test.")
        return 2

    results, failed = {}, 0
    print(f"{'case':60} {'verdict':8} detail")
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
        print(f"{name:60} {v:8} {detail}")

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    (EVIDENCE / "reliability-gate.json").write_text(
        json.dumps(results, indent=2), encoding="utf-8")
    print("-" * 118)
    print(f"summary: {len(CASES) - failed}/{len(CASES)} passed")
    print("NOT injected here: Redis/DB/storage outage, full disk, network partition,")
    print("webhook destination failure. Gate L is NOT fully satisfied by this runner.")

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
