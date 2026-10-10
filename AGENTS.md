# Overview

This is the backend API for Rentaurants (originally known as Rent-a-RESTaurant). Rentaurants is a company that sells SaaS customizable restaurant websites.

Customers can check out our services and pricing at the main marketing site (marketing-client-app). They pay via Stripe and account management is handled by Clerk.

We offer two subscription tiers: Self-Service and Done-For-You. Both give the tenant access to the admin portal (admin-client-app). Self-Service tenants can manually update their restaurant site (restaurant-client-app) themselves via the admin portal. Done-For-You tenants get additional functionality (which is basically the flaghship feature of Rentaurants) that allow them to send email update requests to Rentaurants and our AI agent pipeline handles updating their site for them. A success/fail/clarification email is sent back to the tenant shortly after they initiate their request.

## Structure

- `dotnet-api/` contains the .NET 10 ASP.NET Core API and PostgreSQL persistence.
- `admin-client-app/`, `marketing-client-app/`, and `restaurant-client-app/` are separate Vite/React/TypeScript applications.
- AI agent pipeline sequence: agent-imap-adapter-service -> agent-intake-service -> agent-validator-service -> agent-executor-service.

## Important Conventions

- The API uses tenant context and global query filters for tenant-owned data. Preserve tenant scoping and the existing authorization attributes when changing endpoints or data access.
- The API schema is maintained as SQL documented in `dotnet-api/POSTGRESQL_MIGRATION.md`; this repository has no EF Core migrations project.
- Agent-service-to-API calls use the internal agent API key and tenant slug headers. Keep those service contracts aligned when changing either side.
- For Python agent services, use Python 3.12 for local validation when available.
- Before editing, inspect the owning module and nearby tests. After editing, run the narrowest relevant build, test, or type-check command.
- Do not add or expose credentials in source files. Keep secrets in local or deployment environment configuration.