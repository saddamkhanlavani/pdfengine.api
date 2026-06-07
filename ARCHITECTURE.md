# PdfEngine SaaS Platform - Architecture & Context

This document is the **Single Source of Truth** for the `PdfEngine` project. It is intended to provide rapid context to any AI agents, developers, or maintainers jumping into this project to reduce token usage and improve iteration speed.

## Overview
PdfEngine is a production-grade, multi-tenant SaaS application that converts HTML to PDF using a rendering cluster (Playwright/Puppeteer). It offers a beautiful, dynamic, highly-interactive frontend dashboard where users can manage their API keys, team roles, billing limits, usage history, and security settings (like 2FA).

## Tech Stack
- **Backend:** C# (.NET 8)
- **Architecture:** Clean Architecture (Domain, Application, Infrastructure, API)
- **Database:** Entity Framework Core (PostgreSQL in prod, currently SQLite for testing)
- **Frontend:** Next.js 14+ (App Router), React, Tailwind CSS
- **Styling:** Vanilla Tailwind CSS with complex gradients, blur effects (`backdrop-blur-xl`), and dark mode aesthetics (`bg-[#050510]`, `bg-slate-950`).
- **Animations:** Framer Motion (`<motion.div>`, `AnimatePresence`).
- **Icons:** Lucide React.
- **Authentication:** JWT Bearer tokens for the Dashboard + API Key Authentication (`X-API-KEY`) for external rendering requests.

## Project Structure
- `/src/PdfEngine.API`: The main .NET Web API project. Contains controllers, middlewares, and program configuration.
- `/src/PdfEngine.Domain`: Contains the core Entities (`Tenant`, `User`, `ApiKey`, `UsageRecord`).
- `/src/PdfEngine.Infrastructure`: Contains the DbContext, services, and background workers.
- `/pdfengine.web`: The Next.js frontend dashboard.

## Core Concepts & Entities

### 1. Tenant (Organization)
The core billing and isolation entity. A Tenant represents a single company or workspace.
- Contains the `TwoFactorSecret` (TOTP), Notification settings, and Usage Limits.
- Stores the Stripe Subscription details.

### 2. User
A member of a Tenant. Allows Role-Based Access Control (RBAC).
- Contains `Email` and `PasswordHash`.
- Roles: `SuperAdmin`, `Admin`, `Developer`.

### 3. Authentication Flow
The system supports a hybrid authentication approach:
1. **Dashboard Login:** Users login via `POST /api/auth/login`. This verifies `User.Email` and `User.PasswordHash`. If the Tenant has 2FA enabled, it issues a temporary token and requires `POST /api/auth/verify-2fa`. Once fully authenticated, it issues a JWT Access Token.
2. **API Access:** The `ApiKeyMiddleware` protects endpoints. It checks if the `User.Identity.IsAuthenticated` is true (JWT success). If not, it falls back to checking the `X-API-KEY` header for external API consumers (e.g., when generating a PDF from an external server).

### 4. UI Philosophy & Guidelines
- **NEVER use browser alerts or prompts** (`window.prompt`, `window.alert`). ALWAYS use custom React Modals built with Framer Motion, or `react-hot-toast` for notifications.
- **Aesthetics First:** The design must look extremely premium. Use rounded corners (`rounded-2xl`, `rounded-[2.5rem]`), subtle borders (`border-slate-800`), and glow effects.
- **Micro-interactions:** Buttons should have hover effects, group hovers, and disabled states.

## Key Services
- `PdfEngineDbContext`: The EF Core context located in `PdfEngine.Infrastructure/Data`.
- `fetchApi(url, options)`: The frontend utility in `@/lib/api.ts` that automatically handles JWT injection and 401 redirects to `/login`. Use this for all dashboard fetch calls instead of raw `fetch()`.

## Common Tasks for Agents
- **Adding a new Setting:**
  1. Add the property to `Tenant.cs`.
  2. Update `AccountController.cs` `GetMe()` to return it, and the corresponding `Update` endpoint to save it.
  3. Update `SettingsPage.tsx` to bind the state and send the PUT/POST request using `fetchApi`.
- **Database Migrations:** Remember to run `dotnet ef migrations add <Name> -p src/PdfEngine.Infrastructure -s src/PdfEngine.API` if you change any models.
