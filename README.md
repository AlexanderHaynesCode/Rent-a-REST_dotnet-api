# Rent-a-RESTaurant .NET API

Multi-tenant .NET 10 Web API foundation for a restaurant SaaS.

## What this includes

- Tenant resolution by:
  - custom domain host
  - `/r/{slug}` path format
  - `X-Tenant-Slug` request header
  - `/api/public/restaurants/{slug}` path format
- Tenant-scoped EF Core query filters for all tenant-bound entities.
- Public read endpoint for restaurant pages.
- Admin write endpoints for branding, menu, and hours.
- System provisioning endpoint for onboarding tenants.
- Stripe webhook endpoint to trigger provisioning after checkout.
- Local media namespace provisioning in tenant-scoped folder paths.

## Project layout

- `RentARestaurant.slnx`
- `RentARestaurant.Api`

## Run locally

```powershell
cd .\RentARestaurant.Api
dotnet run --urls http://localhost:5000
```

Health check:

```text
GET http://localhost:5000/health
```

## Seeded demo tenant

On first run, the database is created and seeded with:

- slug: `beef-store`
- admin header user: `owner-demo`

Use these headers for admin calls:

```text
X-Tenant-Slug: beef-store
X-Admin-User-Id: owner-demo
```

## Key endpoints

- `GET /api/public/restaurants/{slug}`
- `GET /api/admin/restaurant`
- `PUT /api/admin/restaurant/branding`
- `PUT /api/admin/restaurant/hours`
- `POST /api/admin/restaurant/menu/categories`
- `POST /api/admin/restaurant/menu/items`
- `POST /api/system/provisioning/tenants`
- `POST /api/stripe/webhooks`

Provisioning request payloads require `subscriptionPlan` with one of:

- `Self-Service`
- `Done-For-You`

## Config

Main settings are in:

- `RentARestaurant.Api/appsettings.json`
- `RentARestaurant.Api/appsettings.Development.json`

Important keys:

- `ConnectionStrings:DefaultConnection`
- `TenantResolution:PlatformHosts`
- `LocalMediaStorage:RootPath`
- `Stripe:WebhookSigningSecret`

## Next hardening steps

- Replace header-based admin identity with JWT auth.
- Verify real Stripe signatures using Stripe's SDK.
- Add migrations and switch connection string to Railway PostgreSQL.
- Add request validation and integration tests.
- Add idempotency store for webhook event IDs.


## Useful notes about the system

The 'status' column in the agent_submissions table in PostgreSQL is an int from 0 to 6, which corresponds with the AgentSubmissionStatus enum values in order:

public enum AgentSubmissionStatus
{
    Received,
    Translated,
    Validated,
    NeedsClarification,
    Applied,
    Rejected,
    Failed
}
