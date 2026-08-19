#!/bin/sh
# Proves the image contains what the engine's claims depend on. Run inside the container:
#
#     docker run --rm pdfengine:local /opt/pdfengine/verify-image.sh
#
# Every check names what breaks if it fails, because an image that boots but is missing a
# font renders quietly wrong rather than crashing.
set -u
fail=0
ok()   { echo "  [PASS] $1"; }
bad()  { echo "  [FAIL] $1"; fail=1; }

echo "PDFEngine image verification"

echo
echo "Chromium — pinned by the Playwright driver, not by the distribution"
shell_bin="$(find /ms-playwright -name 'headless_shell' -type f 2>/dev/null | head -1)"
chrome_bin="$(find /ms-playwright -name 'chrome' -type f 2>/dev/null | head -1)"
bin="${chrome_bin:-$shell_bin}"
if [ -n "$bin" ]; then
    ver="$("$bin" --version 2>/dev/null | head -1)"
    if [ -n "$ver" ]; then ok "Chromium runs: $ver"; else bad "Chromium binary present but will not execute — a shared library is missing"; fi
    ok "installed revisions: $(tr '\n' ' ' < /ms-playwright/INSTALLED-REVISIONS.txt 2>/dev/null)"
else
    bad "no Chromium in /ms-playwright — every render fails"
fi

echo
echo "Fonts — Chromium asks fontconfig, not the engine's resolver"
count="$(fc-list 2>/dev/null | wc -l | tr -d ' ')"
if [ "${count:-0}" -gt 0 ]; then ok "$count font files visible to fontconfig"; else bad "no fonts installed — text renders blank"; fi
for generic in sans-serif serif monospace; do
    match="$(fc-match "$generic" 2>/dev/null)"
    case "$match" in
        *Carlito*|*Caladea*|*Liberation*) ok "$generic resolves to ${match%%:*}" ;;
        "") bad "$generic resolves to nothing" ;;
        *) bad "$generic resolves to ${match%%:*} — not the pinned face, output will not match the baseline" ;;
    esac
done
for legacy in Arial Helvetica "Times New Roman"; do
    match="$(fc-match "$legacy" 2>/dev/null)"
    ok "$legacy -> ${match%%:*}"
done

echo
echo "Playwright driver — the engine shells out to it on every render"
driver_node="$(find /app/.playwright/node -name node -type f 2>/dev/null | head -1)"
if [ -z "$driver_node" ]; then
    bad "no driver node in /app/.playwright — every render fails"
elif [ -x "$driver_node" ]; then
    ok "driver node is executable: $("$driver_node" --version 2>/dev/null)"
else
    bad "driver node is NOT executable — the app boots, health checks pass, and the first render fails with Permission denied"
fi

echo
echo "qpdf — linearization is delegated to it"
if command -v qpdf >/dev/null 2>&1; then ok "qpdf present: $(qpdf --version | head -1)"
else bad "qpdf missing — requests with linearize=true will fail"; fi

echo
echo "Application"
if [ -f /app/PdfEngine.API.dll ]; then ok "PdfEngine.API.dll present"; else bad "application not published into the image"; fi
fonts_dir_count="$(ls /app/Fonts/*.ttf 2>/dev/null | wc -l | tr -d ' ')"
if [ "${fonts_dir_count:-0}" -gt 0 ]; then ok "$fonts_dir_count bundled faces for the PdfSharpCore resolver"
else bad "/app/Fonts is empty — engine-drawn text (headers, watermarks) loses its fonts"; fi

echo
if [ "$fail" -eq 0 ]; then echo "image verification: OK"; else echo "image verification: FAILED"; fi
exit "$fail"
