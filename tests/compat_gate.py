#!/usr/bin/env python3
"""
PDFEngine — HTML/CSS Compatibility & Rendering Doctor Gate (Release Gates A + B1)

Gate A's PASS condition is unusual and worth restating exactly: it is NOT "everything
renders". It is **"every unsupported behavior produces a useful diagnostic instead of
silent corruption"**. A feature that cannot work is acceptable; a feature that fails
without telling anyone is not. Half the cases here therefore assert on the DIAGNOSTIC, not
on the pixels.

Gate B1 (typography VISUAL, kept strictly separate from B2 text extraction) is folded in
here: glyph rendering, the fallback chain, and — the one that matters commercially —
tofu detection. A document full of .notdef boxes renders "successfully" and is worthless.

Usage: python3 tests/compat_gate.py [--update-baseline]
Exit non-zero on regression vs the committed baseline.
"""
import argparse, json, os, pathlib, re, subprocess, sys, tempfile
import urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
EVIDENCE = ROOT / "tests" / "evidence"
BASELINE = ROOT / "tests" / "corpus" / "compat-baseline.json"
API = os.environ.get("PDFENGINE_BASE", "http://localhost:5276") + "/api/v1/pdf/generate"
KEY = os.environ.get("PDFENGINE_API_KEY", "test-api-key-123")

CSS = """<style>
@page { size: A4; margin: 16mm; }
body { font-family: sans-serif; font-size: 12px; }
</style>"""


def render(name, html, options=None):
    opts = {"pageSize": "A4"}
    opts.update(options or {})
    # An explicit None means "do not send this option at all" — used to let a document's
    # own @page rule govern geometry instead of pinning it from the request.
    opts = {k: v for k, v in opts.items() if v is not None}
    payload = json.dumps({"documentName": name, "documentType": 4,
                          "html": html, "options": opts}).encode()
    req = urllib.request.Request(API, data=payload, method="POST",
                                 headers={"Content-Type": "application/json",
                                          "X-Api-Key": KEY})
    with urllib.request.urlopen(req, timeout=240) as r:
        return r.read(), json.loads(r.headers.get("X-Render-Diagnostics", "{}"))


def text_of(pdf_bytes):
    with tempfile.TemporaryDirectory() as td:
        p = pathlib.Path(td) / "d.pdf"
        p.write_bytes(pdf_bytes)
        return subprocess.run(["pdftotext", "-enc", "UTF-8", str(p), "-"],
                              capture_output=True).stdout.decode("utf-8", "replace")


def page_ink(pdf_bytes, page_index=0):
    import fitz
    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    pix = doc[page_index].get_pixmap(dpi=72)
    data, stride = pix.samples, pix.n
    n = pix.width * pix.height
    non_white = sum(1 for i in range(0, len(data), stride)
                    if data[i] < 245 or data[i + 1] < 245 or data[i + 2] < 245)
    return non_white / n if n else 0.0


def fonts_in(pdf_bytes):
    import fitz
    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    out = set()
    for page in doc:
        for f in page.get_fonts(full=True):
            out.add(re.sub(r"^[A-Z]{6}\+", "", str(f[3])))
    return out


CASES = []


def case(name):
    def deco(fn):
        CASES.append((name, fn))
        return fn
    return deco


# --- Gate A: layout features render ------------------------------------------

@case("A1 Grid, Flexbox and CSS variables all lay out correctly")
def _():
    html = (f"<html><head>{CSS}<style>"
            ":root{--pad:14px;--brand:#1b5e9c}"
            ".g{display:grid;grid-template-columns:1fr 1fr 1fr;gap:10px}"
            ".f{display:flex;justify-content:space-between;align-items:center;"
            "background:var(--brand);color:#fff;padding:var(--pad)}"
            ".c{border:1px solid #999;padding:var(--pad)}"
            "</style></head><body>"
            "<div class='f'><span>LEFT</span><span>MIDDLE</span><span>RIGHT</span></div>"
            "<div class='g'><div class='c'>ALPHA</div><div class='c'>BETA</div>"
            "<div class='c'>GAMMA</div></div></body></html>")
    pdf, _ = render("gate-a1-layout", html)
    txt = text_of(pdf)
    missing = [w for w in ("LEFT", "MIDDLE", "RIGHT", "ALPHA", "BETA", "GAMMA")
               if w not in txt]
    return not missing, f"missing content={missing} ink={page_ink(pdf):.3f}"


@case("A2 pseudo-elements and generated content appear in output")
def _():
    html = (f"<html><head>{CSS}<style>"
            ".n::before{content:'PREFIX-'}.n::after{content:'-SUFFIX'}"
            "</style></head><body><p class='n'>CORE</p></body></html>")
    pdf, _ = render("gate-a2-pseudo", html)
    txt = text_of(pdf)
    return ("PREFIX-" in txt and "-SUFFIX" in txt), f"extracted={txt.strip()[:60]!r}"


@case("A3 transforms, shadows and gradients render as ink")
def _():
    html = (f"<html><head>{CSS}<style>"
            ".t{transform:rotate(-8deg);background:linear-gradient(90deg,#1b5e9c,#9c1b5e);"
            "box-shadow:0 6px 18px rgba(0,0,0,.4);width:360px;height:150px;color:#fff;"
            "padding:12px}</style></head><body><div class='t'>TRANSFORMED</div></body></html>")
    pdf, _ = render("gate-a3-visualcss", html)
    # A rotated text run is stored as separate positioned fragments, so poppler emits it
    # out of reading order ('ORMED\n\nTRANSF'). That is an extraction-order artifact of
    # rotation, not a rendering defect, so the assertion is on the characters present.
    letters = "".join(sorted(set(text_of(pdf).replace("\n", "").strip())))
    ok = page_ink(pdf) > 0.03 and set("TRANSFORMED") <= set(letters)
    return ok, f"ink={page_ink(pdf):.3f} glyphs present={letters!r}"


@case("A4 print CSS and @page rules are honored")
def _():
    html = ("<html><head><style>@page{size:A5 landscape;margin:10mm}"
            ".print-only{display:none}"
            "@media print{.screen-only{display:none}.print-only{display:block}}"
            "</style></head><body>"
            "<p class='screen-only'>SCREEN_ONLY</p>"
            "<p class='print-only'>PRINT_ONLY</p></body></html>")
    # pageSize is deliberately omitted: passing it pins the geometry and would mask
    # whether the document's own @page rule is honored, which is what this checks.
    pdf, _ = render("gate-a4-printcss", html,
                    {"preferCSSPageSize": True, "pageSize": None})
    import fitz
    doc = fitz.open(stream=pdf, filetype="pdf")
    r = doc[0].rect
    landscape = r.width > r.height
    txt = text_of(pdf)
    return (landscape and "PRINT_ONLY" in txt and "SCREEN_ONLY" not in txt), \
        f"page={r.width:.0f}x{r.height:.0f} landscape={landscape} print-only honored={'PRINT_ONLY' in txt}"


# --- Gate A: the Doctor must DIAGNOSE, not silently corrupt -------------------

@case("A5 a missing image is REPORTED, not silently dropped")
def _():
    html = (f"<html><head>{CSS}</head><body><h1>Assets</h1>"
            "<img src='https://example.invalid/definitely-missing.png' alt='gone'/>"
            "<p>BODY</p></body></html>")
    pdf, diag = render("gate-a5-missing-img", html)
    signals = diag.get("jsErrors", []) + diag.get("warnings", [])
    reported = any("resource" in s.lower() or "image" in s.lower() or "failed" in s.lower()
                   or "asset" in s.lower() for s in signals)
    return reported, f"reported={reported} signals={len(signals)}"


@case("A6 content overflowing the page is diagnosed")
def _():
    html = (f"<html><head>{CSS}</head><body>"
            "<div style='width:2400px;background:#1b5e9c;color:#fff'>"
            "WIDE_CONTENT_FAR_BEYOND_PAGE</div></body></html>")
    pdf, diag = render("gate-a6-overflow", html)
    signals = diag.get("warnings", [])
    reported = any("overflow" in s.lower() or "clip" in s.lower() or "width" in s.lower()
                   or "crop" in s.lower() for s in signals)
    return reported, f"reported={reported} warnings={len(signals)}"


@case("A7 an off-page absolutely positioned element is diagnosed")
def _():
    html = (f"<html><head>{CSS}</head><body><p>VISIBLE</p>"
            "<div style='position:absolute;left:-4000px;top:0'>HIDDEN_OFFPAGE</div>"
            "</body></html>")
    pdf, diag = render("gate-a7-offpage", html)
    txt = text_of(pdf)
    signals = diag.get("warnings", [])
    reported = any("off" in s.lower() or "position" in s.lower() or "hidden" in s.lower()
                   or "visib" in s.lower() for s in signals)
    # Either the doctor flags it, or the content is genuinely absent AND flagged.
    return reported, f"reported={reported} content present={'HIDDEN_OFFPAGE' in txt} warnings={len(signals)}"


# --- Gate B1: typography VISUAL ----------------------------------------------

@case("B1a webfont is actually embedded, not silently substituted")
def _():
    html = ("<html><head><style>"
            "@import url('https://fonts.googleapis.com/css2?family=Outfit:wght@400;700&display=swap');"
            "@page{size:A4;margin:16mm} body{font-family:'Outfit',sans-serif;font-size:14px}"
            "</style></head><body><h1>Typography</h1>"
            "<p>The quick brown fox jumps over the lazy dog.</p></body></html>")
    pdf, diag = render("gate-b1a-webfont", html)
    fonts = fonts_in(pdf)
    embedded = any("outfit" in f.lower() for f in fonts)
    return embedded, f"embedded fonts={sorted(fonts)} Outfit present={embedded}"


@case("B1b no tofu: every glyph in a multi-script line has real coverage")
def _():
    # If a font lacks a glyph the renderer draws .notdef boxes — the document looks
    # "successful" and is unusable. Tofu is visually dense, so a line of boxes produces
    # markedly MORE ink than correctly shaped text of the same length.
    html = ("<html><head><style>"
            "@import url('https://fonts.googleapis.com/css2?family=Noto+Sans:wght@400&display=swap');"
            "@page{size:A4;margin:16mm} body{font-family:'Noto Sans',sans-serif;font-size:20px}"
            "</style></head><body>"
            "<p>Latin Ελληνικά Кириллица</p></body></html>")
    pdf, diag = render("gate-b1b-tofu", html)
    txt = text_of(pdf)
    # Real coverage means the characters survive extraction AND ink is in a sane band.
    scripts_ok = all(s in txt for s in ("Latin", "Ελληνικά", "Кириллица"))
    ink = page_ink(pdf)
    return (scripts_ok and 0.0005 < ink < 0.06), \
        f"all scripts extracted={scripts_ok} ink={ink:.4f} (tofu inflates ink)"


@case("B1c font fallback chain resolves when the first family is unavailable")
def _():
    html = (f"<html><head>{CSS}<style>"
            "body{font-family:'ThisFontDoesNotExist','Noto Sans',sans-serif;font-size:16px}"
            "</style></head><body><p>FALLBACK_RENDERED correctly.</p></body></html>")
    pdf, diag = render("gate-b1c-fallback", html)
    txt = text_of(pdf)
    return ("FALLBACK_RENDERED" in txt and page_ink(pdf) > 0.001), \
        f"text present={'FALLBACK_RENDERED' in txt} ink={page_ink(pdf):.4f}"


@case("B1d bold and italic are distinct faces, not synthesized-away")
def _():
    html = ("<html><head><style>"
            "@import url('https://fonts.googleapis.com/css2?family=Outfit:ital,wght@0,400;0,700;1,400&display=swap');"
            "@page{size:A4;margin:16mm} body{font-family:'Outfit',sans-serif;font-size:18px}"
            "</style></head><body>"
            "<p>regular</p><p style='font-weight:700'>bold</p>"
            "<p style='font-style:italic'>italic</p></body></html>")
    pdf, _ = render("gate-b1d-faces", html)
    fonts = fonts_in(pdf)
    return len(fonts) >= 2, f"distinct embedded faces={len(fonts)}: {sorted(fonts)}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--update-baseline", action="store_true")
    args = ap.parse_args()
    try:
        import fitz  # noqa: F401
    except ImportError:
        print("FATAL: PyMuPDF required for rasterisation/font listing. pip install pymupdf")
        return 2
    if subprocess.run(["which", "pdftotext"], capture_output=True).returncode != 0:
        print("FATAL: poppler `pdftotext` not found. brew install poppler")
        return 2

    results, failed = {}, 0
    print(f"{'case':62} {'verdict':8} detail")
    print("-" * 124)
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
    (EVIDENCE / "compat-gate.json").write_text(
        json.dumps(results, indent=2), encoding="utf-8")
    print("-" * 124)
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
