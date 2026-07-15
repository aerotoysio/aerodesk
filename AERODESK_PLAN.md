# AeroDesk — Implementation Plan

> A Windows desktop application for airline **call-centre agents** to sell travel using
> **IATA Modern Airline Retailing (Offers & Orders / NDC)**. AeroDesk is a customer-facing
> app that uses **DocumentForge** as its order store. It is a separate, standalone product.

**Repo:** `aerotoysio/aerodesk` · **Local:** `C:\data\aerodesk` · **Owner:** aerotoysio
**Status:** Approved 2026-07-06 — Phase 0 signed off (public repo, `net9.0-windows`).
**Last updated:** 2026-07-06

---

## 1. Decisions locked with Andrew

| Question | Decision |
|---|---|
| Product name | **AeroDesk** |
| Trip types (shopping MVP) | **One-way + return + multi-city** from the start |
| Seat experience | **Interactive seat map** (visual cabin grid) is a core feature, not deferred |
| Code reuse vs Studio.Core | **Copy the patterns** into a standalone `AeroDesk.Core` — no cross-repo dependency |
| Order store | **DocumentForge** over REST (`dfdb serve`), dev default `http://localhost:5001` |
| Payment | **Mock/tokenized only** — never store PAN/CVV; PCI note in code + README |

## 2. Architectural reference

AeroDesk mirrors **DocumentForge Studio** (`C:\DATA\documentforge\src\DocumentForge.Studio`).
Patterns copied (not referenced) into AeroDesk:

- **WPF, `net9.0-windows`, MVVM** via CommunityToolkit.Mvvm (`[ObservableProperty]`,
  `[RelayCommand]`, `partial void On<X>Changed`).
- **Dirkster.AvalonDock** workbench: left nav tree + tabbed document area + File menu + status bar.
  `DockingManager.DocumentsSource` bound to `ObservableCollection<DocumentViewModel>`, a
  `LayoutItemContainerStyle` binding `Model.Title/ContentId/CanClose`, and a `ToString() => Title`
  override on the document VM as the tab-title safety net.
- **Service/transport abstraction** `IRetailingService` (analogue of Studio's `IDfConnection`)
  with an HTTP-backed and an in-memory implementation.
- **Settings/secrets** under `%AppData%\AeroDesk` with **DPAPI** (`ProtectedData`), atomic file
  writes (temp → move), and an exportable/importable settings bundle.
- **`IDialogService`** abstraction so view models stay testable and WPF-free.
- **xUnit** `AeroDesk.Core.Tests` mirroring `DocumentForge.Studio.Core.Tests` (stub `HttpMessageHandler`,
  temp-dir `IDisposable` fixtures, round-trip persistence tests).

## 3. Solution layout

```
C:\data\aerodesk\
  AeroDesk.sln
  src\
    AeroDesk\                WPF app (net9.0-windows, WinExe) — views, dialogs, DI wiring
    AeroDesk.Core\           models, services, settings — NO WPF deps (testable)
  tests\
    AeroDesk.Core.Tests\     xUnit
  installer\                 Inno Setup (.iss) — Phase 5
  scripts\                   build-aerodesk-installer.ps1 — Phase 5
  README.md
  AERODESK_PLAN.md
```

**NuGet (mirroring Studio):** `CommunityToolkit.Mvvm` (app + core), `Dirkster.AvalonDock` (app),
`System.Security.Cryptography.ProtectedData` (core), `xUnit` + `Microsoft.NET.Test.Sdk` (tests).
`AeroDesk.Core` is `net9.0-windows` (Windows-only because of DPAPI), same as Studio.Core.

## 4. Domain model — pragmatic Offers & Orders subset

Immutable records in `AeroDesk.Core.Domain` (no full NDC XML schemas):

- **Shopping:** `ShopRequest` (O&D legs, dates, PTC counts ADT/CHD/INF, cabin), `Offer`,
  `OfferItem`, `FareComponent`, `PriceDetail` (base/taxes/total/currency), `TimeLimit` (offer expiry).
- **Inventory / flights:** `Flight` / `FlightSegment` (carrier, number, dep/arr airport + time,
  cabin availability, fare basis), stored in DF `flights` collection.
- **Order:** `Order` (OrderID, record locator, status, price + payment time limits), `OrderItem`,
  `Passenger` (PTC, name, DOB, contact, travel-doc/FOID/APIS), `Service`/`Ancillary`, `Seat`,
  `Payment`.
- **Context:** `AgentContext` (logged-in agent + agency).
- **Status enum:** `Draft → PendingPayment → Paid → Ticketed`, plus `Cancelled`, `Changed`.

**Offer-construction service** stands in for the airline OMS: generates `Offer`s from `flights`
inventory + a simple fare model (base fare by cabin/distance, tax %, PTC discounting, branded-fare
tiers). Supports one-way, return (paired legs), and multi-city (N legs) itineraries.

## 5. `IRetailingService`

One interface, two implementations — the app binds to the interface only.

```
IRetailingService
  // shopping
  Task<IReadOnlyList<Offer>> SearchOffersAsync(ShopRequest req, CancellationToken ct)
  Task<Offer>                RepriceOfferAsync(string offerId, CancellationToken ct)
  Task<IReadOnlyList<Ancillary>> GetAncillariesAsync(string offerId, CancellationToken ct)
  Task<SeatMap>              GetSeatMapAsync(string offerId, string segmentId, CancellationToken ct)
  // ordering
  Task<Order> CreateOrderAsync(string offerId, IReadOnlyList<Passenger> pax,
                               IReadOnlyList<string> ancillaryIds, CancellationToken ct)
  Task<Order> GetOrderAsync(string orderIdOrLocator, CancellationToken ct)
  Task<Order> ChangeOrderAsync(OrderChange change, string expectedEtag, CancellationToken ct)  // If-Match → 412
  Task<Order> CancelOrderAsync(string orderId, string expectedEtag, CancellationToken ct)
  // payment
  Task<Order> PayOrderAsync(string orderId, PaymentToken token, string expectedEtag, CancellationToken ct)
  // bootstrap
  Task SeedInventoryAsync(CancellationToken ct)
```

- **`DocumentForgeRetailingService`** — typed REST client over `dfdb serve`, copying Studio's
  `HttpConnection`: static `JsonSerializerOptions` (camelCase, case-insensitive), `JsonDocument`
  parsing, `Uri.EscapeDataString` on path segments, `DfHttpException` mapping, and **ETag/If-Match**
  optimistic concurrency with **412 → `EtagConflictException`** on every order mutation.
  Collections: `flights`, `offers`, `orders`, `passengers`, `payments`, `etickets` in DB `airline`.
- **`InMemoryRetailingService`** — self-contained mock (dictionaries + a synthetic inventory set)
  for offline demo and unit tests; same offer-construction + fare logic so behaviour matches.

## 6. Payment & compliance (read carefully)

- `IPaymentGateway` + `MockPaymentGateway` returning an authorization code.
- Payment form is **tokenized**: accept only a token / last-4. **Never persist full PAN or CVV.**
- Code comment + README state production must use a **PCI-DSS-compliant hosted/tokenized gateway**
  (hosted fields / redirect). No real payment credentials, ever.

## 7. Screens / UX (Studio-quality: guided, validated, no jargon dumps)

Dashboard · Shopping (search form → comparable **fare-family cards**) · Offer detail / reprice ·
Ancillaries (**interactive seat map**, bags, meals) · Passenger capture (per-pax validation, APIS/doc
fields) · Order review → create · Payment → confirmation with simulated e-ticket/EMD numbers ·
Order management (retrieve, itinerary, change/cancel).

## 8. Phased plan (PR-per-phase to `master`, green build+tests each phase)

- **Phase 0 — Scaffold:** repo + solution + 3 projects; empty AvalonDock shell launching; settings
  + DPAPI secret store; connection manager; `IRetailingService` skeleton with `InMemoryRetailingService`
  wired so the shell runs offline; `IDialogService`; CI-green `dotnet build`/`dotnet test`.
- **Phase 1 — Shopping:** search form (one-way/return/multi-city) → offer-construction service over
  DF `flights` inventory → offer results as fare-family cards → offer detail/reprice. Seed sample
  inventory. `DocumentForgeRetailingService` search path live.
- **Phase 2 — Order:** passenger capture with validation → `OrderCreate` persisted in DF
  (`PendingPayment`, with price/payment time limits) → order review.
- **Phase 3 — Payment:** `MockPaymentGateway` → pay → status `Paid`/`Ticketed` → issue simulated
  e-tickets (`etickets`) → confirmation screen.
- **Phase 4 — Servicing:** OrderRetrieve (by OrderID/locator), OrderChange (add ancillary / reseat),
  OrderCancel/refund; **ETag/If-Match concurrency** end-to-end (412 conflict UX).
- **Phase 5 — Polish/packaging:** seat-map depth, branded fares, multi-pax refinements, agent/agency
  context, Inno Setup installer (`installer/` + `scripts/build-aerodesk-installer.ps1` copied from Studio).

## 9. DocumentForge integration & bootstrap

- Configurable HTTP connection (default dev `http://localhost:5001`), connection-manager UX from Studio.
- DB `airline` (configurable); collections listed in §5.
- Orders keyed by `OrderID`; **ETag/If-Match** for all mutations.
- **Bootstrap/seed action**: create DB + collections + sample `flights` inventory so the app demos
  end-to-end on a fresh DF node (`SeedInventoryAsync`).

## 10. Working conventions

- Commit-per-feature; PR-per-phase to `master`. Commit messages via file + `git commit -F <file>`;
  end with the `Co-Authored-By: Claude` trailer.
- Keep the solution green each phase (`dotnet build`, `dotnet test`).
- Friendly, guided UI over raw string inputs.

## 11. Open questions — resolved 2026-07-06

1. **GitHub repo:** `aerotoysio/aerodesk`, **public**. ✔
2. **Target framework:** **`net9.0-windows`** — match Studio. ✔
3. **Currency/markets:** default — **USD**, ~8 hub airports for seeded demo inventory.
4. **RuleForge tie-in:** default — simple in-app fare model now; RuleForge integration is a later,
   optional phase.
5. **Agent auth:** default — `AgentContext` is a local profile (name/agency) for the demo.

## 12. Departure Control (DCS) module — added 2026-07-15

A second workbench folded into the same app (decision: shared shell/theme/settings/auth beats a
separate app; role/permission-gated nav gives the persona separation). It drives AeroBus's
`/operations` surface.

- **Core:** `AeroDesk.Core.Operations` — `IOperationsService` seam with `AeroBusOperationsService`
  (HTTP) and `InMemoryOperationsService` (offline demo). Domain records `DepartureFlight` /
  `ManifestPassenger`.
- **Auth — Keycloak staff login:** `Connections/KeycloakAuthClient` (resource-owner/direct-access
  grant → OIDC token) so board/depart carry per-agent identity. Per-connection Keycloak fields on the
  Connect dialog; blank Keycloak URL = retailing-only.
- **UI:** a *Departure Control* nav action on AeroBus/offline connections → `DeparturesDocumentViewModel`
  (station + day board) → `FlightDocumentViewModel` (status actions + passenger manifest with check-in /
  board / board-all).
- **Offline:** `--offline` / *Work Offline* seeds departures + manifests and runs the whole loop with no
  backend.

**Follow-up (backlog):** migrate the **retailing** `AeroBusRetailingService` off the removed
`/admin/users/{slug}/authenticate` endpoint onto this same `KeycloakAuthClient`.
