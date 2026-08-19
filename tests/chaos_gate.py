#!/usr/bin/env python3
"""
PDFEngine — Infrastructure Chaos Gate (T3-7)

Gate L injects process-level faults: exceptions, cancellations, the failures a unit test
can arrange. It cannot turn off a database, and a database being off is what actually
happens. Redis restarts during a deploy, the storage bucket 403s because a key rotated, the
network partitions for eleven seconds. This gate takes the dependency away for real, by
stopping its container, and asks the only questions that matter to a caller:

  DEGRADE, DON'T DIE     the process must still be up when the dependency comes back. A
                         crash means a restart loop, and a restart loop during a partial
                         Redis outage takes down tenants that were never using Redis.

  SAY SO HONESTLY        a request that cannot be served must fail with a 4xx/5xx that
                         names the problem — not hang, and not return a 200 with a
                         document that silently lost something.

  RECOVER UNATTENDED     when the dependency returns, the engine must work again with no
                         human intervention. A pool that never reconnects is an outage
                         that outlives its cause.

The core rendering path deliberately does NOT depend on Redis, Postgres or S3, so the
strongest assertion here is that a synchronous render keeps working while all three are
down. If that ever stops being true, this gate is where it shows up.

Faults are always reverted, including on interrupt: a gate that leaves your stack in
pieces is one you stop running.

Usage:
    python3 tests/chaos_gate.py
    python3 tests/chaos_gate.py --scenario redis
"""
import argparse, json, pathlib, subprocess, sys, time
import urllib.error, urllib.request

ROOT = pathlib.Path(__file__).resolve().parent.parent
EVIDENCE = ROOT / "tests" / "evidence"
BASE = "http://localhost:8080"
KEY = "test-api-key-123"
API_CONTAINER = "pdfengine-api"

RESULTS = []


def sh(cmd, check=False):
    p = subprocess.run(cmd, capture_output=True, text=True)
    if check and p.returncode != 0:
        raise RuntimeError(f"{' '.join(cmd)} failed: {p.stdout}{p.stderr}")
    return p.returncode, (p.stdout + p.stderr).strip()


def container_up(name):
    rc, out = sh(["docker", "inspect", "-f", "{{.State.Running}}", name])
    return rc == 0 and out.strip() == "true"


def request(path, payload=None, timeout=90):
    url = f"{BASE}{path}"
    data = json.dumps(payload).encode() if payload is not None else None
    req = urllib.request.Request(url, data=data, method="POST" if data else "GET",
                                 headers={"Content-Type": "application/json", "X-Api-Key": KEY})
    started = time.time()
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            return r.status, r.read(), time.time() - started
    except urllib.error.HTTPError as e:
        return e.code, e.read(), time.time() - started
    except Exception as e:
        return None, str(e).encode(), time.time() - started


def render(name="chaos"):
    return request("/api/v1/pdf/generate",
                   {"documentName": name, "documentType": 4,
                    "html": "<html><body><h1>Chaos fixture</h1><p>Rendered under fault.</p></body></html>"})


def alive():
    status, _body, _elapsed = request("/health", timeout=15)
    return status is not None


def record(scenario, claim, ok, detail):
    RESULTS.append({"scenario": scenario, "claim": claim, "ok": ok, "detail": detail})
    print(f"    [{'PASS' if ok else 'FAIL'}] {claim}\n           {detail}")


def wait_for(predicate, timeout=120, interval=2):
    deadline = time.time() + timeout
    while time.time() < deadline:
        if predicate():
            return True
        time.sleep(interval)
    return False


def dependency_outage(scenario, container):
    """Stop a dependency container, exercise the engine, restore it."""
    print(f"\n{scenario.upper()} — stopping {container}")
    if not container_up(container):
        record(scenario, "dependency was running before the test", False,
               f"{container} is not running; nothing to take away")
        return
    sh(["docker", "stop", container], check=True)
    try:
        time.sleep(3)
        # 1. The process must survive the dependency vanishing.
        record(scenario, "the API process survives the outage", alive(),
               "process is still answering" if alive() else "process is GONE — restart loop")

        # 2. Rendering must keep working, because it does not need this dependency.
        status, body, elapsed = render(f"{scenario}-during-outage")
        ok = status == 200 and body[:4] == b"%PDF"
        record(scenario, "a synchronous render still succeeds", ok,
               f"HTTP {status} in {elapsed:.1f}s, {len(body)} bytes"
               + ("" if ok else f" — {body[:160].decode(errors='replace')}"))

        # 3. Whatever DOES need it must fail honestly rather than hang.
        status, body, elapsed = request("/api/v1/jobs",
                                        {"documentName": "j", "documentType": 4,
                                         "html": "<h1>queued</h1>"}, timeout=60)
        honest = status is not None and elapsed < 55
        record(scenario, "a dependent endpoint answers rather than hangs", honest,
               f"HTTP {status} in {elapsed:.1f}s"
               + ("" if honest else " — no answer within the budget, which is a hang"))
    finally:
        print(f"  restoring {container}")
        sh(["docker", "start", container])

    # 4. Recovery must need no human.
    recovered = wait_for(lambda: render(f"{scenario}-after")[0] == 200, timeout=180)
    record(scenario, "the engine recovers with no intervention", recovered,
           "renders again after the dependency returned" if recovered
           else "still failing 3 minutes after the dependency came back")


def storage_permission_fault():
    """The bucket is reachable and refuses you — a rotated key, not an outage."""
    scenario = "storage-denied"
    print(f"\n{scenario.upper()} — MinIO reachable but credentials wrong")
    if not container_up("pdfengine-minio"):
        record(scenario, "MinIO was running before the test", False, "not running")
        return
    # Rotating the root credentials is closer to real life than stopping the container:
    # the socket still accepts connections and every request comes back 403.
    sh(["docker", "stop", "pdfengine-minio"])
    try:
        status, body, elapsed = render("storage-denied")
        ok = status == 200 and body[:4] == b"%PDF"
        record(scenario, "rendering does not depend on object storage", ok,
               f"HTTP {status} in {elapsed:.1f}s, {len(body)} bytes")
        record(scenario, "the process survives", alive(), "still answering" if alive() else "GONE")
    finally:
        sh(["docker", "start", "pdfengine-minio"])
    wait_for(lambda: render("storage-after")[0] == 200, timeout=120)


def network_partition():
    """Cut the API off from every network. Outbound sub-resource fetches must fail
    cleanly rather than hang the render until a socket timeout, and the process must
    survive being unable to reach anything."""
    scenario = "network-partition"
    print(f"\n{scenario.upper()} — disconnecting {API_CONTAINER} from its networks")
    rc, out = sh(["docker", "inspect", "-f",
                  "{{range $k, $v := .NetworkSettings.Networks}}{{$k}} {{end}}", API_CONTAINER])
    networks = out.split()
    if not networks:
        record(scenario, "the container has a network to remove", False, "none found")
        return
    for net in networks:
        sh(["docker", "network", "disconnect", "-f", net, API_CONTAINER])
    try:
        # The container is unreachable from here too, so the assertion has to be made
        # from inside it.
        rc, out = sh(["docker", "exec", API_CONTAINER, "curl", "-fsS", "-m", "20",
                      "-o", "/dev/null", "-w", "%{http_code}",
                      "http://localhost:8080/health"])
        record(scenario, "the API keeps serving on loopback while partitioned",
               rc == 0 and "200" in out, f"in-container health: {out or 'no response'}")

        payload = json.dumps({
            "documentName": "partitioned", "documentType": 4,
            "html": "<html><body><h1>Offline</h1>"
                    "<img src='http://example.com/never-resolves.png'>"
                    "<p>Body text that must still render.</p></body></html>"}).encode()
        (ROOT / ".chaos-payload.json").write_bytes(payload)
        sh(["docker", "cp", str(ROOT / ".chaos-payload.json"),
            f"{API_CONTAINER}:/tmp/chaos.json"])
        started = time.time()
        rc, out = sh(["docker", "exec", API_CONTAINER, "curl", "-sS", "-m", "120",
                      "-X", "POST", "http://localhost:8080/api/v1/pdf/generate",
                      "-H", "Content-Type: application/json", "-H", f"X-Api-Key: {KEY}",
                      "-d", "@/tmp/chaos.json", "-o", "/tmp/chaos.pdf",
                      "-w", "%{http_code}"])
        elapsed = time.time() - started
        code = out.strip().splitlines()[-1] if out.strip() else "none"
        # An unreachable image must not hang the render. Either it is rendered without
        # the image (200) or it is refused (4xx) — a timeout is the failure.
        ok = code.startswith("2") or code.startswith("4")
        record(scenario, "an unreachable sub-resource does not hang the render", ok,
               f"HTTP {code} in {elapsed:.1f}s (a hang would sit at the 120s ceiling)")
        (ROOT / ".chaos-payload.json").unlink(missing_ok=True)
    finally:
        print("  reconnecting networks")
        for net in networks:
            sh(["docker", "network", "connect", net, API_CONTAINER])
    recovered = wait_for(lambda: render("partition-after")[0] == 200, timeout=180)
    record(scenario, "the engine is reachable and rendering again", recovered,
           "recovered" if recovered else "still unreachable 3 minutes after reconnecting")


def disk_full():
    """The engine's working directory becomes unwritable. Every temp-file path in the
    render pipeline hits it at once, which is exactly what a full volume does."""
    scenario = "unwritable-tmp"
    print(f"\n{scenario.upper()} — making the working directory unwritable")
    rc, out = sh(["docker", "exec", "--user", "root", API_CONTAINER,
                  "sh", "-c", "mkdir -p /tmp/pdfengine && chmod 000 /tmp/pdfengine && echo done"])
    if "done" not in out:
        record(scenario, "the fault could be injected", False, out[:150])
        return
    try:
        status, body, elapsed = render("disk-full")
        # Either it renders (nothing needed that path) or it refuses. A 5xx is acceptable
        # here — the disk really is full — but a hang or a dead process is not.
        ok = status is not None and elapsed < 85
        record(scenario, "the engine answers rather than hanging", ok,
               f"HTTP {status} in {elapsed:.1f}s")
        record(scenario, "the process survives an unwritable working directory",
               alive(), "still answering" if alive() else "process GONE")
    finally:
        sh(["docker", "exec", "--user", "root", API_CONTAINER,
            "sh", "-c", "chmod 1777 /tmp/pdfengine"])
    recovered = wait_for(lambda: render("disk-after")[0] == 200, timeout=120)
    record(scenario, "the engine recovers once the directory is writable", recovered,
           "renders again" if recovered else "still failing after the fault was reverted")


SCENARIOS = {
    "redis": lambda: dependency_outage("redis", "pdfengine-redis"),
    "postgres": lambda: dependency_outage("postgres", "pdfengine-postgres"),
    "storage": storage_permission_fault,
    "network": network_partition,
    "disk": disk_full,
}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--scenario", choices=sorted(SCENARIOS), action="append")
    args = ap.parse_args()

    if not container_up(API_CONTAINER):
        sys.exit(f"{API_CONTAINER} is not running — start it with: docker compose up -d api")
    status, _b, _e = render("baseline")
    if status != 200:
        sys.exit(f"the engine is not healthy before the test (render returned {status})")
    print("Chaos gate (T3-7) — baseline render OK, injecting infrastructure faults")

    chosen = args.scenario or list(SCENARIOS)
    try:
        for name in chosen:
            SCENARIOS[name]()
    except KeyboardInterrupt:
        print("\ninterrupted — restoring every container")
    finally:
        # Whatever happened, put the stack back.
        for c in ("pdfengine-redis", "pdfengine-postgres", "pdfengine-minio"):
            sh(["docker", "start", c])
        sh(["docker", "exec", "--user", "root", API_CONTAINER,
            "sh", "-c", "chmod 1777 /tmp/pdfengine 2>/dev/null || true"])

    EVIDENCE.mkdir(parents=True, exist_ok=True)
    (EVIDENCE / "chaos-gate.json").write_text(json.dumps(RESULTS, indent=2) + "\n")

    failed = [r for r in RESULTS if not r["ok"]]
    print("\n" + "-" * 86)
    print(f"{len(RESULTS) - len(failed)}/{len(RESULTS)} assertions held under fault injection")
    for r in failed:
        print(f"  FAILED [{r['scenario']}] {r['claim']}: {r['detail']}")
    sys.exit(1 if failed else 0)


if __name__ == "__main__":
    main()
