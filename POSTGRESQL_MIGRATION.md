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
  ├──── (1:N) Menu ──── (1:N) MenuCategory ──── (1:N) MenuItem
    ├──── (1:N) BusinessHour
    └──── (1:N) ProvisioningSession
    └──── (1:1) TenantSubscription
    └──── (1:N) SubscriptionEvent
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

**Stripe integration columns:**

If you want to persist the Stripe customer and subscription identifiers on the tenant row, add these columns and indexes:

```sql
ALTER TABLE tenants
ADD COLUMN stripe_customer_id VARCHAR(100),
ADD COLUMN stripe_subscription_id VARCHAR(100);

CREATE UNIQUE INDEX idx_tenants_stripe_customer_id ON tenants(stripe_customer_id);
CREATE UNIQUE INDEX idx_tenants_stripe_subscription_id ON tenants(stripe_subscription_id);
```

**Note:** `custom_domain` needs a unique index because `Tenant.CustomDomain` is configured with `HasIndex(x => x.CustomDomain).IsUnique()` in `AppDbContext`. Postgres unique indexes allow multiple `NULL` values, so tenants without a custom domain are unaffected.

---

### 2. ProvisioningSessions Table

```sql
CREATE TABLE provisioning_sessions (
    id UUID PRIMARY KEY,
    checkout_session_id VARCHAR(255) UNIQUE,
    restaurant_name VARCHAR(200) NOT NULL,
    owner_email VARCHAR(320) NOT NULL,
    owner_external_user_id VARCHAR(255) NOT NULL,
    subscription_plan VARCHAR(50) NOT NULL,
    requested_slug VARCHAR(120),
    custom_domain VARCHAR(255),
    status VARCHAR(40) NOT NULL DEFAULT 'pending_payment',
    stripe_customer_id VARCHAR(100),
    stripe_subscription_id VARCHAR(100),
    tenant_id UUID UNIQUE,
    tenant_slug VARCHAR(120),
    failure_reason TEXT,
    created_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,

    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE SET NULL
);

CREATE INDEX idx_provisioning_sessions_status ON provisioning_sessions(status);
CREATE INDEX idx_provisioning_sessions_owner_email ON provisioning_sessions(owner_email);
```

This table is only for live Stripe provisioning. It lets the API map a Stripe Checkout session id back to the tenant signup record while the webhook finishes provisioning.

---

### 2b. TenantSubscriptions Table

Stores real Stripe subscription metadata. A row is written only after a successful signup (Stripe `checkout.session.completed` webhook), so a failed payment creates no row. Upgrades update the row immediately; downgrades are scheduled via a Stripe subscription schedule and recorded in `pending_plan` / `pending_plan_effective_utc` until the period ends. The row is kept in sync by the `customer.subscription.updated` / `customer.subscription.deleted` webhooks.

```sql
CREATE TABLE public.tenant_subscriptions (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL UNIQUE,
    stripe_customer_id VARCHAR(100) NOT NULL,
    stripe_subscription_id VARCHAR(100) NOT NULL UNIQUE,
    stripe_subscription_item_id VARCHAR(100),
    stripe_price_id VARCHAR(100) NOT NULL,
    plan VARCHAR(50) NOT NULL,
    status VARCHAR(40) NOT NULL,
    monthly_amount_cents INTEGER NOT NULL,
    currency CHAR(3) NOT NULL DEFAULT 'usd',
    onboarding_fee_cents INTEGER NOT NULL DEFAULT 0,
    onboarding_fee_paid_utc TIMESTAMP WITH TIME ZONE,
    current_period_start_utc TIMESTAMP WITH TIME ZONE,
    current_period_end_utc TIMESTAMP WITH TIME ZONE,
    cancel_at_period_end BOOLEAN NOT NULL DEFAULT false,
    canceled_utc TIMESTAMP WITH TIME ZONE,
    latest_invoice_id VARCHAR(100),
    trial_end_utc TIMESTAMP WITH TIME ZONE,
    pending_plan VARCHAR(50),
    pending_plan_effective_utc TIMESTAMP WITH TIME ZONE,
    previous_plan VARCHAR(50),
    plan_changed_utc TIMESTAMP WITH TIME ZONE,
    payment_failed_utc TIMESTAMP WITH TIME ZONE,
    grace_ends_utc TIMESTAMP WITH TIME ZONE,
    grace_reminder_sent_utc TIMESTAMP WITH TIME ZONE,
    created_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT tenant_subscriptions_plan_check CHECK (plan IN ('Self-Service', 'Done-For-You')),
    CONSTRAINT tenant_subscriptions_pending_plan_check CHECK (pending_plan IS NULL OR pending_plan IN ('Self-Service', 'Done-For-You')),
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE
);

CREATE INDEX idx_tenant_subscriptions_stripe_customer_id ON tenant_subscriptions(stripe_customer_id);
CREATE INDEX idx_tenant_subscriptions_status ON tenant_subscriptions(status);
```

---

### 2c. SubscriptionEvents Table

Append-only audit log of subscription changes per tenant. Rows are written by the API (signup, upgrade, downgrade requested/canceled, and the Stripe-driven downgrade start and cancellation events), never updated or deleted. Upgrades are logged only by the app's own upgrade completion, not by webhook syncs.

```sql
CREATE TABLE public.subscription_events (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL,
    event_type VARCHAR(40) NOT NULL,
    from_plan VARCHAR(50),
    to_plan VARCHAR(50),
    status VARCHAR(40),
    stripe_subscription_id VARCHAR(100),
    effective_utc TIMESTAMP WITH TIME ZONE,
    occurred_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,

    CONSTRAINT subscription_events_type_check CHECK (event_type IN (
        'signup', 'upgrade', 'downgrade_requested', 'downgrade_canceled',
        'downgrade_started', 'cancel_requested', 'cancel_reverted', 'canceled',
        'reactivated', 'payment_failed', 'suspended', 'payment_recovered')),
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE
);

CREATE INDEX idx_subscription_events_tenant_occurred ON subscription_events(tenant_id, occurred_utc);
```

`effective_utc` is when the change takes effect (e.g. the period end for `downgrade_requested` and `cancel_requested`).

**Failed-payment grace period (existing databases):** run this to add the grace columns and the new event types.

```sql
ALTER TABLE public.tenant_subscriptions
ADD COLUMN payment_failed_utc TIMESTAMP WITH TIME ZONE,
ADD COLUMN grace_ends_utc TIMESTAMP WITH TIME ZONE,
ADD COLUMN grace_reminder_sent_utc TIMESTAMP WITH TIME ZONE;

ALTER TABLE public.subscription_events DROP CONSTRAINT subscription_events_type_check;
ALTER TABLE public.subscription_events ADD CONSTRAINT subscription_events_type_check CHECK (event_type IN (
    'signup', 'upgrade', 'downgrade_requested', 'downgrade_canceled',
    'downgrade_started', 'cancel_requested', 'cancel_reverted', 'canceled',
    'reactivated', 'payment_failed', 'suspended', 'payment_recovered'));
```

When Stripe first marks a subscription `past_due`, the API sets `payment_failed_utc` and `grace_ends_utc` (7 days later) and never resets them while the episode lasts. Stripe's retry window (1 week, final action "unpaid") moves the subscription to `unpaid`, which suspends the tenant (`is_active = false`, `subscription_state = 'suspended'`). A later `active` status clears the episode and restores the tenant.

---

### 3. RestaurantProfiles Table

```sql
CREATE TABLE restaurant_profiles (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL UNIQUE,
    display_name VARCHAR(200) NOT NULL,
    tagline TEXT NOT NULL DEFAULT '',
    announcement TEXT NOT NULL DEFAULT '',
    primary_hex_color VARCHAR(12) NOT NULL DEFAULT '#222222',
    secondary_hex_color VARCHAR(12) NOT NULL DEFAULT '#adadad',
    logo_url TEXT,
    hero_image_url TEXT,
    primary_cta_url TEXT,
    address VARCHAR(300),
    phone_number VARCHAR(30),
    use_legacy_hours_style BOOLEAN NOT NULL DEFAULT FALSE,
    show_top_hours_section BOOLEAN NOT NULL DEFAULT TRUE,
    top_hours_style VARCHAR(20) NOT NULL DEFAULT 'standard',
    top_hours_open_status_detail VARCHAR(30) NOT NULL DEFAULT 'none',
    show_bottom_hours_section BOOLEAN NOT NULL DEFAULT TRUE,
    use_legacy_bottom_hours_style BOOLEAN NOT NULL DEFAULT FALSE,
    show_custom_background BOOLEAN NOT NULL DEFAULT FALSE,
    custom_background_hex_color_1 VARCHAR(12),
    custom_background_hex_color_2 VARCHAR(12),
    custom_background_position_1_x INTEGER NOT NULL DEFAULT 20,
    custom_background_position_1_y INTEGER NOT NULL DEFAULT 10,
    custom_background_position_2_x INTEGER NOT NULL DEFAULT 80,
    custom_background_position_2_y INTEGER NOT NULL DEFAULT 10,
    custom_background_main_hex_color VARCHAR(12),
    custom_background_main_color_active BOOLEAN NOT NULL DEFAULT TRUE,
    custom_background_splash_1_active BOOLEAN NOT NULL DEFAULT TRUE,
    custom_background_splash_2_active BOOLEAN NOT NULL DEFAULT TRUE,
    custom_background_image_url TEXT,
    custom_background_image_active BOOLEAN NOT NULL DEFAULT TRUE,
    allow_menu_pdf_download BOOLEAN NOT NULL DEFAULT TRUE,
    show_top_contact_section BOOLEAN NOT NULL DEFAULT FALSE,
    show_bottom_contact_section BOOLEAN NOT NULL DEFAULT TRUE,
    contact_hours_white_background BOOLEAN NOT NULL DEFAULT FALSE,
    contact_hours_white_background_transparency INTEGER NOT NULL DEFAULT 0,
    apply_transparency_to_menu_items BOOLEAN NOT NULL DEFAULT FALSE,
    hide_hero_image BOOLEAN NOT NULL DEFAULT FALSE,
    updated_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    CONSTRAINT display_name_not_empty CHECK (display_name != ''),
    CONSTRAINT hex_color_format_primary CHECK (primary_hex_color ~ '^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$'),
    CONSTRAINT hex_color_format_secondary CHECK (secondary_hex_color ~ '^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$'),
    CONSTRAINT hex_color_format_custom_background_1 CHECK (custom_background_hex_color_1 IS NULL OR custom_background_hex_color_1 ~ '^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$'),
    CONSTRAINT hex_color_format_custom_background_2 CHECK (custom_background_hex_color_2 IS NULL OR custom_background_hex_color_2 ~ '^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$'),
    CONSTRAINT custom_background_position_range CHECK (
        custom_background_position_1_x BETWEEN 0 AND 100 AND
        custom_background_position_1_y BETWEEN 0 AND 100 AND
        custom_background_position_2_x BETWEEN 0 AND 100 AND
        custom_background_position_2_y BETWEEN 0 AND 100),
    CONSTRAINT hex_color_format_custom_background_main CHECK (custom_background_main_hex_color IS NULL OR custom_background_main_hex_color ~ '^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$'),
    CONSTRAINT top_hours_style_values CHECK (top_hours_style IN ('standard', 'open-status')),
    CONSTRAINT top_hours_open_status_detail_values CHECK (top_hours_open_status_detail IN ('none', 'today', 'today-and-tomorrow')),
    CONSTRAINT contact_hours_white_background_transparency_range CHECK (contact_hours_white_background_transparency BETWEEN 0 AND 100)
);

CREATE INDEX idx_restaurant_profiles_updated_utc ON restaurant_profiles(updated_utc);
```

**Note:** `RestaurantProfile.PrimaryHexColor`/`SecondaryHexColor` are configured with `HasMaxLength(12)` in `AppDbContext`, so `VARCHAR(7)` was too tight and would reject any EF-validated value longer than 7 chars. Widened to 12 and the CHECK now also accepts 8-digit hex (with alpha channel).

**For existing databases, add the new headline column manually:**

```sql
ALTER TABLE restaurant_profiles
ADD COLUMN announcement TEXT NOT NULL DEFAULT '';
```

**For existing databases, add the optional address and phone number columns:**

```sql
ALTER TABLE restaurant_profiles
ADD COLUMN IF NOT EXISTS address VARCHAR(300),
ADD COLUMN IF NOT EXISTS phone_number VARCHAR(30);
```

**For existing databases, add the hours display style toggle (false = new hours CSS, true = legacy hours CSS):**

```sql
ALTER TABLE restaurant_profiles
ADD COLUMN IF NOT EXISTS use_legacy_hours_style BOOLEAN NOT NULL DEFAULT FALSE;
```

**For existing databases, add the bottom-of-page hours section settings (the second hours section shown beneath the menu on the restaurant site; `show_bottom_hours_section` defaults to TRUE so existing sites gain the section, and `use_legacy_bottom_hours_style` false = new hours CSS, true = legacy hours CSS). Run this before deploying the API, since EF selects both columns:**

```sql
ALTER TABLE restaurant_profiles
ADD COLUMN IF NOT EXISTS show_bottom_hours_section BOOLEAN NOT NULL DEFAULT TRUE,
ADD COLUMN IF NOT EXISTS use_legacy_bottom_hours_style BOOLEAN NOT NULL DEFAULT FALSE;
```

**For existing databases, add the top-of-page hours section settings (the hours section shown above the menu on the restaurant site). `show_top_hours_section` defaults to TRUE and `top_hours_style` defaults to `'standard'` (the full weekly hours list, styled by the existing `use_legacy_hours_style`), so existing sites look unchanged. `'open-status'` replaces the list with an "Open Now!"/"Closed" message, and `top_hours_open_status_detail` controls the lines beneath it: `'none'`, `'today'` (today's hours) or `'today-and-tomorrow'`. Run this before deploying the API, since EF selects all three columns:**

```sql
ALTER TABLE restaurant_profiles
ADD COLUMN IF NOT EXISTS show_top_hours_section BOOLEAN NOT NULL DEFAULT TRUE,
ADD COLUMN IF NOT EXISTS top_hours_style VARCHAR(20) NOT NULL DEFAULT 'standard',
ADD COLUMN IF NOT EXISTS top_hours_open_status_detail VARCHAR(30) NOT NULL DEFAULT 'none';

ALTER TABLE restaurant_profiles
ADD CONSTRAINT top_hours_style_values CHECK (top_hours_style IN ('standard', 'open-status')),
ADD CONSTRAINT top_hours_open_status_detail_values CHECK (top_hours_open_status_detail IN ('none', 'today', 'today-and-tomorrow'));
```

**For existing databases, add the custom background settings (two radial-gradient glow colors drawn over the restaurant site's default background; `show_custom_background` defaults to FALSE so existing sites keep the default background, and the colors stay stored while the toggle is off). Run this before deploying the API, since EF selects all three columns:**

```sql
ALTER TABLE restaurant_profiles
ADD COLUMN IF NOT EXISTS show_custom_background BOOLEAN NOT NULL DEFAULT FALSE,
ADD COLUMN IF NOT EXISTS custom_background_hex_color_1 VARCHAR(12),
ADD COLUMN IF NOT EXISTS custom_background_hex_color_2 VARCHAR(12);

ALTER TABLE restaurant_profiles
ADD CONSTRAINT hex_color_format_custom_background_1 CHECK (custom_background_hex_color_1 IS NULL OR custom_background_hex_color_1 ~ '^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$'),
ADD CONSTRAINT hex_color_format_custom_background_2 CHECK (custom_background_hex_color_2 IS NULL OR custom_background_hex_color_2 ~ '^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$');
```

**For existing databases, add the custom background glow positions (the `X% Y%` in each radial gradient's `circle at X% Y%`, set by the admin portal's Horizontal/Vertical sliders in steps of 10). Defaults are color 1 at 20% 10% and color 2 at 80% 10%. Run this before deploying the API, since EF selects all four columns:**

```sql
ALTER TABLE restaurant_profiles
ADD COLUMN IF NOT EXISTS custom_background_position_1_x INTEGER NOT NULL DEFAULT 20,
ADD COLUMN IF NOT EXISTS custom_background_position_1_y INTEGER NOT NULL DEFAULT 10,
ADD COLUMN IF NOT EXISTS custom_background_position_2_x INTEGER NOT NULL DEFAULT 80,
ADD COLUMN IF NOT EXISTS custom_background_position_2_y INTEGER NOT NULL DEFAULT 10;

ALTER TABLE restaurant_profiles
ADD CONSTRAINT custom_background_position_range CHECK (
    custom_background_position_1_x BETWEEN 0 AND 100 AND
    custom_background_position_1_y BETWEEN 0 AND 100 AND
    custom_background_position_2_x BETWEEN 0 AND 100 AND
    custom_background_position_2_y BETWEEN 0 AND 100);
```

**For existing databases, add the custom background layers (all only apply while `show_custom_background` is TRUE). Bottom to top on the restaurant site: the main background color, then the page background image (with a light readability overlay), then the two splash colors; the hero image sits above all of them. `custom_background_main_hex_color` NULL means the site's default near-white page color. The image URL is only set by the admin `images/custom-background` upload/delete endpoints, never by the branding PUT. Every `_active` flag defaults to TRUE, so existing sites with the custom background on look the same. Run this before deploying the API, since EF selects all six columns:**

```sql
ALTER TABLE restaurant_profiles
ADD COLUMN IF NOT EXISTS custom_background_main_hex_color VARCHAR(12),
ADD COLUMN IF NOT EXISTS custom_background_main_color_active BOOLEAN NOT NULL DEFAULT TRUE,
ADD COLUMN IF NOT EXISTS custom_background_splash_1_active BOOLEAN NOT NULL DEFAULT TRUE,
ADD COLUMN IF NOT EXISTS custom_background_splash_2_active BOOLEAN NOT NULL DEFAULT TRUE,
ADD COLUMN IF NOT EXISTS custom_background_image_url TEXT,
ADD COLUMN IF NOT EXISTS custom_background_image_active BOOLEAN NOT NULL DEFAULT TRUE;

ALTER TABLE restaurant_profiles
ADD CONSTRAINT hex_color_format_custom_background_main CHECK (custom_background_main_hex_color IS NULL OR custom_background_main_hex_color ~ '^#[0-9A-Fa-f]{6}([0-9A-Fa-f]{2})?$');
```

**For existing databases, add the Contact section placement settings (the restaurant site's address/phone Contact section, set by the admin portal's "Show top-of-page Contact section" and "Show bottom-of-page Contact section" checkboxes on the Branding page). The top section sits after the top-of-page hours section and before the menu; the bottom section is the last section on the page. Tenants can show either, both, or neither. `show_top_contact_section` defaults to FALSE and `show_bottom_contact_section` defaults to TRUE, so existing sites keep their single Contact section at the bottom. Either section still only appears while the tenant has an address or phone number. Run this before deploying the API, since EF selects both columns:**

```sql
ALTER TABLE restaurant_profiles
ADD COLUMN IF NOT EXISTS show_top_contact_section BOOLEAN NOT NULL DEFAULT FALSE,
ADD COLUMN IF NOT EXISTS show_bottom_contact_section BOOLEAN NOT NULL DEFAULT TRUE;
```

**For existing databases, add the Contact and Hours white background settings (set by the admin portal's "Add white background to Contact and Hours sections" checkbox and its transparency slider on the Branding page). When `contact_hours_white_background` is TRUE, every Contact and Hours section on the restaurant site (including the top-of-page "Open Now!"/"Closed" variant), plus the restaurant name and tagline under the hero and the top of the menu section (the "Menu" heading, PDF download button, and menu tabs), sits on a white card styled like the menu item cards. `contact_hours_white_background_transparency` only fades the card's white fill: 0 = solid white (the default, matching the menu item cards), 100 = fully see-through. The transparency is kept while the checkbox is off so re-enabling restores it.**

```sql
ALTER TABLE restaurant_profiles
ADD COLUMN IF NOT EXISTS contact_hours_white_background BOOLEAN NOT NULL DEFAULT FALSE,
ADD COLUMN IF NOT EXISTS contact_hours_white_background_transparency INTEGER NOT NULL DEFAULT 0;

ALTER TABLE restaurant_profiles
ADD CONSTRAINT contact_hours_white_background_transparency_range CHECK (contact_hours_white_background_transparency BETWEEN 0 AND 100);
```

**For existing databases, add the apply-transparency-to-menu-items setting (set by the admin portal's "Apply transparency setting to menu items" checkbox, nested under "Add white background to Contact and Hours sections" on the Branding page). When both `contact_hours_white_background` and `apply_transparency_to_menu_items` are TRUE, the restaurant site's menu item cards fade their white fill by `contact_hours_white_background_transparency` too. It defaults to FALSE, so menu item cards keep their solid white fill. The setting is kept while the white background checkbox is off so re-enabling restores it. Run this before deploying the API, since EF selects the column:**

```sql
ALTER TABLE restaurant_profiles
ADD COLUMN IF NOT EXISTS apply_transparency_to_menu_items BOOLEAN NOT NULL DEFAULT FALSE;
```

**For existing databases, add the hide hero image setting (set by the admin portal's "Hide hero image" checkbox on the Branding page). When `hide_hero_image` is TRUE, the restaurant site leaves out the hero image at the top of the page and shows the logo in line with the display name/tagline row instead of overlapping the hero. `hero_image_url` is left as is, so unchecking the box brings the uploaded hero image back.**

```sql
ALTER TABLE restaurant_profiles
ADD COLUMN IF NOT EXISTS hide_hero_image BOOLEAN NOT NULL DEFAULT FALSE;
```

---

### 4. TenantUsers Table

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

### 4b. Menus Table

A tenant can have up to 5 menus (e.g. "Lunch", "Dinner"); the cap is enforced by the API (`MenuLimits`), not the DB. Every tenant has at least one: provisioning creates a default menu named `Menu`, and the API refuses to delete a tenant's last menu. Hidden menus (`is_visible = FALSE`) and their categories are left out of the public storefront response.

```sql
CREATE TABLE menus (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    name VARCHAR(120) NOT NULL,
    description VARCHAR(300) NOT NULL DEFAULT '',
    sort_order INTEGER NOT NULL DEFAULT 0,
    is_visible BOOLEAN NOT NULL DEFAULT TRUE,

    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    UNIQUE(tenant_id, name),
    CONSTRAINT name_not_empty CHECK (name != '')
);

CREATE INDEX idx_menus_sort_order ON menus(tenant_id, sort_order);
```

**For existing databases, add multiple-menu support.** Every existing tenant gets one default menu named `Menu`, and all of its current categories are moved into it, so existing storefronts look the same. This also adds `restaurant_profiles.allow_menu_pdf_download` (the storefront's "Download Menu (PDF)" button; defaults to TRUE so existing sites keep the button). Run it in two steps around the API deploy:

**Step 1: run before deploying the API** (the new API selects `menus`, `menu_categories.menu_id` and `allow_menu_pdf_download`). `menu_id` stays nullable here so the old API can keep creating categories until the new one is live. Safe to re-run.

```sql
BEGIN;

CREATE TABLE IF NOT EXISTS menus (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    name VARCHAR(120) NOT NULL,
    description VARCHAR(300) NOT NULL DEFAULT '',
    sort_order INTEGER NOT NULL DEFAULT 0,
    is_visible BOOLEAN NOT NULL DEFAULT TRUE,

    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    UNIQUE(tenant_id, name),
    CONSTRAINT name_not_empty CHECK (name != '')
);

CREATE INDEX IF NOT EXISTS idx_menus_sort_order ON menus(tenant_id, sort_order);

-- One default "Menu" per tenant.
INSERT INTO menus (tenant_id, name, sort_order)
SELECT id, 'Menu', 0 FROM tenants
ON CONFLICT (tenant_id, name) DO NOTHING;

ALTER TABLE menu_categories
ADD COLUMN IF NOT EXISTS menu_id UUID REFERENCES menus(id) ON DELETE CASCADE;

-- Point every existing category at its tenant's default menu.
UPDATE menu_categories c
SET menu_id = m.id
FROM menus m
WHERE m.tenant_id = c.tenant_id AND m.name = 'Menu' AND c.menu_id IS NULL;

CREATE INDEX IF NOT EXISTS idx_menu_categories_menu_id ON menu_categories(menu_id, sort_order);

ALTER TABLE restaurant_profiles
ADD COLUMN IF NOT EXISTS allow_menu_pdf_download BOOLEAN NOT NULL DEFAULT TRUE;

COMMIT;
```

**Step 2: run right after the new API is live.** It catches any category the old API created between the two steps, makes `menu_id` required, and changes category-name uniqueness from per-tenant to per-menu ("Appetizers" can be on both Lunch and Dinner). The old constraint name below is PostgreSQL's default for `UNIQUE(tenant_id, name)`; if your DB named it differently, look it up with `\d menu_categories` first.

```sql
BEGIN;

UPDATE menu_categories c
SET menu_id = m.id
FROM menus m
WHERE m.tenant_id = c.tenant_id AND m.name = 'Menu' AND c.menu_id IS NULL;

ALTER TABLE menu_categories ALTER COLUMN menu_id SET NOT NULL;

ALTER TABLE menu_categories DROP CONSTRAINT IF EXISTS menu_categories_tenant_id_name_key;
ALTER TABLE menu_categories ADD CONSTRAINT menu_categories_menu_id_name_key UNIQUE (menu_id, name);

COMMIT;
```

---

### 5. MenuCategories Table

```sql
CREATE TABLE menu_categories (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    menu_id UUID NOT NULL,
    name VARCHAR(120) NOT NULL,
    description VARCHAR(300) NOT NULL DEFAULT '',
    sort_order INTEGER NOT NULL DEFAULT 0,

    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    FOREIGN KEY (menu_id) REFERENCES menus(id) ON DELETE CASCADE,
    UNIQUE(menu_id, name),
    CONSTRAINT name_not_empty CHECK (name != '')
);

CREATE INDEX idx_menu_categories_tenant_id ON menu_categories(tenant_id);
CREATE INDEX idx_menu_categories_sort_order ON menu_categories(tenant_id, sort_order);
CREATE INDEX idx_menu_categories_menu_id ON menu_categories(menu_id, sort_order);
```

**Note:** `UNIQUE(menu_id, name)` matches `HasIndex(x => new { x.MenuId, x.Name }).IsUnique()` in `AppDbContext`, so category names only need to be unique within a menu. (Before multiple menus it was `UNIQUE(tenant_id, name)`.) The API caps each tenant at 100 categories and 1,000 items (`MenuLimits`).

**For existing databases, add the optional category description column:**

```sql
ALTER TABLE menu_categories
ADD COLUMN description VARCHAR(300) NOT NULL DEFAULT '';
```

---

### 6. MenuItems Table

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
    CONSTRAINT close_time_after_open_or_overnight CHECK (
        is_closed = true
        OR close_time > open_time
        OR (close_time < open_time AND close_time <= TIME '04:00'))
);

CREATE INDEX idx_business_hours_tenant_id ON business_hours(tenant_id);
```

**Note on `day_of_week`:** Uses 0-6 mapping (0=Sunday, 1=Monday, ..., 6=Saturday) matching .NET's `DayOfWeek` enum.

**Note on `date`:** `BusinessHour.Date` is a nullable `DateOnly?` used for date-specific overrides (e.g. holiday hours), and the unique index in `AppDbContext` is on `(TenantId, DayOfWeek, Date)`, not just `(TenantId, DayOfWeek)`. This section previously omitted the `date` column entirely, which didn't match the entity and would have broken inserts.

**Note on overnight hours:** A `close_time` earlier than `open_time` means the hours run past midnight into the next morning (e.g. 10:00 – 02:00, or 10:00 – 00:00 for closing at midnight). Overnight closing times are capped at 04:00 because the restaurant site only checks the previous day's hours between midnight and 4 AM. Equal open and close times are rejected. The same rule is enforced by `BusinessHourRules` in the API and by the admin portal's hours editors.

**For existing databases, allow overnight hours (replaces the old `close_time_after_open` constraint, which required `close_time > open_time`). Existing rows already satisfy the new constraint. Run this before deploying the API and admin portal, otherwise overnight hours pass validation but fail on save:**

```sql
ALTER TABLE business_hours DROP CONSTRAINT IF EXISTS close_time_after_open;

ALTER TABLE business_hours
ADD CONSTRAINT close_time_after_open_or_overnight CHECK (
    is_closed = true
    OR close_time > open_time
    OR (close_time < open_time AND close_time <= TIME '04:00'));
```

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
    CONSTRAINT close_time_after_open_or_overnight CHECK (
        is_closed = true
        OR close_time > open_time
        OR (close_time < open_time AND close_time <= TIME '04:00'))
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
    address VARCHAR(300),
    phone_number VARCHAR(30),
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

CREATE TABLE menus (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    name VARCHAR(120) NOT NULL,
    description VARCHAR(300) NOT NULL DEFAULT '',
    sort_order INTEGER NOT NULL DEFAULT 0,
    is_visible BOOLEAN NOT NULL DEFAULT TRUE,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    UNIQUE(tenant_id, name),
    CONSTRAINT name_not_empty CHECK (name != '')
);

CREATE INDEX idx_menus_sort_order ON menus(tenant_id, sort_order);

CREATE TABLE menu_categories (
    id UUID PRIMARY KEY DEFAULT gen_random_uuid(),
    tenant_id UUID NOT NULL,
    menu_id UUID NOT NULL,
    name VARCHAR(120) NOT NULL,
    description VARCHAR(300) NOT NULL DEFAULT '',
    sort_order INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE,
    FOREIGN KEY (menu_id) REFERENCES menus(id) ON DELETE CASCADE,
    UNIQUE(menu_id, name),
    CONSTRAINT name_not_empty CHECK (name != '')
);

CREATE INDEX idx_menu_categories_tenant_id ON menu_categories(tenant_id);
CREATE INDEX idx_menu_categories_sort_order ON menu_categories(tenant_id, sort_order);
CREATE INDEX idx_menu_categories_menu_id ON menu_categories(menu_id, sort_order);

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
    CONSTRAINT close_time_after_open_or_overnight CHECK (
        is_closed = true
        OR close_time > open_time
        OR (close_time < open_time AND close_time <= TIME '04:00'))
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
5. **Uniqueness**: `restaurant_profiles` has a unique constraint on `tenant_id` (1:1 relationship), `menus` has a unique constraint on `(tenant_id, name)`, `menu_categories` has a unique constraint on `(menu_id, name)`, `tenant_users` has a unique constraint on both `external_user_id` alone and `(tenant_id, external_user_id)`, `tenants.custom_domain` has a unique index, and `business_hours` has a unique constraint on `(tenant_id, day_of_week, date)`.

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
    clarification_context TEXT,
    received_utc TIMESTAMP WITH TIME ZONE NOT NULL DEFAULT CURRENT_TIMESTAMP,
    updated_utc TIMESTAMP WITH TIME ZONE,
    FOREIGN KEY (tenant_id) REFERENCES tenants(id) ON DELETE CASCADE
);

CREATE INDEX idx_agent_submissions_tenant_status ON agent_submissions(tenant_id, status);
CREATE INDEX idx_agent_submissions_tenant_received ON agent_submissions(tenant_id, received_utc);

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

**Existing databases:** `agent_submissions.clarification_context` was added later. Run this before deploying
the API version that maps it:

```sql
ALTER TABLE agent_submissions ADD COLUMN IF NOT EXISTS clarification_context TEXT;
```

`clarification_context` holds JSON written by agent-intake-service while a submission is `NeedsClarification`
for a logo/hero image question: the attachments it staged in R2 (`filename`, `contentType`, `url`), the slots
already resolved (`resolvedSlots`) and the slots still being asked about (`pendingSlots`).
