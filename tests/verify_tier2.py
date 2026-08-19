#!/usr/bin/env python3
"""
Verify every Tier 2 claim with tools this project did not write.

    python3 tests/verify_tier2.py                      # needs the API on :5276
    python3 tests/verify_tier2.py --pdf some.pdf       # check an existing file only

Nothing here reads the engine's own diagnostics. Each check names the external tool
that produced the verdict, so a reader can re-run that one command by hand. A tool
that is not installed produces SKIP — never a pass, and never a failure.
"""
import argparse, base64, hashlib, json, pathlib, re, shutil, subprocess, sys, tempfile
import urllib.request, urllib.error

API = "http://localhost:5276/api/v1/pdf"
KEY = "test-api-key-123"
OUT = pathlib.Path(tempfile.mkdtemp(prefix="verify-t2-"))
RESULTS = []


def record(claim, tool, ok, detail):
    RESULTS.append((claim, tool, ok, detail))
    mark = "PASS" if ok is True else ("SKIP" if ok is None else "FAIL")
    print(f"  [{mark}] {claim}\n         via {tool}: {detail}")


def have(binary):
    return shutil.which(binary) is not None


def post(endpoint, payload, files=None):
    req = urllib.request.Request(f"{API}/{endpoint}", data=json.dumps(payload).encode(),
                                 method="POST",
                                 headers={"Content-Type": "application/json", "X-Api-Key": KEY})
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            return r.read(), None
    except urllib.error.HTTPError as e:
        return None, f"HTTP {e.code}: {e.read().decode()[:200]}"


def run(cmd):
    p = subprocess.run(cmd, capture_output=True, text=True)
    return p.returncode, p.stdout + p.stderr


# ---------------------------------------------------------------- T2-1 attachments
INVOICE = ('<?xml version="1.0" encoding="UTF-8"?><rsm:CrossIndustryInvoice>'
           '<ID>VERIFY-001</ID><Total>0.00</Total></rsm:CrossIndustryInvoice>')


def check_attachments(pdf_path):
    print("\nT2-1  ATTACHMENTS — can a machine pull the e-invoice XML back out?")
    if not have("pdfdetach"):
        return record("the attachment is listed and extractable", "poppler pdfdetach",
                      None, "poppler not installed (brew install poppler)")
    rc, out = run(["pdfdetach", "-list", str(pdf_path)])
    listed = re.findall(r"\d+:\s*(\S+)", out)
    record("the attachment is listed in the file", "poppler pdfdetach -list",
           bool(listed), f"listed={listed}")
    if not listed:
        return
    # Byte-identity has to be checked against a payload THIS script supplied. Comparing
    # a file someone else rendered against our fixture only proves they differ.
    fresh, err = post("generate", {"documentName": "att", "documentType": 4,
                                   "html": "<h1>Invoice VERIFY-001</h1>",
                                   "options": {"attachments": [
                                       {"fileName": "factur-x.xml",
                                        "contentBase64": base64.b64encode(INVOICE.encode()).decode(),
                                        "mimeType": "text/xml", "relationship": "Data"}]}})
    if err:
        record("it extracts byte-identical to what was supplied", "poppler + sha256",
               False, err)
    else:
        probe = OUT / "attach.pdf"; probe.write_bytes(fresh)
        run(["pdfdetach", "-saveall", "-o", str(OUT), str(probe)])
        got = (OUT / "factur-x.xml").read_bytes()
        want = hashlib.sha256(INVOICE.encode()).hexdigest()
        record("it extracts byte-identical to what was supplied", "poppler + sha256",
               hashlib.sha256(got).hexdigest() == want,
               f"sha256 in={want[:16]}… out={hashlib.sha256(got).hexdigest()[:16]}… "
               f"({len(got)} bytes)")
    raw = pdf_path.read_bytes()
    record("it is an ASSOCIATED file, which PDF/A-3 requires", "byte inspection",
           b"/AF" in raw, "/AF present in the catalog" if b"/AF" in raw else "/AF missing")
    record("the MIME type is not double-escaped", "byte inspection",
           b"text#232Fxml" not in raw, "no text#232Fxml in the file")


# ---------------------------------------------------------------- T2-2 signatures
def check_signature(pdf_path):
    print("\nT2-2  SIGNATURE — does it verify outside this project, and does tampering break it?")
    raw = pdf_path.read_bytes()
    br = re.search(rb"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]", raw)
    hexed = re.search(rb"/Contents\s*<([0-9A-Fa-f]+)>", raw)
    if not (br and hexed):
        return record("the document is signed", "byte inspection", False, "no /ByteRange")
    if not have("openssl"):
        return record("the signature verifies", "openssl", None, "openssl not installed")
    a, b, c, d = (int(x) for x in br.groups())
    signed = raw[a:a + b] + raw[c:c + d]
    der = bytes.fromhex(hexed.group(1).decode()).rstrip(b"\x00")
    (OUT / "sig.der").write_bytes(der)
    (OUT / "content.bin").write_bytes(signed)
    rc, out = run(["openssl", "cms", "-verify", "-inform", "DER", "-in", str(OUT / "sig.der"),
                   "-content", str(OUT / "content.bin"), "-binary", "-noverify",
                   "-out", "/dev/null"])
    record("the signature verifies over the bytes the file declares", "openssl cms -verify",
           rc == 0, out.strip().splitlines()[-1] if out.strip() else "ok")

    # Flip one byte of visible page content and re-check. This must FAIL.
    target = raw.find(b"Capability")
    if target > 0:
        broken = bytearray(raw); broken[target] = ord("X")
        (OUT / "tampered.bin").write_bytes(bytes(broken)[a:a + b] + bytes(broken)[c:c + d])
        rc2, _ = run(["openssl", "cms", "-verify", "-inform", "DER", "-in", str(OUT / "sig.der"),
                      "-content", str(OUT / "tampered.bin"), "-binary", "-noverify",
                      "-out", "/dev/null"])
        record("changing ONE byte of the page breaks it", "openssl cms -verify",
               rc2 != 0, "tampered content is rejected" if rc2 else "STILL VERIFIED — bad")

    rc3, out3 = run(["openssl", "cms", "-cmsout", "-inform", "DER", "-in", str(OUT / "sig.der"),
                     "-print", "-noout"])
    record("it carries an RFC 3161 timestamp (PAdES B-T)", "openssl cms -print",
           "1.2.840.113549.1.9.16.2.14" in out3,
           "signatureTimeStampToken attribute present"
           if "1.2.840.113549.1.9.16.2.14" in out3 else "no timestamp attribute")
    record("it carries its own validation data (PAdES B-LT)", "byte inspection",
           b"/Type /DSS" in raw or b"/DSS" in raw,
           f"/DSS present, {raw.count(b'%%EOF')} %%EOF markers (incremental update)")


# ---------------------------------------------------------------- T2-3 form fields
def check_forms(pdf_path):
    print("\nT2-3  FORM FIELDS — are they visible on the page AND actually fillable?")
    try:
        from pypdf import PdfReader, PdfWriter
    except ImportError:
        return record("the fields fill and the values persist", "pypdf",
                      None, "pypdf not installed (pip install pypdf)")
    fields = PdfReader(str(pdf_path)).get_fields() or {}
    text_fields = [k for k, v in fields.items() if v.get("/FT") == "/Tx"]
    record("an independent reader sees the fields", "pypdf get_fields",
           bool(text_fields), f"text fields={text_fields}")
    if not text_fields:
        return

    # A field with no /AP draws NOTHING in viewers that ignore /NeedAppearances —
    # macOS Preview among them. Structure alone is not a usable form.
    import fitz
    doc = fitz.open(str(pdf_path))
    missing = [w.field_name for p in doc for w in p.widgets()
               if w.field_type_string != "Signature"
               and "/AP" not in doc.xref_object(w.xref)]
    record("every field carries an appearance stream, so it is VISIBLE", "PyMuPDF xref dump",
           not missing, "all widgets have /AP" if not missing else f"no /AP on {missing}")

    writer = PdfWriter(clone_from=str(pdf_path))
    page = next(i for i, p in enumerate(writer.pages)
                if any(a for a in (p.get("/Annots") or [])))
    typed = {text_fields[0]: "typed by the verifier"}
    import contextlib, io, logging
    logging.getLogger("pypdf").setLevel(logging.ERROR)
    for p in writer.pages:
        with contextlib.redirect_stdout(io.StringIO()):
            try:
                writer.update_page_form_field_values(p, typed)
            except Exception:
                continue
    filled = OUT / "filled.pdf"
    with open(filled, "wb") as f:
        writer.write(f)
    back = (PdfReader(str(filled)).get_fields() or {}).get(text_fields[0], {}).get("/V")
    record("a typed value persists across save and reopen", "pypdf round-trip",
           back == "typed by the verifier", f"read back {back!r}")
    if have("pdftoppm"):
        rc, out = run(["pdftoppm", "-r", "70", "-png", str(filled), str(OUT / "filled")])
        record("the filled document renders with no viewer warnings", "poppler pdftoppm",
               "Error" not in out and "Unknown font" not in out,
               out.strip()[:80] or f"rendered clean, images in {OUT}")


# ---------------------------------------------------------------- T2-4 / T2-5 / T2-6
def check_transform_and_print(pdf_path):
    print("\nT2-4  PAGE OPERATIONS / T2-5  FAST WEB VIEW / T2-6  PRINT PRODUCTION")
    if not have("qpdf"):
        record("the linearized file really is linearized", "qpdf", None, "qpdf not installed")
    else:
        pdf, err = post("generate", {"documentName": "lin", "documentType": 4,
                                     "html": "<h1>A</h1><p style='page-break-after:always'>x</p><h1>B</h1>",
                                     "options": {"linearize": True}})
        if err:
            record("the linearized file really is linearized", "qpdf", False, err)
        else:
            p = OUT / "lin.pdf"; p.write_bytes(pdf)
            rc, out = run(["qpdf", "--check", str(p)])
            record("the linearized file really is linearized", "qpdf --check",
                   "File is linearized" in out, "qpdf reports: File is linearized"
                   if "File is linearized" in out else out.strip()[:120])

    if have("pdfinfo"):
        pdf, err = post("generate", {"documentName": "bleed", "documentType": 4,
                                     "html": "<h1>Trim</h1>",
                                     "options": {"pageSize": "A4", "bleedMm": 3,
                                                 "cropMarks": True}})
        if err:
            record("bleed produces a page larger than the trim box", "pdfinfo", False, err)
        else:
            p = OUT / "bleed.pdf"; p.write_bytes(pdf)
            rc, out = run(["pdfinfo", "-box", str(p)])
            media = re.search(r"MediaBox:\s+[\d.]+\s+[\d.]+\s+([\d.]+)\s+([\d.]+)", out)
            trim = re.search(r"TrimBox:\s+[\d.]+\s+[\d.]+\s+([\d.]+)\s+([\d.]+)", out)
            ok = bool(media and trim and float(media.group(1)) > float(trim.group(1)))
            record("the sheet is bigger than the finished page (real bleed)", "pdfinfo -box",
                   ok, f"MediaBox w={media.group(1) if media else '?'} "
                       f"TrimBox w={trim.group(1) if trim else 'MISSING'}")
    else:
        record("bleed produces a page larger than the trim box", "pdfinfo", None,
               "poppler not installed")

    pdf, err = post("generate", {"documentName": "src", "documentType": 4,
                                 "html": "<h1>1</h1><div style='page-break-before:always'><h1>2</h1></div>"
                                         "<div style='page-break-before:always'><h1>3</h1></div>"})
    if err:
        return record("extract preserves the order asked for", "PyMuPDF", False, err)
    import fitz
    body = {"documentName": "extracted", "file": base64.b64encode(pdf).decode(),
            "operation": "extract", "pages": "3,1"}
    got, err = post("transform", body)
    if err:
        return record("extract preserves the order asked for", "PyMuPDF", False, err)
    d = fitz.open(stream=got, filetype="pdf")
    order = [p.get_text().strip()[:1] for p in d]
    record("extract 3,1 returns pages in THAT order, not sorted", "PyMuPDF text read",
           order == ["3", "1"], f"pages came back as {order}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--pdf", default="PDFEngine_Capability_Proof_Sheet.pdf")
    args = ap.parse_args()
    pdf_path = pathlib.Path(args.pdf)
    if not pdf_path.exists():
        sys.exit(f"no such file: {pdf_path}")

    print(f"Verifying {pdf_path} with external tools. Artifacts in {OUT}")
    check_attachments(pdf_path)
    check_signature(pdf_path)
    check_forms(pdf_path)
    check_transform_and_print(pdf_path)

    failed = [r for r in RESULTS if r[2] is False]
    skipped = [r for r in RESULTS if r[2] is None]
    print(f"\n{'-' * 86}")
    print(f"{len(RESULTS) - len(failed) - len(skipped)}/{len(RESULTS)} verified externally, "
          f"{len(failed)} failed, {len(skipped)} could not be checked")
    for claim, tool, _ok, detail in failed:
        print(f"  FAILED: {claim} — {tool}: {detail}")
    for claim, tool, _ok, detail in skipped:
        print(f"  UNCHECKED: {claim} — {tool}: {detail}")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
