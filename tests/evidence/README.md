# Generated Evidence

Machine-generated proof artifacts. **Never hand-edited.**

---

## Evidence standard (added 2026-08-16)

A release blocker may be marked **CLOSED**, and a gate may be marked **PASSED**, only
when a file in this directory demonstrates it. Prose in a table is *not* evidence.

Every closed RB and every passing gate must satisfy all four:

| # | Requirement |
|---|---|
| 1 | **Artifact exists here** and is committed |
| 2 | **Reproducible** — the exact command that produced it is recorded |
| 3 | **Verdict is machine-readable** (exit code, `isCompliant`, `Passed!`) — not human judgement |
| 4 | **Registry row links to the artifact path**, not a description |

**Why this rule exists.** RB-3 was first marked CLOSED on the strength of a sentence in
a table. The underlying work was genuinely done and the evidence genuinely existed — but
nothing in the document *pointed at it*, so a reader had to trust the author. That is the
same failure mode as the superseded `docs/` files that claimed fabricated pass rates.
Trust-the-author is not a control. This standard removes the author from the loop.

---

## Current artifacts

| File | Proves | Command | Verdict |
|---|---|---|---|
| `unit-tests.log` | Unit suite green | `dotnet test` | `Passed! Failed: 0, Passed: 57` |
| `text-extraction-gate.log` | Gate B2 armed, no regressions | `python3 tests/extraction_gate.py` | exit `0`, `Gate PASSED` |
| `text-extraction-gate.json` | Per-script text-layer verdicts | (same) | `PASS=9 SPACING=4 PARTIAL=2 FAIL=0` |
| `verapdf/pdfa2b-basic.xml` | PDF/A-2b conformance | veraPDF 1.30.2 `--flavour 2b` | `isCompliant="true" 144/0` |
| `verapdf/pdfa2b-tagged.xml` | PDF/A-2b + tagged structure tree | veraPDF `--flavour 2b` | `isCompliant="true" 144/0` |
| `verapdf/pdfa2b-images-tagged.xml` | PDF/A-2b + optimized images + tagging | veraPDF `--flavour 2b` | `isCompliant="true" 144/0` |
| `verapdf/pdfa3b-basic.xml` | PDF/A-3b conformance | veraPDF `--flavour 3b` | `isCompliant="true" 146/0` |
| `rb6-icc-provenance.log` | ICC profile is MIT-generated, structurally valid | `python3` structural read | all required tags present |

---

## Still required before any release

veraPDF **in CI** (currently run manually) · PAC reports (PDF/UA) · visual regression
diffs · page-utilization audit · performance benchmarks from a reproducible harness ·
sustained-render memory trace · security suite output · integration/E2E/chaos results.

---

## Extraction oracle

poppler `pdftotext` plus one independent extractor.
**Do NOT use PyMuPDF as the oracle** — it misreported Devanagari conjuncts during this
programme and produces false failures.

## Running the gates with veraPDF

veraPDF is a Java program. On a host with no JVM on PATH it exits without producing a
report, and the PDF/A cases can then only be reported as SKIP — never as a pass, and (since
2026-08-19) never as a failure either, because "not checked" and "not conformant" are
different findings and only one of them is a bug in the engine.

    export JAVA_HOME=/opt/homebrew/opt/openjdk
    export PATH="$JAVA_HOME/bin:$PATH"
    python3 tests/output_gate.py --verapdf /path/to/verapdf
