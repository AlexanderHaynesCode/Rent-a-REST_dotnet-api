i# PostgreSQL Database Schema Migration Guide

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
| `DayOfWeek` (enum) | INTEGER | SMALLINT (or custom ENUM type) |

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
    name VARCHAR(255) NOT NULL,
    slug VARCHAR(255) NOT NULL UNIQUE,
    custom_domain VARCHAR(255),
    is_active BOOLEAN NOT NULL DEFAULT true,
    subscription_plan VARCHAR(50) NOT NULL DEFAULT 'Self-Service',
    subscription_state VARCHAR(50) NOT NULL DEFAULT 'active',
    created_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    -- Indexes for common queries
    CONSTRAINT tenant_name_not_empty CHECK (name != ''),
    CONSTRAINT tenant_slug_not_empty CHECK (slug != '')
);

CREATE INDEX idx_tenants_slug ON tenants(slug);
CREATE INDEX idx_tenants_is_active ON tenants(is_active);
CREATE INDEX idx_tenants_created_utc ON tenants(created_utc);
```

---

### 2. RestaurantProfiles Table

```sql
CREATE TABLE restaurant_profiles (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL UNIQUE,
    display_name VARCHAR(255) NOT NULL,
    tagline TEXT,
    primary_hex_color VARCHAR(7) NOT NULL DEFAULT '#222222',
    secondary_hex_color VARCHAR(7) NOT NULL DEFAULT '#ffffff',
    logo_url TEXT,
    hero_image_url TEXT,
    primary_cta_url TEXT,
    updated_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    CONSTRAINT display_name_not_empty CHECK (display_name != ''),
    CONSTRAINT hex_color_format_primary CHECK (primary_hex_color ~ '^#[0-9A-Fa-f]{6}$'),
    CONSTRAINT hex_color_format_secondary CHECK (secondary_hex_color ~ '^#[0-9A-Fa-f]{6}$')
);

CREATE INDEX idx_restaurant_profiles_tenant_id ON restaurant_profiles(tenant_id);
CREATE INDEX idx_restaurant_profiles_updated_utc ON restaurant_profiles(updated_utc);
```

---

### 3. TenantUsers Table

```sql
CREATE TABLE tenant_users (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    external_user_id VARCHAR(255) NOT NULL,
    email VARCHAR(255) NOT NULL,
    role VARCHAR(50) NOT NULL DEFAULT 'Owner',
    created_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    UNIQUE(tenant_id, external_user_id),
    CONSTRAINT email_not_empty CHECK (email != ''),
    CONSTRAINT external_user_id_not_empty CHECK (external_user_id != '')
);

CREATE INDEX idx_tenant_users_tenant_id ON tenant_users(tenant_id);
CREATE INDEX idx_tenant_users_external_user_id ON tenant_users(external_user_id);
CREATE INDEX idx_tenant_users_email ON tenant_users(email);
CREATE INDEX idx_tenant_users_created_utc ON tenant_users(created_utc);
```

---

### 4. MenuCategories Table

```sql
CREATE TABLE menu_categories (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    name VARCHAR(255) NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    CONSTRAINT name_not_empty CHECK (name != '')
);

CREATE INDEX idx_menu_categories_tenant_id ON menu_categories(tenant_id);
CREATE INDEX idx_menu_categories_sort_order ON menu_categories(tenant_id, sort_order);
```

---

### 5. MenuItems Table

```sql
CREATE TABLE menu_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    category_id UUID NOT NULL,
    name VARCHAR(255) NOT NULL,
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
    day_of_week SMALLINT NOT NULL,
    open_time TIME NOT NULL,
    close_time TIME NOT NULL,
    is_closed BOOLEAN NOT NULL DEFAULT false,
    
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    UNIQUE(tenant_id, day_of_week),
    CONSTRAINT day_of_week_valid CHECK (day_of_week >= 0 AND day_of_week <= 6),
    CONSTRAINT close_time_after_open CHECK (close_time > open_time OR is_closed = true)
);

CREATE INDEX idx_business_hours_tenant_id ON business_hours(tenant_id);
```

**Note on `day_of_week`:** Uses 0-6 mapping (0=Sunday, 1=Monday, ..., 6=Saturday) matching .NET's `DayOfWeek` enum.

---

## Alternative: Using PostgreSQL Enum for DayOfWeek

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
    name VARCHAR(255) NOT NULL,
    slug VARCHAR(255) NOT NULL UNIQUE,
    custom_domain VARCHAR(255),
    is_active BOOLEAN NOT NULL DEFAULT true,
    subscription_plan VARCHAR(50) NOT NULL DEFAULT 'Self-Service',
    subscription_state VARCHAR(50) NOT NULL DEFAULT 'active',
    created_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    CONSTRAINT tenant_name_not_empty CHECK (name != ''),
    CONSTRAINT tenant_slug_not_empty CHECK (slug != '')
);

CREATE INDEX idx_tenants_slug ON tenants(slug);
CREATE INDEX idx_tenants_is_active ON tenants(is_active);
CREATE INDEX idx_tenants_created_utc ON tenants(created_utc);

-- Step 3: Create dependent tables
CREATE TABLE restaurant_profiles (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL UNIQUE,
    display_name VARCHAR(255) NOT NULL,
    tagline TEXT,
    primary_hex_color VARCHAR(7) NOT NULL DEFAULT '#222222',
    secondary_hex_color VARCHAR(7) NOT NULL DEFAULT '#ffffff',
    logo_url TEXT,
    hero_image_url TEXT,
    primary_cta_url TEXT,
    updated_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    CONSTRAINT display_name_not_empty CHECK (display_name != ''),
    CONSTRAINT hex_color_format_primary CHECK (primary_hex_color ~ '^#[0-9A-Fa-f]{6}$'),
    CONSTRAINT hex_color_format_secondary CHECK (secondary_hex_color ~ '^#[0-9A-Fa-f]{6}$')
);

CREATE INDEX idx_restaurant_profiles_tenant_id ON restaurant_profiles(tenant_id);
CREATE INDEX idx_restaurant_profiles_updated_utc ON restaurant_profiles(updated_utc);

CREATE TABLE tenant_users (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    external_user_id VARCHAR(255) NOT NULL,
    email VARCHAR(255) NOT NULL,
    role VARCHAR(50) NOT NULL DEFAULT 'Owner',
    created_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    UNIQUE(tenant_id, external_user_id),
    CONSTRAINT email_not_empty CHECK (email != ''),
    CONSTRAINT external_user_id_not_empty CHECK (external_user_id != '')
);

CREATE INDEX idx_tenant_users_tenant_id ON tenant_users(tenant_id);
CREATE INDEX idx_tenant_users_external_user_id ON tenant_users(external_user_id);
CREATE INDEX idx_tenant_users_email ON tenant_users(email);
CREATE INDEX idx_tenant_users_created_utc ON tenant_users(created_utc);

CREATE TABLE menu_categories (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    name VARCHAR(255) NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    CONSTRAINT name_not_empty CHECK (name != '')
);

CREATE INDEX idx_menu_categories_tenant_id ON menu_categories(tenant_id);
CREATE INDEX idx_menu_categories_sort_order ON menu_categories(tenant_id, sort_order);

CREATE TABLE menu_items (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    category_id UUID NOT NULL,
    name VARCHAR(255) NOT NULL,
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
    day_of_week SMALLINT NOT NULL,
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
5. **Uniqueness**: `restaurant_profiles` has a unique constraint on `tenant_id` (1:1 relationship), and `business_hours` has a unique constraint on `(tenant_id, day_of_week)`.

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
