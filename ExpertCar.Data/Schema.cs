namespace ExpertCar.Data;

internal static class Schema
{
    internal const int CurrentVersion = 6;
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
        """,
        """
        ALTER TABLE vehicles ADD COLUMN raw_year TEXT;
        ALTER TABLE vehicles ADD COLUMN color TEXT;
        ALTER TABLE vehicles ADD COLUMN vin TEXT CHECK(vin IS NULL OR length(vin)=17);
        CREATE TABLE vehicle_ownerships (
          id INTEGER PRIMARY KEY, vehicle_id INTEGER NOT NULL REFERENCES vehicles(id),
          customer_id INTEGER NOT NULL REFERENCES customers(id),
          start_date TEXT NOT NULL, end_date TEXT,
          source_note TEXT, CHECK(end_date IS NULL OR end_date >= start_date)
        );
        CREATE UNIQUE INDEX ux_vehicle_current_owner ON vehicle_ownerships(vehicle_id) WHERE end_date IS NULL;
        ALTER TABLE insurance_policies ADD COLUMN confirmation_state TEXT NOT NULL DEFAULT 'Pending'
          CHECK(confirmation_state IN ('Pending','Confirmed','Rejected'));
        CREATE TABLE vendors (
          id INTEGER PRIMARY KEY, code TEXT UNIQUE, name TEXT NOT NULL,
          phone TEXT, note TEXT
        );
        ALTER TABLE work_orders ADD COLUMN workflow_status TEXT NOT NULL DEFAULT 'Estimate'
          CHECK(workflow_status IN ('Estimate','Accepted','InProgress','AwaitingCheckout','Settled','Cancelled','ImportReview'));
        UPDATE work_orders SET workflow_status=CASE status
          WHEN 'Completed' THEN 'AwaitingCheckout' WHEN 'Void' THEN 'Cancelled'
          ELSE status END;
        CREATE TRIGGER work_orders_legacy_status_insert AFTER INSERT ON work_orders
          WHEN NEW.workflow_status='Estimate' AND NEW.status<>'Estimate'
        BEGIN
          UPDATE work_orders SET workflow_status=CASE NEW.status
            WHEN 'Completed' THEN 'AwaitingCheckout' WHEN 'Void' THEN 'Cancelled'
            ELSE NEW.status END WHERE id=NEW.id;
        END;
        ALTER TABLE work_orders ADD COLUMN diagnosis TEXT;
        ALTER TABLE work_orders ADD COLUMN repair_description TEXT;
        ALTER TABLE work_orders ADD COLUMN additional_work_notice TEXT;
        ALTER TABLE work_orders ADD COLUMN signature_label TEXT;
        ALTER TABLE work_orders ADD COLUMN estimate_expires_on TEXT;
        CREATE TABLE estimates (
          id INTEGER PRIMARY KEY, work_order_id INTEGER NOT NULL REFERENCES work_orders(id),
          version INTEGER NOT NULL CHECK(version > 0), issued_on TEXT NOT NULL,
          expires_on TEXT, customer_snapshot_json TEXT NOT NULL,
          model_json TEXT NOT NULL, UNIQUE(work_order_id,version)
        );
        ALTER TABLE work_order_lines ADD COLUMN part_number TEXT;
        ALTER TABLE work_order_lines ADD COLUMN vendor_id INTEGER REFERENCES vendors(id);
        ALTER TABLE work_order_lines ADD COLUMN labor_mode TEXT CHECK(labor_mode IN ('Fixed','Hourly'));
        ALTER TABLE work_order_lines ADD COLUMN labor_minutes INTEGER CHECK(labor_minutes IS NULL OR labor_minutes >= 0);
        ALTER TABLE work_order_lines ADD COLUMN hourly_rate_minor INTEGER CHECK(hourly_rate_minor IS NULL OR hourly_rate_minor >= 0);
        ALTER TABLE work_order_lines ADD COLUMN line_category TEXT NOT NULL DEFAULT 'Labor'
          CHECK(line_category IN ('Labor','Part','Consumable','Disposal','Outsource','Discount'));
        UPDATE work_order_lines SET line_category='Part' WHERE kind='Part';
        CREATE TRIGGER work_order_lines_legacy_kind_insert AFTER INSERT ON work_order_lines
          WHEN NEW.line_category='Labor' AND NEW.kind='Part'
        BEGIN
          UPDATE work_order_lines SET line_category='Part' WHERE id=NEW.id;
        END;
        ALTER TABLE work_order_lines ADD COLUMN addition_id INTEGER REFERENCES work_order_additions(id);
        CREATE TABLE work_order_additions (
          id INTEGER PRIMARY KEY, work_order_id INTEGER NOT NULL REFERENCES work_orders(id),
          title TEXT NOT NULL, reason TEXT, state TEXT NOT NULL DEFAULT 'Pending'
          CHECK(state IN ('Pending','Accepted','Rejected')),
          proposed_at TEXT NOT NULL, UNIQUE(work_order_id,id)
        );
        CREATE TABLE customer_consents (
          id INTEGER PRIMARY KEY, work_order_id INTEGER NOT NULL REFERENCES work_orders(id),
          addition_id INTEGER REFERENCES work_order_additions(id),
          estimate_id INTEGER REFERENCES estimates(id),
          decision TEXT NOT NULL CHECK(decision IN ('Accepted','Rejected')),
          method TEXT NOT NULL, customer_name TEXT NOT NULL, occurred_at TEXT NOT NULL,
          evidence_note TEXT, CHECK(addition_id IS NOT NULL OR estimate_id IS NOT NULL)
        );
        ALTER TABLE payments ADD COLUMN received_at TEXT;
        CREATE TABLE receivables (
          work_order_id INTEGER PRIMARY KEY REFERENCES work_orders(id),
          total_minor INTEGER NOT NULL CHECK(total_minor >= 0),
          paid_minor INTEGER NOT NULL DEFAULT 0 CHECK(paid_minor >= 0),
          due_minor INTEGER NOT NULL CHECK(due_minor >= 0),
          updated_at TEXT NOT NULL,
          CHECK(total_minor = paid_minor + due_minor)
        );
        ALTER TABLE print_snapshots ADD COLUMN schema_version INTEGER NOT NULL DEFAULT 4;
        ALTER TABLE print_snapshots ADD COLUMN tax_rate TEXT;
        ALTER TABLE print_snapshots ADD COLUMN card_rate TEXT;
        ALTER TABLE print_snapshots ADD COLUMN tax_rounding_minor INTEGER;
        ALTER TABLE print_snapshots ADD COLUMN card_rounding_minor INTEGER;
        CREATE TABLE import_source_links (
          source_file TEXT NOT NULL, source_table TEXT NOT NULL, source_key TEXT NOT NULL,
          target_entity TEXT NOT NULL, target_id INTEGER NOT NULL,
          first_run_id INTEGER NOT NULL REFERENCES import_runs(id),
          PRIMARY KEY(source_file,source_table,source_key)
        );
        INSERT OR IGNORE INTO import_source_links(source_file,source_table,source_key,target_entity,target_id,first_run_id)
          SELECT source_file,source_table,source_key,target_entity,target_id,run_id
          FROM import_records WHERE target_entity IS NOT NULL AND target_id IS NOT NULL ORDER BY id;
        CREATE INDEX ix_ownership_vehicle ON vehicle_ownerships(vehicle_id,start_date);
        CREATE INDEX ix_estimates_order ON estimates(work_order_id,version);
        CREATE INDEX ix_additions_order ON work_order_additions(work_order_id,state);
        CREATE INDEX ix_catalog_services_name ON service_items(name);
        CREATE INDEX ix_catalog_parts_name ON parts(name);
        """,
        """
        CREATE TABLE staff (
          id INTEGER PRIMARY KEY, source_code TEXT UNIQUE, name TEXT NOT NULL, role TEXT
        );
        CREATE INDEX ix_contacts_value ON customer_contacts(value);
        CREATE INDEX ix_catalog_services_code ON service_items(code);
        CREATE INDEX ix_catalog_parts_code ON parts(code);
        """,
        """
        ALTER TABLE work_orders ADD COLUMN promised_delivery_on TEXT;
        CREATE INDEX ix_orders_delivery ON work_orders(promised_delivery_on,workflow_status);
        """
    ];
}
