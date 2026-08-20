#!/usr/bin/env bash
# PDFEngine — Release Gate Runner
#
# ONE entry point used by both a developer laptop and CI, deliberately: a CI-only
# pipeline drifts from what people actually run locally, and then "works on my
# machine" becomes a real category of failure.
#
#   ./tests/run-gates.sh            # run every available gate
#   ./tests/run-gates.sh --update   # accept current results as the new baseline
#
# Exit code 0 = every gate passed. Non-zero = at least one gate failed/regressed.
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

UPDATE=""
[[ "${1:-}" == "--update" ]] && UPDATE="--update-baseline"

API_URL="${PDFENGINE_BASE:-http://localhost:5276}"
VERAPDF="${VERAPDF_BIN:-/tmp/pdfengine-indepth/verapdf-install/verapdf}"
EVIDENCE="$ROOT/tests/evidence"
mkdir -p "$EVIDENCE/verapdf"

FAILED=0
SKIPPED=0
declare -a RESULTS=()

record() { RESULTS+=("$1|$2|$3"); }

hr() { printf '%.0s─' {1..78}; echo; }

banner() { echo; hr; echo "  $1"; hr; }

# Fail fast on a wrong toolchain rather than reporting it as a test failure. A
# Homebrew dotnet earlier in PATH shadows the .NET 8 SDK and the test host dies with
# "You must install or update .NET", which reads exactly like a broken test suite.
if ! dotnet --list-runtimes 2>/dev/null | grep -q "Microsoft.NETCore.App 8\."; then
  echo "FATAL: the 'dotnet' on PATH ($(command -v dotnet)) has no .NET 8 runtime."
  echo "       Gates cannot run. Fix PATH / install the .NET 8 runtime and retry."
  exit 3
fi

# ---------------------------------------------------------------- unit tests
banner "GATE: unit tests"
# Retried once: a concurrently-running API can transiently lock shared build output
# and abort the test host. Verified this is a flake, not a real failure, so one retry
# is honest here -- a genuine test failure still fails twice and is still reported.
run_unit() { dotnet test --nologo -v q 2>&1 | tee "$EVIDENCE/unit-tests.log" | grep -qE "^Passed!"; }
if run_unit || { echo "  retrying (possible build-output lock)..."; sleep 3; run_unit; }; then
  grep -E "Passed!" "$EVIDENCE/unit-tests.log" | tail -1
  record "unit tests" PASS "$(grep -oE 'Passed: +[0-9]+' "$EVIDENCE/unit-tests.log" | tail -1)"
else
  echo "FAILED"; tail -5 "$EVIDENCE/unit-tests.log"
  record "unit tests" FAIL "see unit-tests.log"; FAILED=$((FAILED+1))
fi

# ------------------------------------------------------- API-dependent gates
api_up() { curl -sf -o /dev/null --max-time 5 "$API_URL/health" 2>/dev/null; }

if ! api_up; then
  banner "API not reachable at $API_URL — skipping API-dependent gates"
  echo "  Start it with:"
  echo "    ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/PdfEngine.API/PdfEngine.API.csproj --urls $API_URL"
  record "Gate B2 text extraction" SKIP "API down"
  record "Gate M platform"         SKIP "API down"
  record "Gates C+D pagination"    SKIP "API down"
  record "Gate E tables"           SKIP "API down"
  record "Gate J determinism"      SKIP "API down"
  record "Gate H PDF/A + PDF/UA"   SKIP "API down"
  SKIPPED=6
else
  # Gate B2 — typography text layer
  banner "GATE B2: typography — text layer (15 scripts)"
  if python3 tests/extraction_gate.py $UPDATE 2>&1 | tee "$EVIDENCE/text-extraction-gate.log" | tail -3; then
    record "Gate B2 text extraction" PASS "$(grep -oE 'summary:.*' "$EVIDENCE/text-extraction-gate.log" | tail -1)"
  else
    record "Gate B2 text extraction" FAIL "regression vs baseline"; FAILED=$((FAILED+1))
  fi

  # Gate M — platform / auth / tenancy
  banner "GATE M: platform, auth & tenant isolation"
  if python3 tests/platform_gate.py $UPDATE 2>&1 | tee "$EVIDENCE/platform-gate.log" | tail -3; then
    record "Gate M platform" PASS "$(grep -oE 'summary:.*' "$EVIDENCE/platform-gate.log" | tail -1)"
  else
    record "Gate M platform" FAIL "regression vs baseline"; FAILED=$((FAILED+1))
  fi

  # Gates C + D — pagination & page utilization
  banner "GATES C+D: pagination & page utilization"
  if python3 tests/pagination_gate.py $UPDATE 2>&1 | tee "$EVIDENCE/pagination-gate.log" | tail -3; then
    record "Gates C+D pagination" PASS "$(grep -oE 'summary:.*' "$EVIDENCE/pagination-gate.log" | tail -1)"
  else
    record "Gates C+D pagination" FAIL "regression vs baseline"; FAILED=$((FAILED+1))
  fi

  # Gate E — tables
  banner "GATE E: table fragmentation (rowspan/colspan across page breaks)"
  if python3 tests/table_gate.py $UPDATE 2>&1 | tee "$EVIDENCE/table-gate.log" | tail -3; then
    record "Gate E tables" PASS "$(grep -oE 'summary:.*' "$EVIDENCE/table-gate.log" | tail -1)"
  else
    record "Gate E tables" FAIL "regression vs baseline"; FAILED=$((FAILED+1))
  fi

  # Gates A + B1 — HTML/CSS compatibility, Rendering Doctor, typography VISUAL
  banner "GATES A+B1: HTML/CSS compatibility, rendering doctor & visual typography"
  if python3 tests/compat_gate.py $UPDATE 2>&1 | tee "$EVIDENCE/compat-gate.log" | tail -3; then
    record "Gates A+B1 compat" PASS "$(grep -oE 'summary:.*' "$EVIDENCE/compat-gate.log" | tail -1)"
  else
    record "Gates A+B1 compat" FAIL "regression vs baseline"; FAILED=$((FAILED+1))
  fi

  # Tier 1 typesetting — running headers/footers, target-counter, leaders, page selectors
  banner "TYPESETTING (T1): running headers, target-counter, leaders, page selectors"
  if python3 tests/typesetting_gate.py $UPDATE 2>&1 | tee "$EVIDENCE/typesetting-gate.log" | tail -3; then
    record "Typesetting T1" PASS "$(grep -oE 'summary:.*' "$EVIDENCE/typesetting-gate.log" | tail -1)"
  else
    record "Typesetting T1" FAIL "regression vs baseline"; FAILED=$((FAILED+1))
  fi

  # Gate F — figures & charts
  banner "GATE F: figures, charts & caption grouping"
  if python3 tests/figures_gate.py $UPDATE 2>&1 | tee "$EVIDENCE/figures-gate.log" | tail -3; then
    record "Gate F figures" PASS "$(grep -oE 'summary:.*' "$EVIDENCE/figures-gate.log" | tail -1)"
  else
    record "Gate F figures" FAIL "regression vs baseline"; FAILED=$((FAILED+1))
  fi

  # Gate G — document navigation
  banner "GATE G: outline, links, cross-references & structure tree"
  if python3 tests/navigation_gate.py $UPDATE 2>&1 | tee "$EVIDENCE/navigation-gate.log" | tail -3; then
    record "Gate G navigation" PASS "$(grep -oE 'summary:.*' "$EVIDENCE/navigation-gate.log" | tail -1)"
  else
    record "Gate G navigation" FAIL "regression vs baseline"; FAILED=$((FAILED+1))
  fi

  # Gate I — security (input surface + SSRF + resource limits)
  banner "GATE I: security — sanitization, SSRF & resource limits"
  if python3 tests/security_gate.py $UPDATE 2>&1 | tee "$EVIDENCE/security-gate.log" | tail -3; then
    record "Gate I security" PASS "$(grep -oE 'summary:.*' "$EVIDENCE/security-gate.log" | tail -1)"
  else
    record "Gate I security" FAIL "regression vs baseline"; FAILED=$((FAILED+1))
  fi

  # Gate L — reliability & chaos. Runs BEFORE the performance gate because it
  # deliberately kills the browser process; leaving it last would hand a cold engine
  # to whatever ran next and misattribute the startup cost.
  banner "GATE L: reliability & chaos (fault injection)"
  if python3 tests/reliability_gate.py $UPDATE 2>&1 | tee "$EVIDENCE/reliability-gate.log" | tail -3; then
    record "Gate L reliability" PASS "$(grep -oE 'summary:.*' "$EVIDENCE/reliability-gate.log" | tail -1)"
  else
    record "Gate L reliability" FAIL "regression vs baseline"; FAILED=$((FAILED+1))
  fi

  # Gate K — performance. Skipped unless explicitly requested: the soak loop takes
  # minutes and its thresholds are machine-relative, so it would make the default
  # run slow and flaky on shared CI hardware.
  if [[ -n "${RUN_PERF_GATE:-}" ]]; then
    banner "GATE K: performance & scaling"
    if python3 tests/performance_gate.py --soak "${PERF_SOAK:-200}" $UPDATE 2>&1 | tee "$EVIDENCE/performance-gate.log" | tail -4; then
      record "Gate K performance" PASS "$(grep -oE 'summary:.*' "$EVIDENCE/performance-gate.log" | tail -1)"
    else
      record "Gate K performance" FAIL "regression vs baseline"; FAILED=$((FAILED+1))
    fi
  else
    record "Gate K performance" SKIP "set RUN_PERF_GATE=1 to run (slow, machine-relative)"
    SKIPPED=$((SKIPPED+1))
  fi

  # Gate J — determinism / reproducibility
  banner "GATE J: determinism & reproducibility"
  if python3 tests/determinism_gate.py $UPDATE 2>&1 | tee "$EVIDENCE/determinism-gate.log" | tail -3; then
    record "Gate J determinism" PASS "$(grep -oE 'summary:.*' "$EVIDENCE/determinism-gate.log" | tail -1)"
  else
    record "Gate J determinism" FAIL "output drifted vs pinned baseline"; FAILED=$((FAILED+1))
  fi

  # Gate H — PDF/A + PDF/UA conformance via veraPDF
  banner "GATE H: PDF/A + PDF/UA conformance (veraPDF)"
  if [[ -x "$VERAPDF" ]]; then
    python3 tests/conformance_gate.py --verapdf "$VERAPDF" $UPDATE 2>&1 | tee "$EVIDENCE/conformance-gate.log" | tail -4
    case "${PIPESTATUS[0]}" in
      0) record "Gate H PDF/A + PDF/UA" PASS "$(grep -oE 'summary:.*' "$EVIDENCE/conformance-gate.log" | tail -1)" ;;
      2) record "Gate H PDF/A + PDF/UA" SKIP "validator could not run (tooling)"; SKIPPED=$((SKIPPED+1)) ;;
      *) record "Gate H PDF/A + PDF/UA" FAIL "regression vs baseline"; FAILED=$((FAILED+1)) ;;
    esac
  else
    echo "  veraPDF not found at $VERAPDF — set VERAPDF_BIN. SKIPPING (not a pass)."
    record "Gate H PDF/A + PDF/UA" SKIP "veraPDF not installed"; SKIPPED=$((SKIPPED+1))
  fi
  # Committed secrets. Cheap, and it caught a real one on its first run.
  banner "SECRETS: values that must come from the environment"
  if python3 tests/secrets_gate.py 2>&1 | tee "$EVIDENCE/secrets-gate.log" | tail -3; then
    record "Committed secrets" PASS "no secret values in tracked config"
  else
    record "Committed secrets" FAIL "a secret is present in a tracked all-environment file"; FAILED=$((FAILED+1))
  fi

  # Tier 2 output features — attachments, signatures, forms, page ops, print production
  banner "OUTPUT: Tier 2 PDF output features"
  if [[ -x "$VERAPDF" ]]; then
    OUTPUT_ARGS="--verapdf $VERAPDF"
  else
    OUTPUT_ARGS=""
  fi
  if python3 tests/output_gate.py $OUTPUT_ARGS $UPDATE 2>&1 | tee "$EVIDENCE/output-gate.log" | tail -3; then
    record "Output Tier 2" PASS "$(grep -oE 'summary:.*' "$EVIDENCE/output-gate.log" | tail -1)"
  else
    record "Output Tier 2" FAIL "regression vs baseline"; FAILED=$((FAILED+1))
  fi

  # T3-1 — PDF/UA across feature COMBINATIONS, not one document at a time
  banner "T3-1: accessibility across feature combinations"
  if [[ -x "$VERAPDF" ]]; then
    if python3 tests/accessibility_gate.py --verapdf "$VERAPDF" 2>&1 | tee "$EVIDENCE/accessibility-gate.log" | tail -3; then
      record "T3-1 accessibility" PASS "$(grep -oE '[0-9]+/[0-9]+ as expected.*' "$EVIDENCE/accessibility-gate.log" | tail -1)"
    else
      record "T3-1 accessibility" FAIL "a combination no longer behaves as recorded"; FAILED=$((FAILED+1))
    fi
  else
    record "T3-1 accessibility" SKIP "veraPDF not installed"; SKIPPED=$((SKIPPED+1))
  fi

  # T3-3 — fuzzing. Short by default so it can run per-push; the nightly run is longer.
  banner "T3-3: fuzzing (generated adversarial input)"
  if python3 tests/fuzz_gate.py --base "$API_URL" --cases "${FUZZ_CASES:-120}" 2>&1 | tee "$EVIDENCE/fuzz-gate.log" | tail -4; then
    record "T3-3 fuzzing" PASS "$(grep -oE '[0-9]+ inputs in.*' "$EVIDENCE/fuzz-gate.log" | tail -1)"
  else
    record "T3-3 fuzzing" FAIL "$(grep -oE 'fuzz gate: FAILED.*' "$EVIDENCE/fuzz-gate.log" | tail -1)"; FAILED=$((FAILED+1))
  fi
fi

# ------------------------------------------------------------------ summary
banner "RELEASE GATE SUMMARY"
printf "  %-28s %-6s %s\n" "GATE" "RESULT" "DETAIL"
for r in "${RESULTS[@]}"; do
  IFS='|' read -r n v d <<< "$r"
  printf "  %-28s %-6s %s\n" "$n" "$v" "$d"
done
hr
if (( FAILED > 0 )); then
  echo "  RESULT: FAILED — $FAILED gate(s) regressed."
  exit 1
fi
if (( SKIPPED > 0 )); then
  echo "  RESULT: INCOMPLETE — $SKIPPED gate(s) skipped. A skip is NOT a pass."
  exit 2
fi
echo "  RESULT: ALL AVAILABLE GATES PASSED"
echo
echo "  Note: every gate A-M now has a runner. Gates I, K and L are PARTIAL by"
echo "  design — each prints exactly what it does not cover. 'All gates passed'"
echo "  still does NOT mean release-ready."
echo "  See docs/PDFENGINE_RELEASE_GATES.md — 'all available gates passed' is NOT"
echo "  the same as release-ready."
exit 0
