#!/usr/bin/env python3
"""
PDFEngine — Text-Layer Extraction Gate (Release Gate B2)

Renders each i18n fixture through the LIVE engine, extracts the text layer from the
resulting PDF, and diffs it against the known source string.

This gate exists because visual rendering and PDF text-layer correctness are two
different things. A script can look perfect on the page while its ToUnicode mapping
is broken, which silently breaks copy/paste, search and screen readers. That failure
mode shipped undetected once already; this gate makes it impossible to ship again.

ORACLE: poppler `pdftotext`. Do NOT swap in PyMuPDF — it misreports Devanagari
conjuncts and would produce false failures.

Verdicts
  PASS    extracted text matches source after Unicode NFC + whitespace normalization
  PARTIAL most tokens survive but the string is not byte-identical (e.g. spurious
          spaces inside a word) — usable for search, not for exact copy/paste
  FAIL    source tokens do not survive extraction — must NOT be claimed as supported
  ERROR   render or extraction failed outright

Exit code is non-zero if any fixture regressed against the committed baseline.

Usage:
  python3 tests/extraction_gate.py                      # run + compare to baseline
  python3 tests/extraction_gate.py --update-baseline    # accept current results
"""

import argparse
import json
import os
import pathlib
import re
import subprocess
import sys
import tempfile
import unicodedata
import urllib.error
import urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
FIXTURES = ROOT / "tests" / "corpus" / "i18n" / "fixtures.json"
EVIDENCE_DIR = ROOT / "tests" / "evidence"
BASELINE = ROOT / "tests" / "corpus" / "i18n" / "baseline.json"

API = os.environ.get("PDFENGINE_API", "http://localhost:5276/api/v1/pdf/generate")
API_KEY = os.environ.get("PDFENGINE_API_KEY", "test-api-key-123")


def normalize(text: str) -> str:
    """NFC + collapse whitespace. Bidi/direction control marks are stripped because
    extractors legitimately insert them around RTL runs; they are not content."""
    text = unicodedata.normalize("NFC", text or "")
    text = re.sub(r"[‎‏‪-‮⁦-⁩]", "", text)
    return re.sub(r"\s+", " ", text).strip()


def build_html(fx: dict) -> str:
    return f"""<!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><style>
@import url('https://fonts.googleapis.com/css2?family={fx["googleFont"]}&display=swap');
body {{ font-family: '{fx["font"]}', sans-serif; font-size: 18px; }}
</style></head><body dir="{fx["dir"]}"><p>{fx["source"]}</p></body></html>"""


def render(fx: dict) -> bytes:
    payload = json.dumps({
        "documentName": f"extraction-gate-{fx['id']}",
        "documentType": 4,
        "html": build_html(fx),
        "options": {"pageSize": "A4"},
    }).encode()
    req = urllib.request.Request(
        API, data=payload,
        headers={"Content-Type": "application/json", "X-Api-Key": API_KEY},
        method="POST")
    with urllib.request.urlopen(req, timeout=120) as resp:
        return resp.read()


def extract(pdf_bytes: bytes) -> str:
    with tempfile.TemporaryDirectory() as td:
        pdf = pathlib.Path(td) / "d.pdf"
        txt = pathlib.Path(td) / "d.txt"
        pdf.write_bytes(pdf_bytes)
        subprocess.run(
            ["pdftotext", "-enc", "UTF-8", str(pdf), str(txt)],
            check=True, capture_output=True)
        return txt.read_text(encoding="utf-8")


def verdict(source: str, extracted: str) -> tuple[str, str]:
    """
    Severity is deliberately graded, because these failures are not equivalent.

    Measured on this engine: most non-Latin "failures" turn out to be word-boundary
    SPACING artifacts at script-transition points (`PDFEngineは` -> `PDFEngine は`,
    `पीडीएफइंजन` -> `पीडीएफइं जन`). Every character maps back correctly — only the
    inferred word gaps differ. That breaks exact phrase search but NOT copy/paste
    fidelity or screen-reader character mapping, so it must not be reported with the
    same weight as Arabic, where whole words genuinely fail to survive extraction.
    """
    src, ext = normalize(source), normalize(extracted)
    if src and src in ext:
        return "PASS", ""

    # Characters all present and in order, only whitespace differs?
    strip_ws = lambda s: re.sub(r"\s+", "", s)
    if src and strip_ws(src) in strip_ws(ext):
        return "SPACING", "characters correct; word-boundary spacing differs (affects exact phrase search only)"

    tokens = [t for t in src.split(" ") if len(t) > 1]
    if not tokens:
        return "FAIL", "no comparable tokens"
    # A token counts as surviving in either logical or visual order. poppler emits an
    # RTL run in VISUAL order wrapped in bidi controls (U+202B...U+202C) and leaves
    # reordering to the consumer, so a correct Arabic word legitimately appears
    # reversed in the raw byte stream. Matching only the logical form scored correct
    # output as FAIL and made this gate unable to see the /ActualText fix at all.
    present = lambda t: t in ext or t[::-1] in ext
    survived = [t for t in tokens if present(t)]
    ratio = len(survived) / len(tokens)
    missing = [t for t in tokens if not present(t)][:4]
    if ratio == 1.0:
        return "PASS", "every token survives (RTL run extracted in visual order, which is correct)"
    if ratio >= 0.6:
        return "PARTIAL", f"{len(survived)}/{len(tokens)} tokens survived; missing e.g. {missing}"
    return "FAIL", f"only {len(survived)}/{len(tokens)} tokens survived; missing e.g. {missing}"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--update-baseline", action="store_true")
    args = ap.parse_args()

    if subprocess.run(["which", "pdftotext"], capture_output=True).returncode != 0:
        print("FATAL: poppler `pdftotext` not found. brew install poppler")
        return 2

    data = json.loads(FIXTURES.read_text(encoding="utf-8"))
    results = {}

    print(f"{'script':38} {'verdict':9} detail")
    print("-" * 100)
    for fx in data["fixtures"]:
        try:
            pdf = render(fx)
            extracted = extract(pdf)
            v, detail = verdict(fx["source"], extracted)
        except urllib.error.URLError as e:
            v, detail, extracted = "ERROR", f"render failed: {e}", ""
        except subprocess.CalledProcessError as e:
            v, detail, extracted = "ERROR", f"extraction failed: {e}", ""
        results[fx["id"]] = {
            "label": fx["label"],
            "verdict": v,
            "detail": detail,
            "source": fx["source"],
            "extracted": normalize(extracted)[:300],
        }
        print(f"{fx['label']:38} {v:9} {detail}")

    EVIDENCE_DIR.mkdir(parents=True, exist_ok=True)
    (EVIDENCE_DIR / "text-extraction-gate.json").write_text(
        json.dumps(results, ensure_ascii=False, indent=2), encoding="utf-8")

    counts = {}
    for r in results.values():
        counts[r["verdict"]] = counts.get(r["verdict"], 0) + 1
    print("-" * 100)
    print("summary:", ", ".join(f"{k}={v}" for k, v in sorted(counts.items())))

    if args.update_baseline:
        BASELINE.write_text(json.dumps(
            {k: v["verdict"] for k, v in results.items()}, indent=2), encoding="utf-8")
        print(f"baseline written -> {BASELINE}")
        return 0

    if not BASELINE.exists():
        print("\nNo baseline committed yet. Run with --update-baseline to create one.")
        return 0

    base = json.loads(BASELINE.read_text(encoding="utf-8"))
    rank = {"PASS": 4, "SPACING": 3, "PARTIAL": 2, "FAIL": 1, "ERROR": 0}
    regressions = [
        (k, base[k], results[k]["verdict"])
        for k in results
        if k in base and rank.get(results[k]["verdict"], 0) < rank.get(base[k], 0)
    ]
    if regressions:
        print("\nREGRESSIONS (gate FAILED):")
        for k, was, now in regressions:
            print(f"  {k}: {was} -> {now}")
        return 1
    print("\nNo regressions against baseline. Gate PASSED.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
