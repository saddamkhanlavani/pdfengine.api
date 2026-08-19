#!/usr/bin/env python3
"""
PDFEngine — PDF/A + PDF/UA Conformance Gate (Release Gate H)

Automates what was previously run by hand. veraPDF evidence existing on disk is not
the same as a gate: it only becomes one when it re-runs on every change and fails the
build on regression.

Usage:
  python3 tests/conformance_gate.py --verapdf /path/to/verapdf [--update-baseline]
Exit non-zero on regression vs the committed baseline.
"""
import argparse, json, os, pathlib, re, subprocess, sys, tempfile
import urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
EVIDENCE = ROOT / "tests" / "evidence"
VERA_DIR = EVIDENCE / "verapdf"
BASELINE = ROOT / "tests" / "corpus" / "conformance-baseline.json"
API = os.environ.get("PDFENGINE_BASE", "http://localhost:5276") + "/api/v1/pdf/generate"
KEY = os.environ.get("PDFENGINE_API_KEY", "test-api-key-123")

# Minimal semantic HTML: headings, a real data table with scope, a figure WITH alt
# text, and NO list markers — the two authoring patterns proven to break PDF/UA are
# deliberately excluded here and covered by the accessibility diagnostics instead.
PX = ("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk"
      "+A8AAQUBAScY42YAAAAASUVORK5CYII=")
DOC = f"""<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><title>Conformance Fixture</title></head>
<body><h1>Quarterly Report</h1><p>Narrative paragraph with real content.</p>
<h2>Financials</h2>
<table><thead><tr><th scope="col">Quarter</th><th scope="col">Revenue</th></tr></thead>
<tbody><tr><td>Q1</td><td>$1.2M</td></tr><tr><td>Q2</td><td>$1.5M</td></tr></tbody></table>
<h2>Chart</h2>
<img src="data:image/png;base64,{PX}" width="80" height="40" alt="Revenue trend chart"/>
</body></html>"""

# name -> (options, veraPDF flavour)
FIXTURES = {
    "pdfa2b-basic":        ({"pdfaCompliance": "PDF/A-2b"}, "2b"),
    "pdfa3b-basic":        ({"pdfaCompliance": "PDF/A-3b"}, "3b"),
    "pdfa2b-tagged":       ({"pdfaCompliance": "PDF/A-2b", "generateTaggedPdf": True}, "2b"),
    "ua1-tagged-only":     ({"generateTaggedPdf": True}, "ua1"),
    "ua1-tagged-pdfa":     ({"pdfaCompliance": "PDF/A-2b", "generateTaggedPdf": True}, "ua1"),
    "pdfa2b-tagged-images": ({"pdfaCompliance": "PDF/A-2b", "generateTaggedPdf": True,
                              "optimizeImages": True}, "2b"),
}


def render(name, options):
    opts = {"pageSize": "A4", "title": "Conformance Fixture", "author": "PdfEngine"}
    opts.update(options)
    payload = json.dumps({"documentName": name, "documentType": 4,
                          "html": DOC, "options": opts}).encode()
    req = urllib.request.Request(API, data=payload, method="POST",
                                 headers={"Content-Type": "application/json",
                                          "X-Api-Key": KEY})
    with urllib.request.urlopen(req, timeout=180) as r:
        return r.read()


def validate(verapdf, pdf_bytes, flavour, out_xml):
    with tempfile.TemporaryDirectory() as td:
        p = pathlib.Path(td) / "d.pdf"
        p.write_bytes(pdf_bytes)
        res = subprocess.run([verapdf, "--flavour", flavour, str(p)],
                             capture_output=True, timeout=300)
    xml = res.stdout.decode("utf-8", "replace")
    out_xml.write_text(xml, encoding="utf-8")
    compliant = 'isCompliant="true"' in xml
    passed = re.search(r'passedRules="(\d+)"', xml)
    failed = re.search(r'failedRules="(\d+)"', xml)
    clauses = re.findall(r'clause="([^"]+)"[^>]*testNumber="(\d+)"[^>]*status="failed"', xml)
    return compliant, (passed.group(1) if passed else "?"), (failed.group(1) if failed else "?"), clauses


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--verapdf", default=os.environ.get("VERAPDF_BIN", "verapdf"))
    ap.add_argument("--update-baseline", action="store_true")
    args = ap.parse_args()

    if not (pathlib.Path(args.verapdf).exists() or
            subprocess.run(["which", args.verapdf], capture_output=True).returncode == 0):
        print(f"FATAL: veraPDF not found at {args.verapdf!r}. Set VERAPDF_BIN.")
        return 2

    # Preflight: prove the validator actually RUNS before interpreting anything it
    # says. Without this, a missing Java made veraPDF emit nothing and every fixture
    # was reported as a conformance REGRESSION -- a tooling fault masquerading as a
    # product defect, which is the worst kind of false signal in a release gate.
    probe = subprocess.run([args.verapdf, "--version"], capture_output=True, timeout=120)
    if b"veraPDF" not in probe.stdout:
        print("FATAL: veraPDF did not run (is Java on PATH?).")
        print("       stdout:", probe.stdout[-200:].decode("utf-8", "replace").strip())
        print("       stderr:", probe.stderr[-200:].decode("utf-8", "replace").strip())
        print("       This is a TOOLING failure, not a conformance regression.")
        return 2

    VERA_DIR.mkdir(parents=True, exist_ok=True)
    results, failed = {}, 0
    print(f"{'fixture':26} {'flavour':8} {'verdict':8} detail")
    print("-" * 100)
    for name, (options, flavour) in FIXTURES.items():
        try:
            pdf = render(name, options)
            ok, p, f, clauses = validate(args.verapdf, pdf, flavour,
                                         VERA_DIR / f"{name}.xml")
            detail = f"{p} passed / {f} failed"
            if clauses:
                detail += "  failing: " + ", ".join(f"{c} t{t}" for c, t in clauses[:3])
        except urllib.error.URLError as e:
            ok, detail = False, f"render failed: {e}"
        except Exception as e:                                # noqa: BLE001
            ok, detail = False, f"exception: {e}"
        v = "PASS" if ok else "FAIL"
        if not ok:
            failed += 1
        results[name] = {"verdict": v, "flavour": flavour, "detail": detail}
        print(f"{name:26} {flavour:8} {v:8} {detail}")

    (EVIDENCE / "conformance-gate.json").write_text(
        json.dumps(results, indent=2), encoding="utf-8")
    print("-" * 100)
    print(f"summary: {len(FIXTURES) - failed}/{len(FIXTURES)} conformant")

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
