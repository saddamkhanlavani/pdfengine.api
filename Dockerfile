# PDFEngine runtime image.
#
# The point of this image is REPRODUCIBILITY, not convenience. The engine's output is
# byte-deterministic on one machine (Gate J) and that claim only travels if the three
# things that decide the bytes travel with it:
#
#   1. Chromium  — pinned by the Playwright driver (1.58.1 → Chromium revision 1208).
#                  A different Chromium lays text out differently. Never `apt install
#                  chromium`, which floats with the distribution.
#   2. Fonts     — the bundled OFL faces are installed system-wide AND fontconfig is
#                  given a deterministic ordering, because Chromium picks a fallback by
#                  asking fontconfig and a different answer is a different PDF.
#   3. qpdf      — linearization is delegated to it; without it `linearize: true` fails.
#
# Build:  docker build -t pdfengine:local .
# Verify: docker run --rm pdfengine:local /opt/pdfengine/verify-image.sh

# ---------------------------------------------------------------------------- build
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore against the project files alone, so a source-only change does not re-resolve
# every package.
COPY PdfEngine.sln ./
COPY src/PdfEngine.Domain/PdfEngine.Domain.csproj        src/PdfEngine.Domain/
COPY src/PdfEngine.Application/PdfEngine.Application.csproj src/PdfEngine.Application/
COPY src/PdfEngine.Infrastructure/PdfEngine.Infrastructure.csproj src/PdfEngine.Infrastructure/
COPY src/PdfEngine.API/PdfEngine.API.csproj              src/PdfEngine.API/
RUN dotnet restore src/PdfEngine.API/PdfEngine.API.csproj

COPY src/ src/
RUN dotnet publish src/PdfEngine.API/PdfEngine.API.csproj \
        -c Release -o /app/publish /p:UseAppHost=false

# ------------------------------------------------------------------------- browsers
# Downloaded in its own stage so the runtime image never carries the download tooling.
# The driver's own bundled node runs the CLI, which avoids installing PowerShell purely
# to execute playwright.ps1.
FROM build AS browsers
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright
RUN set -eux; \
    node_bin="$(find /app/publish/.playwright/node -name node -type f | head -1)"; \
    "$node_bin" /app/publish/.playwright/package/cli.js install chromium; \
    # Record what was actually installed. The reproducibility gate compares this against
    # a running container, so a silent browser bump shows up as a diff rather than as a
    # mysterious change in rendered output.
    ls -1 /ms-playwright > /ms-playwright/INSTALLED-REVISIONS.txt

# -------------------------------------------------------------------------- runtime
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime

ENV DEBIAN_FRONTEND=noninteractive \
    PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    PLAYWRIGHT_SKIP_BROWSER_DOWNLOAD=1 \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false \
    ASPNETCORE_URLS=http://+:8080

# Chromium's shared-library dependencies, plus qpdf for linearization and fontconfig so
# the bundled faces are discoverable. Versions are whatever the pinned base image resolves
# to; the base image tag is the pin.
RUN apt-get update && apt-get install --no-install-recommends -y \
        libnss3 libnspr4 libdbus-1-3 libatk1.0-0 libatk-bridge2.0-0 libcups2 \
        libdrm2 libxkbcommon0 libxcomposite1 libxdamage1 libxfixes3 libxrandr2 \
        libgbm1 libpango-1.0-0 libcairo2 libasound2 libatspi2.0-0 libx11-6 \
        libxcb1 libxext6 libexpat1 libuuid1 \
        qpdf fontconfig \
        curl ca-certificates \
    && rm -rf /var/lib/apt/lists/*

COPY --from=browsers /ms-playwright /ms-playwright
COPY --from=build /app/publish /app

# `dotnet publish` does not preserve the execute bit on the Playwright driver's bundled
# node, and COPY does not restore it. The app then starts, passes every health check, and
# fails the FIRST RENDER with "Permission denied" — a failure that looks like a Chromium
# problem and is not. Restored here and asserted by verify-image.sh.
RUN find /app/.playwright -type f \( -name node -o -name '*.sh' \) -exec chmod +x {} + \
    && find /ms-playwright -type f \( -name 'chrome' -o -name 'headless_shell' \
       -o -name '*.sh' \) -exec chmod +x {} +

# The engine resolves its own fonts from /app/Fonts for PdfSharpCore. Chromium does not
# use that resolver — it asks fontconfig — so the same faces are installed system-wide.
# Without this, a container renders text in whatever the base image happens to ship,
# which on a slim image is nothing at all.
RUN mkdir -p /usr/share/fonts/truetype/pdfengine \
    && cp /app/Fonts/*.ttf /usr/share/fonts/truetype/pdfengine/ 2>/dev/null || true \
    && fc-cache -f \
    && fc-list | wc -l

# Deterministic font fallback. Chromium asks fontconfig for a generic family and a
# machine-dependent answer is a machine-dependent PDF.
COPY docker/fonts.conf /etc/fonts/local.conf
COPY docker/verify-image.sh /opt/pdfengine/verify-image.sh
RUN chmod +x /opt/pdfengine/verify-image.sh && fc-cache -f

# Chromium is run with --no-sandbox (see BrowserManager), which is only acceptable
# because the process itself is unprivileged and the container is the boundary.
RUN useradd --create-home --shell /usr/sbin/nologin --uid 10001 pdfengine \
    && mkdir -p /home/pdfengine/.cache /tmp/pdfengine \
    && chown -R pdfengine:pdfengine /home/pdfengine /tmp/pdfengine /ms-playwright
USER pdfengine

WORKDIR /app
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=40s --retries=3 \
    # /health/live, deliberately: this decides whether the container is KILLED, and a
# dependency being unreachable is not a reason to kill a working engine.
CMD curl -fsS -m 5 http://localhost:8080/health/live || exit 1

ENTRYPOINT ["dotnet", "PdfEngine.API.dll"]
