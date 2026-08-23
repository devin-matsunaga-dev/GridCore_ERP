# SPEC.md — Utility ERP (MVP)

Source of truth for *what* to build. Distilled from the MVP Feature Set. Seven integrated modules + two end-to-end demonstration workflows. External systems are **simulated behind provider interfaces** so the product shows realistic end-to-end behavior with no production integrations.

## Guiding principle

Prove the ERP *connects* utility business processes — not a pile of unrelated CRUD screens. Success = the two demonstration workflows run reliably and each transaction produces the expected downstream operational, inventory, customer, and financial effects.

## Modules

### 1. Customers & Service Locations
Customer records (account number, name, contacts, class residential/commercial, status, deposit). Service locations (physical address, separate from customer). Service accounts connect customer↔location with states Active/Pending/Disconnected/Closed. Start-service and stop-service workflows with account history.

### 2. Meters & Consumption
Meter registry (meter number, serial, type, status, assigned service location). Manual readings + history. Consumption calculated from previous→current reading. **Meter simulator** generates a billing-cycle batch of realistic readings incl. high-usage / zero-usage / missing-reading exceptions. Behind `IMeterReadingProvider`.

### 3. Billing
Rate engine: base charges + tiered consumption rates with effective dates. Bill generation from consumption. Bill states Draft/Issued/PartiallyPaid/Paid/Overdue/Cancelled-Adjusted. Adjustments (authorized credits/corrections) with audit trail.

### 4. Simulated Payments
Sandbox provider simulating Approved/Declined/InsufficientFunds/Timeout/Refunded. Approved payment has **real internal effects**: reduces balance, updates invoice, records payment, generates accounting activity. Behind `IPaymentProvider` for later production swap.

### 5. Assets
Utility asset classes: transformer, pole, meter, generator, substation equipment, vehicle. Fields: tag, type, manufacturer/model, serial, install date, status, condition, location (lat/long stored, no GIS). Completed work orders become part of the asset's maintenance history.

### 6. Work Orders & Maintenance
Types: inspection, preventive maintenance, repair, meter replacement, service connection, disconnection. Workflow Open→Assigned→InProgress→Completed/Cancelled. Links to asset, service location, and customer where applicable. Records crew/technician, priority, dates, notes, labor, materials consumed. Simulated crews behind a crew provider.

### 7. Inventory & Purchasing
Items, warehouses, quantity on hand, minimum stock, adjustments, receipts, material issuance. Issuing parts to a work order reduces inventory and records material against the job. Procurement: Purchase Request→Approval→Purchase Order→Receive Goods. Simulated vendors behind `IVendorProvider`.

### 8. Basic Finance
Chart of accounts + journal entries — enough double-entry to show operations flowing into finance. Billing: Dr AR / Cr Utility Revenue. Payment: Dr Cash / Cr AR. Purchasing: Dr Inventory / Cr AP. Views: AR, AP, journal, trial balance. Full enterprise accounting is out of scope.

### 9. Administration, Security, Audit
RBAC roles: Administrator, Customer Service, Billing, Finance, Warehouse, Technician, Supervisor, Manager. Permissions gate sensitive actions (bill adjustments, approvals, inventory adjustments). Audit trail: user, timestamp, entity/action, before/after values. Lightweight approval workflow for purchase requests and selected financial/operational actions.

## Demonstration Workflows (the acceptance heart of the MVP)

**Revenue Cycle:** Create Customer → Create Service Account → Assign Meter → Generate Simulated Reading → Calculate Consumption → Generate Bill → Run Simulated Payment → Update Balance → Generate Accounting Entries.

**Operations & Maintenance Cycle:** Asset Problem Identified → Create Work Order → Assign Crew → Issue/Reserve Parts → Complete Repair → Reduce Inventory → Update Asset Maintenance History → Record Costs.

## Simulation Environment
Meter simulator, payment simulator, vendor simulator, crew simulator, and **seed/demo mode** (small utility dataset: customers, locations, meters, assets, bills, inventory, work orders) for demos and automated tests.

## Integration architecture
Keep external dependencies behind provider interfaces from day one: `IMeterReadingProvider`, `IPaymentProvider`, `IVendorProvider` (+ crew). MVP uses simulators; production swaps implementations without redesigning core domains.

## Deferred beyond MVP
SCADA, production AMI, real payments, payroll, advanced GIS, outage prediction, mobile field apps, bank reconciliation, complex regulatory reporting, demand forecasting, advanced analytics/AI.
