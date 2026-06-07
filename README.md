# PdfEngine API - Enterprise SaaS Rendering Engine

PdfEngine is a production-grade, horizontally scalable microservice built to convert raw HTML into high-fidelity PDF documents using headless Chromium (Playwright). 

Unlike basic wrapper libraries, PdfEngine is architected as a **multi-tenant SaaS backend**, featuring strict resource governance, distributed asynchronous queueing, and robust observability.

---

## 🚀 Project Evolution & Features

This project was systematically built through 7 distinct architectural phases to ensure enterprise-grade reliability.

### Phase 1: Core Domain & Clean Architecture
- Established a strict **Clean Architecture** (Domain -> Application -> Infrastructure -> API).
- Implemented **CQRS** using MediatR to cleanly separate Request/Response logic from business operations.

### Phase 2: Headless Engine (Playwright)
- Integrated Microsoft **Playwright** as the underlying rendering engine.
- Implemented robust lifecycle management (browser pooling, context isolation per request).
- Guaranteed pixel-perfect rendering of modern CSS, Flexbox, WebFonts, and dynamic JavaScript.

### Phase 3: The SaaS Guard Layer
- **API Key Authentication**: Middleware explicitly guards all endpoints and resolves requests to specific `ApiClients` (Tenants).
- **Validation Pipeline**: FluentValidation intercepts bad payloads, enforcing strict rules (e.g., HTML payload size limits) to protect engine memory.
- **Rate Limiting**: Sliding Window rate limits prevent API spam and abuse on a per-tenant basis.
- **SSRF Prevention**: Implemented guards against Server-Side Request Forgery by disabling network access to local/private IP ranges within the headless browser.

### Phase 4: Extreme Concurrency & Resource Governance
- **Global Semaphore Limits**: Hard-capped the maximum number of concurrent Playwright renders to prevent CPU starvation and out-of-memory (OOM) crashes.
- **Tenant Isolation**: Introduced a secondary Tenant-Level `SemaphoreSlim`. If the system allows 4 global renders, a single tenant can only consume a maximum of 2 slots, ensuring "noisy neighbors" cannot degrade the service for other users.
- **Cancellation Safety**: Deep integration with `.NET CancellationTokens` allows the system to instantly abort Playwright renders if a client disconnects, freeing up expensive render slots immediately.

### Phase 5 & 6: Observability (The Control Plane)
- **Metrics (Prometheus)**: Exposes real-time telemetry on `/metrics` (`pdf_requests_total`, `pdf_generation_duration_ms`, active renders, and queue length).
- **Structured Logging (Serilog)**: All logs are output as Queryable JSON, automatically enriched with `TenantName`, `JobId`, and `TraceId` for effortless distributed tracing.
- **Proactive Health Checks**: The `/health` endpoint directly pings the internal Chromium processes, allowing external Load Balancers to route traffic away if the local browser engine crashes.

### Phase 7: True Asynchronous Scaling (Queue & Worker)
- **Non-Blocking API**: API endpoints instantly return `202 Accepted` with a `JobId`, completely eliminating HTTP connection timeouts.
- **Distributed Redis Queue (`StackExchange.Redis`)**: Jobs are serialized and pushed to a robust Redis List, preventing job loss during application restarts.
- **Background Workers (`IHostedService`)**: Background .NET workers poll the Redis queue and process PDFs completely out-of-band from the HTTP threads. This allows for massive **Horizontal Scaling** (you can run 50 worker containers simultaneously against one Redis instance).

---

## 🛠️ Getting Started

### Prerequisites
- .NET 8 SDK
- Redis (Docker: `docker run -d -p 6379:6379 redis`)

### Running the API
```bash
cd src/PdfEngine.API
dotnet build
dotnet run
```

### Usage (The Asynchronous Flow)

**1. Submit a Job**
```bash
curl -X POST http://localhost:5276/api/pdf/jobs \
  -H "X-API-KEY: test-key-123" \
  -H "Content-Type: application/json" \
  -d '{"html":"<h1>Hello World</h1>","documentName":"test"}'
```
*Returns `202 Accepted` and `jobId`.*

**2. Poll Job Status**
```bash
curl -H "X-API-KEY: test-key-123" http://localhost:5276/api/pdf/jobs/{jobId}
```
*Returns `{ "status": "Completed", "fileUrl": "/api/pdf/jobs/{jobId}/download" }`*

**3. Download PDF**
```bash
curl -H "X-API-KEY: test-key-123" http://localhost:5276/api/pdf/jobs/{jobId}/download -o result.pdf
```
