namespace ExpertCar.Data;

internal static class Schema
{
    internal const int CurrentVersion = 3;
    internal static readonly string[] Migrations =
    [
        """
        CREATE TABLE customers (
          id INTEGER PRIMARY KEY, code TEXT NOT NULL UNIQUE, name TEXT NOT NULL,
          address TEXT, postal_code TEXT, note TEXT
        );
        CREATE TABLE customer_contacts (
          id INTEGER PRIMARY KEY, customer_id INTEGER NOT NULL REFERENCES customers(id),
          kind TEXT NOT NULL, value TEXT NOT NULL, is_primary INTEGER NOT NULL DEFAULT 0 CHECK(is_primary IN (0,1))
        );
        CREATE TABLE vehicles (
          id INTEGER PRIMARY KEY, current_customer_id INTEGER NOT NULL REFERENCES customers(id),
          plate TEXT NOT NULL, plate_key TEXT NOT NULL, make TEXT, model TEXT,
          model_year INTEGER, odometer_km INTEGER, note TEXT,
          CHECK(odometer_km IS NULL OR odometer_km >= 0)
        );
        CREATE TABLE service_items (
          id INTEGER PRIMARY KEY, code TEXT NOT NULL UNIQUE, name TEXT NOT NULL,
          default_labor_minor INTEGER NOT NULL DEFAULT 0 CHECK(default_labor_minor >= 0),
          default_cost_minor INTEGER NOT NULL DEFAULT 0 CHECK(default_cost_minor >= 0), note TEXT
        );
        CREATE TABLE parts (
          id INTEGER PRIMARY KEY, code TEXT NOT NULL UNIQUE, name TEXT NOT NULL,
          unit TEXT, specification TEXT,
          default_price_minor INTEGER NOT NULL DEFAULT 0 CHECK(default_price_minor >= 0),
          cost_minor INTEGER NOT NULL DEFAULT 0 CHECK(cost_minor >= 0)
        );
        CREATE TABLE service_parts (
          id INTEGER PRIMARY KEY, service_id INTEGER NOT NULL REFERENCES service_items(id),
          part_id INTEGER NOT NULL REFERENCES parts(id), line_no INTEGER NOT NULL CHECK(line_no > 0),
          qty_scaled INTEGER NOT NULL CHECK(qty_scaled >= 0),
          unit_price_minor INTEGER NOT NULL CHECK(unit_price_minor >= 0),
          default_use INTEGER NOT NULL DEFAULT 1 CHECK(default_use IN (0,1)),
          UNIQUE(service_id, line_no)
        );
        CREATE TABLE work_orders (
          id INTEGER PRIMARY KEY, number TEXT NOT NULL UNIQUE,
          customer_id INTEGER NOT NULL REFERENCES customers(id), vehicle_id INTEGER NOT NULL REFERENCES vehicles(id),
          status TEXT NOT NULL CHECK(status IN ('Estimate','InProgress','Completed','Settled','Void','ImportReview')),
          order_date TEXT NOT NULL, opened_at TEXT, closed_at TEXT, odometer_km INTEGER,
          note TEXT, customer_snapshot_json TEXT, plate_snapshot TEXT,
          subtotal_minor INTEGER NOT NULL DEFAULT 0 CHECK(subtotal_minor >= 0),
          discount_minor INTEGER NOT NULL DEFAULT 0 CHECK(discount_minor >= 0),
          tax_minor INTEGER NOT NULL DEFAULT 0 CHECK(tax_minor >= 0),
          total_minor INTEGER NOT NULL DEFAULT 0 CHECK(total_minor >= 0),
          revision INTEGER NOT NULL DEFAULT 1 CHECK(revision > 0),
          CHECK(odometer_km IS NULL OR odometer_km >= 0),
          CHECK(discount_minor <= subtotal_minor)
        );
        CREATE TABLE work_order_lines (
          id INTEGER PRIMARY KEY, work_order_id INTEGER NOT NULL REFERENCES work_orders(id),
          line_no INTEGER NOT NULL CHECK(line_no > 0),
          kind TEXT NOT NULL CHECK(kind IN ('Service','Part','Labor')),
          service_id INTEGER REFERENCES service_items(id), part_id INTEGER REFERENCES parts(id),
          code_snapshot TEXT, name_snapshot TEXT NOT NULL, unit TEXT,
          qty_scaled INTEGER NOT NULL CHECK(qty_scaled >= 0),
          unit_price_minor INTEGER NOT NULL CHECK(unit_price_minor >= 0),
          discount_minor INTEGER NOT NULL DEFAULT 0 CHECK(discount_minor >= 0),
          total_minor INTEGER NOT NULL CHECK(total_minor >= 0), note TEXT,
          UNIQUE(work_order_id, line_no)
        );
        CREATE TABLE payments (
          id INTEGER PRIMARY KEY, work_order_id INTEGER NOT NULL REFERENCES work_orders(id),
          paid_on TEXT, method TEXT NOT NULL, amount_minor INTEGER NOT NULL CHECK(amount_minor > 0), note TEXT
        );
        CREATE TABLE insurers (
          id INTEGER PRIMARY KEY, code TEXT NOT NULL UNIQUE, name TEXT NOT NULL, phone TEXT, address TEXT
        );
        CREATE TABLE insurance_policies (
          id INTEGER PRIMARY KEY, vehicle_id INTEGER NOT NULL REFERENCES vehicles(id),
          customer_id INTEGER NOT NULL REFERENCES customers(id), insurer_id INTEGER NOT NULL REFERENCES insurers(id),
          policy_no TEXT, kind TEXT NOT NULL, start_date TEXT, end_date TEXT,
          premium_minor INTEGER CHECK(premium_minor IS NULL OR premium_minor >= 0), note TEXT
        );
        CREATE TABLE insurance_policy_items (
          id INTEGER PRIMARY KEY, policy_id INTEGER NOT NULL REFERENCES insurance_policies(id),
          kind TEXT NOT NULL, start_date TEXT, end_date TEXT,
          premium_minor INTEGER CHECK(premium_minor IS NULL OR premium_minor >= 0)
        );
        CREATE TABLE reminders (
          id INTEGER PRIMARY KEY, vehicle_id INTEGER NOT NULL REFERENCES vehicles(id),
          kind TEXT NOT NULL CHECK(kind IN ('Maintenance','Inspection','Insurance')),
          due_date TEXT, due_km INTEGER, interval_days INTEGER,
          lead_days INTEGER NOT NULL DEFAULT 30 CHECK(lead_days >= 0),
          source_ref TEXT, state TEXT NOT NULL DEFAULT 'Active', last_printed_at TEXT,
          CHECK(due_km IS NULL OR due_km >= 0),
          CHECK(interval_days IS NULL OR interval_days > 0)
        );
        CREATE TABLE print_snapshots (
          id INTEGER PRIMARY KEY, work_order_id INTEGER NOT NULL REFERENCES work_orders(id),
          document_kind TEXT NOT NULL, revision INTEGER NOT NULL CHECK(revision > 0),
          model_json TEXT NOT NULL, created_at TEXT NOT NULL
        );
        CREATE TABLE work_order_events (
          id INTEGER PRIMARY KEY, work_order_id INTEGER NOT NULL REFERENCES work_orders(id),
          action TEXT NOT NULL, occurred_at TEXT NOT NULL, change_json TEXT NOT NULL
        );
        CREATE TABLE import_runs (
          id INTEGER PRIMARY KEY, source_fingerprint TEXT NOT NULL,
          rule_version TEXT NOT NULL, status TEXT NOT NULL,
          scanned_count INTEGER NOT NULL DEFAULT 0, imported_count INTEGER NOT NULL DEFAULT 0,
          issue_count INTEGER NOT NULL DEFAULT 0,
          UNIQUE(source_fingerprint, rule_version)
        );
        CREATE TABLE import_records (
          id INTEGER PRIMARY KEY, run_id INTEGER NOT NULL REFERENCES import_runs(id),
          source_file TEXT NOT NULL, source_table TEXT NOT NULL, source_key TEXT NOT NULL,
          source_row INTEGER, target_entity TEXT, target_id INTEGER,
          approved_values_json TEXT,
          UNIQUE(run_id, source_file, source_table, source_key)
        );
        CREATE TABLE import_issues (
          id INTEGER PRIMARY KEY, run_id INTEGER NOT NULL REFERENCES import_runs(id),
          source_row INTEGER, error_code TEXT NOT NULL, state TEXT NOT NULL DEFAULT 'Open', detail TEXT
        );
        CREATE TABLE app_settings (key TEXT PRIMARY KEY, value_json TEXT NOT NULL);
        """,
        """
        CREATE INDEX ix_vehicles_plate_key ON vehicles(plate_key);
        CREATE INDEX ix_vehicles_customer ON vehicles(current_customer_id);
        CREATE INDEX ix_contacts_customer ON customer_contacts(customer_id);
        CREATE INDEX ix_orders_vehicle ON work_orders(vehicle_id, order_date);
        CREATE INDEX ix_orders_customer ON work_orders(customer_id, order_date);
        CREATE INDEX ix_payments_order ON payments(work_order_id);
        CREATE INDEX ix_policies_vehicle ON insurance_policies(vehicle_id, end_date);
        CREATE INDEX ix_reminders_due ON reminders(state, due_date);
        CREATE INDEX ix_reminders_vehicle ON reminders(vehicle_id);
        CREATE INDEX ix_import_issues_run ON import_issues(run_id, state);
        """,
        """
        ALTER TABLE service_items ADD COLUMN unit TEXT NOT NULL DEFAULT '次';
        """
    ];
}
