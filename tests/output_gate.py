#!/usr/bin/env python3
"""
PDFEngine — PDF Output Features Gate (Feature Backlog Tier 2)

Tier 1 is about how a document is LAID OUT. This tier is about what the resulting file can
be USED for, and each item maps to a customer segment that cannot be served without it:

  T2-1  attachments / embedded files   EU e-invoicing (Factur-X / ZUGFeRD)
  T2-2  digital signatures            signed contracts and approvals (PAdES B-T)
  T2-3  interactive form fields      fillable text fields and checkboxes
  T2-4  split / rotate / flatten / N-up  document assembly
  T2-5  linearization                large documents opened in a browser
  T2-6  bleed / crop marks           print production (RGB only — no CMYK, no PDF/X)

Attachments are checked with real tools rather than by reading back our own writer:
poppler's `pdfdetach` for the attachment pane, and veraPDF for whether the result is
actually the PDF/A-3 an e-invoicing recipient will demand. A file that opens fine and fails
the recipient's validator is a rejected invoice, not a cosmetic bug.

Usage: python3 tests/output_gate.py [--verapdf /path/to/verapdf] [--update-baseline]
"""
import argparse, base64, json, os, pathlib, re, subprocess, sys, tempfile
import urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
EVIDENCE = ROOT / "tests" / "evidence"
BASELINE = ROOT / "tests" / "corpus" / "output-baseline.json"
API = os.environ.get("PDFENGINE_BASE", "http://localhost:5276") + "/api/v1/pdf/generate"
KEY = os.environ.get("PDFENGINE_API_KEY", "test-api-key-123")
VERAPDF = None

INVOICE_XML = ('<?xml version="1.0" encoding="UTF-8"?>'
               '<rsm:CrossIndustryInvoice><ID>INV-2026-042</ID><Total>121.90</Total>'
               '</rsm:CrossIndustryInvoice>')

INVOICE_HTML = """<html lang="en"><head><meta charset="utf-8"><title>Invoice INV-2026-042</title></head>
<body style="font-family:sans-serif"><h1>Invoice INV-2026-042</h1>
<p>Total due: EUR 121.90</p></body></html>"""


def render(name, html, options=None):
    payload = json.dumps({"documentName": name, "documentType": 4, "html": html,
                          "options": options or {}}).encode()
    req = urllib.request.Request(API, data=payload, method="POST",
                                 headers={"Content-Type": "application/json", "X-Api-Key": KEY})
    try:
        with urllib.request.urlopen(req, timeout=300) as r:
            return r.read(), json.loads(r.headers.get("X-Render-Diagnostics", "{}")), None
    except urllib.error.HTTPError as e:
        return None, {}, f"HTTP {e.code}: {e.read().decode()[:200]}"


def attachments_of(pdf_bytes):
    """What a real reader's attachment pane would list."""
    with tempfile.TemporaryDirectory() as td:
        p = pathlib.Path(td) / "d.pdf"
        p.write_bytes(pdf_bytes)
        out = subprocess.run(["pdfdetach", "-list", str(p)], capture_output=True, text=True).stdout
        return re.findall(r"^\s*\d+:\s*(.+)$", out, re.M)


def extract_attachment(pdf_bytes, index=1):
    with tempfile.TemporaryDirectory() as td:
        p = pathlib.Path(td) / "d.pdf"
        p.write_bytes(pdf_bytes)
        out = pathlib.Path(td) / "payload"
        subprocess.run(["pdfdetach", "-save", str(index), "-o", str(out), str(p)],
                       capture_output=True)
        return out.read_bytes() if out.exists() else b""


def verapdf_compliant(pdf_bytes, flavour):
    """None when veraPDF is unavailable — reported as SKIP, never as a pass."""
    if not VERAPDF:
        return None
    with tempfile.TemporaryDirectory() as td:
        p = pathlib.Path(td) / "d.pdf"
        p.write_bytes(pdf_bytes)
        out = subprocess.run([VERAPDF, "--flavour", flavour, str(p)],
                             capture_output=True, text=True).stdout
    m = re.search(r'isCompliant="(\w+)"', out)
    return m.group(1) == "true" if m else False


CASES = []


def case(name):
    def deco(fn):
        CASES.append((name, fn))
        return fn
    return deco


def b64(text):
    return base64.b64encode(text.encode()).decode()


# --- T2-1: attachments ---------------------------------------------------------------

@case("T2-1a an embedded file appears in the attachment pane and extracts byte-identical")
def _():
    att = [{"fileName": "factur-x.xml", "contentBase64": b64(INVOICE_XML),
            "mimeType": "text/xml", "description": "Factur-X invoice data",
            "relationship": "Data"}]
    pdf, _diag, err = render("t21-basic", INVOICE_HTML, {"attachments": att})
    if err:
        return False, err
    listed = attachments_of(pdf)
    extracted = extract_attachment(pdf)
    return (listed == ["factur-x.xml"] and extracted.decode() == INVOICE_XML), \
        f"listed={listed}; extracted matches original={extracted.decode() == INVOICE_XML}"


@case("T2-1b a Factur-X carrier is genuinely PDF/A-3b conformant")
def _():
    # The whole point of the feature. An e-invoice that is not PDF/A-3 is refused by the
    # recipient, and nothing about the file looks wrong when you open it.
    att = [{"fileName": "factur-x.xml", "contentBase64": b64(INVOICE_XML),
            "mimeType": "text/xml", "description": "Factur-X invoice data",
            "relationship": "Data"}]
    pdf, _diag, err = render("t21-facturx", INVOICE_HTML,
                             {"pdfaCompliance": "PDF/A-3b", "attachments": att,
                              "title": "Invoice INV-2026-042", "author": "PDFEngine"})
    if err:
        return False, err
    compliant = verapdf_compliant(pdf, "3b")
    if compliant is None:
        return None, "veraPDF unavailable — SKIPPED (not a pass)"
    return compliant and attachments_of(pdf) == ["factur-x.xml"], \
        f"PDF/A-3b compliant={compliant}, attachment present={attachments_of(pdf)}"


@case("T2-1c the MIME type survives as a real MIME type")
def _():
    # A PDF name cannot hold a literal '/'. Hand-escaping it double-escapes — measured,
    # 'text/xml' was written as 'text#2Fxml' and veraPDF failed PDF/A-3 clause 6.8.
    att = [{"fileName": "data.xml", "contentBase64": b64("<x/>"), "mimeType": "text/xml"}]
    pdf, _diag, err = render("t21-mime", INVOICE_HTML, {"attachments": att})
    if err:
        return False, err
    raw = pdf.decode("latin-1")
    good = "/text#2Fxml" in raw
    doubled = "#232F" in raw
    return (good and not doubled), f"correctly escaped={good}, double-escaped={doubled}"


@case("T2-1d attachments plus PDF/A-2b are REFUSED, not silently wrong")
def _():
    # PDF/A-2 permits only embedded PDF/A documents. Producing the file anyway would give
    # the caller something that fails their recipient's validator.
    att = [{"fileName": "data.xml", "contentBase64": b64("<x/>"), "mimeType": "text/xml"}]
    _pdf, _diag, err = render("t21-bad-level", INVOICE_HTML,
                              {"pdfaCompliance": "PDF/A-2b", "attachments": att})
    return (err is not None and "400" in err), f"refused={err is not None}; {(err or '')[:90]}"


@case("T2-1e several attachments with different relationships all survive")
def _():
    files = [
        {"fileName": "invoice.xml", "contentBase64": b64("<invoice/>"),
         "mimeType": "text/xml", "relationship": "Data"},
        {"fileName": "terms.txt", "contentBase64": b64("Payment terms: 30 days."),
         "mimeType": "text/plain", "relationship": "Supplement"},
        {"fileName": "notes.txt", "contentBase64": b64("Internal notes."),
         "mimeType": "text/plain", "relationship": "Unspecified"},
    ]
    pdf, _diag, err = render("t21-many", INVOICE_HTML, {"attachments": files})
    if err:
        return False, err
    listed = attachments_of(pdf)
    raw = pdf.decode("latin-1")
    rels = [r for r in ("/Data", "/Supplement", "/Unspecified") if r in raw]
    return (len(listed) == 3 and len(rels) == 3), f"listed={listed}; relationships present={rels}"


@case("T2-1f a document with no attachments is untouched")
def _():
    pdf, _diag, err = render("t21-none", INVOICE_HTML, {})
    if err:
        return False, err
    raw = pdf.decode("latin-1")
    return (attachments_of(pdf) == [] and "/EmbeddedFile" not in raw), \
        f"attachments={attachments_of(pdf)}; no /EmbeddedFile in file={'/EmbeddedFile' not in raw}"


# --- T2-4: split / rotate / flatten / N-up -------------------------------------------

TRANSFORM_API = os.environ.get("PDFENGINE_BASE", "http://localhost:5276") + "/api/v1/pdf/transform"


def transform(body):
    payload = json.dumps(body).encode()
    req = urllib.request.Request(TRANSFORM_API, data=payload, method="POST",
                                 headers={"Content-Type": "application/json", "X-Api-Key": KEY})
    try:
        with urllib.request.urlopen(req, timeout=180) as r:
            return r.read(), None
    except urllib.error.HTTPError as e:
        return None, f"HTTP {e.code}: {e.read().decode()[:160]}"


def source_pdf(pages=6):
    body = "".join(f"<div style='page-break-after:always;font:28px sans-serif'>SRCPAGE{i}</div>"
                   for i in range(1, pages + 1))
    pdf, _diag, err = render("t24-src", f"<html><body>{body}</body></html>", {"pageSize": "A4"})
    if err:
        raise RuntimeError(err)
    return base64.b64encode(pdf).decode()


def page_texts(pdf_bytes):
    import fitz
    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    return [" ".join(p.get_text().split()) for p in doc], doc


@case("T2-4a extract takes a page selection, in the order it was written")
def _():
    # Order is preserved on purpose: "3,1" is a reordering request, and sorting it would
    # quietly refuse to do what was asked.
    src = source_pdf()
    picked, err = transform({"documentName": "x", "file": src, "operation": "extract", "pages": "1-2,5"})
    if err:
        return False, err
    reordered, err2 = transform({"documentName": "x", "file": src, "operation": "extract", "pages": "3,1"})
    if err2:
        return False, err2
    a, _ = page_texts(picked)
    b, _ = page_texts(reordered)
    return (a == ["SRCPAGE1", "SRCPAGE2", "SRCPAGE5"] and b == ["SRCPAGE3", "SRCPAGE1"]), \
        f"selection={a}; reordering={b}"


@case("T2-4b rotate turns the page and is additive, not absolute")
def _():
    # Asserted on the page's /Rotate value, not on its width and height. The rect is a
    # derived quantity and is NOT a reliable proxy: measured, a page at /Rotate 180 still
    # reported a landscape rect, so a fixture reading dimensions would have failed a
    # rotation that was entirely correct.
    src = source_pdf(2)
    once, err = transform({"documentName": "x", "file": src, "operation": "rotate", "rotation": 90})
    if err:
        return False, err
    _t, doc = page_texts(once)
    first = doc[0].rotation

    # Rotating the result again must reach 180 — a page that already carried a rotation
    # must end up turned BY the amount asked for, not reset TO it.
    twice, err2 = transform({"documentName": "x", "file": base64.b64encode(once).decode(),
                             "operation": "rotate", "rotation": 90})
    if err2:
        return False, err2
    _t2, doc2 = page_texts(twice)
    second = doc2[0].rotation

    return (first == 90 and second == 180), \
        f"one 90deg turn -> /Rotate {first}; a second -> /Rotate {second} (want 90 then 180)"


@case("T2-4c N-up places the requested number of pages per sheet, text intact")
def _():
    src = source_pdf()
    two, err = transform({"documentName": "x", "file": src, "operation": "nup", "pagesPerSheet": 2})
    if err:
        return False, err
    four, err2 = transform({"documentName": "x", "file": src, "operation": "nup", "pagesPerSheet": 4})
    if err2:
        return False, err2
    a, doc_a = page_texts(two)
    b, _ = page_texts(four)
    # Six source pages: 3 sheets at 2-up, 2 sheets at 4-up. Text must survive — N-up that
    # rasterised the pages would still "look right" and lose every text layer.
    return (len(a) == 3 and a[0] == "SRCPAGE1 SRCPAGE2"
            and len(b) == 2 and b[0] == "SRCPAGE1 SRCPAGE2 SRCPAGE3 SRCPAGE4"
            and doc_a[0].rect.width > doc_a[0].rect.height), \
        f"2-up={a}; 4-up sheets={len(b)}, first={b[0] if b else '-'}"


@case("T2-4d flatten removes interactivity WITHOUT rasterising the page")
def _():
    # "Flatten" means different things in different tools. Here it removes the interactive
    # layer and keeps text as text — a flattened document must stay searchable.
    src = source_pdf(3)
    out, err = transform({"documentName": "x", "file": src, "operation": "flatten"})
    if err:
        return False, err
    texts, doc = page_texts(out)
    raw = out.decode("latin-1")
    return (texts == ["SRCPAGE1", "SRCPAGE2", "SRCPAGE3"]
            and "/Annots" not in raw and "/AcroForm" not in raw), \
        f"text preserved={texts}; /Annots removed={'/Annots' not in raw}; /AcroForm removed={'/AcroForm' not in raw}"


@case("T2-4e malformed transform requests are refused, never 500")
def _():
    src = source_pdf(2)
    checks = {
        "unknown operation": {"documentName": "x", "file": src, "operation": "squish"},
        "rotation 45": {"documentName": "x", "file": src, "operation": "rotate", "rotation": 45},
        "extract with no pages": {"documentName": "x", "file": src, "operation": "extract"},
        "not base64": {"documentName": "x", "file": "!!!", "operation": "flatten"},
        "not a pdf": {"documentName": "x", "file": base64.b64encode(b"hello").decode(), "operation": "flatten"},
    }
    wrong = []
    for label, body in checks.items():
        _out, err = transform(body)
        if err is None or "400" not in err:
            wrong.append(f"{label} -> {err or 'ACCEPTED'}")
    return not wrong, f"all refused with 400={not wrong}; {wrong}"


# --- T2-2: digital signatures --------------------------------------------------------

_SIGNING_PFX = None


def signing_pfx():
    """A throwaway self-signed signing certificate, generated by the gate.

    Generated rather than committed: a private key in the repository is a liability, and a
    certificate with a fixed expiry turns into a gate that fails on a date nobody chose.
    """
    global _SIGNING_PFX
    if _SIGNING_PFX is not None:
        return _SIGNING_PFX
    d = pathlib.Path(tempfile.mkdtemp(prefix="pdfengine-signing-"))
    subprocess.run(["openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes",
                    "-keyout", str(d / "k.pem"), "-out", str(d / "c.pem"),
                    "-days", "365", "-subj", "/CN=PDFEngine Gate Signer/O=PDFEngine"],
                   capture_output=True, check=True)
    subprocess.run(["openssl", "pkcs12", "-export", "-out", str(d / "s.pfx"),
                    "-inkey", str(d / "k.pem"), "-in", str(d / "c.pem"),
                    "-passout", "pass:gatepw"], capture_output=True, check=True)
    _SIGNING_PFX = base64.b64encode((d / "s.pfx").read_bytes()).decode()
    return _SIGNING_PFX


def signature_verifies(pdf_bytes):
    """Verify the detached CMS against exactly the bytes the file's /ByteRange declares.

    Checked with openssl rather than by reading back our own writer — the whole point of a
    signature is that an independent tool accepts it.
    """
    br = re.search(rb"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]", pdf_bytes)
    contents = re.search(rb"/Contents\s*<([0-9A-Fa-f]+)>", pdf_bytes)
    if not br or not contents:
        return False, "no /ByteRange or /Contents in the document"
    a, b, c, d = (int(x) for x in br.groups())
    with tempfile.TemporaryDirectory() as td:
        content = pathlib.Path(td) / "content.bin"
        der = pathlib.Path(td) / "sig.der"
        content.write_bytes(pdf_bytes[a:a + b] + pdf_bytes[c:c + d])
        der.write_bytes(bytes.fromhex(contents.group(1).decode()).rstrip(b"\x00"))
        r = subprocess.run(["openssl", "cms", "-verify", "-inform", "DER", "-in", str(der),
                            "-content", str(content), "-binary", "-noverify", "-out", os.devnull],
                           capture_output=True, text=True)
    return r.returncode == 0, (r.stderr or "").strip().splitlines()[:1]


CONTRACT_HTML = ('<html><body style="font-family:sans-serif"><h1>Signed contract</h1>'
                 '<p>Agreed terms.</p></body></html>')


@case("T2-2a a signed document carries a signature openssl accepts")
def _():
    # PDFsharp 6.2.1 builds the signature STRUCTURE correctly but its own computation signs
    # the wrong bytes — measured, it hands the signer 2,133 bytes beginning with whitespace
    # while writing a /ByteRange declaring 2,922 beginning at the PDF header, and openssl
    # rejects the result. The engine therefore computes the CMS itself over exactly what the
    # finished file declares. This case is what proves that.
    pdf, _diag, err = render("t22-sign", CONTRACT_HTML,
                             {"signingCertificateBase64": signing_pfx(),
                              "signingCertificatePassword": "gatepw",
                              "signatureReason": "Approved for payment",
                              "signatureLocation": "Berlin"})
    if err:
        return False, err
    ok, detail = signature_verifies(pdf)
    subfilter = re.findall(rb"/SubFilter\s*/([A-Za-z0-9.]+)", pdf)[:1]
    return (ok and subfilter == [b"adbe.pkcs7.detached"]), \
        f"openssl verified={ok} {detail}; SubFilter={subfilter}"


@case("T2-2b the signature covers the whole document — tampering breaks it")
def _():
    # A signature that still verifies after the file is edited is worthless. One byte of
    # visible text is flipped inside the signed range and the signature must fail.
    pdf, _diag, err = render("t22-tamper", CONTRACT_HTML,
                             {"signingCertificateBase64": signing_pfx(),
                              "signingCertificatePassword": "gatepw"})
    if err:
        return False, err
    intact, _ = signature_verifies(pdf)

    marker = pdf.find(b"/Type")          # a byte well inside the first signed range
    tampered = bytearray(pdf)
    tampered[marker + 1] = ord("X") if tampered[marker + 1] != ord("X") else ord("Y")
    broken, _ = signature_verifies(bytes(tampered))
    return (intact and not broken), f"intact document verifies={intact}; tampered document verifies={broken} (must be False)"


@case("T2-2c signing metadata reaches the signature dictionary")
def _():
    # Reason and location only. Contact info is deliberately NOT offered as an option:
    # PDFsharp accepts one and never writes it, the dictionary is created during save so it
    # cannot be set beforehand, and adding the key afterwards would shift the bytes the
    # signature seals. An option that reliably does nothing is worse than no option.
    pdf, _diag, err = render("t22-meta", CONTRACT_HTML,
                             {"signingCertificateBase64": signing_pfx(),
                              "signingCertificatePassword": "gatepw",
                              "signatureReason": "REASONMARKER",
                              "signatureLocation": "LOCATIONMARKER"})
    if err:
        return False, err
    present = [m for m in (b"REASONMARKER", b"LOCATIONMARKER") if m in pdf]
    ok, _ = signature_verifies(pdf)
    return (len(present) == 2 and ok), \
        f"in the signature dictionary: {[p.decode() for p in present]}; still verifies={ok}"


TSA_URL = os.environ.get("PDFENGINE_TSA", "http://timestamp.digicert.com")


def tsa_reachable():
    try:
        req = urllib.request.Request(TSA_URL, data=b"", method="POST")
        urllib.request.urlopen(req, timeout=15)
        return True
    except urllib.error.HTTPError:
        return True          # a 4xx means it is alive and rejecting an empty query
    except Exception:
        return False


@case("T2-2g a timestamped signature is PAdES B-T, not just a basic signature")
def _():
    # Without a trusted timestamp the only evidence of WHEN a document was signed is the
    # signer's own clock, and the signature stops being verifiable the day the certificate
    # expires. This is the difference between a signature that satisfies an auditor and one
    # that merely exists.
    if not tsa_reachable():
        return None, f"timestamp authority {TSA_URL} unreachable — SKIPPED (not a pass)"

    pdf, _diag, err = render("t22-tsa", CONTRACT_HTML,
                             {"signingCertificateBase64": signing_pfx(),
                              "signingCertificatePassword": "gatepw",
                              "timestampUrl": TSA_URL})
    if err:
        return False, err

    verified, _ = signature_verifies(pdf)
    with tempfile.TemporaryDirectory() as td:
        der = pathlib.Path(td) / "sig.der"
        contents = re.search(rb"/Contents\s*<([0-9A-Fa-f]+)>", pdf).group(1).decode()
        der.write_bytes(bytes.fromhex(contents).rstrip(b"\x00"))
        printed = subprocess.run(["openssl", "cms", "-cmsout", "-inform", "DER",
                                  "-in", str(der), "-print"], capture_output=True, text=True).stdout
    timestamped = "id-smime-aa-timeStampToken" in printed
    return (verified and timestamped), \
        f"signature verifies={verified}; RFC 3161 timestamp token embedded={timestamped}"


@case("T2-2h an unreachable timestamp authority FAILS rather than dropping the timestamp")
def _():
    # A caller who asked for a timestamp needs the signature to outlive the certificate.
    # Handing back an untimestamped signature is the failure they would never notice.
    _pdf, _diag, err = render("t22-tsa-bad", CONTRACT_HTML,
                              {"signingCertificateBase64": signing_pfx(),
                               "signingCertificatePassword": "gatepw",
                               "timestampUrl": "http://127.0.0.1:9/nope"})
    return (err is not None and "400" in err), f"refused={err is not None}: {(err or '')[:100]}"


@case("T2-2d signing and encryption are refused together, not silently broken")
def _():
    # Encrypting after signing rewrites the bytes the signature seals. A document that says
    # it is signed and does not verify is worse than one that was never signed.
    _pdf, _diag, err = render("t22-both", CONTRACT_HTML,
                              {"signingCertificateBase64": signing_pfx(),
                               "signingCertificatePassword": "gatepw",
                               "userPassword": "secret"})
    return (err is not None and "400" in err), f"refused={err is not None}: {(err or '')[:100]}"


@case("T2-2e a bad certificate fails clearly, never with a 500")
def _():
    checks = {
        "wrong password": {"signingCertificateBase64": signing_pfx(), "signingCertificatePassword": "wrong"},
        "not base64": {"signingCertificateBase64": "!!!", "signingCertificatePassword": ""},
        "not a certificate": {"signingCertificateBase64": base64.b64encode(b"hello").decode(),
                              "signingCertificatePassword": ""},
    }
    wrong = []
    for label, opts in checks.items():
        _pdf, _diag, err = render(f"t22-bad-{label}", CONTRACT_HTML, opts)
        if err is None or "400" not in err:
            wrong.append(f"{label} -> {err or 'ACCEPTED'}")
    return not wrong, f"all refused with 400={not wrong}; {wrong}"


@case("T2-2f an unsigned document carries no signature dictionary")
def _():
    pdf, _diag, err = render("t22-none", CONTRACT_HTML, {})
    if err:
        return False, err
    return (b"/ByteRange" not in pdf and b"adbe.pkcs7" not in pdf), \
        "no /ByteRange and no /SubFilter in an unsigned document"


# --- T2-5: linearization (fast web view) ---------------------------------------------

def is_linearized(pdf_bytes, password=None):
    """Ask qpdf itself — the reference implementation — rather than trusting our own writer."""
    with tempfile.TemporaryDirectory() as td:
        p = pathlib.Path(td) / "d.pdf"
        p.write_bytes(pdf_bytes)
        cmd = ["qpdf", "--check"] + ([f"--password={password}"] if password else []) + [str(p)]
        out = subprocess.run(cmd, capture_output=True, text=True).stdout
    return "File is linearized" in out


LONG_HTML = ("<html><body>" + "".join(
    f"<div style='page-break-after:always'>Page {i} content</div>" for i in range(1, 30)) + "</body></html>")


@case("T2-5a linearize produces a genuinely fast-web-view document")
def _():
    off, _diag, err = render("t25-off", LONG_HTML, {"pageSize": "A4"})
    if err:
        return False, err
    on, _diag2, err2 = render("t25-on", LONG_HTML, {"pageSize": "A4", "linearize": True})
    if err2:
        return False, err2
    # Both directions asserted: a document that is linearized either way would prove nothing.
    return (not is_linearized(off) and is_linearized(on)), \
        f"default linearized={is_linearized(off)} (want False); requested linearized={is_linearized(on)} (want True)"


@case("T2-5b an encrypted document can still be linearized, and stays encrypted")
def _():
    pdf, _diag, err = render("t25-enc", LONG_HTML,
                             {"pageSize": "A4", "linearize": True,
                              "userPassword": "secret", "ownerPassword": "owner"})
    if err:
        return False, err
    with tempfile.TemporaryDirectory() as td:
        p = pathlib.Path(td) / "d.pdf"
        p.write_bytes(pdf)
        enc = subprocess.run(["qpdf", "--show-encryption", "--password=secret", str(p)],
                             capture_output=True, text=True).stdout
    # R = 6 is AES-256: linearizing must not quietly drop the encryption.
    return (is_linearized(pdf, "secret") and "R = 6" in enc), \
        f"linearized={is_linearized(pdf, 'secret')}; encryption still AES-256={'R = 6' in enc}"


@case("T2-5c linearize plus signing is refused rather than silently dropped")
def _():
    # Applying the signature rewrites the document and undoes the fast-web-view layout.
    # Measured: the result came back signed and NOT linearized, with nothing to say so.
    _pdf, _diag, err = render("t25-sign", LONG_HTML,
                              {"pageSize": "A4", "linearize": True,
                               "signingCertificateBase64": signing_pfx(),
                               "signingCertificatePassword": "gatepw"})
    return (err is not None and "400" in err), f"refused={err is not None}: {(err or '')[:100]}"


# --- T2-6: print production (bleed, crop marks, page boxes) --------------------------

FLYER_HTML = ('<html><body style="margin:0"><div style="background:#3355bb;position:absolute;'
              'inset:0"></div><h1 style="position:relative;color:#fff;padding:40px">Flyer</h1>'
              '</body></html>')


def page_boxes_mm(pdf_bytes):
    import fitz
    page = fitz.open(stream=pdf_bytes, filetype="pdf")[0]
    def mm(v):
        return round(v * 25.4 / 72, 1)
    return {"media": (mm(page.mediabox.width), mm(page.mediabox.height)),
            "trim": (mm(page.trimbox.width), mm(page.trimbox.height)),
            "bleed": (mm(page.bleedbox.width), mm(page.bleedbox.height))}


@case("T2-6a bleed enlarges the sheet and records the finished size as the TrimBox")
def _():
    # The TrimBox is the part that matters: it is how a printer knows where to cut. Bleed
    # without it is just a bigger page.
    pdf, _diag, err = render("t26-bleed", FLYER_HTML,
                             {"pageSize": "A4", "printBackground": True, "bleedMm": 3})
    if err:
        return False, err
    b = page_boxes_mm(pdf)
    # A4 trim, +3mm of bleed on every side.
    trim_is_a4 = abs(b["trim"][0] - 210) < 1.5 and abs(b["trim"][1] - 297) < 1.5
    bleed_is_bigger = abs(b["bleed"][0] - 216) < 1.5 and abs(b["bleed"][1] - 303) < 1.5
    return (trim_is_a4 and bleed_is_bigger), f"{b} (want trim ~210x297, bleed ~216x303)"


@case("T2-6b artwork actually extends into the bleed, rather than being framed in white")
def _():
    # The whole point of bleed. A page merely centred on a larger sheet would satisfy the
    # boxes and leave a white sliver when trimmed — the exact defect bleed exists to avoid.
    import fitz
    pdf, _diag, err = render("t26-ink", FLYER_HTML,
                             {"pageSize": "A4", "printBackground": True, "bleedMm": 3})
    if err:
        return False, err
    page = fitz.open(stream=pdf, filetype="pdf")[0]
    pix = page.get_pixmap(dpi=72)
    # Sample just inside the sheet edge — inside the bleed, outside the trim.
    x, y = 3, pix.height // 2
    r, g, b = pix.pixel(x, y)[:3]
    inked = not (r > 240 and g > 240 and b > 240)
    return inked, f"pixel inside the bleed = rgb({r},{g},{b}); inked={inked}"


@case("T2-6c crop marks add sheet OUTSIDE the bleed and are drawn there")
def _():
    pdf, _diag, err = render("t26-marks", FLYER_HTML,
                             {"pageSize": "A4", "printBackground": True,
                              "bleedMm": 3, "cropMarks": True})
    if err:
        return False, err
    b = page_boxes_mm(pdf)
    # Marks need their own margin: media must exceed bleed, and bleed must exceed trim.
    return (b["media"][0] > b["bleed"][0] + 10 and b["bleed"][0] > b["trim"][0]), \
        f"{b} (media must exceed bleed, bleed must exceed trim)"


@case("T2-6d a document without bleed keeps a single page box")
def _():
    pdf, _diag, err = render("t26-none", FLYER_HTML, {"pageSize": "A4", "printBackground": True})
    if err:
        return False, err
    b = page_boxes_mm(pdf)
    return b["media"] == b["trim"] == b["bleed"], f"{b} (all three boxes must be the same)"


@case("T2-6e the engine does NOT claim CMYK or PDF/X")
def _():
    # Chromium emits RGB and the engine performs no colour conversion. Saying so on every
    # print job is the difference between an honest limitation and a printer's surprise.
    _pdf, diag, err = render("t26-claims", FLYER_HTML,
                             {"pageSize": "A4", "printBackground": True, "bleedMm": 3})
    if err:
        return False, err
    notices = [w for w in diag.get("warnings", []) if "Print notice" in w]
    said = notices[0] if notices else ""
    return ("RGB" in said and "CMYK" in said and "PDF/X" in said), \
        f"colour limitation stated at render time: {bool(notices)}"


# --- T2-3: interactive form fields ---------------------------------------------------

FORM_HTML = ('<html><body style="font-family:sans-serif"><h1>Application form</h1>'
             '<p>Full name:</p><p style="margin-top:34px">Email:</p>'
             '<p style="margin-top:34px">I agree to the terms</p></body></html>')

FORM_FIELDS = [
    {"name": "full_name", "type": "text", "page": 1, "x": 120, "y": 95,
     "width": 220, "height": 18, "toolTip": "Your legal name", "required": True},
    {"name": "email", "type": "text", "page": 1, "x": 120, "y": 129,
     "width": 220, "height": 18, "value": "a@b.invalid"},
    {"name": "agree", "type": "checkbox", "page": 1, "x": 120, "y": 163,
     "width": 14, "height": 14, "value": "true"},
]


def widgets_of(pdf_bytes):
    """Read the fields back with an independent reader, not our own writer."""
    import fitz
    doc = fitz.open(stream=pdf_bytes, filetype="pdf")
    return doc.is_form_pdf, [(w.field_name, w.field_type_string, w.field_value,
                              [round(v) for v in w.rect]) for w in doc[0].widgets()]


@case("T2-3a fillable text and checkbox fields are real fields a reader can see")
def _():
    # The backlog had this BLOCKED on the library — true that PdfTextField has no public
    # constructor in either PdfSharpCore or PDFsharp, wrong that this made it impossible. A
    # field is a widget annotation plus an AcroForm entry, both ordinary dictionaries.
    pdf, _diag, err = render("t23-fields", FORM_HTML, {"pageSize": "A4", "formFields": FORM_FIELDS})
    if err:
        return False, err
    is_form, widgets = widgets_of(pdf)
    names = [w[0] for w in widgets]
    types = sorted({w[1] for w in widgets})
    return (is_form and names == ["full_name", "email", "agree"]
            and types == ["CheckBox", "Text"]), \
        f"is_form={bool(is_form)}; fields={names}; types={types}"


@case("T2-3b field values and coordinates survive the top-left to PDF conversion")
def _():
    # PDF's origin is bottom-left and the API's is top-left. Getting that wrong puts every
    # field on the page, mirrored vertically — which still looks like a working form.
    pdf, _diag, err = render("t23-coords", FORM_HTML, {"pageSize": "A4", "formFields": FORM_FIELDS})
    if err:
        return False, err
    _is_form, widgets = widgets_of(pdf)
    by_name = {w[0]: w for w in widgets}
    email_value = by_name.get("email", (None, None, None, None))[2]
    checked = by_name.get("agree", (None, None, None, None))[2]
    name_rect = by_name.get("full_name", (None, None, None, [0, 0, 0, 0]))[3]
    email_rect = by_name.get("email", (None, None, None, [0, 0, 0, 0]))[3]
    # full_name was declared above email, so it must sit higher on the page.
    ordered = name_rect[1] < email_rect[1]
    return (email_value == "a@b.invalid" and checked in ("Yes", "On") and ordered), \
        f"email value={email_value!r}; checkbox={checked!r}; declared order preserved top-to-bottom={ordered}"


@case("T2-3c form fields and PDF/A are refused together")
def _():
    # Fields are drawn by the reader via NeedAppearances; PDF/A requires baked appearances
    # and embedded fonts. Producing the file anyway hands the caller something their
    # archival validator rejects.
    _pdf, _diag, err = render("t23-pdfa", FORM_HTML,
                              {"pageSize": "A4", "formFields": FORM_FIELDS,
                               "pdfaCompliance": "PDF/A-3b"})
    return (err is not None and "400" in err), f"refused={err is not None}: {(err or '')[:95]}"


@case("T2-3d malformed field declarations are refused, never 500")
def _():
    checks = {
        "unnamed": [{"name": "", "type": "text"}],
        "unknown type": [{"name": "x", "type": "slider"}],
        "page zero": [{"name": "x", "type": "text", "page": 0}],
    }
    wrong = []
    for label, fields in checks.items():
        _pdf, _diag, err = render(f"t23-bad-{label}", FORM_HTML,
                                  {"pageSize": "A4", "formFields": fields})
        if err is None or "400" not in err:
            wrong.append(f"{label} -> {err or 'ACCEPTED'}")
    return not wrong, f"all refused with 400={not wrong}; {wrong}"


@case("T2-3e a document with no fields is not a form")
def _():
    pdf, _diag, err = render("t23-none", FORM_HTML, {"pageSize": "A4"})
    if err:
        return False, err
    is_form, widgets = widgets_of(pdf)
    return (not is_form and not widgets), f"is_form={bool(is_form)}; widgets={len(widgets)}"


def main():
    global VERAPDF
    ap = argparse.ArgumentParser()
    ap.add_argument("--verapdf", default=os.environ.get("VERAPDF_BIN"))
    ap.add_argument("--update-baseline", action="store_true")
    args = ap.parse_args()

    if args.verapdf and (pathlib.Path(args.verapdf).exists()
                         or subprocess.run(["which", args.verapdf], capture_output=True).returncode == 0):
        VERAPDF = args.verapdf

    if subprocess.run(["which", "qpdf"], capture_output=True).returncode != 0:
        print("FATAL: qpdf not found — T2-5 cannot be checked. brew install qpdf")
        return 2

    if subprocess.run(["which", "pdfdetach"], capture_output=True).returncode != 0:
        print("FATAL: poppler `pdfdetach` not found. brew install poppler")
        return 2

    results, failed, skipped = {}, 0, 0
    print(f"{'case':70} {'verdict':8} detail")
    print("-" * 130)
    for name, fn in CASES:
        try:
            ok, detail = fn()
        except urllib.error.URLError as e:
            ok, detail = False, f"render failed: {e}"
        except Exception as e:                                    # noqa: BLE001
            ok, detail = False, f"exception: {e}"
        verdict = "SKIP" if ok is None else ("PASS" if ok else "FAIL")
        if verdict == "FAIL":
            failed += 1
        if verdict == "SKIP":
            skipped += 1
        results[name] = {"verdict": verdict, "detail": detail}
        print(f"{name:70} {verdict:8} {detail}")

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    (EVIDENCE / "output-gate.json").write_text(json.dumps(results, indent=2), encoding="utf-8")
    print("-" * 130)
    print(f"summary: {len(CASES) - failed - skipped}/{len(CASES)} passed"
          + (f", {skipped} skipped" if skipped else ""))
    if not VERAPDF:
        print("veraPDF not supplied — PDF/A conformance was NOT checked. Pass --verapdf.")
    print("NOT covered: CMYK conversion and PDF/X conformance are deliberately NOT\n"
          "implemented — see T2-6 in the backlog for both reasons.")

    if args.update_baseline:
        BASELINE.parent.mkdir(parents=True, exist_ok=True)
        BASELINE.write_text(json.dumps({k: v["verdict"] for k, v in results.items()}, indent=2),
                            encoding="utf-8")
        print(f"baseline written -> {BASELINE}")
        return 0

    if BASELINE.exists():
        base = json.loads(BASELINE.read_text(encoding="utf-8"))
        rank = {"PASS": 2, "SKIP": 1, "FAIL": 0}
        regressions = [(k, base[k], results[k]["verdict"]) for k in results
                       if k in base and rank[results[k]["verdict"]] < rank.get(base[k], 0)]
        if regressions:
            print("\nREGRESSIONS (gate FAILED):")
            for k, was, now in regressions:
                print(f"  {k}: {was} -> {now}")
            return 1

    return 1 if failed else 0


if __name__ == "__main__":
    sys.exit(main())
