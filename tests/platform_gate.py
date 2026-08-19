#!/usr/bin/env python3
"""
PDFEngine — Platform Gate (Release Gate M)  [RB-5]

Exercises the API/auth/tenancy surface end-to-end against a running instance.

Why a script rather than WebApplicationFactory: these assertions are about the real
deployed HTTP surface — status codes, auth rejection, tenant scoping, validation —
which is exactly what a customer hits. It also runs against the same Postgres/Redis
the app really uses, instead of a substituted in-memory stack that can pass while
production fails.

Usage:
  python3 tests/platform_gate.py            # run, compare to baseline
  python3 tests/platform_gate.py --update-baseline
Exit code is non-zero on any regression.
"""
import argparse, json, os, pathlib, sys, urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
EVIDENCE = ROOT / "tests" / "evidence"
BASELINE = ROOT / "tests" / "corpus" / "platform-baseline.json"
BASE = os.environ.get("PDFENGINE_BASE", "http://localhost:5276")
KEY = os.environ.get("PDFENGINE_API_KEY", "test-api-key-123")
KEY_B = os.environ.get("PDFENGINE_API_KEY_B", "test-api-key-tenant-b")  # separate tenant

MIN_HTML = {"documentName": "platform-gate", "documentType": 4,
            "html": "<html><body><p>gate</p></body></html>", "options": {"pageSize": "A4"}}


def call(method, path, body=None, key=None, timeout=90):
    """Returns (status, bytes). Never raises on HTTP error status."""
    data = json.dumps(body).encode() if body is not None else None
    req = urllib.request.Request(BASE + path, data=data, method=method)
    if data is not None:
        req.add_header("Content-Type", "application/json")
    if key:
        req.add_header("X-Api-Key", key)
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read()
    except urllib.error.HTTPError as e:
        return e.code, e.read()
    except Exception as e:                                    # noqa: BLE001
        return -1, str(e).encode()


CHECKS = []


def check(name, critical=True):
    def deco(fn):
        CHECKS.append((name, fn, critical))
        return fn
    return deco


@check("health endpoint is public and reports healthy")
def _():
    s, b = call("GET", "/health")
    return s == 200, f"status={s} body={b[:40]!r}"


@check("render WITHOUT api key is rejected")
def _():
    s, _b = call("POST", "/api/v1/pdf/generate", MIN_HTML)
    return s in (401, 403), f"status={s} (want 401/403)"


@check("render with INVALID api key is rejected")
def _():
    s, _b = call("POST", "/api/v1/pdf/generate", MIN_HTML, key="totally-bogus-key")
    return s in (401, 403), f"status={s} (want 401/403)"


@check("render with VALID api key returns a real PDF")
def _():
    s, b = call("POST", "/api/v1/pdf/generate", MIN_HTML, key=KEY)
    return s == 200 and b[:5] == b"%PDF-", f"status={s} magic={b[:5]!r}"


@check("malformed body is rejected with 4xx, not 500")
def _():
    s, _b = call("POST", "/api/v1/pdf/generate", {"documentName": "x"}, key=KEY)
    return 400 <= s < 500, f"status={s} (want 4xx)"


@check("neither html nor url is rejected with a clear message")
def _():
    s, b = call("POST", "/api/v1/pdf/generate",
                {"documentName": "x", "documentType": 4}, key=KEY)
    return s == 400 and b"exactly one" in b.lower(), f"status={s} body={b[:90]!r}"


@check("SSRF: cloud metadata endpoint is blocked with BLOCKED_URL 400")
def _():
    s, b = call("POST", "/api/v1/pdf/generate",
                {"documentName": "ssrf", "documentType": 4,
                 "url": "http://169.254.169.254/latest/meta-data/",
                 "options": {"pageSize": "A4"}}, key=KEY)
    return s == 400 and b"BLOCKED_URL" in b, f"status={s} body={b[:90]!r}"


@check("batch size cap is enforced (51 rejected)")
def _():
    items = [dict(MIN_HTML, documentName=f"b{i}") for i in range(51)]
    s, b = call("POST", "/api/v1/pdf/jobs/batch", items, key=KEY)
    return s == 400 and b"maximum" in b.lower(), f"status={s} body={b[:90]!r}"


@check("batch boundary accepted (50 queued)")
def _():
    items = [dict(MIN_HTML, documentName=f"b{i}") for i in range(50)]
    s, b = call("POST", "/api/v1/pdf/jobs/batch", items, key=KEY)
    return s in (200, 202), f"status={s}"


@check("job status for an unknown id is not a 500")
def _():
    s, _b = call("GET", "/api/v1/pdf/jobs/00000000-0000-0000-0000-000000000000", key=KEY)
    return s in (400, 401, 403, 404), f"status={s} (want 4xx, got 500 = leak)"


@check("job download without api key is rejected")
def _():
    s, _b = call("GET",
                 "/api/v1/pdf/jobs/00000000-0000-0000-0000-000000000000/download")
    return s in (401, 403, 404), f"status={s}"


@check("merge endpoint validates input (<2 files rejected)")
def _():
    s, b = call("POST", "/api/v1/pdf/merge",
                {"documentName": "m", "files": ["only-one"]}, key=KEY)
    return s == 400, f"status={s} body={b[:90]!r}"


@check("PDF/A + encryption combination is rejected (spec conflict)")
def _():
    body = dict(MIN_HTML)
    body["options"] = {"pageSize": "A4", "pdfaCompliance": "PDF/A-2b",
                       "ownerPassword": "x"}
    s, b = call("POST", "/api/v1/pdf/generate", body, key=KEY)
    return s == 400, f"status={s} body={b[:90]!r}"


@check("tenant B has its own working credentials")
def _():
    s, b = call("POST", "/api/v1/pdf/generate", MIN_HTML, key=KEY_B)
    return s == 200 and b[:5] == b"%PDF-", f"status={s}"


@check("TENANT ISOLATION: tenant B cannot read tenant A's job")
def _():
    # Tenant A queues a job...
    s, b = call("POST", "/api/v1/pdf/jobs",
                dict(MIN_HTML, documentName="tenant-a-private"), key=KEY)
    if s not in (200, 202):
        return False, f"could not queue job as tenant A (status={s})"
    try:
        job_id = json.loads(b).get("jobId") or json.loads(b).get("JobId")
    except Exception:                                        # noqa: BLE001
        return False, f"unparseable queue response: {b[:120]!r}"
    if not job_id:
        return False, f"no jobId in response: {b[:120]!r}"

    # ...tenant B must NOT be able to see it. 404/403 are both acceptable
    # (404 is preferable - it does not confirm the resource exists).
    sb, bb = call("GET", f"/api/v1/pdf/jobs/{job_id}", key=KEY_B)
    leaked = sb == 200
    return (not leaked), f"tenantA job={job_id[:8]}... tenantB got {sb}" + (
        " *** CROSS-TENANT LEAK ***" if leaked else "")


@check("TENANT ISOLATION: tenant B cannot download tenant A's job")
def _():
    s, b = call("POST", "/api/v1/pdf/jobs",
                dict(MIN_HTML, documentName="tenant-a-private-dl"), key=KEY)
    if s not in (200, 202):
        return False, f"could not queue job as tenant A (status={s})"
    try:
        job_id = json.loads(b).get("jobId") or json.loads(b).get("JobId")
    except Exception:                                        # noqa: BLE001
        return False, f"unparseable: {b[:100]!r}"
    if not job_id:
        return False, "no jobId"
    sb, _ = call("GET", f"/api/v1/pdf/jobs/{job_id}/download", key=KEY_B)
    return sb != 200, f"tenantB download got {sb}" + (" *** LEAK ***" if sb == 200 else "")


@check("tenant A CAN read its own job (isolation is not just blanket denial)")
def _():
    s, b = call("POST", "/api/v1/pdf/jobs",
                dict(MIN_HTML, documentName="tenant-a-own"), key=KEY)
    if s not in (200, 202):
        return False, f"queue status={s}"
    try:
        job_id = json.loads(b).get("jobId") or json.loads(b).get("JobId")
    except Exception:                                        # noqa: BLE001
        return False, f"unparseable: {b[:100]!r}"
    sa, _ = call("GET", f"/api/v1/pdf/jobs/{job_id}", key=KEY)
    return sa == 200, f"owner got {sa} (want 200)"


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--update-baseline", action="store_true")
    args = ap.parse_args()

    results, failed = {}, 0
    print(f"{'check':62} {'verdict':8} detail")
    print("-" * 110)
    for name, fn, critical in CHECKS:
        try:
            ok, detail = fn()
        except Exception as e:                                # noqa: BLE001
            ok, detail = False, f"exception: {e}"
        verdict = "PASS" if ok else ("FAIL" if critical else "WARN")
        if not ok and critical:
            failed += 1
        results[name] = {"verdict": verdict, "detail": detail}
        print(f"{name:62} {verdict:8} {detail}")

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    (EVIDENCE / "platform-gate.json").write_text(
        json.dumps(results, indent=2), encoding="utf-8")
    print("-" * 110)
    print(f"summary: {len(CHECKS) - failed}/{len(CHECKS)} passed")

    if args.update_baseline:
        BASELINE.parent.mkdir(parents=True, exist_ok=True)
        BASELINE.write_text(json.dumps(
            {k: v["verdict"] for k, v in results.items()}, indent=2), encoding="utf-8")
        print(f"baseline written -> {BASELINE}")
        return 0

    if BASELINE.exists():
        base = json.loads(BASELINE.read_text(encoding="utf-8"))
        rank = {"PASS": 2, "WARN": 1, "FAIL": 0}
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
