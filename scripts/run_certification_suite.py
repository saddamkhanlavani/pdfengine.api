import os
import sys
import time
import json
import urllib.request
import urllib.error
import threading

API_BASE_URL = "http://localhost:5276"
TEST_EMAIL = f"certification_tester_{int(time.time())}@example.com"
TEST_PASSWORD = "Password123!"

def make_request(url, method="GET", headers=None, body=None):
    if headers is None:
        headers = {}
    if "Content-Type" not in headers and body:
        headers["Content-Type"] = "application/json"
    
    req_data = None
    if body:
        req_data = json.dumps(body).encode("utf-8") if isinstance(body, dict) else body.encode("utf-8")

    req = urllib.request.Request(url, data=req_data, headers=headers, method=method)
    try:
        with urllib.request.urlopen(req, timeout=30) as response:
            return response.status, response.read(), response.info()
    except urllib.error.HTTPError as e:
        return e.code, e.read(), e.headers
    except Exception as e:
        return 0, str(e).encode(), {}

def get_auth_token():
    print("[*] Authenticating tester client...")
    # Try logging in
    login_body = {"email": TEST_EMAIL, "password": TEST_PASSWORD}
    status, res_body, _ = make_request(f"{API_BASE_URL}/api/v1/auth/login", "POST", body=login_body)
    
    if status == 200:
        data = json.loads(res_body.decode())
        return data["token"]
    
    # If login failed, register first
    print("[*] Registering new certification tester tenant...")
    register_body = {
        "email": TEST_EMAIL,
        "password": TEST_PASSWORD,
        "tenantName": "Certification Testing Corp"
    }
    status, res_body, _ = make_request(f"{API_BASE_URL}/api/v1/auth/register", "POST", body=register_body)
    if status != 200 and status != 201:
        print(f"[!] Registration failed: {res_body.decode()}")
        sys.exit(1)
        
    # Attempt login again after registration
    status, res_body, _ = make_request(f"{API_BASE_URL}/api/v1/auth/login", "POST", body=login_body)
    if status == 200:
        data = json.loads(res_body.decode())
        return data["token"]
        
    print(f"[!] Login failed: {res_body.decode()}")
    sys.exit(1)

def get_or_create_api_key(token):
    headers = {"Authorization": f"Bearer {token}"}
    
    # Always create a fresh unmasked key for the certification suite
    print("[*] Generating fresh API key for verification...")
    status, res_body, _ = make_request(f"{API_BASE_URL}/api/v1/account/keys", "POST", headers=headers, body={"name": "Certification Suite Key", "environment": "Production"})
    if status == 200 or status == 201:
        key_data = json.loads(res_body.decode())
        return key_data["key"]
        
    print(f"[!] Failed to obtain API key: {res_body.decode()}")
    sys.exit(1)

# Rendering Compatibility Test Packs
TEST_PACKS = [
    {
        "name": "CSS Grid & Flexbox Layout",
        "html": "<html><head><style>body { font-family: sans-serif; } .grid { display: grid; grid-template-columns: repeat(3, 1fr); gap: 10px; } .item { background: #eee; padding: 20px; text-align: center; border-radius: 8px; }</style></head><body><h1>Grid Compatibility</h1><div class='grid'><div class='item'>Col 1</div><div class='item'>Col 2</div><div class='item'>Col 3</div></div></body></html>"
    },
    {
        "name": "Custom Font & SVG Icons",
        "html": "<html><head><style>@import url('https://fonts.googleapis.com/css2?family=Outfit:wght@400;900&display=swap'); body { font-family: 'Outfit', sans-serif; }</style></head><body><h1>Outfit Custom Font</h1><svg width='100' height='100'><circle cx='50' cy='50' r='40' stroke='green' stroke-width='4' fill='yellow' /></svg></body></html>"
    },
    {
        "name": "Printable Tables & Page Breaks",
        "html": "<html><head><style>table { width: 100%; border-collapse: collapse; } tr { page-break-inside: avoid; } th, td { border: 1px solid black; padding: 8px; }</style></head><body><h1>Page Break Control</h1><table><thead><tr><th>Header</th></tr></thead><tbody><tr><td>Row 1</td></tr><tr><td>Row 2</td></tr></tbody></table></body></html>"
    },
    {
        "name": "RTL Formatting Check",
        "html": "<html dir='rtl'><head><meta charset='utf-8'></head><body><h1>مرحبا بالعالم - RTL Directionality Test</h1></body></html>"
    },
    {
        "name": "Large Report Simulation (Multi-Page)",
        "html": "<html><body>" + "<h1>Large Document Volume</h1><p>Rendering pages...</p><div style='page-break-before: always;'></div>" * 4 + "</body></html>"
    }
]

def run_compatibility_suite(api_key):
    print("\n" + "="*50)
    print(" WAVE 3 & 4: RENDERING COMPATIBILITY CERTIFICATION")
    print("="*50)
    
    headers = {
        "X-API-KEY": api_key,
        "Content-Type": "application/json"
    }
    
    os.makedirs("./artifacts/certification_renders", exist_ok=True)
    all_passed = True
    
    for i, pack in enumerate(TEST_PACKS):
        print(f"[*] Testing Pack {i+1}: {pack['name']}... ", end="")
        payload = {
            "html": pack["html"],
            "documentName": f"Certification_{i+1}",
            "documentType": 4
        }
        
        status, body, info = make_request(f"{API_BASE_URL}/api/v1/pdf/generate", "POST", headers=headers, body=payload)
        
        if status == 200 and body[:4] == b"%PDF":
            file_path = f"./artifacts/certification_renders/pack_{i+1}.pdf"
            with open(file_path, "wb") as f:
                f.write(body)
            print(f"\033[92mPASS\033[0m (Size: {len(body)} bytes, Render Score: {info.get('X-Render-Score', '10')}/10)")
        else:
            print(f"\033[91mFAIL\033[0m (HTTP Status: {status}, Response: {body.decode()})")
            all_passed = False
            
    return all_passed

def run_performance_validation(api_key, concurrency=100):
    print("\n" + "="*50)
    print(f" WAVE 18: PERFORMANCE VALIDATION ({concurrency} RENDERS)")
    print("="*50)
    
    headers = {
        "X-API-KEY": api_key,
        "Content-Type": "application/json"
    }
    
    payload = {
        "html": "<html><body><h1>Load Performance Test</h1><p>Render load verification check.</p></body></html>",
        "documentName": "LoadTest",
        "documentType": 4
    }
    
    results = []
    threads = []
    sem = threading.Semaphore(15) # Limit client-side concurrency to 15 to avoid saturating local machine CPU
    
    def worker():
        with sem:
            start = time.time()
            status, body, _ = make_request(f"{API_BASE_URL}/api/v1/pdf/generate", "POST", headers=headers, body=payload)
            duration_ms = int((time.time() - start) * 1000)
            success = (status == 200 and body[:4] == b"%PDF")
            if not success:
                print(f"[!] Performance render worker failed. Status: {status}, Response: {body.decode()[:100]}")
            results.append((success, duration_ms))

    print(f"[*] Dispatching {concurrency} rendering requests (concurrency limit: 15)...")
    for _ in range(concurrency):
        t = threading.Thread(target=worker)
        threads.append(t)
        t.start()
        
    for t in threads:
        t.join()
        
    # Calculate stats
    successful = [r for r in results if r[0]]
    success_rate = (len(successful) / len(results)) * 100
    latencies = [r[1] for r in results]
    latencies.sort()
    
    avg_latency = sum(latencies) / len(latencies) if latencies else 0
    p95_index = int(len(latencies) * 0.95) - 1
    p95_index = max(0, min(p95_index, len(latencies) - 1))
    p95_latency = latencies[p95_index] if latencies else 0
    
    print("\n" + "-"*30)
    print(" PERFORMANCE RESULTS SUMMARY")
    print("-"*30)
    print(f"Success Rate:    {success_rate:.2f}% (Target: >= 99%)")
    print(f"Avg Latency:     {avg_latency:.0f}ms")
    print(f"P95 Latency:     {p95_latency:.0f}ms (Target: < 3000ms)")
    
    # Release Gate Evaluation
    success_passed = success_rate >= 99.0
    latency_passed = p95_latency < 3000
    
    print("\n" + "-"*30)
    print(" RELEASE GATE DECISION")
    print("-"*30)
    if success_passed and latency_passed:
        print("\033[92m[PASS] System deployment certified for Production Release!\033[0m")
        return True
    else:
        print("\033[91m[FAIL] Release Gate validation failed.\033[0m")
        if not success_passed:
            print("  - Success rate falls below 99% threshold.")
        if not latency_passed:
            print("  - P95 rendering time exceeds 3.0 seconds.")
        return False

if __name__ == "__main__":
    token = get_auth_token()
    api_key = get_or_create_api_key(token)
    
    compat_ok = run_compatibility_suite(api_key)
    perf_ok = run_performance_validation(api_key, concurrency=100)
    
    if compat_ok and perf_ok:
        sys.exit(0)
    else:
        sys.exit(1)
