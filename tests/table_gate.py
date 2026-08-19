#!/usr/bin/env python3
"""
PDFEngine — Table Fragmentation Gate (Release Gate E)

The known weak spot. `ProtectRowspanContinuationsAsync` exists and has unit tests, but
it had NEVER been exercised against a real page boundary — which is the only place it
matters. This gate builds tables engineered to straddle page breaks and asserts on the
rendered PDF.

Assertions are about DATA INTEGRITY, not aesthetics:
  - no cell content is lost across a fragmentation boundary
  - no cell content is duplicated (a classic rowspan-continuation bug)
  - repeated <thead> appears on every page the table occupies
  - running totals still reconcile after fragmentation

Usage:
  python3 tests/table_gate.py [--update-baseline]
Exit non-zero on regression vs the committed baseline.
"""
import argparse, json, os, pathlib, re, subprocess, sys, tempfile
import urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
EVIDENCE = ROOT / "tests" / "evidence"
BASELINE = ROOT / "tests" / "corpus" / "table-baseline.json"
API = os.environ.get("PDFENGINE_BASE", "http://localhost:5276") + "/api/v1/pdf/generate"
KEY = os.environ.get("PDFENGINE_API_KEY", "test-api-key-123")

CSS = """<style>
@page { size: A4; margin: 15mm; }
body { font-family: sans-serif; font-size: 11px; }
table { width:100%; border-collapse: collapse; }
th,td { border:1px solid #888; padding:4px 6px; }
thead { display: table-header-group; }
h1 { font-size: 16px; }
</style>"""


def render(name, html):
    payload = json.dumps({"documentName": name, "documentType": 4, "html": html,
                          "options": {"pageSize": "A4"}}).encode()
    req = urllib.request.Request(API, data=payload, method="POST",
                                 headers={"Content-Type": "application/json",
                                          "X-Api-Key": KEY})
    with urllib.request.urlopen(req, timeout=180) as r:
        return r.read()


def pages_text(pdf_bytes):
    """Per-page text via poppler; poppler is the agreed extraction oracle."""
    with tempfile.TemporaryDirectory() as td:
        p = pathlib.Path(td) / "d.pdf"
        p.write_bytes(pdf_bytes)
        out = []
        # -f/-l per page keeps page attribution exact
        n = int(re.search(rb"Pages:\s+(\d+)",
                          subprocess.run(["pdfinfo", str(p)], capture_output=True).stdout).group(1))
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


# ---------------------------------------------------------------- fixtures

@case("rowspan group straddling a page break keeps every cell exactly once")
def _():
    # 60 groups x 3 rows; the rowspan'd label cell must not be lost or duplicated
    rows = []
    for g in range(60):
        rows.append(f'<tr><td rowspan="3">GROUP-{g:03d}</td><td>r0</td><td>V{g:03d}-0</td></tr>')
        rows.append(f'<tr><td>r1</td><td>V{g:03d}-1</td></tr>')
        rows.append(f'<tr><td>r2</td><td>V{g:03d}-2</td></tr>')
    html = f"<html><head>{CSS}</head><body><h1>Rowspan</h1><table><thead><tr><th>Group</th><th>Row</th><th>Value</th></tr></thead><tbody>{''.join(rows)}</tbody></table></body></html>"
    pdf = render("gate-e-rowspan", html)
    txt = "\n".join(pages_text(pdf))
    missing = [f"GROUP-{g:03d}" for g in range(60) if f"GROUP-{g:03d}" not in txt]
    dupes = [f"GROUP-{g:03d}" for g in range(60) if txt.count(f"GROUP-{g:03d}") > 1]
    lost_vals = [f"V{g:03d}-{r}" for g in range(60) for r in range(3)
                 if f"V{g:03d}-{r}" not in txt]
    ok = not missing and not dupes and not lost_vals
    return ok, f"missing_labels={len(missing)} duplicated_labels={len(dupes)} lost_values={len(lost_vals)}"


@case("colspan rows survive fragmentation intact")
def _():
    rows = []
    for i in range(90):
        if i % 10 == 0:
            rows.append(f'<tr><td colspan="3">SECTION-{i:03d}</td></tr>')
        rows.append(f'<tr><td>A{i:03d}</td><td>B{i:03d}</td><td>C{i:03d}</td></tr>')
    html = f"<html><head>{CSS}</head><body><h1>Colspan</h1><table><thead><tr><th>A</th><th>B</th><th>C</th></tr></thead><tbody>{''.join(rows)}</tbody></table></body></html>"
    pdf = render("gate-e-colspan", html)
    txt = "\n".join(pages_text(pdf))
    miss_sec = [f"SECTION-{i:03d}" for i in range(0, 90, 10) if f"SECTION-{i:03d}" not in txt]
    miss_cell = [f"{c}{i:03d}" for i in range(90) for c in "ABC" if f"{c}{i:03d}" not in txt]
    ok = not miss_sec and not miss_cell
    return ok, f"missing_sections={len(miss_sec)} missing_cells={len(miss_cell)}"


@case("thead repeats on EVERY page the table spans")
def _():
    rows = "".join(f"<tr><td>R{i:04d}</td><td>data</td></tr>" for i in range(400))
    html = f"<html><head>{CSS}</head><body><h1>Header repeat</h1><table><thead><tr><th>MARKER_REF</th><th>MARKER_VAL</th></tr></thead><tbody>{rows}</tbody></table></body></html>"
    pdf = render("gate-e-thead", html)
    pages = pages_text(pdf)
    # pages that contain table body rows must also contain the header
    body_pages = [i for i, t in enumerate(pages) if re.search(r"R\d{4}", t)]
    missing = [i + 1 for i in body_pages if "MARKER_REF" not in pages[i]]
    ok = len(body_pages) > 1 and not missing
    return ok, f"table_pages={len(body_pages)} pages_missing_header={missing}"


@case("running total still reconciles after fragmentation")
def _():
    total = 0
    rows = []
    for i in range(300):
        amt = (i * 7) % 97 + 1
        total += amt
        rows.append(f"<tr><td>ID{i:04d}</td><td>{amt}</td><td>{total}</td></tr>")
    html = f"<html><head>{CSS}</head><body><h1>Totals</h1><table><thead><tr><th>ID</th><th>Amt</th><th>Running</th></tr></thead><tbody>{''.join(rows)}</tbody><tfoot><tr><td>FINAL</td><td></td><td>{total}</td></tr></tfoot></table></body></html>"
    pdf = render("gate-e-totals", html)
    txt = "\n".join(pages_text(pdf))
    ok = f"FINAL" in txt and str(total) in txt and all(f"ID{i:04d}" in txt for i in range(300))
    return ok, f"final_total={total} present={str(total) in txt}"


@case("row taller than a page does not silently vanish")
def _():
    tall = "<br>".join(f"line {i}" for i in range(90))
    html = f"<html><head>{CSS}</head><body><h1>Tall row</h1><table><thead><tr><th>K</th><th>V</th></tr></thead><tbody><tr><td>BEFORE</td><td>x</td></tr><tr><td>TALLROW</td><td>{tall}</td></tr><tr><td>AFTER</td><td>y</td></tr></tbody></table></body></html>"
    pdf = render("gate-e-tallrow", html)
    txt = "\n".join(pages_text(pdf))
    ok = "BEFORE" in txt and "AFTER" in txt and "TALLROW" in txt and "line 89" in txt
    return ok, f"before={'BEFORE' in txt} tall={'TALLROW' in txt} lastline={'line 89' in txt} after={'AFTER' in txt}"


@case("wide table (25 columns) does not drop trailing columns")
def _():
    head = "".join(f"<th>H{c:02d}</th>" for c in range(25))
    rows = "".join("<tr>" + "".join(f"<td>c{c:02d}r{r:02d}</td>" for c in range(25)) + "</tr>"
                   for r in range(40))
    html = f"<html><head>{CSS}</head><body><h1>Wide</h1><table style='font-size:6px'><thead><tr>{head}</tr></thead><tbody>{rows}</tbody></table></body></html>"
    pdf = render("gate-e-wide", html)
    txt = "\n".join(pages_text(pdf))
    missing_last_col = [f"c24r{r:02d}" for r in range(40) if f"c24r{r:02d}" not in txt]
    ok = not missing_last_col
    return ok, f"missing_last_column_cells={len(missing_last_col)}/40"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--update-baseline", action="store_true")
    args = ap.parse_args()

    for tool in ("pdftotext", "pdfinfo"):
        if subprocess.run(["which", tool], capture_output=True).returncode != 0:
            print(f"FATAL: poppler `{tool}` not found. brew install poppler")
            return 2

    results, failed = {}, 0
    print(f"{'case':64} {'verdict':8} detail")
    print("-" * 118)
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
    (EVIDENCE / "table-gate.json").write_text(
        json.dumps(results, indent=2), encoding="utf-8")
    print("-" * 118)
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
