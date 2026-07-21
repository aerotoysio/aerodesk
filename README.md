# AeroDesk

A Windows desktop application for airline **agents** — two workbenches in one app:

- **Retailing** (call-centre): sell travel using **IATA Modern Airline Retailing (Offers & Orders / NDC)**.
- **Departure Control (DCS)** (check-in counter / boarding gate): work a station's departures, manage
  flight status, and check in / board passengers via **[AeroBus](https://github.com/aerotoysio/aerobus)**.

The workbench shows the sections a connected backend supports (and, in future, the agent's role), so a
call-centre agent and a gate agent share one installed app but see the tools relevant to them.

AeroDesk runs entirely on **AeroBus** — every sale, order and departure-control action goes through
the AeroBus API with one Keycloak agent login. There is no offline mode and no direct database
access: connected to AeroBus, or dead in the water. It is a standalone product by aerotoysio, and
shares the desktop architecture of DocumentForge Studio (WPF + AvalonDock + MVVM).

See **[AERODESK_PLAN.md](AERODESK_PLAN.md)** for the full implementation plan and phase breakdown.

## Status

Planning / scaffolding. Phase 0 (solution scaffold + AvalonDock shell) is the first build target.

## Architecture (at a glance)

- **WPF**, `net9.0-windows`, **MVVM** via CommunityToolkit.Mvvm.
- **Dirkster.AvalonDock** docking workbench (nav tree + tabbed documents + status bar).
- `AeroDesk` (WPF app) · `AeroDesk.Core` (models/services/settings, no WPF deps) · `AeroDesk.Core.Tests` (xUnit).
- `IRetailingService` (AeroBus `/offer` + `/order`) for retailing.
- `IOperationsService` (AeroBus `/operations`) for departure control.
- Settings/secrets stored per-user under `%AppData%\AeroDesk` with DPAPI encryption.

## Departure Control (DCS)

The departure-control workbench (`AeroDesk.Core.Operations`) drives AeroBus's `/operations` surface:
list a station's departures for a day → open a flight → work the **passenger manifest** (check in,
assign seat, board) → change flight status (**Start Boarding**, **Depart**).

**Auth — one Keycloak agent login for everything.** The agent signs in once (direct access grant
against the realm AeroBus validates, client `aeroboard`) and that session drives **both** workbenches —
reservations and departure control — with automatic token refresh across the shift. Every sale,
check-in, board and depart carries per-agent identity. Agent accounts are created and approved by the
organisation's admin in **AeroStudio** (Users page); Keycloak self-registration stays off
(a self-register + approval queue is a noted follow-up). AeroBus grants the operational permissions
(`operations.view` / `operations.manage`) to the `editor` and `org-admin` roles out of the box.

## Payment & compliance

This is a demo. AeroDesk does **not** process real cards or store card data. Payment goes through a
mock, tokenized gateway (`IPaymentGateway` / `MockPaymentGateway`) that returns an authorization code;
only a token / last-4 is ever handled, and **full PAN and CVV are never persisted**. A production
deployment MUST use a **PCI-DSS-compliant hosted/tokenized gateway** (hosted fields or redirect).
