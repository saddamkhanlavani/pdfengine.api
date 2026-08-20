#!/usr/bin/env python3
"""
PDFEngine — Committed Secrets Gate

The JWT signing key sat in appsettings.json from the initial commit until release
preparation. It survived a security review, a release-blocker pass and a dozen gates,
because every one of them asked whether the engine renders correctly and none asked what
was sitting in the config file next to it.

This gate asks. It fails when a value that must come from the environment is found in a
TRACKED file — and it deliberately scans git's index rather than the working tree, because
a secret that is only untracked is one `git add -A` away from being committed.

Development-only files are exempt by design: a development default is not a secret, it is
a convenience, and the engine refuses to start on one outside Development
(StartupConfigValidator). What must never happen is a real-looking secret in a file that
every environment loads.

Usage: python3 tests/secrets_gate.py
"""
import pathlib
import re
import subprocess
import sys

ROOT = pathlib.Path(__file__).resolve().parent.parent

# Files allowed to hold development credentials, because nothing outside Development reads
# them and the engine refuses to boot on their values elsewhere.
EXEMPT = {
    "src/PdfEngine.API/appsettings.Development.json",
    "tests/secrets_gate.py",          # names the patterns it looks for
    "docs/DEPLOYMENT.md",             # documents the failure text
    "docs/PDFENGINE_FEATURE_BACKLOG.md",
}
EXEMPT_PREFIXES = ("tests/evidence/", "tests/corpus/", "docs/proof/")

# Config keys whose value must never be present in a tracked, all-environment file.
SECRET_KEYS = ("Jwt:Key", "Stripe:SecretKey", "AWS:SecretKey", "AWS:AccessKey")

PATTERNS = [
    (re.compile(r'"Key"\s*:\s*"(?!\s*")([^"]{16,})"'), "Jwt:Key has a value"),
    (re.compile(r'"SecretKey"\s*:\s*"(?!\s*")([^"]{8,})"'), "a SecretKey has a value"),
    (re.compile(r'Password=(?!;|"|\s*$)([^;"\s]+)'), "a connection string carries a password"),
    (re.compile(r'\bSuperSecretKey\w*'), "the historical committed JWT key"),
    (re.compile(r'\bsk_live_[A-Za-z0-9]{8,}'), "a live Stripe key"),
    (re.compile(r'\bAKIA[0-9A-Z]{16}\b'), "an AWS access key id"),
]


def tracked_files():
    out = subprocess.run(["git", "ls-files"], cwd=str(ROOT),
                         capture_output=True, text=True).stdout
    return [line for line in out.splitlines() if line]


def main():
    findings = []
    scanned = 0
    for rel in tracked_files():
        if rel in EXEMPT or rel.startswith(EXEMPT_PREFIXES):
            continue
        # Only configuration and compose-style files: source code that MENTIONS a key name
        # is not a leak, and scanning everything produces noise nobody reads.
        if not re.search(r"(appsettings.*\.json|docker-compose.*\.ya?ml|\.env$)", rel):
            continue
        path = ROOT / rel
        try:
            text = path.read_text(encoding="utf-8", errors="replace")
        except OSError:
            continue
        scanned += 1
        for line_no, line in enumerate(text.splitlines(), 1):
            if "${" in line:      # an environment interpolation is the correct shape
                continue
            for pattern, why in PATTERNS:
                m = pattern.search(line)
                if m:
                    findings.append((rel, line_no, why, m.group(0)[:60]))

    print(f"Committed-secrets gate — scanned {scanned} tracked config file(s)\n")
    if not findings:
        print("  [PASS] no secret values in tracked all-environment configuration")
        print(f"\n  keys covered: {', '.join(SECRET_KEYS)}, connection-string passwords")
        return 0

    for rel, line_no, why, snippet in findings:
        print(f"  [FAIL] {rel}:{line_no} — {why}\n         {snippet}")
    print(f"\nsecrets gate: FAILED — {len(findings)} value(s) that must come from the environment")
    print("Move development values to appsettings.Development.json and supply the rest via")
    print("environment variables. See docs/DEPLOYMENT.md.")
    return 1


if __name__ == "__main__":
    sys.exit(main())
