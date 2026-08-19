#!/usr/bin/env python3
"""
PDFEngine — Document Navigation Gate (Release Gate G)

Gate G requires: bookmarks/outline · internal links · external links · table of contents ·
page-number cross-references derived from ACTUAL final PDF page numbers (never a simulated
counter) · named anchors · destinations · metadata · document language · structure tree.

The cross-reference requirement is the sharp one, and it is why this gate exists: page
references were implemented wrong TWICE in this repo — first by counting only forced
breaks, then by a scroll-height estimate — and both times the output looked plausible.
Only a check against the real rendered PDF catches that class of bug, so every assertion
here reads the produced PDF rather than the engine's own belief about it.

Usage: python3 tests/navigation_gate.py [--update-baseline]
Exit non-zero on regression vs the committed baseline.
"""
import argparse, json, os, pathlib, re, subprocess, sys, tempfile
import urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
EVIDENCE = ROOT / "tests" / "evidence"
BASELINE = ROOT / "tests" / "corpus" / "navigation-baseline.json"
API = os.environ.get("PDFENGINE_BASE", "http://localhost:5276") + "/api/v1/pdf/generate"
KEY = os.environ.get("PDFENGINE_API_KEY", "test-api-key-123")

CSS = """<style>
@page { size: A4; margin: 20mm 16mm; }
body { font-family: sans-serif; font-size: 12px; }
.fresh { page-break-before: always; }
</style>"""


def render(name, html, options=None):
    opts = {"pageSize": "A4"}
    opts.update(options or {})
    payload = json.dumps({"documentName": name, "documentType": 4,
                          "html": html, "options": opts}).encode()
    req = urllib.request.Request(API, data=payload, method="POST",
                                 headers={"Content-Type": "application/json",
                                          "X-Api-Key": KEY})
    with urllib.request.urlopen(req, timeout=180) as r:
        return r.read()


def with_pdf(pdf_bytes):
    """Writes the PDF to a temp path and returns (path, TemporaryDirectory)."""
    td = tempfile.TemporaryDirectory()
    p = pathlib.Path(td.name) / "d.pdf"
    p.write_bytes(pdf_bytes)
    return p, td


def pdfinfo(pdf_bytes, extra=()):
    p, td = with_pdf(pdf_bytes)
    with td:
        return subprocess.run(["pdfinfo", *extra, str(p)],
                              capture_output=True).stdout.decode("utf-8", "replace")


def pages_text(pdf_bytes):
    p, td = with_pdf(pdf_bytes)
    with td:
        n = int(re.search(r"Pages:\s+(\d+)", pdfinfo(pdf_bytes)).group(1))
        out = []
        for i in range(1, n + 1):
            t = p.with_name(f"{i}.txt")
            subprocess.run(["pdftotext", "-enc", "UTF-8", "-f", str(i), "-l", str(i),
                            str(p), str(t)], check=True, capture_output=True)
            out.append(t.read_text(encoding="utf-8"))
        return out


def raw(pdf_bytes):
    """Uncompressed PDF source, for structures poppler does not surface."""
    p, td = with_pdf(pdf_bytes)
    with td:
        r = subprocess.run(["qpdf", "--qdf", "--object-streams=disable",
                            str(p), str(p.with_name("q.pdf"))], capture_output=True)
        target = p.with_name("q.pdf") if r.returncode == 0 else p
        return target.read_bytes().decode("latin-1")


CASES = []


def case(name):
    def deco(fn):
        CASES.append((name, fn))
        return fn
    return deco


@case("G1 outline is generated from headings with correct nesting")
def _():
    html = (f"<html><head>{CSS}</head><body>"
            "<h1>Annual Report</h1><p>intro</p>"
            "<div class='fresh'><h2>Financials</h2><p>a</p><h3>Revenue</h3><p>b</p></div>"
            "<div class='fresh'><h2>Risk</h2><p>c</p></div></body></html>")
    pdf = render("gate-g1-outline", html, {"generateOutlineFromHeadings": True})
    src = raw(pdf)
    titles = ["Annual Report", "Financials", "Revenue", "Risk"]
    # Outline titles are stored as PDF strings; UTF-16BE hex for non-ASCII, literal here.
    found = [t for t in titles if f"({t})" in src or t.encode("utf-16-be").hex().upper() in src.upper()]
    has_outlines = "/Outlines" in src
    return (has_outlines and len(found) == len(titles)), \
        f"/Outlines={has_outlines} titles found={len(found)}/{len(titles)} missing={[t for t in titles if t not in found]}"


@case("G2 page cross-references match the REAL page, not a simulated counter")
def _():
    secs = ["overview", "methodology", "results", "appendix"]
    toc = "".join(f"<div>{s} .......... <span data-pdfengine-pageref='{s}'></span></div>"
                  for s in secs)
    body = "".join(f"<div class='fresh'><h2 id='{s}'>Section {s}</h2>"
                   f"<p>{'Filler. ' * 80}</p></div>" for s in secs)
    html = f"<html><head>{CSS}</head><body><h1>Contents</h1>{toc}{body}</body></html>"
    pdf = render("gate-g2-xref", html, {"enablePageReferences": True})
    pages = pages_text(pdf)
    wrong, claimed = [], {}
    for s in secs:
        m = re.search(rf"{s} \.+ (\d+)", pages[0])
        claimed[s] = int(m.group(1)) if m else None
        actual = next((i + 1 for i, t in enumerate(pages) if f"Section {s}" in t), None)
        if claimed[s] != actual:
            wrong.append(f"{s}: toc={claimed[s]} actual={actual}")
    return (not wrong and all(v is not None for v in claimed.values())), \
        f"resolved={sum(v is not None for v in claimed.values())}/{len(secs)} mismatches={wrong}"


@case("G3 internal anchor links produce real PDF link annotations")
def _():
    html = (f"<html><head>{CSS}</head><body>"
            "<h1>Index</h1><p><a href='#target'>Jump to target</a></p>"
            "<div class='fresh'><h2 id='target'>Target Section</h2><p>arrived</p></div>"
            "</body></html>")
    pdf = render("gate-g3-internal", html)
    src = raw(pdf)
    has_link = "/Subtype /Link" in src or "/Subtype/Link" in src
    # An internal link resolves to a destination, not a URI action.
    has_dest = "/Dest" in src or "/GoTo" in src
    return (has_link and has_dest), f"link annotation={has_link} destination={has_dest}"


@case("G4 external links are preserved with their URI intact")
def _():
    html = (f"<html><head>{CSS}</head><body><h1>Links</h1>"
            "<p><a href='https://example.com/pricing'>Pricing</a></p></body></html>")
    pdf = render("gate-g4-external", html)
    src = raw(pdf)
    has_uri = "https://example.com/pricing" in src
    has_action = "/URI" in src
    return (has_uri and has_action), f"URI preserved={has_uri} /URI action={has_action}"


@case("G5 document metadata and language reach the PDF catalog")
def _():
    html = ("<!DOCTYPE html><html lang='en'><head><meta charset='utf-8'>"
            f"{CSS}</head><body><h1>Meta</h1><p>x</p></body></html>")
    pdf = render("gate-g5-meta", html, {"title": "Quarterly Review",
                                        "author": "Finance Team",
                                        "subject": "Q3 results",
                                        "keywords": "finance,quarterly",
                                        "generateTaggedPdf": True})
    info = pdfinfo(pdf)
    src = raw(pdf)
    fields = {"Title": "Quarterly Review", "Author": "Finance Team",
              "Subject": "Q3 results", "Keywords": "finance,quarterly"}
    missing = [k for k, v in fields.items() if v not in info]
    has_lang = "/Lang" in src
    return (not missing and has_lang), f"missing metadata={missing} /Lang present={has_lang}"


@case("G6 tagged output carries a real structure tree")
def _():
    html = ("<!DOCTYPE html><html lang='en'><head><meta charset='utf-8'>"
            f"{CSS}</head><body><h1>Structured</h1><p>Narrative.</p>"
            "<table><thead><tr><th scope='col'>K</th></tr></thead>"
            "<tbody><tr><td>v</td></tr></tbody></table></body></html>")
    pdf = render("gate-g6-structtree", html, {"generateTaggedPdf": True})
    src = raw(pdf)
    checks = {"/StructTreeRoot": "/StructTreeRoot" in src,
              "/MarkInfo": "/MarkInfo" in src,
              "heading /H1": "/H1" in src,
              "table /Table": "/Table" in src}
    return all(checks.values()), " ".join(f"{k}={v}" for k, v in checks.items())


@case("G7 unresolvable cross-reference is reported, not silently blank")
def _():
    # A pageref pointing at an id that does not exist must not fail silently — a ToC
    # entry that renders as an empty gap is worse than one that says it could not resolve.
    html = (f"<html><head>{CSS}</head><body><h1>Contents</h1>"
            "<div>ghost .......... <span data-pdfengine-pageref='does-not-exist'></span></div>"
            "<div class='fresh'><h2 id='real'>Real Section</h2><p>x</p></div></body></html>")
    payload = json.dumps({"documentName": "gate-g7-unresolved", "documentType": 4,
                          "html": html, "options": {"pageSize": "A4",
                                                    "enablePageReferences": True}}).encode()
    req = urllib.request.Request(API, data=payload, method="POST",
                                 headers={"Content-Type": "application/json",
                                          "X-Api-Key": KEY})
    with urllib.request.urlopen(req, timeout=180) as r:
        diag = json.loads(r.headers.get("X-Render-Diagnostics", "{}"))
        pdf = r.read()
    warned = any("pageref" in w.lower() or "cross-reference" in w.lower()
                 or "unresolved" in w.lower() for w in diag.get("warnings", []))
    marker = "?" in pages_text(pdf)[0]
    return (warned or marker), \
        f"diagnostic warning={warned} visible unresolved marker={marker}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--update-baseline", action="store_true")
    args = ap.parse_args()
    for tool in ("pdftotext", "pdfinfo"):
        if subprocess.run(["which", tool], capture_output=True).returncode != 0:
            print(f"FATAL: poppler `{tool}` not found. brew install poppler")
            return 2
    if subprocess.run(["which", "qpdf"], capture_output=True).returncode != 0:
        print("FATAL: `qpdf` not found (needed to inspect compressed PDF structures).")
        print("       brew install qpdf")
        return 2

    results, failed = {}, 0
    print(f"{'case':64} {'verdict':8} detail")
    print("-" * 122)
    for name, fn in CASES:
        try:
            ok, detail = fn()
        except urllib.error.URLError as e:
            ok, detail = False, f"render failed: {e}"
        except Exception as e:                                # noqa: BLE001
            ok, detail = False, f"exception: {e}"
        v = "PASS" if ok else "FAIL"
        if not ok:
            failed += 1
        results[name] = {"verdict": v, "detail": detail}
        print(f"{name:64} {v:8} {detail}")

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    (EVIDENCE / "navigation-gate.json").write_text(
        json.dumps(results, indent=2), encoding="utf-8")
    print("-" * 122)
    print(f"summary: {len(CASES) - failed}/{len(CASES)} passed")

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
