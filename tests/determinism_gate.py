#!/usr/bin/env python3
"""
PDFEngine — Determinism & Reproducibility Gate (Release Gate J)

The commercial problem this protects against: Chromium updates, and last month's
invoice silently reflows. Every team running HTML→PDF in production has that fear and
nobody sells the fix. This gate is the mechanism behind the claim.

Raw byte equality is deliberately NOT the assertion. Every PDF save embeds a fresh
document /ID and timestamps, so raw bytes differ by design on every render. Measured:
the produced byte LENGTH is identical run to run while the hash differs purely from
those volatile fields. Asserting on raw bytes would therefore fail forever and teach
everyone to ignore the gate.

Three fingerprints are compared instead, each catching a different class of drift:
  STRUCTURAL  page count, page geometry, per-page text, embedded font set
  VISUAL      rasterised page pixels (catches layout shifts text alone cannot see)
  SIZE        byte length (catches silent asset/compression changes)

Usage: python3 tests/determinism_gate.py [--runs N] [--update-baseline]
Exit non-zero on non-determinism or on regression vs the committed baseline.
"""
import argparse, hashlib, json, os, pathlib, re, subprocess, sys, tempfile
import urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
EVIDENCE = ROOT / "tests" / "evidence"
BASELINE = ROOT / "tests" / "corpus" / "determinism-baseline.json"
API = os.environ.get("PDFENGINE_BASE", "http://localhost:5276") + "/api/v1/pdf/generate"
KEY = os.environ.get("PDFENGINE_API_KEY", "test-api-key-123")

CSS = """<style>
@page { size: A4; margin: 18mm; }
body { font-family: sans-serif; font-size: 12px; }
.grid { display: grid; grid-template-columns: 1fr 1fr; gap: 14px; }
.card { border: 1px solid #bbb; padding: 12px; border-radius: 6px; }
table { width: 100%; border-collapse: collapse; }
td, th { border: 1px solid #999; padding: 4px; }
</style>"""

# Deliberately exercises the subsystems most likely to drift: layout engine, font
# shaping, SVG rasterisation, table fragmentation and image handling.
PX = ("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk"
      "+A8AAQUBAScY42YAAAAASUVORK5CYII=")
DOCS = {
    "text-only": f"<html><head>{CSS}</head><body><h1>Text</h1><p>{'Stable content. ' * 90}</p></body></html>",
    "grid-and-table": (f"<html><head>{CSS}</head><body><h1>Mixed</h1>"
                       "<div class='grid'><div class='card'><h3>A</h3><p>alpha</p></div>"
                       "<div class='card'><h3>B</h3><p>beta</p></div></div>"
                       "<table><thead><tr><th>K</th><th>V</th></tr></thead><tbody>" +
                       "".join(f"<tr><td>k{i}</td><td>v{i}</td></tr>" for i in range(120)) +
                       "</tbody></table></body></html>"),
    "svg-and-image": (f"<html><head>{CSS}</head><body><h1>Graphics</h1>"
                      "<svg viewBox='0 0 200 80' width='200' height='80'>"
                      "<defs><linearGradient id='g'><stop offset='0%' stop-color='#36c'/>"
                      "<stop offset='100%' stop-color='#c36'/></linearGradient></defs>"
                      "<rect width='200' height='80' fill='url(#g)'/>"
                      "<path d='M0 60 Q50 10 100 40 T200 20' stroke='#fff' fill='none' stroke-width='3'/>"
                      "</svg>"
                      f"<img src='data:image/png;base64,{PX}' width='60' height='30' alt='dot'/>"
                      "</body></html>"),
    "webfont": ("<html><head><style>"
                "@import url('https://fonts.googleapis.com/css2?family=Outfit:wght@400;700&display=swap');"
                "@page{size:A4;margin:18mm} body{font-family:'Outfit',sans-serif;font-size:12px}"
                "</style></head><body><h1>Webfont</h1>"
                f"<p>{'Shaped with a remote webfont. ' * 40}</p></body></html>"),
    # Deliberately non-deterministic content — reads the clock and Math.random on every
    # render. Without the pinning options below this fixture CANNOT pass, which is what
    # makes it a real test of them rather than of the renderer in general.
    "clock-and-random": (f"<html><head>{CSS}</head><body><h1>Volatile</h1>"
                         "<p id='t'>pending</p><p id='r'>pending</p>"
                         "<script>document.getElementById('t').textContent="
                         "'generated '+new Date().toISOString();"
                         "document.getElementById('r').textContent="
                         "Array.from({length:8},()=>Math.random().toFixed(6)).join(' ');"
                         "</script></body></html>"),
}

# Per-fixture option overrides. Everything else uses the defaults.
OPTIONS = {
    "clock-and-random": {"allowScripts": True,
                         "fixedDateUtc": "2026-01-01T00:00:00Z",
                         "randomSeed": 42},
}


def render(name, html):
    options = {"pageSize": "A4", "title": "Determinism Fixture", "author": "PdfEngine"}
    options.update(OPTIONS.get(name, {}))
    payload = json.dumps({"documentName": name, "documentType": 4, "html": html,
                          "options": options}).encode()
    req = urllib.request.Request(API, data=payload, method="POST",
                                 headers={"Content-Type": "application/json",
                                          "X-Api-Key": KEY})
    with urllib.request.urlopen(req, timeout=240) as r:
        return r.read(), r.headers.get("X-PdfEngine-Engine-Version")


def fingerprints(pdf_bytes):
    """(structural, visual) hashes with volatile PDF fields excluded."""
    import fitz
    doc = fitz.open(stream=pdf_bytes, filetype="pdf")

    struct = []
    for page in doc:
        r = page.rect
        struct.append(f"PAGE {r.width:.2f}x{r.height:.2f}")
        struct.append(page.get_text())
        for f in sorted(page.get_fonts(full=True)):
            # basefont names carry a random subset prefix (ABCDEF+Name) -> strip it
            struct.append("FONT " + re.sub(r"^[A-Z]{6}\+", "", str(f[3])))
    struct_hash = hashlib.sha256("\n".join(struct).encode("utf-8")).hexdigest()

    vis = hashlib.sha256()
    for page in doc:
        vis.update(page.get_pixmap(dpi=72).tobytes("png"))
    return struct_hash, vis.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--runs", type=int, default=3)
    ap.add_argument("--update-baseline", action="store_true")
    args = ap.parse_args()

    try:
        import fitz  # noqa: F401
    except ImportError:
        print("FATAL: PyMuPDF required for rasterisation. pip install pymupdf")
        return 2

    results, failed = {}, 0
    print(f"{'fixture':18} {'runs':5} {'verdict':8} detail")
    print("-" * 112)
    for name, html in DOCS.items():
        try:
            pairs = [render(name, html) for _ in range(args.runs)]
            renders = [b for b, _ in pairs]
            # Reproducibility is only meaningful if the engine build is reportable, so
            # the version header is asserted here rather than assumed.
            versions = {v for _, v in pairs}
            fps = [fingerprints(b) for b in renders]
            sizes = {len(b) for b in renders}
            structs = {f[0] for f in fps}
            visuals = {f[1] for f in fps}
            has_version = all(v for _, v in pairs) and len(versions) == 1
            ok = (len(structs) == 1 and len(visuals) == 1 and len(sizes) == 1
                  and has_version)
            detail = (f"struct={'stable' if len(structs)==1 else f'{len(structs)} VARIANTS'} "
                      f"visual={'stable' if len(visuals)==1 else f'{len(visuals)} VARIANTS'} "
                      f"size={'stable' if len(sizes)==1 else sorted(sizes)} "
                      f"engine={sorted(versions)[0] if has_version else 'MISSING/UNSTABLE'} "
                      f"sig={sorted(structs)[0][:12]}")
        except urllib.error.URLError as e:
            ok, detail = False, f"render failed: {e}"
            structs = set()
        except Exception as e:                                # noqa: BLE001
            ok, detail = False, f"exception: {e}"
            structs = set()
        v = "PASS" if ok else "FAIL"
        if not ok:
            failed += 1
        results[name] = {"verdict": v, "detail": detail,
                         "structural": sorted(structs)[0] if len(structs) == 1 else None}
        print(f"{name:18} {args.runs:<5} {v:8} {detail}")

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    (EVIDENCE / "determinism-gate.json").write_text(
        json.dumps(results, indent=2), encoding="utf-8")
    print("-" * 112)
    print(f"summary: {len(DOCS) - failed}/{len(DOCS)} deterministic across {args.runs} runs")

    if args.update_baseline:
        BASELINE.parent.mkdir(parents=True, exist_ok=True)
        BASELINE.write_text(json.dumps(
            {k: {"verdict": v["verdict"], "structural": v["structural"]}
             for k, v in results.items()}, indent=2), encoding="utf-8")
        print(f"baseline written -> {BASELINE}")
        print("NOTE: structural signatures are pinned. A Chromium/font upgrade that")
        print("      changes layout will now FAIL this gate - which is the point.")
        return 0

    if BASELINE.exists():
        base = json.loads(BASELINE.read_text(encoding="utf-8"))
        drift = []
        for k, v in results.items():
            if k not in base:
                continue
            if v["verdict"] != "PASS" and base[k]["verdict"] == "PASS":
                drift.append(f"{k}: became non-deterministic")
            elif (base[k]["structural"] and v["structural"]
                  and base[k]["structural"] != v["structural"]):
                drift.append(f"{k}: OUTPUT CHANGED vs pinned baseline "
                             f"({base[k]['structural'][:12]} -> {v['structural'][:12]})")
        if drift:
            print("\nDETERMINISM DRIFT (gate FAILED):")
            for d in drift:
                print("  " + d)
            print("\n  If this change was intentional (engine/Chromium/font upgrade),")
            print("  re-run with --update-baseline AFTER reviewing the visual diff.")
            return 1

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
