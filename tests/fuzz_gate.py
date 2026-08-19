#!/usr/bin/env python3
"""
PDFEngine — Fuzzing Gate (T3-3)

Gate I checks a fixed adversarial list: the attacks someone thought of. Every entry on it
passes, which is exactly why it stopped being informative — a list only ever finds the bug
you already imagined. This gate generates inputs nobody wrote down.

The DoS this class of bug produces is not hypothetical here: ~6,000 nested elements
overflowed AngleSharp's parser stack and TERMINATED THE API PROCESS. It was found by a
person looking, not by a test, and its siblings are what this gate is for.

Invariants asserted after every single input. A fuzzer without invariants is a load
generator:

  ALIVE      the process must still be serving. A 4xx is a good answer, a 5xx is a bug,
             and a dead process is a P0 — one unauthenticated request taking the service
             down for every tenant.
  BOUNDED    a response must arrive within the budget. A hang is a DoS with better manners.
  NO 5xx     malformed input is the caller's fault and must be reported as theirs. A 500
             means an unhandled exception path, which is where memory corruption and
             information disclosure live.
  NO SSRF    no generated URL may cause a fetch of an internal address, however it is
             spelled — decimal IP, IPv6-mapped, redirect chain, credential-prefixed host.

Everything is seeded, so a failure reproduces exactly: the seed is printed on every run and
failing inputs are written to tests/corpus/fuzz-findings/.

Usage:
    python3 tests/fuzz_gate.py                          # 400 cases, all families
    python3 tests/fuzz_gate.py --cases 2000 --seed 7
    python3 tests/fuzz_gate.py --family ssrf
"""
import argparse, base64, gzip, hashlib, json, pathlib, random, string
import sys, threading, time, zlib
import http.server, socketserver
import urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
FINDINGS = ROOT / "tests" / "corpus" / "fuzz-findings"
EVIDENCE = ROOT / "tests" / "evidence"
BASE = "http://localhost:8080"
KEY = "test-api-key-123"
BUDGET_S = 75

rng = random.Random()
findings = []
stats = {"sent": 0, "ok": 0, "rejected": 0, "server_error": 0, "timeout": 0, "dead": 0}


# ----------------------------------------------------------------- generators
TAGS = ["div", "span", "p", "table", "tr", "td", "ul", "li", "section", "article",
        "b", "i", "svg", "g", "text", "form", "label", "h1", "h2", "blockquote"]
CSS_PROPS = ["width", "height", "margin", "padding", "font-size", "line-height",
             "transform", "filter", "grid-template-columns", "clip-path", "content",
             "background", "column-count", "aspect-ratio", "translate", "rotate"]
CSS_VALUES = ["calc(100% - 1px)", "1e309px", "-99999em", "NaN", "inherit", "initial",
              "var(--undefined)", "attr(data-x)", "0/0", "1fr 1fr", "repeat(9999, 1px)",
              "url(data:,)", "translate(1e10px)", "blur(1e6px)", "counter(x)"]
# Zero-width space, BOM, right-to-left override, an astral-plane emoji, and markup
# fragments that end a construct the parser is in the middle of.
WEIRD = ["​", "﻿", "‮", "\U0001F600", "\\", "</", "<!--", "-->",
         "]]>", "&#x0;", "&nbsp;" * 3, "\r\n", "\t", "'", '"', "\x00"]


def rand_text(n=12):
    return "".join(rng.choice(string.ascii_letters + string.digits + " ") for _ in range(n))


def gen_html_structure():
    """Nesting, unbalanced tags, attribute abuse — the parser's structural edges."""
    style = rng.choice([
        "", "<style>* { %s: %s }</style>" % (rng.choice(CSS_PROPS), rng.choice(CSS_VALUES)),
        "<style>@media print { @page { size: %s } }</style>" % rng.choice(
            ["A4", "0 0", "-1mm 5mm", "99999in 1px", "landscape portrait"]),
        "<style>%s</style>" % ("div{" * rng.randint(1, 40)),
        "<style>@import url('data:text/css,@import url(data:text/css,body{})');</style>",
    ])
    # 512 is the declared nesting cap, so the interesting values sit either side of it.
    depth = rng.choice([3, 40, 400, 480, 520, 900, 5000])
    tag = rng.choice(TAGS)
    close = rng.random() < 0.6
    body = ("<%s>" % tag) * depth + rand_text() + (("</%s>" % tag) * depth if close else "")
    if rng.random() < 0.3:
        attr = rng.choice(["style", "class", "id", "data-x", "onload", "src", "href"])
        body = f"<div {attr}='{rand_text(rng.choice([10, 5000, 100000]))}'>{body}</div>"
    if rng.random() < 0.25:
        body += rng.choice(WEIRD) * rng.randint(1, 50)
    return f"<html><head>{style}</head><body>{body}</body></html>"


def gen_entity_bomb():
    """Billion laughs and its relatives. Expansion happens before any size limit sees it."""
    kind = rng.choice(["xml-entity", "html-entity", "svg-use", "css-import"])
    if kind == "xml-entity":
        levels = rng.randint(3, 9)
        ents = "".join(
            f'<!ENTITY e{i} "{"&e%d;" % (i - 1) * 10}">' if i else '<!ENTITY e0 "lol">'
            for i in range(levels))
        return (f'<?xml version="1.0"?><!DOCTYPE t [{ents}]>'
                f'<html><body><p>&e{levels - 1};</p></body></html>')
    if kind == "html-entity":
        return "<html><body>" + "&amp;" * rng.choice([100, 100000]) + "</body></html>"
    if kind == "svg-use":
        # Recursive <use> — a classic renderer hang.
        return ('<html><body><svg><defs><g id="a"><use href="#a"/></g></defs>'
                '<use href="#a"/></svg></body></html>')
    return ("<html><head><style>" + "@import url('#');" * rng.choice([10, 10000]) +
            "</style></head><body>x</body></html>")


def gen_decompression_bomb():
    """Payloads that are small on the wire and enormous once expanded."""
    kind = rng.choice(["png-zeros", "gzip-datauri", "svg-repeat", "base64-garbage"])
    if kind == "png-zeros":
        # Highly compressible payload: tiny encoded, huge once inflated.
        raw = b"\x00" * rng.choice([1 << 16, 1 << 22])
        blob = base64.b64encode(zlib.compress(raw, 9)).decode()
        return (f'<html><body><img src="data:image/png;base64,{blob}" '
                f'width="100" height="100"></body></html>')
    if kind == "gzip-datauri":
        blob = base64.b64encode(gzip.compress(b"A" * (1 << 22))).decode()
        return f'<html><body><img src="data:image/gif;base64,{blob}"></body></html>'
    if kind == "svg-repeat":
        n = rng.choice([1000, 200000])
        return ("<html><body><svg viewBox='0 0 10 10'>" +
                "<rect width='1' height='1'/>" * min(n, 200000) + "</svg></body></html>")
    return (f'<html><body><img src="data:image/png;base64,'
            f'{"A" * rng.choice([100, 1000000])}"></body></html>')


SSRF_HOSTS = [
    "127.0.0.1", "localhost", "0.0.0.0", "[::1]", "[::ffff:127.0.0.1]",
    "[0:0:0:0:0:ffff:127.0.0.1]",
    "2130706433", "0x7f000001", "0177.0.0.1", "127.1", "127.0.0.1.nip.io",
    "169.254.169.254", "metadata.google.internal", "100.64.0.1", "10.0.0.1",
    "192.168.1.1", "172.16.0.1", "[fd00::1]", "[2002:7f00:0001::]",
    "user:pass@127.0.0.1", "127.0.0.1:8080", "LOCALHOST",
    # Ideographic full stops, which some URL parsers normalise back to dots.
    "127。0。0。1",
]
SSRF_SCHEMES = ["http", "https", "file", "gopher", "ftp", "jar", "blob", "view-source"]


def gen_ssrf():
    host = rng.choice(SSRF_HOSTS)
    scheme = rng.choice(SSRF_SCHEMES)
    path = rng.choice(["/", "/latest/meta-data/", "/etc/passwd", "/health", "//evil"])
    url = f"{scheme}://{host}{path}"
    how = rng.choice(["img", "css", "font", "iframe", "svg-image", "fetch", "object"])
    body = {
        "img": f'<img src="{url}">',
        "css": f'<link rel="stylesheet" href="{url}">',
        "font": f'<style>@font-face{{font-family:x;src:url("{url}")}}body{{font-family:x}}</style>',
        "iframe": f'<iframe src="{url}"></iframe>',
        "svg-image": f'<svg><image href="{url}" width="10" height="10"/></svg>',
        "fetch": f'<script>fetch("{url}")</script>',
        "object": f'<object data="{url}"></object>',
    }[how]
    return f"<html><body>{body}</body></html>", url


def gen_pdf_bytes():
    """Malformed PDFs for the endpoints that PARSE a PDF rather than produce one."""
    kind = rng.choice(["truncated", "bad-xref", "loop", "huge-count", "not-pdf", "empty"])
    good = (b"%PDF-1.7\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj\n"
            b"2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj\n"
            b"3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 612 792]>>endobj\n"
            b"trailer<</Root 1 0 R>>\n%%EOF\n")
    if kind == "truncated":
        return good[:rng.randint(10, len(good) - 1)]
    if kind == "bad-xref":
        return good.replace(b"trailer", b"xref\n0 99999999\ntrailer")
    if kind == "loop":
        return good.replace(b"/Parent 2 0 R", b"/Parent 3 0 R")
    if kind == "huge-count":
        return good.replace(b"/Count 1", b"/Count 2147483647")
    if kind == "not-pdf":
        return bytes(rng.getrandbits(8) for _ in range(rng.randint(1, 2000)))
    return b""


# ------------------------------------------------------------------- harness
def alive():
    try:
        with urllib.request.urlopen(f"{BASE}/health", timeout=10) as r:
            return r.status == 200
    except Exception:
        return False


def send(endpoint, payload):
    req = urllib.request.Request(f"{BASE}/api/v1/pdf/{endpoint}",
                                 data=json.dumps(payload).encode(), method="POST",
                                 headers={"Content-Type": "application/json", "X-Api-Key": KEY})
    started = time.time()
    try:
        with urllib.request.urlopen(req, timeout=BUDGET_S) as r:
            return r.status, r.read(), time.time() - started
    except urllib.error.HTTPError as e:
        return e.code, e.read(), time.time() - started
    except Exception as e:
        return None, str(e).encode(), time.time() - started


def record(family, why, payload_desc, detail):
    FINDINGS.mkdir(parents=True, exist_ok=True)
    digest = hashlib.sha256(str(payload_desc).encode()).hexdigest()[:12]
    path = FINDINGS / f"{family}-{why}-{digest}.json"
    path.write_text(json.dumps({"family": family, "why": why, "detail": detail,
                                "input": str(payload_desc)[:20000]}, indent=2) + "\n")
    findings.append({"family": family, "why": why, "detail": detail, "saved": path.name})
    print(f"\n  !! {why.upper()} [{family}] {detail}\n     saved: {path.name}", flush=True)


def check(family, endpoint, payload, budget=BUDGET_S):
    stats["sent"] += 1
    status, body, elapsed = send(endpoint, payload)
    desc = json.dumps(payload)[:20000]

    if status is None:
        if elapsed >= budget - 1:
            stats["timeout"] += 1
            record(family, "hang", desc, f"no response within {budget}s")
        else:
            # Connection dropped mid-request: check whether the process is gone.
            if not alive():
                stats["dead"] += 1
                record(family, "process-death", desc,
                       f"connection dropped and /health is down after {elapsed:.1f}s")
            else:
                stats["rejected"] += 1
        return status, body

    if status >= 500:
        stats["server_error"] += 1
        record(family, "server-error", desc,
               f"HTTP {status} in {elapsed:.1f}s: {body[:180].decode(errors='replace')}")
    elif status >= 400:
        stats["rejected"] += 1
    else:
        stats["ok"] += 1

    if not alive():
        stats["dead"] += 1
        record(family, "process-death", desc, f"process died after HTTP {status}")
    return status, body


# --------------------------------------------------------------------- families
def fuzz_html(_sink):
    check("html", "generate", {"documentName": "f", "documentType": 4,
                               "html": gen_html_structure()})


def fuzz_entity(_sink):
    check("entity-expansion", "generate", {"documentName": "f", "documentType": 4,
                                           "html": gen_entity_bomb()})


def fuzz_bomb(_sink):
    check("decompression-bomb", "generate", {"documentName": "f", "documentType": 4,
                                             "html": gen_decompression_bomb()})


def fuzz_ssrf(sink):
    html, url = gen_ssrf()
    before = sink.hits()
    check("ssrf", "generate", {"documentName": "f", "documentType": 4, "html": html})
    if sink.hits() > before:
        record("ssrf", "ssrf-fetch", html, f"engine fetched the internal sink via {url}")


def fuzz_options(_sink):
    """The options object is attacker-controlled too, and is where numbers become geometry."""
    opts = {}
    for key, values in {
        "pageSize": ["A4", "", "A" * 5000, "1x1", "-A4", None, "letter"],
        "bleedMm": [0, -1, 1e9, 0.0001, 99999],
        "scale": [1, 0, -3, 1e12],
        "pageRanges": ["1", "-", "9999999-1", "1-", ",,,", "1" * 2000],
        "marginTop": ["10mm", "-99999mm", "1e400px", "NaN", ""],
        "watermarkText": ["x", "‮", "A" * 100000],
        "rotation": [0, 90, 37, -1, 2 ** 31],
        "pagesPerSheet": [2, 0, -4, 999999],
    }.items():
        if rng.random() < 0.5:
            opts[key] = rng.choice(values)
    check("options", "generate", {"documentName": "f", "documentType": 4,
                                  "html": "<h1>x</h1>", "options": opts})


def fuzz_pdf_input(_sink):
    """Endpoints that PARSE a PDF: a different attack surface from HTML entirely."""
    blob = base64.b64encode(gen_pdf_bytes()).decode()
    op = rng.choice(["extract", "rotate", "nup", "flatten"])
    check("pdf-input", "transform", {"documentName": "f", "file": blob, "operation": op,
                                     "pages": rng.choice(["1", "1-9999", "", "0", "-1"]),
                                     "rotation": rng.choice([90, 37, -1]),
                                     "pagesPerSheet": rng.choice([2, 0, 999])})


FAMILIES = {"html": fuzz_html, "entity": fuzz_entity, "bomb": fuzz_bomb,
            "ssrf": fuzz_ssrf, "options": fuzz_options, "pdf": fuzz_pdf_input}


# ------------------------------------------------------------------- SSRF sink
class Sink:
    """A local HTTP server the engine must never be talked into fetching.

    Counting requests here is the only honest SSRF check. Asserting that the API returned
    an error proves nothing: the fetch can succeed and the render still fail for an
    unrelated reason, and then a real SSRF passes the test.
    """

    def __init__(self):
        self._hits = 0
        self._lock = threading.Lock()
        sink = self

        class Handler(http.server.BaseHTTPRequestHandler):
            def do_GET(self):
                with sink._lock:
                    sink._hits += 1
                self.send_response(200)
                self.send_header("Content-Type", "text/plain")
                self.end_headers()
                self.wfile.write(b"INTERNAL")

            def log_message(self, *_args):
                pass

        self.server = socketserver.TCPServer(("0.0.0.0", 0), Handler)
        self.port = self.server.server_address[1]
        threading.Thread(target=self.server.serve_forever, daemon=True).start()

    def hits(self):
        with self._lock:
            return self._hits

    def stop(self):
        self.server.shutdown()


def main():
    global BASE
    ap = argparse.ArgumentParser()
    ap.add_argument("--cases", type=int, default=400)
    ap.add_argument("--seed", type=int, default=None)
    ap.add_argument("--family", choices=sorted(FAMILIES), action="append")
    ap.add_argument("--base", default=BASE)
    args = ap.parse_args()

    BASE = args.base
    seed = args.seed if args.seed is not None else random.randrange(1 << 30)
    rng.seed(seed)

    if not alive():
        sys.exit(f"nothing serving at {BASE} — start it with: docker compose up -d api")

    sink = Sink()
    # The engine runs in a container, so its 127.0.0.1 is not this machine's. Both
    # spellings are generated: the loopback form tests the container's own internals,
    # the gateway form tests reaching the host it runs on.
    SSRF_HOSTS.append(f"127.0.0.1:{sink.port}")
    SSRF_HOSTS.append(f"host.docker.internal:{sink.port}")
    SSRF_HOSTS.append(f"172.17.0.1:{sink.port}")
    chosen = args.family or sorted(FAMILIES)
    print(f"Fuzzing {BASE} — {args.cases} cases, seed {seed}, families {chosen}")
    print(f"SSRF sink listening on port {sink.port}\n")

    started = time.time()
    try:
        for i in range(args.cases):
            FAMILIES[chosen[i % len(chosen)]](sink)
            if (i + 1) % 25 == 0:
                print(f"  {i+1:5}/{args.cases}  ok={stats['ok']} rejected={stats['rejected']} "
                      f"5xx={stats['server_error']} hang={stats['timeout']} "
                      f"dead={stats['dead']}", flush=True)
    except KeyboardInterrupt:
        print("\ninterrupted")
    finally:
        sink.stop()

    duration = time.time() - started
    print("\n" + "=" * 78)
    print(f"{stats['sent']} inputs in {duration/60:.1f} min (seed {seed})")
    print(f"  rendered            {stats['ok']}")
    print(f"  refused with 4xx    {stats['rejected']}   <- the correct answer for bad input")
    print(f"  5xx server errors   {stats['server_error']}")
    print(f"  hangs               {stats['timeout']}")
    print(f"  process deaths      {stats['dead']}")
    print(f"  SSRF fetches        {sum(1 for f in findings if f['why'] == 'ssrf-fetch')}")

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    (EVIDENCE / "fuzz-gate.json").write_text(json.dumps(
        {"seed": seed, "cases": args.cases, "families": chosen,
         "duration_s": round(duration, 1), "stats": stats, "findings": findings},
        indent=2) + "\n")

    print("-" * 78)
    if findings:
        print(f"fuzz gate: FAILED — {len(findings)} finding(s), reproduce with --seed {seed}")
        for f in findings:
            print(f"  [{f['family']}] {f['why']}: {f['detail'][:110]}")
        sys.exit(1)
    print("fuzz gate: PASSED — no 5xx, no hang, no process death, no SSRF fetch")


if __name__ == "__main__":
    main()
