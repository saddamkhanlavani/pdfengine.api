#!/usr/bin/env python3
"""
PDFEngine — Performance Gate (Release Gate K)

Gate K benchmarks 1/5/20/100/500/1000 pages, a 10,000-row table, concurrent renders, cold
and warm start, and font/chart/image-heavy documents; and its PASS condition includes a
sustained 10,000-render run proving memory stays bounded.

Scope is stated honestly: this runner covers page-count scaling, the 10k-row table,
concurrency, and warm-start latency, and it runs a BOUNDED sustained loop (default 200
renders, not 10,000) to detect per-render leakage. The full 10,000-render soak and
cold-start-from-container measurements are NOT performed here, so Gate K is NOT fully
satisfied by this runner alone.

Thresholds are intentionally generous and machine-relative. The purpose is to catch
ORDER-OF-MAGNITUDE regressions — a change that makes a 100-page document take 60s instead
of 6s — not to certify absolute speed on unknown hardware.

Usage: python3 tests/performance_gate.py [--soak N] [--update-baseline]
"""
import argparse, json, os, pathlib, re, statistics, subprocess, sys, tempfile, time
import concurrent.futures as futures
import urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
EVIDENCE = ROOT / "tests" / "evidence"
BASELINE = ROOT / "tests" / "corpus" / "performance-baseline.json"
API = os.environ.get("PDFENGINE_BASE", "http://localhost:5276") + "/api/v1/pdf/generate"
KEY = os.environ.get("PDFENGINE_API_KEY", "test-api-key-123")

CSS = ("<style>@page{size:A4;margin:16mm}body{font-family:sans-serif;font-size:12px}"
       "table{width:100%;border-collapse:collapse}td,th{border:1px solid #999;padding:3px}"
       ".fresh{page-break-before:always}</style>")


def render(name, html, options=None, timeout=600):
    opts = {"pageSize": "A4"}
    opts.update(options or {})
    payload = json.dumps({"documentName": name, "documentType": 4,
                          "html": html, "options": opts}).encode()
    req = urllib.request.Request(API, data=payload, method="POST",
                                 headers={"Content-Type": "application/json",
                                          "X-Api-Key": KEY})
    t0 = time.perf_counter()
    with urllib.request.urlopen(req, timeout=timeout) as r:
        data = r.read()
    return data, (time.perf_counter() - t0) * 1000


def page_count(pdf_bytes):
    with tempfile.TemporaryDirectory() as td:
        p = pathlib.Path(td) / "d.pdf"
        p.write_bytes(pdf_bytes)
        out = subprocess.run(["pdfinfo", str(p)], capture_output=True).stdout
        m = re.search(rb"Pages:\s+(\d+)", out)
        return int(m.group(1)) if m else -1


def doc_of_pages(n):
    return (f"<html><head>{CSS}</head><body>" +
            "".join(f"<div class='fresh'><h2>Section {i}</h2><p>{'Body text. ' * 60}</p></div>"
                    for i in range(n)) +
            "</body></html>")


RESULTS, FAILED = {}, 0


def record(name, ok, detail):
    global FAILED
    v = "PASS" if ok else "FAIL"
    if not ok:
        FAILED += 1
    RESULTS[name] = {"verdict": v, "detail": detail}
    print(f"{name:52} {v:8} {detail}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--soak", type=int, default=200,
                    help="sustained renders for the leak check (Gate K asks for 10,000)")
    ap.add_argument("--update-baseline", action="store_true")
    args = ap.parse_args()
    if subprocess.run(["which", "pdfinfo"], capture_output=True).returncode != 0:
        print("FATAL: poppler `pdfinfo` not found. brew install poppler")
        return 2

    print(f"{'case':52} {'verdict':8} detail")
    print("-" * 112)

    # Warm the browser so the first measured render is not paying startup cost.
    render("warmup", "<html><body>warm</body></html>")

    # --- page-count scaling ---
    per_page = {}
    for pages in (1, 5, 20, 100):
        try:
            pdf, ms = render(f"perf-{pages}p", doc_of_pages(pages))
            got = page_count(pdf)
            per_page[pages] = ms / max(got, 1)
            record(f"K1 {pages:>3} sections render",
                   got >= pages and ms < 120_000,
                   f"{got} pages in {ms:,.0f}ms ({ms/max(got,1):,.0f}ms/page)")
        except Exception as e:                                # noqa: BLE001
            record(f"K1 {pages:>3} sections render", False, f"exception: {e}")

    # Cost per page must not blow up with size — that is the regression that matters.
    if len(per_page) >= 2:
        small, large = per_page.get(1), per_page.get(100)
        if small and large:
            ratio = large / small
            record("K2 per-page cost does not degrade with document size",
                   ratio < 3.0,
                   f"1-page={small:,.0f}ms/pg vs 100-page={large:,.0f}ms/pg (ratio {ratio:.2f}, want <3)")

    # --- 10,000-row table ---
    try:
        rows = "".join(f"<tr><td>{i}</td><td>Item {i}</td><td>{i*7%991}</td><td>OK</td></tr>"
                       for i in range(10_000))
        html = (f"<html><head>{CSS}</head><body><h1>Large table</h1><table>"
                "<thead><tr><th>#</th><th>Name</th><th>Value</th><th>State</th></tr></thead>"
                f"<tbody>{rows}</tbody></table></body></html>")
        pdf, ms = render("perf-10krow", html, timeout=900)
        got = page_count(pdf)
        record("K3 10,000-row table completes",
               got > 50 and ms < 300_000, f"{got} pages in {ms:,.0f}ms")
    except Exception as e:                                    # noqa: BLE001
        record("K3 10,000-row table completes", False, f"exception: {e}")

    # --- concurrency ---
    try:
        doc = doc_of_pages(5)
        t0 = time.perf_counter()
        with futures.ThreadPoolExecutor(max_workers=8) as ex:
            outs = list(ex.map(lambda i: render(f"perf-conc-{i}", doc)[1], range(8)))
        wall = (time.perf_counter() - t0) * 1000
        serial_est = sum(outs)
        record("K4 8 concurrent renders all succeed and overlap",
               len(outs) == 8 and wall < serial_est,
               f"wall={wall:,.0f}ms vs serial-sum={serial_est:,.0f}ms, "
               f"median={statistics.median(outs):,.0f}ms")
    except Exception as e:                                    # noqa: BLE001
        record("K4 8 concurrent renders all succeed and overlap", False, f"exception: {e}")

    # --- sustained run / leak proxy ---
    try:
        doc = doc_of_pages(3)
        times = []
        for i in range(args.soak):
            times.append(render(f"perf-soak-{i}", doc)[1])
        first = statistics.median(times[:max(5, args.soak // 10)])
        last = statistics.median(times[-max(5, args.soak // 10):])
        drift = last / first if first else 0
        # Steadily rising latency across a sustained run is the observable signature of
        # a leak; a bounded engine stays flat.
        record(f"K5 sustained {args.soak} renders stay flat (leak proxy)",
               drift < 1.6,
               f"median first10%={first:,.0f}ms last10%={last:,.0f}ms drift={drift:.2f}x (want <1.6)")
    except Exception as e:                                    # noqa: BLE001
        record(f"K5 sustained {args.soak} renders stay flat (leak proxy)", False, f"exception: {e}")

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    (EVIDENCE / "performance-gate.json").write_text(
        json.dumps(RESULTS, indent=2), encoding="utf-8")
    print("-" * 112)
    print(f"summary: {len(RESULTS) - FAILED}/{len(RESULTS)} passed")
    print(f"NOT covered: the full 10,000-render soak (ran {args.soak}), cold-start from a")
    print("cold container, and 500/1000-page documents. Gate K is NOT fully satisfied here.")

    if args.update_baseline:
        BASELINE.parent.mkdir(parents=True, exist_ok=True)
        BASELINE.write_text(json.dumps(
            {k: v["verdict"] for k, v in RESULTS.items()}, indent=2), encoding="utf-8")
        print(f"baseline written -> {BASELINE}")
        return 0

    if BASELINE.exists():
        base = json.loads(BASELINE.read_text(encoding="utf-8"))
        rank = {"PASS": 1, "FAIL": 0}
        regressions = [(k, base[k], RESULTS[k]["verdict"]) for k in RESULTS
                       if k in base and rank[RESULTS[k]["verdict"]] < rank.get(base[k], 0)]
        if regressions:
            print("\nREGRESSIONS (gate FAILED):")
            for k, w, n in regressions:
                print(f"  {k}: {w} -> {n}")
            return 1

    return 1 if FAILED else 0


if __name__ == "__main__":
    sys.exit(main())
