# AeroDesk

A Windows desktop application for airline **call-centre agents** to sell travel using
**IATA Modern Airline Retailing (Offers & Orders / NDC)**.

AeroDesk is a customer-facing app that uses **[DocumentForge](https://github.com/aerotoysio/documentforge)**
as its order store. It is a separate, standalone product by aerotoysio, and shares the desktop
architecture of DocumentForge Studio (WPF + AvalonDock + MVVM).

See **[AERODESK_PLAN.md](AERODESK_PLAN.md)** for the full implementation plan and phase breakdown.

## Status

Planning / scaffolding. Phase 0 (solution scaffold + AvalonDock shell) is the first build target.

## Architecture (at a glance)

- **WPF**, `net9.0-windows`, **MVVM** via CommunityToolkit.Mvvm.
- **Dirkster.AvalonDock** docking workbench (nav tree + tabbed documents + status bar).
- `AeroDesk` (WPF app) · `AeroDesk.Core` (models/services/settings, no WPF deps) · `AeroDesk.Core.Tests` (xUnit).
- `IRetailingService` abstraction with a **DocumentForge** HTTP implementation and an **in-memory** mock.
- Settings/secrets stored per-user under `%AppData%\AeroDesk` with DPAPI encryption.

## DocumentForge

AeroDesk talks to a running `dfdb serve` node over REST (dev default `http://localhost:5001`) and
persists offers/orders/passengers/payments as JSON documents in database `airline`. A bootstrap
action seeds sample flight inventory so the app demos end to end on a fresh node.

## Payment & compliance

This is a demo. AeroDesk does **not** process real cards or store card data. Payment goes through a
mock, tokenized gateway (`IPaymentGateway` / `MockPaymentGateway`) that returns an authorization code;
only a token / last-4 is ever handled, and **full PAN and CVV are never persisted**. A production
deployment MUST use a **PCI-DSS-compliant hosted/tokenized gateway** (hosted fields or redirect).
