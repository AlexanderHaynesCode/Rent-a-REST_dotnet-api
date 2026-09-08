# PostgreSQL Database Schema Migration Guide

## Overview
This document details the database schema for the Rent-a-Restaurant API, mapping the current SQLite/EF Core structure to PostgreSQL.

---

## Type Mappings

| C# Type | SQLite | PostgreSQL |
|---------|--------|-----------|
| `Guid` | BLOB | UUID |
| `string` (unbounded) | TEXT | TEXT or VARCHAR |
| `string` (nullable) | TEXT NULL | TEXT NULL or VARCHAR NULL |
| `decimal` | REAL | NUMERIC(10,2) |
| `bool` | INTEGER | BOOLEAN |
| `DateTime` (UTC) | TEXT | TIMESTAMP WITH TIME ZONE |
| `TimeOnly` | TEXT | TIME |
| `int` | INTEGER | INTEGER |
| `DayOfWeek` / status enums | INTEGER | INTEGER (default EF mapping; no value converter configured) |

---

## Table Relationships Diagram

```
Tenant (1) ──────── (1) RestaurantProfile
  │
  ├──── (1:N) TenantUser
  ├──── (1:N) MenuCategory ──── (1:N) MenuItem
  └──── (1:N) BusinessHour
```

---

## SQL Creation Queries

### Prerequisites: Enable UUID Extension
```sql
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";
```

---

### 1. Tenants Table

```sql
CREATE TABLE tenants (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name VARCHAR(200) NOT NULL,
    slug VARCHAR(120) NOT NULL UNIQUE,
    custom_domain VARCHAR(255),
    is_active BOOLEAN NOT NULL DEFAULT true,
    subscription_plan VARCHAR(50) NOT NULL DEFAULT 'Self-Service',
    subscription_state VARCHAR(50) NOT NULL DEFAULT 'active',
    created_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    -- Indexes for common queries
    CONSTRAINT tenant_name_not_empty CHECK (name != ''),
    CONSTRAINT tenant_slug_not_empty CHECK (slug != '')
);

CREATE UNIQUE INDEX idx_tenants_custom_domain ON tenants(custom_domain);
CREATE INDEX idx_tenants_is_active ON tenants(is_active);
CREATE INDEX idx_tenants_created_utc ON tenants(created_utc);
```

**Note:** `custom_domain` needs a unique index because `Tenant.CustomDomain` is configured with `HasIndex(x => x.CustomDomain).IsUnique()` in `AppDbContext`. Postgres unique indexes allow multiple `NULL` values, so tenants without a custom domain are unaffected.

---

### 2. RestaurantProfiles Table

```sql
CREATE TABLE restaurant_profiles (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL UNIQUE,
    display_name VARCHAR(200) NOT NULL,
    tagline TEXT NOT NULL DEFAULT '',
    primary_hex_color VARCHAR(12) NOT NULL DEFAULT '#222222',
    secondary_hex_color VARCHAR(12) NOT NULL DEFAULT '#adadad',
    logo_url TEXT,
    hero_image_url TEXT,
    primary_cta_url TEXT,
    updated_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    CONSTRAINT display_name_not_empty CHECK (display_name != ''),
    CONSTRAINT hex_color_format_primary CHECK (primary_hex_color ~ '^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$'),
    CONSTRAINT hex_color_format_secondary CHECK (secondary_hex_color ~ '^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$')
);

CREATE INDEX idx_restaurant_profiles_updated_utc ON restaurant_profiles(updated_utc);
```

**Note:** `RestaurantProfile.PrimaryHexColor`/`SecondaryHexColor` are configured with `HasMaxLength(12)` in `AppDbContext`, so `VARCHAR(7)` was too tight and would reject any EF-validated value longer than 7 chars. Widened to 12 and the CHECK now also accepts 8-digit hex (with alpha channel).

---

### 3. TenantUsers Table

```sql
CREATE TABLE tenant_users (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    external_user_id VARCHAR(255) NOT NULL,
    email VARCHAR(320) NOT NULL,
    role VARCHAR(50) NOT NULL DEFAULT 'Owner',
    created_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    UNIQUE(tenant_id, external_user_id),
    CONSTRAINT email_not_empty CHECK (email != ''),
    CONSTRAINT external_user_id_not_empty CHECK (external_user_id != '')
);

CREATE UNIQUE INDEX idx_tenant_users_external_user_id ON tenant_users(external_user_id);
CREATE INDEX idx_tenant_users_tenant_id ON tenant_users(tenant_id);
CREATE INDEX idx_tenant_users_email ON tenant_users(email);
CREATE INDEX idx_tenant_users_created_utc ON tenant_users(created_utc);
```

**Notes:**
- `email` widened to `VARCHAR(320)` to match `HasMaxLength(320)` in `AppDbContext` (RFC 5321 max email length); `VARCHAR(255)` would reject valid long addresses the app allows.
- `external_user_id` needs its own **global** unique index (not just the `(tenant_id, external_user_id)` composite) because `AppDbContext` also configures `HasIndex(x => x.ExternalUserId).IsUnique()`. This means the same external user id cannot currently belong to more than one tenant — confirm that's intentional before recreating the DB.

---

### 4. MenuCategories Table

```sql
CREATE TABLE menu_categories (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    name VARCHAR(120) NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    UNIQUE(tenant_id, name),
    CONSTRAINT name_not_empty CHECK (name != '')
);

CREATE INDEX idx_menu_categories_tenant_id ON menu_categories(tenant_id);
CREATE INDEX idx_menu_categories_sort_order ON menu_categories(tenant_id, sort_order);
```

**Note:** Added `UNIQUE(tenant_id, name)` to match `HasIndex(x => new { x.TenantId, x.Name }).IsUnique()` in `AppDbContext` — without it, the DB would silently allow duplicate category names per tenant.

---

### 5. MenuItems Table

```sql
CREATE TABLE menu_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    category_id UUID NOT NULL,
    name VARCHAR(140) NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    price NUMERIC(10, 2) NOT NULL,
    is_available BOOLEAN NOT NULL DEFAULT true,
    sort_order INTEGER NOT NULL DEFAULT 0,
    
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    FOREIGN KEY (category_id) REFERENCES menu_categories(id) ON DELETE CASCADE,
    CONSTRAINT name_not_empty CHECK (name != ''),
    CONSTRAINT price_positive CHECK (price >= 0)
);

CREATE INDEX idx_menu_items_tenant_id ON menu_items(tenant_id);
CREATE INDEX idx_menu_items_category_id ON menu_items(category_id);
CREATE INDEX idx_menu_items_is_available ON menu_items(is_available);
CREATE INDEX idx_menu_items_sort_order ON menu_items(category_id, sort_order);
```

---

### 6. BusinessHours Table

```sql
CREATE TABLE business_hours (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    day_of_week INTEGER NOT NULL,
    open_time TIME NOT NULL,
    close_time TIME NOT NULL,
    is_closed BOOLEAN NOT NULL DEFAULT false,
    date DATE,
    
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    UNIQUE(tenant_id, day_of_week, date),
    CONSTRAINT day_of_week_valid CHECK (day_of_week >= 0 AND day_of_week <= 6),
    CONSTRAINT close_time_after_open CHECK (close_time > open_time OR is_closed = true)
);

CREATE INDEX idx_business_hours_tenant_id ON business_hours(tenant_id);
```

**Note on `day_of_week`:** Uses 0-6 mapping (0=Sunday, 1=Monday, ..., 6=Saturday) matching .NET's `DayOfWeek` enum.

**Note on `date`:** `BusinessHour.Date` is a nullable `DateOnly?` used for date-specific overrides (e.g. holiday hours), and the unique index in `AppDbContext` is on `(TenantId, DayOfWeek, Date)`, not just `(TenantId, DayOfWeek)`. This section previously omitted the `date` column entirely, which didn't match the entity and would have broken inserts.

---

## Alternative: Using PostgreSQL Enum for DayOfWeek

> **Not currently used by the app.** `AppDbContext` does not configure a value converter for `BusinessHour.DayOfWeek`, so EF Core maps it to a plain `integer` column. Only use this alternative if you also add `HasConversion<string>()` (or similar) to the entity configuration — otherwise EF will fail to read/write a native Postgres enum column.

If you prefer type safety, create a custom enum type:

```sql
CREATE TYPE day_of_week_enum AS ENUM (
    'Sunday',
    'Monday',
    'Tuesday',
    'Wednesday',
    'Thursday',
    'Friday',
    'Saturday'
);

-- Then modify business_hours table:
CREATE TABLE business_hours (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    day_of_week day_of_week_enum NOT NULL,
    open_time TIME NOT NULL,
    close_time TIME NOT NULL,
    is_closed BOOLEAN NOT NULL DEFAULT false,
    date DATE,
    
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    UNIQUE(tenant_id, day_of_week),
    CONSTRAINT close_time_after_open CHECK (close_time > open_time OR is_closed = true)
);

CREATE INDEX idx_business_hours_tenant_id ON business_hours(tenant_id);
```

---

## Complete Migration Script

Run this in order to create the entire schema:

```sql
-- Step 1: Enable UUID extension
CREATE EXTENSION IF NOT EXISTS "uuid-ossp";

-- Step 2: Create base table (Tenants)
CREATE TABLE tenants (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    name VARCHAR(200) NOT NULL,
    slug VARCHAR(120) NOT NULL UNIQUE,
    custom_domain VARCHAR(255),
    is_active BOOLEAN NOT NULL DEFAULT true,
    subscription_plan VARCHAR(50) NOT NULL DEFAULT 'Self-Service',
    subscription_state VARCHAR(50) NOT NULL DEFAULT 'active',
    created_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT tenant_name_not_empty CHECK (name != ''),
    CONSTRAINT tenant_slug_not_empty CHECK (slug != '')
);

CREATE UNIQUE INDEX idx_tenants_custom_domain ON tenants(custom_domain);
CREATE INDEX idx_tenants_is_active ON tenants(is_active);
CREATE INDEX idx_tenants_created_utc ON tenants(created_utc);

-- Step 3: Create dependent tables
CREATE TABLE restaurant_profiles (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL UNIQUE,
    display_name VARCHAR(200) NOT NULL,
    tagline TEXT NOT NULL DEFAULT '',
    primary_hex_color VARCHAR(12) NOT NULL DEFAULT '#222222',
    secondary_hex_color VARCHAR(12) NOT NULL DEFAULT '#ffffff',
    logo_url TEXT,
    hero_image_url TEXT,
    primary_cta_url TEXT,
    updated_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    CONSTRAINT display_name_not_empty CHECK (display_name != ''),
    CONSTRAINT hex_color_format_primary CHECK (primary_hex_color ~ '^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$'),
    CONSTRAINT hex_color_format_secondary CHECK (secondary_hex_color ~ '^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$')
);

CREATE INDEX idx_restaurant_profiles_updated_utc ON restaurant_profiles(updated_utc);

CREATE TABLE tenant_users (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    external_user_id VARCHAR(255) NOT NULL,
    email VARCHAR(320) NOT NULL,
    role VARCHAR(50) NOT NULL DEFAULT 'Owner',
    created_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    UNIQUE(tenant_id, external_user_id),
    CONSTRAINT email_not_empty CHECK (email != ''),
    CONSTRAINT external_user_id_not_empty CHECK (external_user_id != '')
);

CREATE UNIQUE INDEX idx_tenant_users_external_user_id ON tenant_users(external_user_id);
CREATE INDEX idx_tenant_users_tenant_id ON tenant_users(tenant_id);
CREATE INDEX idx_tenant_users_email ON tenant_users(email);
CREATE INDEX idx_tenant_users_created_utc ON tenant_users(created_utc);

CREATE TABLE menu_categories (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    name VARCHAR(120) NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    UNIQUE(tenant_id, name),
    CONSTRAINT name_not_empty CHECK (name != '')
);

CREATE INDEX idx_menu_categories_tenant_id ON menu_categories(tenant_id);
CREATE INDEX idx_menu_categories_sort_order ON menu_categories(tenant_id, sort_order);

CREATE TABLE menu_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    category_id UUID NOT NULL,
    name VARCHAR(140) NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    price NUMERIC(10, 2) NOT NULL,
    is_available BOOLEAN NOT NULL DEFAULT true,
    sort_order INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    FOREIGN KEY (category_id) REFERENCES menu_categories(id) ON DELETE CASCADE,
    CONSTRAINT name_not_empty CHECK (name != ''),
    CONSTRAINT price_positive CHECK (price >= 0)
);

CREATE INDEX idx_menu_items_tenant_id ON menu_items(tenant_id);
CREATE INDEX idx_menu_items_category_id ON menu_items(category_id);
CREATE INDEX idx_menu_items_is_available ON menu_items(is_available);
CREATE INDEX idx_menu_items_sort_order ON menu_items(category_id, sort_order);

CREATE TABLE business_hours (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    day_of_week INTEGER NOT NULL,
    open_time TIME NOT NULL,
    close_time TIME NOT NULL,
    is_closed BOOLEAN NOT NULL DEFAULT false,    
    date DATE,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    UNIQUE(tenant_id, day_of_week, date),
    CONSTRAINT day_of_week_valid CHECK (day_of_week >= 0 AND day_of_week <= 6),
    CONSTRAINT close_time_after_open CHECK (close_time > open_time OR is_closed = true)
);

CREATE INDEX idx_business_hours_tenant_id ON business_hours(tenant_id);
```

---

## Entity Framework Core Connection String

Once you've created the PostgreSQL database, update your `appsettings.json`:

```json
{
  "ConnectionStrings": {
    "DefaultConnection": "Host=your-host;Database=your-db;Username=your-user;Password=your-password;"
  }
}
```

And update your DbContext configuration in `Program.cs`:

```csharp
// From:
services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=app.db"));

// To:
services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(configuration.GetConnectionString("DefaultConnection")));
```

**NuGet Package Required:**
```bash
dotnet add package Npgsql.EntityFrameworkCore.PostgreSQL
```

---

## Notes

1. **Timestamps**: All `DateTime` fields use `TIMESTAMP WITH TIME ZONE` to preserve UTC context. EF Core will handle conversion.
2. **Constraints**: Added CHECK constraints to match Entity Framework validation and prevent invalid data.
3. **Indexes**: Created on foreign keys, frequently queried columns, and sort orders for optimal query performance.
4. **Cascading Deletes**: All foreign keys use `ON DELETE CASCADE` to maintain referential integrity when tenants are deleted.
5. **Uniqueness**: `restaurant_profiles` has a unique constraint on `tenant_id` (1:1 relationship), `menu_categories` has a unique constraint on `(tenant_id, name)`, `tenant_users` has a unique constraint on both `external_user_id` alone and `(tenant_id, external_user_id)`, `tenants.custom_domain` has a unique index, and `business_hours` has a unique constraint on `(tenant_id, day_of_week, date)`.

---

## Data Migration Considerations

If you have existing SQLite data, you'll need to:
1. Export from SQLite (e.g., using `sqlite3` CLI or a tool)
2. Transform GUIDs (SQLite stores as BLOB) to UUID format for PostgreSQL
3. Transform datetime strings if they're not ISO 8601 format
4. Use a migration tool or ETL script to load into PostgreSQL

Consider using tools like:
- **DBeaver**: Import/Export wizard
- **pgAdmin**: CSV import
- **Custom script**: Write a .NET console app to read SQLite and insert to PostgreSQL

---

## Agent System Tables (Phase 0 addition)

Two additional tables support the AI agent pipeline (agent-intake-service, agent-validator-service,
agent-executor-service). There is no EF Core Migrations project in this repo, so run this SQL
manually against the target Postgres database (same as the tables above).

```sql
CREATE TABLE agent_submissions (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    channel VARCHAR(20) NOT NULL,
    sender_identifier VARCHAR(320) NOT NULL,
    raw_body_text TEXT,
    attachment_refs TEXT,
    status INTEGER NOT NULL DEFAULT 0,
    translated_json TEXT,
    validator_confidence DOUBLE PRECISION,
    rejection_reason VARCHAR(1000),
    received_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_utc TIMESTAMP WITH TIME ZONE,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE
);

CREATE INDEX idx_agent_submissions_tenant_status ON agent_submissions(tenant_id, status);

CREATE TABLE agent_change_audits (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    submission_id UUID NOT NULL,
    pre_change_snapshot_json TEXT NOT NULL,
    diff_summary_json TEXT NOT NULL,
    applied_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    rollback_expires_utc TIMESTAMP WITH TIME ZONE NOT NULL,
    rolled_back BOOLEAN NOT NULL DEFAULT false,
    rolled_back_utc TIMESTAMP WITH TIME ZONE,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    FOREIGN KEY (submission_id) REFERENCES agent_submissions(id) ON DELETE RESTRICT
);

CREATE INDEX idx_agent_change_audits_tenant_rollback ON agent_change_audits(tenant_id, rollback_expires_utc);
```

**Note on `status`:** Matches the C# `AgentSubmissionStatus` enum ordinal (0=Received, 1=Translated,
2=Validated, 3=NeedsClarification, 4=Applied, 5=Rejected, 6=Failed).
