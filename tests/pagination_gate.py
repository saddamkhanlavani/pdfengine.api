#!/usr/bin/env python3
"""
PDFEngine — Pagination & Page-Utilization Gate (Release Gates C + D)

Every fixture here is a REGRESSION TEST FOR A BUG THAT ACTUALLY SHIPPED. The
pagination engine is the core differentiator and the most-changed code in the repo;
four real defects were found in it in a single session, and until now nothing
protected any of the fixes.

Covered (each maps to a real defect):
  C1 grid-nested heading forced a break, splitting the grid and stranding a BLANK PAGE
  C2 native CSS page-break-after desynced internal page tracking -> blank pages
     and unnecessarily split sections across the whole document
  C3 page references were wrong twice: forced-break-only counting, then a
     scroll-height approach that broke different entries. Now resolved from the
     REAL rendered PDF, and cross-checked against Chromium's own outline.
  C4 keep-together blocks must never split
  C5 orphan headings must not be stranded at a page bottom
  D1 whitespace must be REPORTED with an attributed cause
  D2 no unexplained blank pages anywhere

Usage: python3 tests/pagination_gate.py [--update-baseline]
Exit non-zero on regression vs the committed baseline.
"""
import argparse, json, os, pathlib, re, subprocess, sys, tempfile
import urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
EVIDENCE = ROOT / "tests" / "evidence"
BASELINE = ROOT / "tests" / "corpus" / "pagination-baseline.json"
API = os.environ.get("PDFENGINE_BASE", "http://localhost:5276") + "/api/v1/pdf/generate"
KEY = os.environ.get("PDFENGINE_API_KEY", "test-api-key-123")

CSS = """<style>
@page { size: A4; margin: 20mm 16mm; }
body { font-family: sans-serif; font-size: 12px; }
.grid-2 { display: grid; grid-template-columns: 1fr 1fr; gap: 16px; }
.grid-3 { display: grid; grid-template-columns: repeat(3,1fr); gap: 12px; }
.card { border: 1px solid #ccc; border-radius: 8px; padding: 14px; }
.keep { page-break-inside: avoid; border: 2px solid #111; padding: 16px; }
.fresh { page-break-before: always; }
.after { page-break-after: always; }
.filler { height: 900px; background: #f2f2f2; }
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
        return r.read(), r.headers.get("X-Render-Diagnostics", "{}")


def pages_text(pdf_bytes):
    with tempfile.TemporaryDirectory() as td:
        p = pathlib.Path(td) / "d.pdf"
        p.write_bytes(pdf_bytes)
        n = int(re.search(rb"Pages:\s+(\d+)",
                subprocess.run(["pdfinfo", str(p)], capture_output=True).stdout).group(1))
        out = []
        for i in range(1, n + 1):
            t = pathlib.Path(td) / f"{i}.txt"
            subprocess.run(["pdftotext", "-enc", "UTF-8", "-f", str(i), "-l", str(i),
                            str(p), str(t)], check=True, capture_output=True)
            out.append(t.read_text(encoding="utf-8"))
        return out


CASES = []


def case(name):
    def deco(fn):
        CASES.append((name, fn))
        return fn
    return deco


@case("C1 grid-nested heading does not split the grid or strand a blank page")
def _():
    html = (f"<html><head>{CSS}</head><body><h1>Grid</h1><div class='grid-2'>"
            "<div class='card'><h3>Card A heading</h3><p>Alpha content.</p></div>"
            "<div class='card'><h3>Card B heading</h3><p>Beta content.</p></div>"
            "</div><p>Trailing paragraph.</p></body></html>")
    pdf, _ = render("gate-c1-grid", html)
    pages = pages_text(pdf)
    blanks = [i + 1 for i, t in enumerate(pages) if not t.strip()]
    # both cards must land on the SAME page - that was the bug
    same = any("Card A heading" in t and "Card B heading" in t for t in pages)
    return (same and not blanks), f"pages={len(pages)} blanks={blanks} both_cards_together={same}"


@case("C2 native page-break-after does not desync tracking or create blank pages")
def _():
    html = (f"<html><head>{CSS}</head><body>"
            "<div class='after'><h2>Section A</h2><div class='filler'></div></div>"
            "<div><h2>Section B</h2><div class='grid-3'>"
            "<div class='card'>1</div><div class='card'>2</div><div class='card'>3</div></div>"
            "<div class='keep'><h3>Keep block</h3><p>Must not split.</p></div>"
            "</div></body></html>")
    pdf, _ = render("gate-c2-nativebreak", html)
    pages = pages_text(pdf)
    blanks = [i + 1 for i, t in enumerate(pages) if not t.strip()]
    # Section B + its grid + keep block should not be scattered over extra pages
    ok = not blanks and len(pages) <= 3
    return ok, f"pages={len(pages)} (want <=3) blanks={blanks}"


@case("C3 ToC page references match the REAL page each target lands on")
def _():
    secs = ["intro", "financials", "risk", "appendix"]
    toc = "".join(f"<div>{s} .......... <span data-pdfengine-pageref='{s}'></span></div>"
                  for s in secs)
    body = "".join(f"<div class='fresh'><h2 id='{s}'>Section {s}</h2>"
                   f"<p>{'Filler. ' * 60}</p></div>" for s in secs)
    html = f"<html><head>{CSS}</head><body><h1>Contents</h1>{toc}{body}</body></html>"
    pdf, _ = render("gate-c3-pageref", html)
    pages = pages_text(pdf)
    toc_txt = pages[0]
    claimed = {}
    for s in secs:
        m = re.search(rf"{s} \.+ (\d+)", toc_txt)
        if m:
            claimed[s] = int(m.group(1))
    wrong = []
    for s in secs:
        actual = next((i + 1 for i, t in enumerate(pages[1:], start=1)
                       if f"Section {s}" in t), None)
        if claimed.get(s) != actual:
            wrong.append(f"{s}: toc={claimed.get(s)} actual={actual}")
    return (not wrong and len(claimed) == len(secs)), \
        f"resolved={len(claimed)}/{len(secs)} mismatches={wrong}"


@case("C4 keep-together block is never split across a page boundary")
def _():
    html = (f"<html><head>{CSS}</head><body><h1>Keep</h1>"
            f"<p>{'Lead in. ' * 220}</p>"
            "<div class='keep'><h3>Certification Statement</h3>"
            f"<p>{'This block must stay whole. ' * 12}</p>"
            "<div>Authorized Signature</div></div></body></html>")
    pdf, _ = render("gate-c4-keep", html)
    pages = pages_text(pdf)
    holding = [i + 1 for i, t in enumerate(pages)
               if "Certification Statement" in t or "Authorized Signature" in t]
    ok = len(set(holding)) == 1
    return ok, f"block spans pages={holding} (want exactly one)"


@case("C5 heading is not stranded alone at the bottom of a page")
def _():
    html = (f"<html><head>{CSS}</head><body>"
            f"<p>{'Filler paragraph text. ' * 230}</p>"
            "<h2>Stranded Candidate</h2>"
            f"<p>{'Body that belongs with the heading. ' * 40}</p></body></html>")
    pdf, _ = render("gate-c5-orphan", html)
    pages = pages_text(pdf)
    hp = next((i for i, t in enumerate(pages) if "Stranded Candidate" in t), None)
    if hp is None:
        return False, "heading missing entirely"
    after = pages[hp].split("Stranded Candidate", 1)[1]
    followers = len(re.findall(r"belongs with the heading", after))
    return followers >= 2, f"heading on page {hp+1} with {followers} following line(s) (want >=2)"


@case("D1 whitespace is reported WITH an attributed cause")
def _():
    html = (f"<html><head>{CSS}</head><body>"
            "<div class='after'><h2>Short A</h2><p>tiny</p></div>"
            "<div class='after'><h2>Short B</h2><p>tiny</p></div>"
            "<div><h2>Short C</h2><p>tiny</p></div></body></html>")
    _pdf, diag = render("gate-d1-whitespace", html)
    d = json.loads(diag or "{}")
    notes = [w for w in d.get("warnings", []) if "Pagination Notice" in w]
    attributed = [w for w in notes if "caused by" in w]
    return bool(attributed), f"{len(notes)} notice(s), {len(attributed)} with attributed cause"


@case("D2 no unexplained blank pages in a mixed real-world document")
def _():
    html = (f"<html><head>{CSS}</head><body>"
            "<h1>Mixed</h1><p>Intro.</p>"
            "<div class='grid-2'><div class='card'><h3>A</h3><p>a</p></div>"
            "<div class='card'><h3>B</h3><p>b</p></div></div>"
            "<div class='fresh'><h2>Table</h2><table><thead><tr><th>K</th><th>V</th></tr></thead>"
            "<tbody>" + "".join(f"<tr><td>k{i}</td><td>v{i}</td></tr>" for i in range(120)) +
            "</tbody></table></div>"
            "<div class='keep'><h3>Signature</h3><p>Whole block.</p></div></body></html>")
    pdf, _ = render("gate-d2-blank", html)
    pages = pages_text(pdf)
    blanks = [i + 1 for i, t in enumerate(pages) if not t.strip()]
    return not blanks, f"pages={len(pages)} blanks={blanks}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--update-baseline", action="store_true")
    args = ap.parse_args()
    for tool in ("pdftotext", "pdfinfo"):
        if subprocess.run(["which", tool], capture_output=True).returncode != 0:
            print(f"FATAL: poppler `{tool}` not found. brew install poppler")
            return 2

    results, failed = {}, 0
    print(f"{'case':66} {'verdict':8} detail")
    print("-" * 120)
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
        print(f"{name:66} {v:8} {detail}")

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    (EVIDENCE / "pagination-gate.json").write_text(
        json.dumps(results, indent=2), encoding="utf-8")
    print("-" * 120)
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
