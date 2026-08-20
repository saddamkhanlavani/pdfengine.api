#!/usr/bin/env python3
"""
PDFEngine — Accessibility Gate (T3-1)

RB-4 proved one document conformant: tagged output, veraPDF PDF/UA-1, 106/0. That is a
feature check, and features do not ship alone. This gate checks COMBINATIONS, because the
question a customer's accessibility auditor asks is not "can it produce a tagged PDF" but
"is the document you actually sent me conformant" — and that document has a running header
on it.

Measured 2026-08-20, and the reason this gate exists: before the fix, tagged + running
header failed 2 clauses and tagged + watermark failed 2. Both are page furniture, so both
are now declared /Artifact and both pass. Footnotes are deliberately NOT declared
artifacts — see below.

Two things this gate refuses to do:

  It will not mark real content as an artifact to make a validator happy. A footnote is
  content the reader is meant to read; artifact-marking it would turn 7 failed checks into
  0 while hiding the footnote from the screen reader it exists for. The combination is
  asserted to FAIL, and to warn, until real /Note structure elements exist.

  It will not treat "veraPDF unavailable" as a pass. veraPDF is a Java program; with no JVM
  it reports UNCHECKED.

On PAC: PAC (PDF Accessibility Checker, axes4) is the tool accessibility auditors actually
run, and it is a Windows-only GUI with no command line, so it cannot run in this gate or in
CI on any machine this project builds on. veraPDF's PDF/UA-1 profile implements the same
Matterhorn Protocol machine checks and is what is automated here. Running PAC remains a
manual pre-release step on Windows — documented in docs/, not simulated here.

Usage:
    python3 tests/accessibility_gate.py --verapdf /path/to/verapdf
    JAVA_HOME=/opt/homebrew/opt/openjdk PATH=$JAVA_HOME/bin:$PATH python3 tests/accessibility_gate.py ...
"""
import argparse, json, os, pathlib, re, subprocess, sys, tempfile
import urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
EVIDENCE = ROOT / "tests" / "evidence"
API = os.environ.get("PDFENGINE_BASE", "http://localhost:5276") + "/api/v1/pdf/generate"
KEY = os.environ.get("PDFENGINE_API_KEY", "test-api-key-123")
VERAPDF = None

PIXEL = ("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk"
         "+A8AAQUBAScY42YAAAAASUVORK5CYII=")


def doc(page_css="", extra_css="", footnote=False):
    note = "<span class='fn'>Footnote text explaining the figure.</span>" if footnote else ""
    return f"""<html lang='en'><head><title>Accessible report</title><style>
@page {{ size: A4; margin: 20mm 16mm; {page_css} }}
body {{ font-family: sans-serif; font-size: 12px; }}
h1 {{ string-set: doctitle content(); font-size: 18px; }}
{extra_css}
</style></head><body>
<h1>Quarterly report</h1><p>{'Body copy for the accessibility fixture. ' * 60}</p>
<h2>Detail</h2><p>More text.{note}</p>
<table><caption>Figures</caption><thead><tr><th>Item</th><th>Value</th></tr></thead>
<tbody><tr><td>A</td><td>1</td></tr></tbody></table>
<img src='data:image/png;base64,{PIXEL}' alt='A single pixel'>
</body></html>"""


HEADER_CSS = "@top-center { content: string(doctitle); font-size: 9pt }"
BASE = {"generateTaggedPdf": True, "title": "Accessible report", "language": "en"}

# (name, html, extra options, must_be_conformant)
CASES = [
    ("tagged alone", doc(), {}, True),
    ("tagged + running header (@page margin box)", doc(page_css=HEADER_CSS), {}, True),
    ("tagged + text watermark", doc(), {"watermarkText": "DRAFT"}, True),
    ("tagged + running header + watermark",
     doc(page_css=HEADER_CSS), {"watermarkText": "DRAFT"}, True),
    ("tagged + PDF/A-2b", doc(), {"pdfaCompliance": "PDF/A-2b"}, True),
    ("tagged + PDF/A-2b + running header",
     doc(page_css=HEADER_CSS), {"pdfaCompliance": "PDF/A-2b"}, True),
    # Conformant since the footnote band became a real /Note structure element. It was
    # NOT made to pass by artifact-marking the band — that would have hidden the footnote
    # from the screen reader it exists for. Verified beyond veraPDF: the Note is a child of
    # the document element, the page's ParentTree resolves its MCID back to it, and the
    # text still extracts.
    ("tagged + footnote", doc(extra_css=".fn { float: footnote }", footnote=True), {}, True),
    # Chromium draws its own header/footer untagged inside its content stream and offers
    # no hook to change it. The engine's @page margin boxes are the conformant route.
    ("tagged + Chromium headerTemplate (KNOWN: upstream, untagged)",
     doc(), {"displayHeaderFooter": True,
             "headerTemplate": "<div style='font-size:9px'>Quarterly report</div>",
             "footerTemplate": "<div style='font-size:9px'>Page <span class='pageNumber'></span></div>"},
     False),
]


def render(name, html, extra):
    options = dict(BASE)
    options.update(extra)
    payload = json.dumps({"documentName": re.sub(r"\W+", "-", name)[:40], "documentType": 4,
                          "html": html, "options": options}).encode()
    req = urllib.request.Request(API, data=payload, method="POST",
                                 headers={"Content-Type": "application/json", "X-Api-Key": KEY})
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            return r.read(), r.headers.get("X-Render-Diagnostics", ""), None
    except urllib.error.HTTPError as e:
        return None, "", f"HTTP {e.code}: {e.read().decode(errors='replace')[:200]}"


def validate(pdf_bytes):
    """(compliant|None, passed, failed, [(clause, test, description)])"""
    if not VERAPDF:
        return None, -1, -1, []
    with tempfile.TemporaryDirectory() as td:
        path = pathlib.Path(td) / "d.pdf"
        path.write_bytes(pdf_bytes)
        out = subprocess.run([VERAPDF, "--flavour", "ua1", str(path)],
                             capture_output=True, text=True).stdout
    m = re.search(r'isCompliant="(\w+)"', out)
    if not m:
        # No report at all means the tool could not run — never a verdict.
        return None, -1, -1, []
    passed = re.search(r'passedChecks="(\d+)"', out)
    failed = re.search(r'failedChecks="(\d+)"', out)
    clauses = {}
    for c, t, d in re.findall(
            r'clause="([^"]+)" testNumber="(\d+)"[^>]*>\s*<description>(.*?)</description>',
            out, re.S):
        clauses.setdefault((c, t), " ".join(d.split())[:90])
    return (m.group(1) == "true",
            int(passed.group(1)) if passed else -1,
            int(failed.group(1)) if failed else -1,
            [(c, t, d) for (c, t), d in clauses.items()])


def main():
    global VERAPDF
    ap = argparse.ArgumentParser()
    ap.add_argument("--verapdf", default=os.environ.get("VERAPDF_BIN"))
    args = ap.parse_args()
    VERAPDF = args.verapdf

    print("Accessibility gate (T3-1) — PDF/UA-1 across feature COMBINATIONS\n")
    if not VERAPDF:
        print("  veraPDF not supplied (--verapdf or VERAPDF_BIN). Nothing can be checked.\n")

    results, failures, unchecked = [], [], []
    for name, html, extra, must_pass in CASES:
        pdf, diag, err = render(name, html, extra)
        if err:
            print(f"  [FAIL] {name}\n         render failed: {err}")
            failures.append(name)
            continue
        compliant, passed, failed, clauses = validate(pdf)
        warned = "Accessibility warning" in (diag or "")

        if compliant is None:
            print(f"  [SKIP] {name}\n         veraPDF unavailable — UNCHECKED (not a pass)")
            unchecked.append(name)
            results.append({"case": name, "checked": False})
            continue

        ok = (compliant == must_pass)
        # A known-failing combination must also WARN. A document that quietly fails an
        # accessibility audit is worse than one that refuses, because nobody finds out
        # until the auditor does.
        if not must_pass and not warned:
            ok = False
        mark = "PASS" if ok else "FAIL"
        expectation = "conformant" if must_pass else "known non-conformant + must warn"
        print(f"  [{mark}] {name}")
        print(f"         veraPDF ua1: compliant={compliant} ({passed} passed, {failed} failed)"
              f" | expected {expectation} | engine warned={warned}")
        for c, t, d in clauses[:3]:
            print(f"           clause {c} test {t}: {d}")
        if not ok:
            failures.append(name)
        results.append({"case": name, "checked": True, "compliant": compliant,
                        "passed": passed, "failed": failed, "expected": must_pass,
                        "engine_warned": warned,
                        "clauses": [f"{c}/{t}" for c, t, _ in clauses]})

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    (EVIDENCE / "accessibility-gate.json").write_text(
        json.dumps({"tool": "veraPDF ua1", "results": results}, indent=2) + "\n")

    print("\n" + "-" * 86)
    print(f"{len(results) - len(failures) - len(unchecked)}/{len(CASES)} as expected, "
          f"{len(failures)} unexpected, {len(unchecked)} unchecked")
    print("PAC (axes4) is Windows-only with no CLI and is NOT run here; veraPDF's PDF/UA-1")
    print("profile implements the same Matterhorn checks. PAC stays a manual step.")
    for f in failures:
        print(f"  UNEXPECTED: {f}")
    sys.exit(1 if failures else 0)


if __name__ == "__main__":
    main()
