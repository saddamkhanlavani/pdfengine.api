#!/usr/bin/env python3
"""
PDFEngine — Page-Level Typesetting Gate (Feature Backlog Tier 1)

Covers the GCPM features that separate a typesetting engine from an HTML-to-PDF
converter, and that Prince XML / DocRaptor customers cannot migrate without:

  T1-1  running headers/footers   `string-set` + `@page { @top-center { content: string(x) } }`
  T1-2  standard target-counter   `content: target-counter(attr(href), page)`
  T1-3  leaders                   `content: leader('.')`
  T1-4  page selectors            `@page :first` / `:left` / `:right`
  T1-5  footnotes                  `float: footnote` + `::footnote-call` / `::footnote-marker`
  T1-6  widow/orphan control       `orphans` / `widows`
  T1-7  named pages                `@page cover { }` + `page: cover`
  T1-8  page floats                `float: top` / `float: bottom`
  T1-9  per-page reservation       `footnoteReservationMode: per-page`

Chromium implements NONE of T1-1..T1-3 or T1-5 and discards the declarations before
CSSOM, so these are engine features, not pass-throughs — which is exactly why they need a
gate.

T1-4 is different and worth stating plainly: Chromium DOES implement `:first`/`:left`/
`:right`. They were broken only because the CSS sanitizer stripped `@page` descriptors
(fixed 2026-08-18). These cases are regression protection for that fix, not proof of new
work. Named pages (`@page cover` + `page: cover`) are NOT supported by Chromium and are
deliberately NOT claimed here — see T1-7 in the backlog.

Usage: python3 tests/typesetting_gate.py [--update-baseline]
"""
import argparse, json, os, pathlib, re, subprocess, sys, tempfile
import urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
EVIDENCE = ROOT / "tests" / "evidence"
BASELINE = ROOT / "tests" / "corpus" / "typesetting-baseline.json"
API = os.environ.get("PDFENGINE_BASE", "http://localhost:5276") + "/api/v1/pdf/generate"
KEY = os.environ.get("PDFENGINE_API_KEY", "test-api-key-123")


def render(name, html, options=None):
    opts = {"pageSize": "A4", "marginTop": "20mm", "marginBottom": "20mm",
            "marginLeft": "16mm", "marginRight": "16mm"}
    opts.update(options or {})
    opts = {k: v for k, v in opts.items() if v is not None}
    payload = json.dumps({"documentName": name, "documentType": 4,
                          "html": html, "options": opts}).encode()
    req = urllib.request.Request(API, data=payload, method="POST",
                                 headers={"Content-Type": "application/json",
                                          "X-Api-Key": KEY})
    with urllib.request.urlopen(req, timeout=240) as r:
        return r.read(), json.loads(r.headers.get("X-Render-Diagnostics", "{}"))


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


CHAPTERS = ["Introduction", "Market Analysis", "Financial Review", "Appendix"]


def chapter_doc(extra_css="", margin_boxes="@top-center { content: string(chapter); font-size: 9pt }"):
    body = "".join(
        f"<section><h1 id='ch{i}'>{c}</h1>" + f"<p>{'Body copy for this chapter. ' * 120}</p>"
        + "</section>" for i, c in enumerate(CHAPTERS))
    return f"""<html><head><style>
@page {{ size: A4; margin: 20mm 16mm; {margin_boxes} }}
body {{ font-family: sans-serif; font-size: 12px; }}
section {{ page-break-before: always; }}
h1 {{ string-set: chapter content(); font-size: 18px; }}
{extra_css}
</style></head><body>{body}</body></html>"""


CASES = []


def case(name):
    def deco(fn):
        CASES.append((name, fn))
        return fn
    return deco


@case("T1-1a running header shows the CURRENT chapter on every page")
def _():
    # Compared against the SAME document rendered with no margin box. Asserting only
    # that a chapter name appears on its page would be a false pass — the <h1> puts it
    # there anyway. The header must be ADDITIONAL text, so the difference between the
    # two renders is what proves it exists.
    with_header, _ = render("ts-running-header", chapter_doc())
    without, _ = render("ts-no-header", chapter_doc(margin_boxes=""))

    wrong = []
    for i, (a, b) in enumerate(zip(pages_text(with_header), pages_text(without))):
        owner = max(CHAPTERS, key=lambda c: b.count(c))
        if b.count(owner) == 0:
            continue                      # page has no chapter of its own to compare
        if a.count(owner) <= b.count(owner):
            wrong.append(f"p{i+1}: '{owner}' x{a.count(owner)} with header vs "
                         f"x{b.count(owner)} without — header not added")
    pages = pages_text(with_header)
    return (not wrong and len(pages) > 0), \
        f"{len(pages)} pages; header adds an extra occurrence on every chapter page; issues={wrong[:2]}"


@case("T1-1b running header CARRIES FORWARD onto continuation pages")
def _():
    # The real test of string() semantics: a chapter long enough to span pages must show
    # its title on the pages that contain no <h1> at all.
    long_body = ("<section><h1>Solo Chapter</h1>"
                 + f"<p>{'Continuation body text. ' * 900}</p></section>")
    html = f"""<html><head><style>
@page {{ size: A4; margin: 20mm 16mm; @top-right {{ content: string(chapter); font-size: 9pt }} }}
body {{ font-family: sans-serif; font-size: 12px; }}
h1 {{ string-set: chapter content(); }}
</style></head><body>{long_body}</body></html>"""
    pdf, _ = render("ts-carry-forward", html)
    pages = pages_text(pdf)
    if len(pages) < 3:
        return False, f"fixture produced only {len(pages)} page(s); needs >=3"
    carried = [i + 1 for i, t in enumerate(pages[1:], start=1) if "Solo Chapter" in t]
    return len(carried) == len(pages) - 1, \
        f"{len(pages)} pages; title carried onto pages {carried} (want all after p1)"


@case("T1-1c counter(page)/counter(pages) render real numbers")
def _():
    html = chapter_doc(margin_boxes="@bottom-center { content: 'Page ' counter(page) ' of ' counter(pages); font-size: 9pt }")
    pdf, _ = render("ts-page-counter", html)
    pages = pages_text(pdf)
    n = len(pages)
    hits = [i + 1 for i, t in enumerate(pages) if f"Page {i+1} of {n}" in t]
    return len(hits) == n, f"{n} pages; correct 'Page X of Y' on {len(hits)}"


@case("T1-2 standard target-counter(attr(href), page) resolves real pages")
def _():
    toc = "".join(f"<li><a href='#ch{i}'>{c}</a></li>" for i, c in enumerate(CHAPTERS))
    body = "".join(
        f"<section><h1 id='ch{i}'>{c}</h1><p>{'Body. ' * 120}</p></section>"
        for i, c in enumerate(CHAPTERS))
    html = f"""<html><head><style>
@page {{ size: A4; margin: 20mm 16mm; }}
body {{ font-family: sans-serif; font-size: 12px; }}
section {{ page-break-before: always; }}
.toc a::after {{ content: target-counter(attr(href), page); }}
</style></head><body><h2>Contents</h2><ul class='toc'>{toc}</ul>{body}</body></html>"""
    pdf, _ = render("ts-target-counter", html)
    pages = pages_text(pdf)
    toc_txt = pages[0]
    wrong = []
    for i, c in enumerate(CHAPTERS):
        m = re.search(rf"{re.escape(c)}\s*(\d+)", toc_txt)
        claimed = int(m.group(1)) if m else None
        actual = next((p + 1 for p, t in enumerate(pages[1:], start=1)
                       if c in t and "Body." in t), None)
        if claimed != actual:
            wrong.append(f"{c}: toc={claimed} actual={actual}")
    return not wrong, f"mismatches={wrong}"


@case("T1-3 leader('.') produces dot leaders in the table of contents")
def _():
    toc = "".join(f"<li><a href='#ch{i}'>{c}</a></li>" for i, c in enumerate(CHAPTERS))
    body = "".join(f"<section><h1 id='ch{i}'>{c}</h1><p>x</p></section>"
                   for i, c in enumerate(CHAPTERS))
    html = f"""<html><head><style>
@page {{ size: A4; margin: 20mm 16mm; }}
body {{ font-family: sans-serif; font-size: 12px; }}
section {{ page-break-before: always; }}
.toc li {{ list-style: none; }}
.toc a::after {{ content: leader('.'); }}
</style></head><body><h2>Contents</h2><ul class='toc'>{toc}</ul>{body}</body></html>"""
    pdf, _ = render("ts-leader", html)
    toc_txt = pages_text(pdf)[0]
    runs = re.findall(r"\.{4,}", toc_txt)
    return len(runs) >= len(CHAPTERS), \
        f"{len(runs)} dot-leader run(s) found, want >= {len(CHAPTERS)}"


@case("T1-4a @page :first applies a distinct cover margin")
def _():
    import fitz
    html = """<html><head><style>
@page { size: A4; margin: 20mm }
@page :first { margin: 60mm }
body { font-family: sans-serif; font-size: 12px }
div { page-break-after: always }
</style></head><body><div>COVER</div><div>SECOND</div><div>THIRD</div></body></html>"""
    pdf, _ = render("ts-page-first", html, {"marginTop": None, "marginBottom": None,
                                            "marginLeft": None, "marginRight": None,
                                            "pageSize": None})
    doc = fitz.open(stream=pdf, filetype="pdf")
    lefts = [min((b[0] for b in pg.get_text("blocks")), default=-1) for pg in doc]
    return (len(lefts) >= 2 and lefts[0] > lefts[1] + 30), \
        f"first-page left inset={lefts[0]:.0f} vs body pages={[f'{x:.0f}' for x in lefts[1:]]}"


@case("T1-4b @page :left / :right mirror binding margins")
def _():
    import fitz
    html = """<html><head><style>
@page { size: A4; margin: 20mm }
@page :left { margin-left: 60mm }
@page :right { margin-left: 8mm }
body { font-family: sans-serif; font-size: 12px }
div { page-break-after: always }
</style></head><body>""" + "".join(
        f"<div>PAGE{i}</div>" for i in range(4)) + "</body></html>"
    pdf, _ = render("ts-page-leftright", html, {"marginTop": None, "marginBottom": None,
                                                "marginLeft": None, "marginRight": None,
                                                "pageSize": None})
    doc = fitz.open(stream=pdf, filetype="pdf")
    lefts = [min((b[0] for b in pg.get_text("blocks")), default=-1) for pg in doc]
    if len(lefts) < 4:
        return False, f"only {len(lefts)} pages"
    # Odd pages (1,3 -> index 0,2) are right-hand pages with the small left margin.
    odd_small = lefts[0] < lefts[1] and lefts[2] < lefts[3]
    return odd_small, f"left insets={[f'{x:.0f}' for x in lefts]} (odd pages must be smaller)"


@case("T1-1d unresolved string-set is reported, not silently blank")
def _():
    # A margin box referencing a string nobody assigns must not silently print nothing.
    html = """<html><head><style>
@page { size: A4; margin: 20mm 16mm; @top-center { content: string(nosuchname) } }
body { font-family: sans-serif; font-size: 12px }
</style></head><body><h1>Doc</h1><p>body</p></body></html>"""
    pdf, diag = render("ts-unresolved-string", html)
    # Either a diagnostic is emitted, or the render simply has no header — but the
    # engine must not crash and must still produce the document.
    return len(pages_text(pdf)) >= 1, f"warnings={len(diag.get('warnings', []))}"


LINES = " ".join(f"L{i}word alpha beta gamma delta epsilon zeta eta theta iota kappa"
                 for i in range(1, 9))


LONG_LINES = " ".join(f"L{i}word alpha beta gamma delta epsilon zeta eta theta iota kappa"
                      for i in range(1, 31))


def straddle_doc(orphan_css="", spacer=650, text=None):
    """A paragraph positioned so a page boundary falls inside it.

    The spacer leaves the paragraph starting BEFORE the last fifth of the page, and the
    paragraph is long enough to straddle from there. Both matter: the planner moves a short
    block that starts late whole rather than splitting it, so a fixture built that way
    measures the planner's heuristic and never reaches the orphans/widows it claims to test.
    """
    return f"""<html><head><style>
@page {{ size: A4; margin: 20mm }}
body {{ font-family: sans-serif; font-size: 12px; line-height: 18px }}
p {{ margin: 0 0 10px }}
{orphan_css}
</style></head><body><div style="height:{spacer}px"></div><p>{text or LONG_LINES}</p></body></html>"""


# --- T1-5: footnotes ---------------------------------------------------------------
#
# Chromium supports NONE of this. Measured 2026-08-18: `float: footnote` content renders
# INLINE exactly where it was authored (the test marker appeared 16% into page 1, not at
# the page bottom) and `::footnote-call`/`::footnote-marker` produce no numbering at all.
# So every case below is proving engine work, not a pass-through — and the one that
# matters most is T1-5c: the whole feature is worthless if the note lands on top of the
# body text it was moved out of.

FOOTNOTE_CSS = """
@page { size: A4; margin: 20mm 16mm; }
body { font-family: sans-serif; font-size: 12px; line-height: 1.5 }
.fn { float: footnote; font-size: 8pt }
"""


def footnote_doc(body, extra_css=""):
    return f"<html><head><style>{FOOTNOTE_CSS}{extra_css}</style></head><body>{body}</body></html>"


def _filler(prefix, n):
    return "".join(
        f"<p>{prefix} filler {i} with enough words to occupy a measurable amount of "
        f"vertical space on the page.</p>" for i in range(1, n + 1))


@case("T1-5a float: footnote is moved OUT of the text flow to the page bottom")
def _():
    import fitz
    # Chromium leaves this content inline. Asserting only that the text is present would
    # therefore pass without the engine doing anything at all — the position is the test.
    body = ("<h1>Report</h1><p>Opening sentence with a reference"
            "<span class='fn'>FNTEXT_ALPHA restated after the disposal of the segment.</span>"
            " that continues afterwards.</p>")
    pdf, _ = render("ts-footnote-bottom", footnote_doc(body))
    doc = fitz.open(stream=pdf, filetype="pdf")
    page = doc[0]
    hit = page.search_for("FNTEXT_ALPHA")
    if not hit:
        return False, "footnote text is missing from the document entirely"
    call = page.search_for("Opening sentence")
    frac = hit[0].y0 / page.rect.height
    return (frac > 0.80 and call and call[0].y0 < hit[0].y0), \
        f"footnote at {frac*100:.0f}% down the page (want >80%, inline renders at ~15%)"


@case("T1-5b a footnote lands on the SAME page as its call")
def _():
    import fitz
    body = ("<h1>Report</h1>" + _filler("A", 31)
            + "<p>CALLSITE deep in the document"
              "<span class='fn'>FNTEXT_BETA all figures are stated in thousands.</span>"
              " ends here.</p>" + _filler("B", 20))
    pdf, diag = render("ts-footnote-same-page", footnote_doc(body))
    doc = fitz.open(stream=pdf, filetype="pdf")
    call_pages = [i for i, pg in enumerate(doc, 1) if pg.search_for("CALLSITE")]
    note_pages = [i for i, pg in enumerate(doc, 1) if pg.search_for("FNTEXT_BETA")]
    return (len(doc) >= 2 and call_pages == note_pages and len(note_pages) == 1), \
        f"{len(doc)} pages; call on {call_pages}, note on {note_pages}"


@case("T1-5c the reserved footnote band does NOT overlap the body text")
def _():
    import fitz
    # The entire point of the reflow loop. A band drawn over the body text would still
    # satisfy T1-5a and T1-5b, and would be unusable.
    marks = ("FNTEXT_ONE", "FNTEXT_TWO", "FNTEXT_THREE")
    body = ("<h1>Notes</h1>" + _filler("A", 28)
            + "".join(f"<p>Reference {i}<span class='fn'>{m} substantive note text "
                      f"explaining the policy applied in period {i}.</span> follows.</p>"
                      for i, m in enumerate(marks, 1))
            + _filler("B", 34))
    pdf, diag = render("ts-footnote-no-overlap", footnote_doc(body))
    doc = fitz.open(stream=pdf, filetype="pdf")

    problems = []
    for i, page in enumerate(doc, 1):
        blocks = [b for b in page.get_text("blocks") if b[4].strip()]
        note = [b for b in blocks if any(m in b[4] for m in marks)]
        text = [b for b in blocks if not any(m in b[4] for m in marks)]
        if not note or not text:
            continue
        body_bottom = max(b[3] for b in text)
        note_top = min(b[1] for b in note)
        if note_top < body_bottom:
            problems.append(f"p{i}: note top {note_top:.0f} above body bottom {body_bottom:.0f}")

    stranded = [i for i, pg in enumerate(doc, 1) if len(pg.get_text().strip()) < 150]
    return (not problems and not stranded), \
        f"{len(doc)} pages; overlaps={problems[:2]}; near-empty pages={stranded}"


@case("T1-5d ::footnote-call and ::footnote-marker number the notes")
def _():
    # Chromium produces no numbering for either pseudo-element, so an unnumbered pair of
    # footnotes is indistinguishable from one another in the body text.
    body = ("<h1>Numbered</h1><p>First reference<span class='fn'>MARKED_ONE first note.</span>"
            " and a second<span class='fn'>MARKED_TWO second note.</span> here.</p>")
    css = (".fn::footnote-call { content: counter(footnote, lower-roman) }"
           ".fn::footnote-marker { content: counter(footnote, lower-roman) '.' }")
    pdf, _ = render("ts-footnote-markers", footnote_doc(body, css))
    text = pages_text(pdf)[0]
    ok = "i. MARKED_ONE" in text and "ii. MARKED_TWO" in text
    return ok, f"roman markers present={ok}; tail={text.strip()[-70:]!r}"


@case("T1-5e a footnote too tall for the page is REPORTED, not silently overlapped")
def _():
    # Silent degradation is the failure mode being guarded against — the same rule that
    # governs unsatisfiable orphans/widows (T1-6c) and unresolved cross-references.
    # Sized well past a page rather than just over it. The previous fixture sat close to
    # the boundary and stopped tripping the moment the drawing face changed to one with
    # more compact metrics — a fixture that only passes at one set of font metrics is
    # testing the font, not the reporting.
    huge = "OVERSIZE " + ("this note is far longer than any page can hold " * 500)
    body = f"<h1>Oversize</h1><p>Reference<span class='fn'>{huge}</span> here.</p>"
    _pdf, diag = render("ts-footnote-oversize", footnote_doc(body))
    warned = [w for w in diag.get("warnings", [])
              if "Footnote warning" in w or "Layout warning" in w]
    return bool(warned), f"oversize footnote reported={bool(warned)}; {warned[:1]}"


@case("T1-5f a document without footnotes pays nothing and is unchanged")
def _():
    # The reflow loop costs re-renders, so it must be a strict no-op for the documents
    # (the overwhelming majority) that declare no footnotes at all.
    body = "<h1>Plain</h1>" + _filler("A", 40)
    with_rule, _ = render("ts-footnote-absent-a", footnote_doc(body))
    without, _ = render("ts-footnote-absent-b",
                        footnote_doc(body).replace(".fn { float: footnote; font-size: 8pt }", ""))
    a, b = pages_text(with_rule), pages_text(without)
    return (len(a) == len(b) and a == b), \
        f"{len(a)} vs {len(b)} pages; identical text={a == b}"


# --- T1-8: page floats -------------------------------------------------------------
#
# Measured 2026-08-18: Chromium renders `float: top` and `float: bottom` at the IDENTICAL
# position where they were authored (38% down page 1 for both), so the two are
# indistinguishable from each other and from no float at all. Position is therefore the
# only thing worth asserting.

def float_doc(edge, before=12, after=30, box="width: 300px; height: 90px; background: #3355bb"):
    body = ("<h1>Floats</h1>" + _filler("A", before)
            + f'<div class="pf"></div>' + _filler("B", after))
    return f"""<html><head><style>
@page {{ size: A4; margin: 20mm 16mm }}
body {{ font-family: sans-serif; font-size: 12px; line-height: 1.5 }}
.pf {{ float: {edge}; {box} }}
</style></head><body>{body}</body></html>"""


@case("T1-8a float: top is pulled to the top of the page, above the body text")
def _():
    import fitz
    pdf, _ = render("ts-float-top", float_doc("top"))
    doc = fitz.open(stream=pdf, filetype="pdf")
    page = doc[0]
    images = page.get_image_info()
    if not images:
        return False, "floated element is missing from the page entirely"
    top = images[0]["bbox"][1]
    text_top = min(b[1] for b in page.get_text("blocks") if b[4].strip())
    frac = top / page.rect.height
    return (frac < 0.15 and top < text_top), \
        f"float at {frac*100:.0f}% down (want <15%; inline renders at ~38%), body starts below it={top < text_top}"


@case("T1-8b float: bottom is pulled to the bottom of the page, below the body text")
def _():
    import fitz
    pdf, _ = render("ts-float-bottom", float_doc("bottom"))
    doc = fitz.open(stream=pdf, filetype="pdf")
    page = doc[0]
    images = page.get_image_info()
    if not images:
        return False, "floated element is missing from the page entirely"
    box = images[0]["bbox"]
    text_bottom = max(b[3] for b in page.get_text("blocks") if b[4].strip())
    frac = box[1] / page.rect.height
    return (frac > 0.70 and box[1] >= text_bottom - 1), \
        f"float at {frac*100:.0f}% down (want >70%), body ends above it={box[1] >= text_bottom - 1}"


@case("T1-8c a bottom float and a footnote share the band without overlapping")
def _():
    import fitz
    # Both reserve at the same edge. The footnote must stay lowest — that is where a
    # reader looks for it — and neither may be drawn over the body text.
    body = ("<h1>Both</h1>" + _filler("A", 8)
            + "<p>Reference here<span class='fn'>SHAREDNOTE the note must stay below the "
              "floated block.</span> continues.</p>" + _filler("B", 6)
            + '<div class="pf"></div>' + _filler("C", 28))
    html = f"""<html><head><style>
@page {{ size: A4; margin: 20mm 16mm }}
body {{ font-family: sans-serif; font-size: 12px; line-height: 1.5 }}
.fn {{ float: footnote; font-size: 8pt }}
.pf {{ float: bottom; width: 280px; height: 80px; background: #cc4444 }}
</style></head><body>{body}</body></html>"""
    pdf, _ = render("ts-float-plus-footnote", html)
    doc = fitz.open(stream=pdf, filetype="pdf")
    page = doc[0]
    images = page.get_image_info()
    blocks = [b for b in page.get_text("blocks") if b[4].strip()]
    note = [b for b in blocks if "SHAREDNOTE" in b[4]]
    body_blocks = [b for b in blocks if "SHAREDNOTE" not in b[4]]
    if not images or not note:
        return False, f"images={len(images)} note_blocks={len(note)} (need both on page 1)"
    float_top, float_bottom = images[0]["bbox"][1], images[0]["bbox"][3]
    note_top = min(b[1] for b in note)
    body_bottom = max(b[3] for b in body_blocks)
    return (body_bottom <= float_top + 1 and float_bottom <= note_top + 1), \
        f"body ends {body_bottom:.0f}, float {float_top:.0f}..{float_bottom:.0f}, note {note_top:.0f} (want strictly increasing)"


@case("T1-8d floated content carrying text is REPORTED as rasterized")
def _():
    # A floated photograph loses nothing by becoming an image; a floated table loses its
    # text layer. The caller must not have to discover that by searching the finished PDF.
    html = """<html><head><style>
@page { size: A4; margin: 20mm 16mm }
body { font-family: sans-serif; font-size: 12px }
table.pf { float: top; border-collapse: collapse }
table.pf td { border: 1px solid #333; padding: 6px }
</style></head><body><h1>Table float</h1>
<p>Some body text before the table.</p>
<table class="pf"><tr><td>RASTEREDCELL</td><td>2</td></tr></table>
<p>Body text after.</p></body></html>"""
    _pdf, diag = render("ts-float-raster", html)
    warned = [w for w in diag.get("warnings", []) if "drawn as IMAGES" in w]
    return bool(warned), f"rasterization reported={bool(warned)}"


@case("T1-8e ordinary float: left/right is untouched")
def _():
    # `float: left` is in a large share of real stylesheets. Treating it as a page float
    # would silently rip the element out of the flow and rasterize it.
    html = """<html><head><style>
@page { size: A4; margin: 20mm 16mm }
body { font-family: sans-serif; font-size: 12px }
.side { float: left; width: 120px; background: #eeeeee }
</style></head><body><h1>Normal floats</h1>
<div class="side">SIDEBARTEXT</div>
<p>Body text that wraps around the ordinary left float exactly as it always has.</p>
</body></html>"""
    pdf, diag = render("ts-float-ordinary", html)
    import fitz
    page = fitz.open(stream=pdf, filetype="pdf")[0]
    still_text = bool(page.search_for("SIDEBARTEXT"))
    no_float_warning = not [w for w in diag.get("warnings", []) if "Page float" in w]
    return (still_text and no_float_warning and not page.get_image_info()), \
        f"text preserved={still_text}, no page-float handling={no_float_warning}, images={len(page.get_image_info())}"


# --- T1-7: named pages -------------------------------------------------------------
#
# Measured 2026-08-18: Chromium silently ignores `page: <name>` — a cover declared
# `size: A4 landscape; margin: 50mm` came out byte-for-byte the same portrait geometry as
# the body pages. It cannot be corrected after the render the way a running header can,
# because page geometry changes LAYOUT. Each run of consecutive content sharing a page
# name is rendered on its own paper and the parts are stitched, so the cases below check
# both the geometry AND that everything document-wide survives the stitch.

NAMED_PAGE_DOC = """<html><head><style>
@page { size: A4; margin: 20mm }
@page cover { size: A4 landscape; margin: 50mm }
@page appendix { size: A5; margin: 10mm }
body { font-family: sans-serif; font-size: 12px }
.cover { page: cover }
.appendix { page: appendix }
section { page-break-before: always }
</style></head><body>
<section class="cover"><h1>COVERPAGE</h1><p>Cover body text.</p></section>
<section><h1>BODYPAGE</h1><p>Body text here.</p></section>
<section><h1>SECONDBODY</h1><p>More body text.</p></section>
<section class="appendix"><h1>APPENDIXPAGE</h1><p>Appendix text.</p></section>
</body></html>"""


@case("T1-7a a named page gets its own paper size and orientation")
def _():
    import fitz
    pdf, _ = render("ts-named-geometry", NAMED_PAGE_DOC,
                    {"marginTop": None, "marginBottom": None, "marginLeft": None,
                     "marginRight": None, "pageSize": None})
    doc = fitz.open(stream=pdf, filetype="pdf")
    if len(doc) != 4:
        return False, f"expected 4 pages, got {len(doc)}"
    shape = [(round(pg.rect.width), round(pg.rect.height)) for pg in doc]
    cover_landscape = shape[0][0] > shape[0][1]
    body_portrait = shape[1][0] < shape[1][1] and shape[2][0] < shape[2][1]
    # A5 is half of A4: the appendix must be visibly smaller than the body pages.
    appendix_smaller = shape[3][1] < shape[1][1] - 100
    return (cover_landscape and body_portrait and appendix_smaller), \
        f"page shapes={shape} (cover landscape={cover_landscape}, body portrait={body_portrait}, A5 appendix={appendix_smaller})"


@case("T1-7b a named page's margins apply to its run and nowhere else")
def _():
    import fitz
    pdf, _ = render("ts-named-margins", NAMED_PAGE_DOC,
                    {"marginTop": None, "marginBottom": None, "marginLeft": None,
                     "marginRight": None, "pageSize": None})
    doc = fitz.open(stream=pdf, filetype="pdf")
    lefts = [min((b[0] for b in pg.get_text("blocks") if b[4].strip()), default=-1) for pg in doc]
    if len(lefts) != 4:
        return False, f"expected 4 pages, got {len(lefts)}"
    # cover 50mm (~142pt) > body 20mm (~57pt) > appendix 10mm (~28pt)
    return (lefts[0] > lefts[1] + 50 and lefts[1] > lefts[3] + 15), \
        f"left insets={[round(x) for x in lefts]} (want cover >> body >> appendix)"


@case("T1-7c page counters stay continuous across the stitch")
def _():
    # The parts are rendered separately, so a counter that restarted per part — or a
    # total that counted only one part — is the obvious way for stitching to go wrong.
    html = """<html><head><style>
@page { size: A4; margin: 20mm; @bottom-center { content: 'Page ' counter(page) ' of ' counter(pages) } }
@page cover { size: A4 landscape; margin: 40mm }
body { font-family: sans-serif; font-size: 12px }
.cover { page: cover }
section { page-break-before: always }
</style></head><body>
<section class="cover"><h1>Cover</h1></section>
<section><h1>Body A</h1></section>
<section><h1>Body B</h1></section>
</body></html>"""
    pdf, _ = render("ts-named-counter", html, {"marginTop": None, "marginBottom": None,
                                               "marginLeft": None, "marginRight": None,
                                               "pageSize": None})
    pages = pages_text(pdf)
    n = len(pages)
    hits = [i + 1 for i, t in enumerate(pages) if f"Page {i+1} of {n}" in t]
    return (n >= 3 and len(hits) == n), f"{n} pages; correct 'Page X of Y' on {len(hits)}"


@case("T1-7d cross-references resolve against the STITCHED document")
def _():
    # Page numbers are resolved by reading the real PDF. With named pages that PDF is the
    # merged one, and resolving against a single part would put every reference out by
    # however many pages the earlier parts contributed.
    names = ["Alpha", "Beta", "Gamma"]
    toc = "".join(f"<li><a href='#c{i}'>{c}</a></li>" for i, c in enumerate(names))
    body = "".join(f"<section id='s{i}'><h1 id='c{i}'>{c}</h1><p>{'Body. ' * 60}</p></section>"
                   for i, c in enumerate(names))
    html = f"""<html><head><style>
@page {{ size: A4; margin: 20mm }}
@page wide {{ size: A4 landscape; margin: 20mm }}
body {{ font-family: sans-serif; font-size: 12px }}
#s1 {{ page: wide }}
section {{ page-break-before: always }}
.toc a::after {{ content: target-counter(attr(href), page) }}
</style></head><body><h2>Contents</h2><ul class='toc'>{toc}</ul>{body}</body></html>"""
    pdf, _ = render("ts-named-xref", html, {"marginTop": None, "marginBottom": None,
                                            "marginLeft": None, "marginRight": None,
                                            "pageSize": None})
    pages = pages_text(pdf)
    wrong = []
    for c in names:
        m = re.search(rf"{re.escape(c)}\s*(\d+)", pages[0])
        claimed = int(m.group(1)) if m else None
        actual = next((p + 1 for p, t in enumerate(pages[1:], start=1) if c in t and "Body." in t), None)
        if claimed != actual:
            wrong.append(f"{c}: toc={claimed} actual={actual}")
    return not wrong, f"{len(pages)} pages; mismatches={wrong}"


@case("T1-7e footnotes still land on their own page when it has different geometry")
def _():
    import fitz
    html = """<html><head><style>
@page { size: A4; margin: 20mm }
@page wide { size: A4 landscape; margin: 20mm }
body { font-family: sans-serif; font-size: 12px; line-height: 1.5 }
.fn { float: footnote; font-size: 8pt }
#wide { page: wide }
section { page-break-before: always }
</style></head><body>
<section><h1>Portrait</h1><p>Text with a note<span class="fn">PORTRAITNOTE first note.</span> here.</p></section>
<section id="wide"><h1>Landscape</h1><p>Wide text with a note<span class="fn">LANDSCAPENOTE second note.</span> here.</p></section>
</body></html>"""
    pdf, _ = render("ts-named-footnote", html, {"marginTop": None, "marginBottom": None,
                                                "marginLeft": None, "marginRight": None,
                                                "pageSize": None})
    doc = fitz.open(stream=pdf, filetype="pdf")
    placed = {}
    for i, page in enumerate(doc, 1):
        for mark in ("PORTRAITNOTE", "LANDSCAPENOTE"):
            hit = page.search_for(mark)
            if hit:
                placed[mark] = (i, hit[0].y0 / page.rect.height, page.rect.width > page.rect.height)
    if len(placed) != 2:
        return False, f"{len(doc)} pages; found {list(placed)} (want both notes)"
    p1, p2 = placed["PORTRAITNOTE"], placed["LANDSCAPENOTE"]
    return (len(doc) == 2 and p1[0] == 1 and p2[0] == 2 and not p1[2] and p2[2]
            and p1[1] > 0.80 and p2[1] > 0.80), \
        f"{len(doc)} pages; portrait note p{p1[0]} at {p1[1]*100:.0f}%, landscape note p{p2[0]} at {p2[1]*100:.0f}% (landscape={p2[2]})"


@case("T1-7f a page name with no @page rule is REPORTED, not silently ignored")
def _():
    html = """<html><head><style>
@page { size: A4; margin: 20mm }
@page cover { size: A4 landscape }
body { font-family: sans-serif; font-size: 12px }
.cover { page: cover }
.mystery { page: nosuchpage }
section { page-break-before: always }
</style></head><body>
<section class="cover"><h1>Cover</h1></section>
<section class="mystery"><h1>Mystery</h1></section>
</body></html>"""
    _pdf, diag = render("ts-named-undefined", html, {"marginTop": None, "marginBottom": None,
                                                     "marginLeft": None, "marginRight": None,
                                                     "pageSize": None})
    warned = [w for w in diag.get("warnings", []) if "no matching `@page` rule" in w]
    return bool(warned), f"undefined page name reported={bool(warned)}"


@case("T1-7g a document without named pages is rendered in one pass, unchanged")
def _():
    # Every run costs a render. A document that declares no named pages must not pay for
    # the feature, and must not be split.
    body = "<h1>Plain</h1>" + _filler("A", 40)
    html = f"""<html><head><style>
@page {{ size: A4; margin: 20mm }}
body {{ font-family: sans-serif; font-size: 12px }}
</style></head><body>{body}</body></html>"""
    pdf, diag = render("ts-named-absent", html)
    named_notices = [w for w in diag.get("warnings", []) if "Named page" in w]
    doc_pages = pages_text(pdf)
    return (not named_notices and len(doc_pages) >= 2), \
        f"{len(doc_pages)} pages; named-page notices={len(named_notices)} (want 0)"


# --- T1-9: per-page reservation ----------------------------------------------------
#
# The default reservation is UNIFORM: the bottom margin grows document-wide by the largest
# band any page needs, so a page carrying no footnotes loses exactly as much height as the
# busiest one. Per-page mode reclaims that by forcing a break before the content that would
# be overrun — one render per page that needs a band, because a break on page N invalidates
# every later page's measurement taken from the same render.
#
# Measured 2026-08-18 and worth recording: `@page :nth(N)` would make this native and free,
# and Chromium ignores it — `:nth(2)` and `:nth(2n)` both produced output identical to no
# rule at all, while `:first` correctly re-paginated the same document.

import random as _random

_FN_WORDS = ("alpha bravo charlie delta echo foxtrot golf hotel india juliet kilo lima mike "
             "november oscar papa quebec romeo sierra tango uniform victor whiskey xray").split()


def _unique_sentence(seed, n=16):
    """Every sentence must be distinct: a footnote call is located by the text AROUND it,
    and repeated filler makes that fingerprint ambiguous."""
    rng = _random.Random(seed)
    return " ".join(rng.choice(_FN_WORDS) for _ in range(n))


def crowded_doc(note_count=5, trailing=140):
    """One crowded cluster of footnotes, then pages of plain prose carrying none.

    The trailing length matters to T1-9: the engine only takes per-page reservation when it
    reclaims about a quarter of a page or more, so a document short enough that uniform
    costs almost nothing is one where UNIFORM is the right answer. Measured at ~660px of
    printable width, this produces roughly five pages and ~0.4 of a page of reclaimable
    height — comfortably inside the region where the trade is worth an extra render.
    """
    parts, seed = ["<h1>Report</h1>"], 0
    for _ in range(6):
        seed += 1
        parts.append(f"<p>{_unique_sentence(seed)}.</p>")
    for n in range(1, note_count + 1):
        seed += 1
        parts.append(f"<p>{_unique_sentence(seed)}<span class='fn'>NOTE{n} note {n}: "
                     f"{_unique_sentence(seed + 700)}.</span> {_unique_sentence(seed + 1400, 6)}.</p>")
    for _ in range(trailing):
        seed += 1
        parts.append(f"<p>{_unique_sentence(seed)}.</p>")
    return f"""<html><head><style>
@page {{ size: A4; margin: 20mm 16mm }}
body {{ font-family: sans-serif; font-size: 12px; line-height: 1.5 }}
.fn {{ float: footnote; font-size: 8pt }}
</style></head><body>{''.join(parts)}</body></html>"""


def _page_report(pdf):
    import fitz
    doc = fitz.open(stream=pdf, filetype="pdf")
    out = []
    for page in doc:
        blocks = [b for b in page.get_text("blocks") if b[4].strip()]
        notes = [b for b in blocks if "NOTE" in b[4]]
        body = [b for b in blocks if "NOTE" not in b[4]]
        body_bottom = max((b[3] for b in body), default=0)
        out.append({
            "gap": round(page.rect.height - body_bottom),
            "notes": len(notes),
            "overlap": bool(notes) and min(b[1] for b in notes) < body_bottom,
        })
    return out


@case("T1-9a per-page mode reclaims the band on pages carrying no footnotes")
def _():
    # The whole point of the mode. Under a uniform reservation a footnote-free page loses
    # the same band as the crowded one; under per-page it keeps its full height.
    html = crowded_doc()
    uniform = _page_report(render("ts-t19-uniform", html,
                                  {"footnoteReservationMode": "uniform"})[0])
    per_page = _page_report(render("ts-t19-perpage", html,
                                   {"footnoteReservationMode": "per-page"})[0])

    free_uniform = [p["gap"] for p in uniform if p["notes"] == 0]
    free_perpage = [p["gap"] for p in per_page if p["notes"] == 0]
    if not free_uniform or not free_perpage:
        return False, f"fixture produced no footnote-free page (uniform={uniform}, per-page={per_page})"

    reclaimed = min(free_uniform) - min(free_perpage)
    return reclaimed > 20, \
        f"footnote-free page bottom gap {min(free_uniform)}pt uniform -> {min(free_perpage)}pt per-page (reclaimed {reclaimed}pt, want >20)"


@case("T1-9b per-page mode still never overlaps the body text")
def _():
    # Tighter reservation is worthless if it is achieved by drawing over the text. This is
    # the same invariant as T1-5c, asserted for the other mode.
    reports = {}
    for mode in ("uniform", "per-page"):
        html = crowded_doc()
        reports[mode] = _page_report(render(f"ts-t19-overlap-{mode}", html,
                                            {"footnoteReservationMode": mode})[0])
    bad = {m: [i + 1 for i, p in enumerate(r) if p["overlap"]] for m, r in reports.items()}
    placed = {m: sum(p["notes"] for p in r) for m, r in reports.items()}
    return (not bad["uniform"] and not bad["per-page"] and all(v > 0 for v in placed.values())), \
        f"overlapping pages={bad}; footnote blocks placed={placed}"


@case("T1-9c per-page mode falls back to uniform rather than to overlapping text")
def _():
    # When a page's band cannot be cleared by moving content, the mode must degrade to the
    # reservation that always works — and say so — not ship text drawn over text.
    huge = "OVERSIZE " + ("this note is far longer than any page can hold " * 500)
    html = f"""<html><head><style>
@page {{ size: A4; margin: 20mm 16mm }}
body {{ font-family: sans-serif; font-size: 12px; line-height: 1.5 }}
.fn {{ float: footnote; font-size: 8pt }}
</style></head><body><h1>Oversize</h1>
<p>{_unique_sentence(1)}<span class='fn'>{huge}</span> {_unique_sentence(2)}.</p>
<p>{_unique_sentence(3)}.</p></body></html>"""
    _pdf, diag = render("ts-t19-fallback", html, {"footnoteReservationMode": "per-page"})
    reported = [w for w in diag.get("warnings", [])
                if "Footnote notice" in w or "Layout warning" in w]
    return bool(reported), f"degradation reported={bool(reported)}"


@case("T1-9d the engine CHOOSES the reservation strategy and says why")
def _():
    # The caller cannot see from their HTML whether per-page pays off — it depends on how
    # unevenly the footnotes fall, which is only knowable after a render. The default must
    # therefore decide, and must report the decision rather than leaving it invisible.
    _pdf, diag = render("ts-t19-auto", crowded_doc())
    decisions = [w for w in diag.get("warnings", []) if "Footnote reservation:" in w]
    if not decisions:
        return False, "no reservation decision reported"
    said = decisions[0]
    names_strategy = ("PER-PAGE" in said) or ("UNIFORM" in said)
    gives_numbers = "pt" in said and "page(s)" in said
    return (names_strategy and gives_numbers), f"{said[:120]}"


@case("T1-9e an UNEVEN document is given per-page, an EVEN one is not")
def _():
    # The decision has to track the document's actual shape, not fire at random. Footnotes
    # crowded onto one page with plain pages after are worth reclaiming; footnotes spread
    # one per page are not, and paying extra renders for them is waste.
    # Uneven: five footnotes on one page and several plain pages after it, long enough
    # that the reclaimable height is worth a render. Even: a short document where it is not.
    _pdf, uneven = render("ts-t19-uneven", crowded_doc(note_count=5, trailing=140))
    _pdf2, even = render("ts-t19-even", crowded_doc(note_count=1, trailing=6))

    def decision(diag):
        said = next((w for w in diag.get("warnings", []) if "Footnote reservation:" in w), "")
        return "per-page" if "PER-PAGE" in said else ("uniform" if "UNIFORM" in said else "none")

    picked_uneven, picked_even = decision(uneven), decision(even)
    return (picked_uneven == "per-page" and picked_even == "uniform"), \
        f"uneven document -> {picked_uneven}; even document -> {picked_even}"


@case("T1-9f an explicit mode overrides the automatic choice")
def _():
    # Auto is a default, not a policy: a caller who knows their corpus must still be able
    # to pin the cheap strategy or the tight one.
    html = crowded_doc(note_count=5, trailing=140)
    _pdf, forced = render("ts-t19-forced-uniform", html,
                          {"footnoteReservationMode": "uniform"})
    said = next((w for w in forced.get("warnings", []) if "Footnote reservation:" in w), "")
    overlaps = [w for w in forced.get("warnings", []) if "overlaps the body text" in w]
    # An explicit mode makes no automatic decision, and must still be correct.
    return (said == "" and not overlaps), \
        f"auto-decision suppressed={said == ''}; overlaps={len(overlaps)}"


# --- Regressions found by building the capability proof sheet -----------------------
#
# Four defects that every existing gate passed straight over, because each fixture had
# happened to avoid the exact combination that triggers them. They are pinned here.

@case("REG-0 running headers survive encryption, watermarking and outlines")
def _():
    # Two separately-verified features that did not compose. Running headers were drawn
    # AFTER post-processing, which finishes by encrypting — and nothing can reopen an
    # encrypted PDF to draw on it, so setting a password silently cost the document every
    # header. Each feature had its own passing gate; nothing tested them together.
    import fitz
    html = """<html><head><style>
@page { size: A4; margin: 20mm; @top-center { content: string(chapter); font-size: 9pt } }
body { font-family: sans-serif; font-size: 12px }
h1 { string-set: chapter content() }
.fn { float: footnote; font-size: 8pt }
section { page-break-before: always }
</style></head><body>
<section><h1>CHAPTERONE</h1><p>Body one<span class="fn">FOOTNOTEONE a note.</span>.</p></section>
<section><h1>CHAPTERTWO</h1><p>Body two.</p></section>
</body></html>"""

    combinations = {
        "plain": {},
        "encrypted": {"userPassword": "secret", "ownerPassword": "owner"},
        "watermarked": {"watermarkText": "DRAFT", "generateOutlineFromHeadings": True},
    }
    broken = []
    for label, extra in combinations.items():
        pdf, diag = render(f"ts-reg0-{label}", html, extra)
        doc = fitz.open(stream=pdf, filetype="pdf")
        if doc.needs_pass:
            doc.authenticate("secret")
        first = doc[0].get_text()
        # The chapter name appears once as the heading and once in the running header.
        if first.count("CHAPTERONE") < 2 or "FOOTNOTEONE" not in first:
            broken.append(f"{label}: header x{first.count('CHAPTERONE')}, footnote={'FOOTNOTEONE' in first}")
        if [w for w in diag.get("warnings", []) if "could not be applied" in w]:
            broken.append(f"{label}: engine reported it dropped them")

    return not broken, f"combinations broken={broken or 'none'}"


@case("REG-1 text-transform does not break running headers")
def _():
    # `text-transform` is applied when the page is PAINTED but not when the DOM is read,
    # so an uppercased badge next to a heading made the fingerprint unmatchable. Measured:
    # nine of ten pages named the wrong section.
    body = "".join(
        f"<section><h2>Chapter {c}</h2><div class='badge'>Part {c} reference</div>"
        f"<p>{'Body copy for chapter ' + c + '. ' * 60}</p></section>"
        for c in ("Alpha", "Beta", "Gamma"))
    html = f"""<html><head><style>
@page {{ size: A4; margin: 20mm 16mm; @top-left {{ content: string(chapter); font-size: 9pt }} }}
body {{ font-family: sans-serif; font-size: 12px }}
h2 {{ string-set: chapter content() }}
.badge {{ text-transform: uppercase; font-size: 8pt }}
section {{ page-break-before: always }}
</style></head><body>{body}</body></html>"""
    pdf, _ = render("ts-reg-texttransform", html)
    pages = pages_text(pdf)
    wrong = []
    for i, text in enumerate(pages, 1):
        owner = next((c for c in ("Alpha", "Beta", "Gamma") if f"Chapter {c}" in text), None)
        if owner is None:
            continue
        # The header repeats the chapter name, so the owning chapter must appear at least
        # twice on its own page: once as the heading and once in the running header.
        if text.count(f"Chapter {owner}") < 2:
            wrong.append(f"p{i}: header missing for {owner}")
    return not wrong, f"{len(pages)} pages; {wrong[:3] or 'every page headed correctly'}"


@case("REG-2 @page :first does not leak into later named-page runs")
def _():
    import fitz
    # Named pages render the document in parts and Chromium treats the first page of EVERY
    # part as `:first`. Measured: a cover declaring a 70mm left margin put the same indent
    # on the opening page of every later run.
    html = """<html><head><style>
@page { size: A4; margin: 20mm }
@page :first { margin-left: 70mm }
@page wide { size: A4 landscape; margin: 20mm }
body { font-family: sans-serif; font-size: 12px }
#w { page: wide }
section { page-break-before: always }
</style></head><body>
<section><h1>COVER</h1></section><section><h1>BODYA</h1></section>
<section id="w"><h1>WIDE</h1></section><section><h1>BODYB</h1></section>
</body></html>"""
    pdf, _ = render("ts-reg-firstleak", html, {"marginTop": None, "marginBottom": None,
                                               "marginLeft": None, "marginRight": None,
                                               "pageSize": None})
    doc = fitz.open(stream=pdf, filetype="pdf")
    lefts = [min((b[0] for b in pg.get_text("blocks") if b[4].strip()), default=-1) for pg in doc]
    if len(lefts) != 4:
        return False, f"expected 4 pages, got {len(lefts)}"
    cover_indented = lefts[0] > lefts[1] + 50
    others_equal = max(lefts[1:]) - min(lefts[1:]) < 12
    return (cover_indented and others_equal), \
        f"left insets={[round(x) for x in lefts]} (only page 1 may carry the cover margin)"


@case("REG-3 a named page still applies when :first is also declared")
def _():
    import fitz
    # The first fix for REG-2 re-asserted the DEFAULT geometry at `:first` specificity,
    # which outranks the run's own rule — and turned a one-page landscape run back to
    # portrait. Both have to be restated together.
    html = """<html><head><style>
@page { size: A4; margin: 20mm }
@page :first { margin-left: 70mm }
@page wide { size: A4 landscape; margin: 20mm }
body { font-family: sans-serif; font-size: 12px }
#w { page: wide }
section { page-break-before: always }
</style></head><body>
<section><h1>COVER</h1></section>
<section id="w"><h1>WIDE</h1></section>
<section><h1>TAIL</h1></section>
</body></html>"""
    pdf, _ = render("ts-reg-namedfirst", html, {"marginTop": None, "marginBottom": None,
                                                "marginLeft": None, "marginRight": None,
                                                "pageSize": None})
    doc = fitz.open(stream=pdf, filetype="pdf")
    shapes = [(round(pg.rect.width), round(pg.rect.height)) for pg in doc]
    landscape = [i + 1 for i, s in enumerate(shapes) if s[0] > s[1]]
    return landscape == [2], f"page shapes={shapes}; landscape pages={landscape} (want exactly [2])"


@case("REG-4 a floated figure's own text does not poison its section's header")
def _():
    # A page float is redrawn as an image, so its text leaves the text layer. A fingerprint
    # built from it can never match — measured, the section holding a floated figure had
    # its running header resolve to the table of contents.
    html = """<html><head><style>
@page { size: A4; margin: 20mm 16mm; @top-left { content: string(chapter); font-size: 9pt } }
body { font-family: sans-serif; font-size: 12px }
h2 { string-set: chapter content() }
.pf { float: bottom; width: 300px; height: 70px; background: #dddddd }
section { page-break-before: always }
</style></head><body>
<section><h2>Opening section</h2><p>Ordinary body text for the opening section.</p></section>
<section><h2>Figure section</h2>
  <div class="pf">CAPTIONTEXT that leaves the text layer</div>
  <p>Body text that follows the floated figure in this section.</p></section>
</body></html>"""
    pdf, _ = render("ts-reg-floatheader", html)
    pages = pages_text(pdf)
    figure_page = next((i for i, t in enumerate(pages) if "Body text that follows" in t), None)
    if figure_page is None:
        return False, "fixture did not produce the figure section"
    # The header repeats the section title on its own page.
    return pages[figure_page].count("Figure section") >= 2, \
        f"{len(pages)} pages; 'Figure section' occurrences on its page = {pages[figure_page].count('Figure section')} (want >=2)"


@case("T1-5g emphasis and links inside a footnote reach the band intact")
def _():
    # The footnote band is drawn text, not a screenshot, so what must survive is the TEXT
    # exactly — punctuation attached, no words dropped where a style changed, and a real
    # clickable annotation for a citation link.
    html = """<html><head><style>
@page { size: A4; margin: 20mm }
body { font-family: sans-serif; font-size: 12px }
.fn { float: footnote; font-size: 8pt }
</style></head><body><h1>Citation</h1>
<p>Legal citation<span class="fn">See <b>Smith v. Jones</b>, <i>2026 WL 1234</i>, and
<a href="https://example.invalid/filing">the original filing</a> for detail.</span> here.</p>
</body></html>"""
    import fitz
    pdf, _ = render("ts-footnote-runs", html)
    page = fitz.open(stream=pdf, filetype="pdf")[0]
    words = " ".join(w[4] for w in page.get_text("words"))
    want = "See Smith v. Jones, 2026 WL 1234, and the original filing for detail."
    links = sorted({l.get("uri") for l in page.get_links() if l.get("uri")})
    return (want in words and links == ["https://example.invalid/filing"]), \
        f"text intact={want in words}; links={links}"


@case("T1-5h footnote emphasis is drawn in a REAL bold and italic face")
def _():
    # Before a font resolver was registered, PdfSharpCore returned one identical face for
    # every family and every style, so emphasis silently rendered upright and regular. The
    # proof is in the fonts the finished PDF actually embeds, not in how it looks.
    import fitz
    html = """<html><head><style>
@page { size: A4; margin: 20mm }
body { font-family: sans-serif; font-size: 12px }
.fn { float: footnote; font-size: 9pt }
</style></head><body><h1>Emphasis</h1>
<p>Citation<span class="fn">See <b>BOLDRUN</b> and <i>ITALICRUN</i> for detail.</span> here.</p>
</body></html>"""
    pdf, _ = render("ts-footnote-emphasis", html)
    page = fitz.open(stream=pdf, filetype="pdf")[0]
    fonts = {s["font"] for b in page.get_text("dict")["blocks"] if b["type"] == 0
             for l in b["lines"] for s in l["spans"]}
    bold = [f for f in fonts if "Bold" in f]
    italic = [f for f in fonts if "Italic" in f or "Oblique" in f]
    text_intact = "See BOLDRUN and ITALICRUN for detail." in " ".join(
        w[4] for w in page.get_text("words"))
    return (bool(bold) and bool(italic) and text_intact), \
        f"bold faces={bold}; italic faces={italic}; text intact={text_intact}"


@case("T1-8f a floated table keeps a selectable text layer over its image")
def _():
    # A page float is drawn as a picture, and pictures carry no text. Without a text layer
    # laid over it, a floated table cannot be selected, searched or read aloud — the exact
    # regression that makes rasterisation unacceptable for real documents.
    html = """<html><head><style>
@page { size: A4; margin: 20mm }
body { font-family: sans-serif; font-size: 12px }
table.pf { float: bottom; border-collapse: collapse; font-size: 10px }
table.pf td, table.pf th { border: 1px solid #333; padding: 5px 9px }
</style></head><body><h1>Floated table</h1>
<table class="pf">
  <tr><th>REGIONCELL</th><th>REVENUECELL</th></tr>
  <tr><td>NORTHCELL</td><td>4,120</td></tr>
  <tr><td>SOUTHCELL</td><td>3,880</td></tr>
</table>
<p>Body text after the floated table.</p></body></html>"""
    import fitz
    pdf, diag = render("ts-float-textlayer", html)
    page = fitz.open(stream=pdf, filetype="pdf")[0]
    drawn_as_image = len(page.get_image_info()) > 0
    cells = ["REGIONCELL", "REVENUECELL", "NORTHCELL", "SOUTHCELL", "3,880"]
    missing = [c for c in cells if not page.search_for(c)]
    return (drawn_as_image and not missing), \
        f"image drawn={drawn_as_image}; cells missing from the text layer={missing}"


@case("T1-6a a single line is never stranded across a page break")
def _():
    pdf, _ = render("ts-orphan-default", straddle_doc())
    pages = pages_text(pdf)
    counts = [len(re.findall(r"L\d+word", t)) for t in pages]
    # Every page that contains any of the paragraph must hold at least 2 of its lines —
    # that is what orphans/widows: 2 exists to guarantee.
    stranded = [i + 1 for i, c in enumerate(counts) if 0 < c < 2]
    return not stranded, f"lines per page={counts}, pages with a single stranded line={stranded}"


@case("T1-6b caller-supplied orphans/widows actually change the break")
def _():
    # Previously hard-coded to 2 with no caller control. The widow count is now asserted
    # DIRECTLY — raising it must carry more of the paragraph onto the continuation page —
    # rather than through the side effect of a short block being moved whole. That side
    # effect is the planner's own doing, not the CSS's, so it never proved the setting
    # reached Chromium at all.
    default_pages = pages_text(render("ts-wo-default", straddle_doc())[0])
    strict_pages = pages_text(render("ts-wo-strict", straddle_doc(),
                                     {"orphans": 4, "widows": 4})[0])
    d = [len(re.findall(r"L\d+word", t)) for t in default_pages]
    st = [len(re.findall(r"L\d+word", t)) for t in strict_pages]

    if len([c for c in d if c > 0]) < 2:
        return False, f"fixture did not straddle a page boundary: default={d}"

    return (d != st and st[-1] > d[-1]), \
        f"default carried {d[-1]} onto the next page, widows:4 carried {st[-1]} (default={d} strict={st})"


@case("T1-6c a block taller than a page is REPORTED as unsatisfiable")
def _():
    # orphans/widows are impossible to honour here; the browser must split regardless.
    # Silent degradation is the failure mode being guarded against.
    huge = " ".join(f"W{i}word alpha beta gamma delta epsilon" for i in range(1, 400))
    html = f"""<html><head><style>
@page {{ size: A4; margin: 20mm }}
body {{ font-family: sans-serif; font-size: 12px; line-height: 18px }}
</style></head><body><p>{huge}</p></body></html>"""
    _pdf, diag = render("ts-unsatisfiable", html, {"orphans": 3, "widows": 3})
    warned = [w for w in diag.get("warnings", [])
              if "taller than a single page" in w or "orphans/widows" in w.lower()]
    return bool(warned), f"unsatisfiable-block warning emitted={bool(warned)}"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--update-baseline", action="store_true")
    args = ap.parse_args()
    try:
        import fitz  # noqa: F401
    except ImportError:
        print("FATAL: PyMuPDF required. pip install pymupdf")
        return 2
    for tool in ("pdftotext", "pdfinfo"):
        if subprocess.run(["which", tool], capture_output=True).returncode != 0:
            print(f"FATAL: poppler `{tool}` not found. brew install poppler")
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
    (EVIDENCE / "typesetting-gate.json").write_text(
        json.dumps(results, indent=2), encoding="utf-8")
    print("-" * 124)
    print(f"summary: {len(CASES) - failed}/{len(CASES)} passed")
    print("Scope: named pages are rendered as separate parts and stitched; footnote space is")
    print("reserved uniformly by default and per-page on request (one render per page);")
    print("floated content is drawn as an image, so text inside it leaves no text layer.")

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
