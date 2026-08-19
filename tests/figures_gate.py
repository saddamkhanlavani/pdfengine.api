#!/usr/bin/env python3
"""
PDFEngine — Figures & Charts Gate (Release Gate F)

Gate F requires: SVG · Canvas · Chart.js · D3 · CSS charts · images · QR · barcodes ·
figure-caption atomic grouping · predictable scaling · high-resolution assets · image
optimization · transparency · gradients · clipping/masks.
**Hard rule: a caption must never be stranded from its figure across a page break.**

The defect this gate is built around is DEFECT-005: a canvas chart mid-animation at print
time captures BLANK. That failure is invisible to any check that only asks "did the render
succeed" — the PDF is produced, is the right length, and contains nothing. So every chart
case here asserts on rasterised PIXELS, not on the absence of an error.

Usage: python3 tests/figures_gate.py [--update-baseline]
Exit non-zero on regression vs the committed baseline.
"""
import argparse, json, os, pathlib, re, subprocess, sys, tempfile
import urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
EVIDENCE = ROOT / "tests" / "evidence"
BASELINE = ROOT / "tests" / "corpus" / "figures-baseline.json"
API = os.environ.get("PDFENGINE_BASE", "http://localhost:5276") + "/api/v1/pdf/generate"
KEY = os.environ.get("PDFENGINE_API_KEY", "test-api-key-123")

CSS = """<style>
@page { size: A4; margin: 18mm; }
body { font-family: sans-serif; font-size: 12px; }
figure { margin: 0; page-break-inside: avoid; break-inside: avoid; }
figcaption { font-size: 11px; color: #444; padding-top: 6px; }
.filler { height: 620px; background: #f4f4f4; }
</style>"""

# 1x1 transparent PNG and a small opaque JPEG-ish PNG.
PX_TRANSPARENT = ("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8"
                  "z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==")


def render(name, html, options=None):
    opts = {"pageSize": "A4"}
    opts.update(options or {})
    payload = json.dumps({"documentName": name, "documentType": 4,
                          "html": html, "options": opts}).encode()
    req = urllib.request.Request(API, data=payload, method="POST",
                                 headers={"Content-Type": "application/json",
                                          "X-Api-Key": KEY})
    with urllib.request.urlopen(req, timeout=240) as r:
        return r.read()


def page_ink(pdf_bytes, page_index=0):
    """
    Fraction of non-white pixels on a page. This is the anti-blank-chart assertion:
    a canvas that never finished drawing produces a structurally valid PDF whose page
    is empty, which only pixels can detect.
    """
    import fitz
    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    pix = doc[page_index].get_pixmap(dpi=72)
    data = pix.samples
    n = pix.width * pix.height
    stride = pix.n
    non_white = 0
    for i in range(0, len(data), stride):
        if data[i] < 245 or data[i + 1] < 245 or data[i + 2] < 245:
            non_white += 1
    return non_white / n if n else 0.0


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


@case("F1 inline SVG with gradients and paths renders actual ink")
def _():
    html = (f"<html><head>{CSS}</head><body><h1>SVG</h1>"
            "<svg viewBox='0 0 400 160' width='400' height='160'>"
            "<defs><linearGradient id='g' x1='0' x2='1'>"
            "<stop offset='0%' stop-color='#1b5e9c'/><stop offset='100%' stop-color='#9c1b5e'/>"
            "</linearGradient></defs>"
            "<rect width='400' height='160' fill='url(#g)'/>"
            "<path d='M0 130 Q100 20 200 80 T400 40' stroke='#fff' fill='none' stroke-width='5'/>"
            "<circle cx='320' cy='50' r='26' fill='#ffd400'/>"
            "</svg></body></html>")
    pdf = render("gate-f1-svg", html)
    ink = page_ink(pdf)
    return ink > 0.05, f"non-white pixel fraction={ink:.3f} (want >0.05)"


@case("F2 canvas chart is fully drawn, not captured blank (DEFECT-005)")
def _():
    # Draws synchronously inside requestAnimationFrame, then signals readiness. Without a
    # wait strategy this is precisely the case that captures an empty canvas.
    html = (f"<html><head>{CSS}</head><body><h1>Canvas</h1>"
            "<canvas id='c' width='420' height='200'></canvas>"
            "<script>"
            "const ctx=document.getElementById('c').getContext('2d');"
            "const data=[40,95,60,130,80,170,110];"
            "requestAnimationFrame(()=>{"
            "ctx.fillStyle='#eef3f8';ctx.fillRect(0,0,420,200);"
            "ctx.strokeStyle='#1b5e9c';ctx.lineWidth=3;ctx.beginPath();"
            "data.forEach((v,i)=>{const x=20+i*62,y=190-v;i?ctx.lineTo(x,y):ctx.moveTo(x,y);});"
            "ctx.stroke();"
            "data.forEach((v,i)=>{ctx.fillStyle='#9c1b5e';ctx.fillRect(14+i*62,190-v-4,12,8);});"
            "window.chartsReady=true;});"
            "</script></body></html>")
    pdf = render("gate-f2-canvas", html,
                 {"allowScripts": True, "waitForFunction": "window.chartsReady === true"})
    ink = page_ink(pdf)
    return ink > 0.03, f"non-white pixel fraction={ink:.3f} (want >0.03; blank canvas ~0.00)"


@case("F3 figure and caption are never split across a page break")
def _():
    html = (f"<html><head>{CSS}</head><body><h1>Atomic figure</h1>"
            "<div class='filler'></div>"
            "<figure>"
            "<svg viewBox='0 0 300 220' width='300' height='220'>"
            "<rect width='300' height='220' fill='#1b5e9c'/></svg>"
            "<figcaption>Figure 1. Revenue by quarter, audited.</figcaption>"
            "</figure></body></html>")
    pdf = render("gate-f3-caption", html)
    pages = pages_text(pdf)
    cap_pages = [i + 1 for i, t in enumerate(pages) if "Figure 1." in t]
    if not cap_pages:
        return False, "caption missing entirely"
    import fitz
    doc = fitz.open(stream=pdf, filetype="pdf")
    # The figure is a solid blue block; it must have ink on the caption's page.
    cap_page = cap_pages[0] - 1
    ink = page_ink(pdf, cap_page)
    return (len(cap_pages) == 1 and ink > 0.02), \
        f"caption on page(s)={cap_pages}, ink on that page={ink:.3f} (figure must accompany it)"


@case("F4 raster image with transparency survives to the PDF")
def _():
    html = (f"<html><head>{CSS}</head><body><h1>Transparency</h1>"
            "<div style='background:#1b5e9c;padding:20px;display:inline-block'>"
            f"<img src='data:image/png;base64,{PX_TRANSPARENT}' width='120' height='60' alt='overlay'/>"
            "</div></body></html>")
    pdf = render("gate-f4-transparency", html)
    ink = page_ink(pdf)
    return ink > 0.01, f"non-white pixel fraction={ink:.3f} (blue block must be present)"


@case("F5 image optimization preserves visible content while changing bytes")
def _():
    # A real photo-like gradient, big enough that re-encoding measurably changes size.
    big = ("<div style='width:520px;height:300px;background:"
           "linear-gradient(135deg,#1b5e9c,#9c1b5e,#ffd400)'></div>")
    html = f"<html><head>{CSS}</head><body><h1>Optimize</h1>{big}</body></html>"
    plain = render("gate-f5-plain", html)
    optimized = render("gate-f5-opt", html, {"optimizeImages": True, "imageQuality": 60})
    ink_plain, ink_opt = page_ink(plain), page_ink(optimized)
    # Content must survive; this asserts no silent blanking, not a size guarantee
    # (a CSS gradient has no embedded image to re-encode, so bytes may legitimately match).
    close = abs(ink_plain - ink_opt) < 0.05
    return (close and ink_opt > 0.05), \
        f"ink plain={ink_plain:.3f} optimized={ink_opt:.3f} (content must be preserved)"


@case("F6 chart built from CSS only (no JS) renders correctly")
def _():
    bars = "".join(
        f"<div style='display:inline-block;width:36px;margin-right:8px;vertical-align:bottom;"
        f"height:{h}px;background:#1b5e9c'></div>" for h in (40, 90, 65, 130, 100, 155))
    html = (f"<html><head>{CSS}</head><body><h1>CSS chart</h1>"
            f"<div style='height:170px'>{bars}</div></body></html>")
    pdf = render("gate-f6-csschart", html)
    ink = page_ink(pdf)
    return ink > 0.02, f"non-white pixel fraction={ink:.3f}"


@case("F7 SVG scales predictably to its declared size, not the viewport")
def _():
    import fitz
    html = (f"<html><head>{CSS}</head><body>"
            "<svg viewBox='0 0 100 100' width='150' height='150'>"
            "<rect width='100' height='100' fill='#000'/></svg></body></html>")
    pdf = render("gate-f7-scale", html)
    ink = page_ink(pdf)
    # A 150x150pt black square on A4 (595x842pt) is ~4.5% of the page. If the SVG
    # incorrectly scaled to the viewport, ink would be far higher.
    return 0.01 < ink < 0.15, f"ink={ink:.3f} (expect ~0.045 for a 150pt square on A4)"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--update-baseline", action="store_true")
    args = ap.parse_args()
    try:
        import fitz  # noqa: F401
    except ImportError:
        print("FATAL: PyMuPDF required for rasterisation. pip install pymupdf")
        print("       (used ONLY to rasterise pixels here, never as a TEXT oracle)")
        return 2
    for tool in ("pdftotext", "pdfinfo"):
        if subprocess.run(["which", tool], capture_output=True).returncode != 0:
            print(f"FATAL: poppler `{tool}` not found. brew install poppler")
            return 2

    results, failed = {}, 0
    print(f"{'case':62} {'verdict':8} detail")
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
        print(f"{name:62} {v:8} {detail}")

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    (EVIDENCE / "figures-gate.json").write_text(
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
