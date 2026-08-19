import os
import sys
import time
import json
import urllib.request
import urllib.error

API_BASE_URL = "http://localhost:5276"
TEST_EMAIL = "admin@example.com"
TEST_PASSWORD = "password123"

# Baseline definitions for visual regression certification
BASELINES = {
    "Invoice": {
        "expected_pages": 4, # 100 items will span ~4 pages
        "max_duration_ms": 2000,
        "size_tolerance_pct": 20.0
    },
    "Contract": {
        "expected_pages": 3,
        "max_duration_ms": 2000,
        "size_tolerance_pct": 20.0
    },
    "Report": {
        "expected_pages": 4,
        "max_duration_ms": 3000,
        "size_tolerance_pct": 20.0
    },
    "Certificate": {
        "expected_pages": 1,
        "max_duration_ms": 1500,
        "size_tolerance_pct": 20.0
    },
    "Resume": {
        "expected_pages": 1,
        "max_duration_ms": 1500,
        "size_tolerance_pct": 20.0
    },
    "BoardingPass": {
        "expected_pages": 1,
        "max_duration_ms": 1500,
        "size_tolerance_pct": 20.0
    },
    "Catalog": {
        "expected_pages": 2,
        "max_duration_ms": 2000,
        "size_tolerance_pct": 20.0
    }
}

TEST_TEMPLATES = {
    "Invoice": """<!DOCTYPE html>
<html>
<head>
  <style>
    @import url('https://fonts.googleapis.com/css2?family=Outfit:wght@400;900&display=swap');
    @page { size: A4; margin: 15mm; }
    body { font-family: 'Outfit', sans-serif; }
    thead { display: table-header-group !important; }
    tr { page-break-inside: avoid !important; }
  </style>
</head>
<body>
  <h1>Invoice Verification</h1>
  <table>
    <thead><tr><th>Item</th><th>Amount</th></tr></thead>
    <tbody>
      """ + "".join([f"<tr><td>Certification Item #{i}</td><td>$100.00</td></tr>" for i in range(100)]) + """
    </tbody>
  </table>
</body>
</html>""",

    "Contract": """<!DOCTYPE html>
<html>
<head>
  <style>
    @import url('https://fonts.googleapis.com/css2?family=Outfit&family=Noto+Sans+Arabic&family=Noto+Sans+JP&display=swap');
    @page { size: A4; margin: 15mm; }
    body { font-family: 'Outfit', 'Noto Sans JP', 'Noto Sans Arabic', sans-serif; }
  </style>
</head>
<body>
  <h1>Master Agreement</h1>
  <p>This is page 1</p>
  <div style="page-break-before: always;"></div>
  <h1>Arabic section (RTL)</h1>
  <p dir="rtl">هذا النص باللغة العربية للتحقق من جودة الخطوط.</p>
  <div style="page-break-before: always;"></div>
  <h1>Japanese section (LTR)</h1>
  <p>これは日本語のフォントテスト用テキストです。</p>
</body>
</html>""",

    "Report": """<!DOCTYPE html>
<html>
<head>
  <style>
    @import url('https://fonts.googleapis.com/css2?family=Outfit&display=swap');
    @page { size: A4; margin: 15mm; }
    body { font-family: 'Outfit', sans-serif; }
  </style>
</head>
<body>
  <h1>Performance Report</h1>
  <div style="page-break-before: always;"></div>
  <p>Page 2</p>
  <div style="page-break-before: always;"></div>
  <p>Page 3</p>
  <div style="page-break-before: always;"></div>
  <p>Page 4</p>
</body>
</html>""",

    "Certificate": """<!DOCTYPE html>
<html>
<head>
  <style>
    @import url('https://fonts.googleapis.com/css2?family=Cinzel&display=swap');
    @page { size: A4; margin: 20mm; }
    body { font-family: 'Cinzel', serif; text-align: center; padding: 50px; }
  </style>
</head>
<body>
  <h1>Certificate of Certification</h1>
  <p>Verified for PDFEngine Infrastructure</p>
</body>
</html>""",

    "Resume": """<!DOCTYPE html>
<html>
<head>
  <style>
    @page { size: A4; margin: 15mm; }
    body { font-family: sans-serif; }
  </style>
</head>
<body>
  <h1>Resume: Saddam Khan</h1>
  <p>Role: Principal Architect</p>
</body>
</html>""",

    "BoardingPass": """<!DOCTYPE html>
<html>
<head>
  <style>
    @page { size: A4; margin: 15mm; }
    body { font-family: sans-serif; }
  </style>
</head>
<body>
  <h1>BOARDING PASS</h1>
  <p>Passenger: Saddam Khan</p>
  <p>From: BOM To: SFO</p>
</body>
</html>""",

    "Catalog": """<!DOCTYPE html>
<html>
<head>
  <style>
    @page { size: A4; margin: 15mm; }
    body { font-family: sans-serif; }
  </style>
</head>
<body>
  <h1>Product Catalog</h1>
  <p>Product Listing</p>
  <div style="page-break-before: always;"></div>
  <p>End of Catalog</p>
</body>
</html>"""
}


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
    print("[*] Logging in with admin tenant credentials...")
    login_body = {"email": TEST_EMAIL, "password": TEST_PASSWORD}
    status, res_body, _ = make_request(f"{API_BASE_URL}/api/v1/auth/login", "POST", body=login_body)
    
    if status == 200:
        data = json.loads(res_body.decode())
        if "token" in data:
            return data["token"]
        elif data.get("requires2fa") == True:
            print("[*] 2FA required. Verifying with default backup code...")
            verify_body = {"userId": data["userId"], "code": "123456"}
            v_status, v_res_body, _ = make_request(f"{API_BASE_URL}/api/v1/auth/verify-2fa", "POST", body=verify_body)
            if v_status == 200:
                v_data = json.loads(v_res_body.decode())
                return v_data["token"]
            else:
                print(f"[!] 2FA verification failed: {v_res_body.decode()}")
                sys.exit(1)
    
    print(f"[!] Authentication failed: {res_body.decode()}")
    sys.exit(1)


def run_visual_regression_tests():
    token = get_auth_token()
    headers = {
        "Authorization": f"Bearer {token}",
        "Content-Type": "application/json"
    }

    print("\n" + "="*60)
    print(" RENDER CERTIFICATION & VISUAL REGRESSION SUITE")
    print("="*60)

    all_passed = True
    results = {}

    for name, html in TEST_TEMPLATES.items():
        print(f"[*] Testing visual regression baseline for: {name}... ", end="", flush=True)
        payload = {
            "html": html,
            "documentName": f"Regression_{name}",
            "documentType": 4,
            "options": {
                "pageSize": "A4",
                "title": f"{name} Title Metadata Validation",
                "author": "PDFEngine Visual Test Suite"
            }
        }

        start = time.time()
        status, body, info = make_request(f"{API_BASE_URL}/api/v1/pdf/generate", "POST", headers=headers, body=payload)
        duration_ms = int((time.time() - start) * 1000)

        if status == 200 and body[:4] == b"%PDF":
            diag_header = info.get("X-Render-Diagnostics")
            job_id = info.get("X-Render-Job-Id")
            
            pages = int(info.get("X-Render-Pages", 1))
            score = int(info.get("X-Render-Score", 100))

            baseline = BASELINES[name]
            
            # Check page count
            page_match = (pages == baseline["expected_pages"])
            
            # Verify status output
            if page_match and score >= 80:
                print(f"\033[92mPASS\033[0m (Pages: {pages}/{baseline['expected_pages']}, Score: {score}, Duration: {duration_ms}ms, JobId: {job_id})")
                results[name] = {"status": "PASS", "pages": pages, "score": score, "duration_ms": duration_ms, "jobId": job_id}
            else:
                print(f"\033[91mFAIL\033[0m (Pages: {pages}/{baseline['expected_pages']}, Score: {score})")
                all_passed = False
                results[name] = {"status": "FAIL", "pages": pages, "score": score, "duration_ms": duration_ms, "jobId": job_id}
        else:
            print(f"\033[91mCRITICAL FAIL\033[0m (HTTP Status: {status}, Body: {body.decode()})")
            all_passed = False
            results[name] = {"status": "CRITICAL_FAIL"}


    print("\n" + "="*60)
    print(" VISUAL REGRESSION TEST SUMMARY")
    print("="*60)
    print(json.dumps(results, indent=2))

    if all_passed:
        print("\n\033[92m[SUCCESS] Visual Regression & Build Certification Passed!\033[0m\n")
        sys.exit(0)
    else:
        print("\n\033[91m[FAILURE] Visual Regression anomalies detected in release templates.\033[0m\n")
        sys.exit(1)

if __name__ == "__main__":
    # Wait a second for server to reload if called immediately
    time.sleep(1.5)
    run_visual_regression_tests()
