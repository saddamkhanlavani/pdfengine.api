#!/usr/bin/env python3
"""
PDFEngine — Cross-machine Reproducibility Gate (T3-4)

Gate J proves the engine is deterministic on ONE machine: render the same HTML twice on
this laptop and the structure, pixels and byte length agree. That claim does not travel.
Chromium, the font set and fontconfig's fallback choices all differ between hosts, and any
one of them silently reflows a customer's invoice.

This gate makes the claim portable by asserting two different things:

  1. ENVIRONMENT — the container's Chromium revision, qpdf version, bundled font digests
     and fontconfig generic-family resolution match what the baseline recorded. This is
     what makes a fingerprint difference EXPLAINABLE instead of mysterious.

  2. OUTPUT — the same corpus rendered through two SEPARATE container instances produces
     identical structural and visual fingerprints, and those fingerprints match the
     committed baseline.

Two containers rather than two requests is the point: a second request to a warm process
shares a browser, a font cache and a JIT. A second container shares only the image, which
is exactly what a second machine shares.

What this does NOT prove: that a different CPU architecture or kernel renders identically.
Proving that needs a second machine, and this gate is what you run there — the baseline it
compares against is committed, so a run on any host is directly comparable to this one.

Usage:
    python3 tests/reproducibility_gate.py                     # verify against baseline
    python3 tests/reproducibility_gate.py --update-baseline   # re-record (state why)
    python3 tests/reproducibility_gate.py --image pdfengine:local
"""
import argparse, hashlib, importlib.util, json, os, pathlib, re, subprocess, sys, time
import urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
BASELINE = ROOT / "tests" / "corpus" / "reproducibility-baseline.json"
EVIDENCE = ROOT / "tests" / "evidence"
COMPOSE_SERVICE = "api"
PORT = 8080

# The corpus and the fingerprint function are Gate J's, deliberately. Two definitions of
# "the same output" would drift apart, and then neither gate means anything.
_spec = importlib.util.spec_from_file_location("determinism_gate", ROOT / "tests" / "determinism_gate.py")
_det = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(_det)
DOCS, OPTIONS, fingerprints = _det.DOCS, _det.OPTIONS, _det.fingerprints


def sh(cmd, **kw):
    p = subprocess.run(cmd, capture_output=True, text=True, **kw)
    return p.returncode, (p.stdout + p.stderr).strip()


def image_environment(image):
    """What the image pins. A diff here explains a diff in output."""
    env = {}
    rc, out = sh(["docker", "run", "--rm", "--entrypoint", "sh", image, "-c",
                  "find /ms-playwright -name headless_shell -o -name chrome | head -1"])
    binary = out.splitlines()[-1] if out else ""
    if binary:
        rc, ver = sh(["docker", "run", "--rm", "--entrypoint", binary, image, "--version"])
        env["chromium"] = ver.strip()
    rc, revs = sh(["docker", "run", "--rm", "--entrypoint", "cat", image,
                   "/ms-playwright/INSTALLED-REVISIONS.txt"])
    env["browser_revisions"] = sorted(r for r in revs.split() if r.startswith("chromium"))
    rc, qpdf = sh(["docker", "run", "--rm", "--entrypoint", "qpdf", image, "--version"])
    env["qpdf"] = qpdf.splitlines()[0].strip() if qpdf else ""
    # fontconfig's answer for the generic families IS the layout for most real HTML.
    resolution = {}
    for family in ("sans-serif", "serif", "monospace", "Arial", "Helvetica",
                   "Times New Roman", "Courier New"):
        rc, match = sh(["docker", "run", "--rm", "--entrypoint", "fc-match", image, family])
        resolution[family] = match.split(":")[0].strip()
    env["font_resolution"] = resolution
    # The faces themselves, by content. A font swapped for a same-named different cut
    # would otherwise pass every other check here.
    rc, digests = sh(["docker", "run", "--rm", "--entrypoint", "sh", image, "-c",
                      "cd /app/Fonts && sha256sum *.ttf 2>/dev/null | sort"])
    env["font_digest"] = hashlib.sha256(digests.encode()).hexdigest()
    env["font_count"] = len(digests.splitlines())
    return env


def wait_healthy(base, timeout=180):
    deadline = time.time() + timeout
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"{base}/health", timeout=5) as r:
                if r.status == 200:
                    return True
        except Exception:
            pass
        time.sleep(3)
    return False


def render_corpus(base):
    """Render every Gate J fixture through the API at `base` and fingerprint it."""
    out = {}
    for name, html in DOCS.items():
        options = {"pageSize": "A4", "title": "Determinism Fixture", "author": "PdfEngine"}
        options.update(OPTIONS.get(name, {}))
        payload = json.dumps({"documentName": name, "documentType": 4,
                              "html": html, "options": options}).encode()
        req = urllib.request.Request(f"{base}/api/v1/pdf/generate", data=payload,
                                     method="POST",
                                     headers={"Content-Type": "application/json",
                                              "X-Api-Key": os.environ.get(
                                                  "PDFENGINE_API_KEY", "test-api-key-123")})
        with urllib.request.urlopen(req, timeout=300) as r:
            pdf = r.read()
        struct, visual = fingerprints(pdf)
        out[name] = {"structural": struct, "visual": visual, "bytes": len(pdf)}
    return out


def restart_container():
    """A genuinely fresh process, not a warm one: recreate, then wait for health."""
    rc, out = sh(["docker", "compose", "up", "-d", "--force-recreate", COMPOSE_SERVICE],
                 cwd=str(ROOT))
    if rc != 0:
        sys.exit(f"could not recreate the container:\n{out}")
    if not wait_healthy(f"http://localhost:{PORT}"):
        sys.exit("container did not become healthy in time")


def compare(label, got, want, failures):
    if got == want:
        print(f"  [PASS] {label}")
        return
    print(f"  [FAIL] {label}")
    if isinstance(got, dict) and isinstance(want, dict):
        for k in sorted(set(got) | set(want)):
            if got.get(k) != want.get(k):
                print(f"         {k}: baseline={want.get(k)!r} now={got.get(k)!r}")
    else:
        print(f"         baseline={want!r}\n         now={got!r}")
    failures.append(label)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--image", default="pdfengine:local")
    ap.add_argument("--update-baseline", action="store_true")
    args = ap.parse_args()

    if sh(["docker", "image", "inspect", args.image])[0] != 0:
        sys.exit(f"image {args.image} not built — run: docker build -t {args.image} .")

    print(f"Reproducibility gate — image {args.image}")
    print("\nEnvironment the image pins")
    env = image_environment(args.image)
    for key in ("chromium", "qpdf"):
        print(f"  {key}: {env[key]}")
    print(f"  browser revisions: {env['browser_revisions']}")
    print(f"  fonts: {env['font_count']} faces, digest {env['font_digest'][:16]}…")
    for family, face in env["font_resolution"].items():
        print(f"    {family:16} -> {face}")

    print("\nRun 1 — fresh container")
    restart_container()
    run1 = render_corpus(f"http://localhost:{PORT}")
    print("\nRun 2 — a SECOND fresh container, sharing only the image")
    restart_container()
    run2 = render_corpus(f"http://localhost:{PORT}")

    failures = []
    print("\nSame image, two separate container instances")
    for name in sorted(DOCS):
        a, b = run1[name], run2[name]
        same = (a["structural"] == b["structural"] and a["visual"] == b["visual"]
                and a["bytes"] == b["bytes"])
        print(f"  [{'PASS' if same else 'FAIL'}] {name}")
        if not same:
            for field in ("structural", "visual", "bytes"):
                if a[field] != b[field]:
                    print(f"         {field}: run1={a[field]} run2={b[field]}")
            failures.append(f"{name} differs between container instances")

    record = {"image_environment": env, "fingerprints": run1}
    if args.update_baseline:
        BASELINE.parent.mkdir(parents=True, exist_ok=True)
        BASELINE.write_text(json.dumps(record, indent=2, sort_keys=True) + "\n")
        print(f"\nbaseline written to {BASELINE.relative_to(ROOT)}")
    elif BASELINE.exists():
        want = json.loads(BASELINE.read_text())
        print("\nAgainst the committed baseline")
        we, be = env, want["image_environment"]
        for key in ("chromium", "qpdf", "browser_revisions", "font_digest", "font_count"):
            compare(f"environment: {key}", we.get(key), be.get(key), failures)
        compare("environment: fontconfig resolution",
                we["font_resolution"], be["font_resolution"], failures)
        for name in sorted(DOCS):
            compare(f"output: {name}", run1.get(name), want["fingerprints"].get(name), failures)
    else:
        print(f"\nNo baseline at {BASELINE.relative_to(ROOT)} — run with --update-baseline")

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    (EVIDENCE / "reproducibility-gate.json").write_text(
        json.dumps(record, indent=2, sort_keys=True) + "\n")

    print("\n" + "-" * 78)
    if failures:
        print(f"reproducibility gate: FAILED ({len(failures)})")
        for f in failures:
            print(f"  {f}")
        sys.exit(1)
    print("reproducibility gate: PASSED — the image renders identically across instances")
    print("Run this same command on another machine; the baseline it compares against is committed.")


if __name__ == "__main__":
    main()
