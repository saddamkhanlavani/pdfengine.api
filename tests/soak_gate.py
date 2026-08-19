#!/usr/bin/env python3
"""
PDFEngine — Soak and Cold-start Gate (T3-5)

Gate K runs a bounded 200-render leak proxy. 200 renders is enough to catch a leak that
grows fast and blind to the one that takes four thousand renders to matter — which is the
one that takes production down at 3am on a Sunday, long after everyone stopped watching.

Three questions, each with a number attached:

  COLD START   how long from `docker run` to serving the first PDF? This is what a scale-up
               event costs, and what an autoscaler's health-check grace period has to
               exceed or the orchestrator kills the container while it is still booting.

  LEAK         does resident memory trend upward across the run? Compared as first decile
               against last decile AND as a least-squares slope, because a browser recycle
               (every 50 renders by design) makes any single sample meaningless.

  DRIFT        does latency degrade? Median of the first tenth against the last tenth.
               A slow leak usually shows up as latency before it shows up as an OOM.

Nothing here is asserted from inside the application. Memory is read with `docker stats`,
which is the kernel's accounting rather than the runtime's opinion of itself.

Usage:
    python3 tests/soak_gate.py                       # the full 10,000
    python3 tests/soak_gate.py --renders 500         # a quick check
    python3 tests/soak_gate.py --concurrency 4
"""
import argparse, json, pathlib, statistics, subprocess, sys, time
import concurrent.futures as futures
import urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
EVIDENCE = ROOT / "tests" / "evidence"
CONTAINER = "pdfengine-api"
BASE = "http://localhost:8080"
KEY = "test-api-key-123"

# Deliberately not one fixture: a leak can live in the table fragmenter, the image
# decoder or the font resolver, and a soak that renders one document exercises one path.
FIXTURES = {
    "text": "<html><body><h1>Report</h1>" + "<p>Paragraph of body text.</p>" * 40 + "</body></html>",
    "table": ("<html><body><table border=1>" +
              "".join(f"<tr><td>row {i}</td><td>value {i}</td></tr>" for i in range(200)) +
              "</table></body></html>"),
    "graphics": ("<html><body><h1>Chart</h1>"
                 "<svg viewBox='0 0 300 120' width='300' height='120'>"
                 "<defs><linearGradient id='g'><stop offset='0%' stop-color='#36c'/>"
                 "<stop offset='100%' stop-color='#c36'/></linearGradient></defs>"
                 "<rect width='300' height='120' fill='url(#g)'/></svg>"
                 "<p>Vector content forces the rasteriser.</p></body></html>"),
    "styled": ("<html><head><style>@page{size:A4;margin:15mm}"
               "body{font-family:sans-serif}.c{column-count:2;column-gap:12px}</style></head>"
               "<body><div class='c'>" + "<p>Column text flows across two columns.</p>" * 30 +
               "</div></body></html>"),
}


def sh(cmd):
    p = subprocess.run(cmd, capture_output=True, text=True)
    return p.returncode, (p.stdout + p.stderr).strip()


def rss_mb():
    """Resident memory as the kernel reports it, not as the runtime describes itself."""
    rc, out = sh(["docker", "stats", "--no-stream", "--format", "{{.MemUsage}}", CONTAINER])
    if rc != 0 or "/" not in out:
        return None
    used = out.split("/")[0].strip()
    for suffix, factor in (("GiB", 1024), ("MiB", 1), ("KiB", 1 / 1024), ("B", 1 / 1048576)):
        if used.endswith(suffix):
            try:
                return float(used[:-len(suffix)]) * factor
            except ValueError:
                return None
    return None


def render(name):
    payload = json.dumps({"documentName": name, "documentType": 4,
                          "html": FIXTURES[name]}).encode()
    req = urllib.request.Request(f"{BASE}/api/v1/pdf/generate", data=payload, method="POST",
                                 headers={"Content-Type": "application/json", "X-Api-Key": KEY})
    started = time.time()
    try:
        with urllib.request.urlopen(req, timeout=180) as r:
            size = len(r.read())
        return time.time() - started, size, None
    except urllib.error.HTTPError as e:
        return time.time() - started, 0, f"HTTP {e.code}: {e.read()[:120].decode(errors='replace')}"
    except Exception as e:
        return time.time() - started, 0, f"{type(e).__name__}: {e}"


def cold_start():
    """Recreate the container and time it to its first served PDF."""
    print("Cold start — recreating the container")
    sh(["docker", "compose", "up", "-d", "--force-recreate", "api"])
    t0 = time.time()
    healthy = None
    deadline = t0 + 300
    while time.time() < deadline:
        try:
            with urllib.request.urlopen(f"{BASE}/health", timeout=5) as r:
                if r.status == 200:
                    healthy = time.time() - t0
                    break
        except Exception:
            pass
        time.sleep(1)
    if healthy is None:
        sys.exit("container never became healthy")
    # Health is not readiness: the first render pays for the browser launch, which is the
    # cost an autoscaler actually has to absorb.
    elapsed, size, err = render("text")
    first_pdf = time.time() - t0
    if err:
        sys.exit(f"first render after cold start failed: {err}")
    print(f"  health endpoint 200 after   {healthy:6.1f}s")
    print(f"  first PDF served after      {first_pdf:6.1f}s  (render itself {elapsed*1000:.0f}ms, {size} bytes)")
    print(f"  browser launch cost         {elapsed:6.1f}s on the first request")
    return {"healthy_s": round(healthy, 2), "first_pdf_s": round(first_pdf, 2),
            "first_render_s": round(elapsed, 2)}


def slope(xs, ys):
    """Least-squares slope; MB per render."""
    n = len(xs)
    if n < 2:
        return 0.0
    mx, my = sum(xs) / n, sum(ys) / n
    denom = sum((x - mx) ** 2 for x in xs)
    return 0.0 if denom == 0 else sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / denom


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--renders", type=int, default=10000)
    ap.add_argument("--concurrency", type=int, default=2)
    ap.add_argument("--sample-every", type=int, default=100)
    ap.add_argument("--skip-cold-start", action="store_true")
    args = ap.parse_args()

    if sh(["docker", "inspect", CONTAINER])[0] != 0:
        sys.exit(f"{CONTAINER} is not running — start it with: docker compose up -d api")

    print(f"Soak gate — {args.renders} renders at concurrency {args.concurrency}\n")
    cold = None if args.skip_cold_start else cold_start()

    names = list(FIXTURES)
    latencies, errors, samples = [], [], []
    started = time.time()
    completed = 0

    print(f"\nSoak — sampling memory every {args.sample_every} renders")
    with futures.ThreadPoolExecutor(max_workers=args.concurrency) as pool:
        pending = set()
        issued = 0
        while completed < args.renders:
            while len(pending) < args.concurrency and issued < args.renders:
                pending.add(pool.submit(render, names[issued % len(names)]))
                issued += 1
            done, pending = futures.wait(pending, return_when=futures.FIRST_COMPLETED)
            for f in done:
                elapsed, size, err = f.result()
                completed += 1
                latencies.append(elapsed)
                if err:
                    errors.append((completed, err))
                if completed % args.sample_every == 0:
                    mb = rss_mb()
                    if mb is not None:
                        samples.append((completed, mb))
                    rate = completed / (time.time() - started)
                    print(f"  {completed:6}/{args.renders}  {mb:7.1f} MB  "
                          f"{elapsed*1000:6.0f}ms  {rate:5.1f}/s  errors={len(errors)}",
                          flush=True)

    duration = time.time() - started
    tenth = max(1, len(latencies) // 10)
    first_lat = statistics.median(latencies[:tenth])
    last_lat = statistics.median(latencies[-tenth:])
    ordered = sorted(latencies)

    print("\n" + "=" * 78)
    print(f"{completed} renders in {duration/60:.1f} min "
          f"({completed/duration:.1f}/s, {len(errors)} errors)")
    print(f"latency  median {statistics.median(latencies)*1000:.0f}ms  "
          f"p95 {ordered[int(len(ordered)*0.95)]*1000:.0f}ms  "
          f"p99 {ordered[int(len(ordered)*0.99)]*1000:.0f}ms  "
          f"max {max(latencies)*1000:.0f}ms")

    failures = []
    error_rate = len(errors) / completed if completed else 1
    if error_rate > 0.001:
        failures.append(f"error rate {error_rate:.2%} exceeds 0.1%")
    for at, err in errors[:5]:
        print(f"  error at render {at}: {err}")

    drift = (last_lat - first_lat) / first_lat if first_lat else 0
    print(f"latency drift  first tenth {first_lat*1000:.0f}ms -> "
          f"last tenth {last_lat*1000:.0f}ms  ({drift:+.1%})")
    if drift > 0.50:
        failures.append(f"latency degraded {drift:.0%} across the run")

    leak = None
    if len(samples) >= 10:
        decile = max(1, len(samples) // 10)
        early = statistics.median(m for _, m in samples[:decile])
        late = statistics.median(m for _, m in samples[-decile:])
        per_render = slope([c for c, _ in samples], [m for _, m in samples])
        growth = (late - early) / early if early else 0
        print(f"memory  first decile {early:.1f} MB -> last decile {late:.1f} MB "
              f"({growth:+.1%}), trend {per_render*1000:+.3f} MB per 1000 renders")
        leak = {"early_mb": round(early, 1), "late_mb": round(late, 1),
                "growth_pct": round(growth * 100, 2),
                "mb_per_1000_renders": round(per_render * 1000, 4)}
        # A browser recycle every 50 renders means memory sawtooths; the question is
        # whether the floor rises. 25% across the run with a positive trend is a leak.
        if growth > 0.25 and per_render > 0:
            failures.append(f"memory grew {growth:.0%} with a positive trend — probable leak")

    record = {"renders": completed, "concurrency": args.concurrency,
              "duration_s": round(duration, 1), "errors": len(errors),
              "error_rate": round(error_rate, 5),
              "latency_ms": {"median": round(statistics.median(latencies) * 1000),
                             "p95": round(ordered[int(len(ordered) * 0.95)] * 1000),
                             "p99": round(ordered[int(len(ordered) * 0.99)] * 1000),
                             "max": round(max(latencies) * 1000)},
              "latency_drift_pct": round(drift * 100, 2),
              "cold_start": cold, "memory": leak,
              "samples": [{"render": c, "mb": round(m, 1)} for c, m in samples]}
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    (EVIDENCE / "soak-gate.json").write_text(json.dumps(record, indent=2) + "\n")

    print("-" * 78)
    if failures:
        print("soak gate: FAILED")
        for f in failures:
            print(f"  {f}")
        sys.exit(1)
    print("soak gate: PASSED")


if __name__ == "__main__":
    main()
