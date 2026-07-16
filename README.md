# AeroDesk

A Windows desktop application for airline **agents** — two workbenches in one app:

- **Retailing** (call-centre): sell travel using **IATA Modern Airline Retailing (Offers & Orders / NDC)**.
- **Departure Control (DCS)** (check-in counter / boarding gate): work a station's departures, manage
  flight status, and check in / board passengers via **[AeroBus](https://github.com/aerotoysio/aerobus)**.

The workbench shows the sections a connected backend supports (and, in future, the agent's role), so a
call-centre agent and a gate agent share one installed app but see the tools relevant to them.

AeroDesk uses **[DocumentForge](https://github.com/aerotoysio/documentforge)** as its retailing order
store and **AeroBus** as the operational middle layer. It is a standalone product by aerotoysio, and
shares the desktop architecture of DocumentForge Studio (WPF + AvalonDock + MVVM).

See **[AERODESK_PLAN.md](AERODESK_PLAN.md)** for the full implementation plan and phase breakdown.

## Status

Planning / scaffolding. Phase 0 (solution scaffold + AvalonDock shell) is the first build target.

## Architecture (at a glance)

- **WPF**, `net9.0-windows`, **MVVM** via CommunityToolkit.Mvvm.
- **Dirkster.AvalonDock** docking workbench (nav tree + tabbed documents + status bar).
- `AeroDesk` (WPF app) · `AeroDesk.Core` (models/services/settings, no WPF deps) · `AeroDesk.Core.Tests` (xUnit).
- `IRetailingService` abstraction (DocumentForge HTTP · AeroBus · in-memory mock) for retailing.
- `IOperationsService` abstraction (AeroBus `/operations` · in-memory mock) for departure control.
- Settings/secrets stored per-user under `%AppData%\AeroDesk` with DPAPI encryption.

## Departure Control (DCS)

The departure-control workbench (`AeroDesk.Core.Operations`) drives AeroBus's `/operations` surface:
list a station's departures for a day → open a flight → work the **passenger manifest** (check in,
assign seat, board) → change flight status (**Start Boarding**, **Depart**). `InMemoryOperationsService`
runs the whole loop offline (`--offline` / *Work Offline*) with no backend, so it demos immediately.

**Auth — Keycloak staff login.** Departure control authenticates the agent against the same Keycloak
realm AeroBus validates (direct access grant → OIDC token), so every board/depart carries per-agent
identity. Configure it per AeroBus connection in the Connect dialog (Keycloak URL / realm / client id);
leave the Keycloak URL blank to connect for retailing only. AeroBus grants the operational permissions
(`operations.view` / `operations.manage`) to the `editor` and `org-admin` roles out of the box.

> Follow-up: the **retailing** side still uses AeroBus's removed agent-login endpoint and will migrate
> to this same Keycloak client.

## DocumentForge

AeroDesk talks to a running `dfdb serve` node over REST (dev default `http://localhost:5001`) and
persists offers/orders/passengers/payments as JSON documents in database `airline`. A bootstrap
action seeds sample flight inventory so the app demos end to end on a fresh node.

## Payment & compliance

This is a demo. AeroDesk does **not** process real cards or store card data. Payment goes through a
mock, tokenized gateway (`IPaymentGateway` / `MockPaymentGateway`) that returns an authorization code;
only a token / last-4 is ever handled, and **full PAN and CVV are never persisted**. A production
deployment MUST use a **PCI-DSS-compliant hosted/tokenized gateway** (hosted fields or redirect).
