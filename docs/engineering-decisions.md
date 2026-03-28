# Engineering Decisions and Caveats

## Guiding constraints

This repository aims to stay:

- free to use
- based on open-source dependencies where practical
- cross-platform across Windows and Linux first
- maintainable over flashy short-term hacks

## Chosen libraries and rationale

## Avalonia UI

Chosen because it is:

- cross-platform desktop capable
- open source
- suitable for MVVM
- independent of web/HTML UI stacks

### Important design choice

We intentionally avoid paid Avalonia Accelerate components and did not base the product on `TreeDataGrid`.

Reason:

- keep the repository free/open-source-friendly
- avoid premium dependency lock-in
- keep long-term control over the core UX

## SQLite + Dapper

Chosen because they are a strong fit for a local desktop tool:

- SQLite is easy to distribute and works well for local persistent state
- Dapper keeps persistence explicit and lightweight
- the repository benefits from predictable SQL rather than heavy ORM machinery

## Docnet.Core for PDF preview

Chosen for first-page PDF rendering because it is:

- MIT licensed
- cross-platform
- capable of rendering PDF pages to image buffers
- sufficient for MVP-grade embedded preview without introducing paid components

### Trade-off

It introduces native PDFium runtimes. That is acceptable, but it means:

- enterprise environments may enforce additional application control policies
- packaging/runtime verification matters more than for pure managed libraries

## Ollama HTTP API

Chosen because the project wants local AI inference rather than cloud-only dependency.

Benefits:

- privacy-friendly local execution
- flexible model selection
- good fit for offline-ish desktop workflows

Current caveat:

- the service exists, but the product flow is not fully wired yet

## Important caveats encountered so far

## 1. "All black UI" problem

Symptom:

- controls rendered as apparently empty black surfaces even though data binding worked

Root causes:

- Avalonia `DataGrid` theme include was missing
- several panels relied too heavily on dark-theme defaults or transparent backgrounds

Resolution:

- added the DataGrid Fluent theme include to `App.axaml`
- applied explicit backgrounds/borders for the main panels

## 2. DataGrid showed data in logs but not visually

Symptom:

- tree selection worked and rows loaded, but the grid looked blank

Resolution:

- included DataGrid styles explicitly
- adjusted grid styling and column sizing

## 3. Preview image did not respond to pane size

Symptom:

- preview image stayed visually huge/similar regardless of splitter position

Root cause:

- the preview lived in a layout that effectively gave the image unbounded measurement

Resolution:

- converted preview area to a bounded grid-based layout
- separated metadata scrolling from preview sizing

## 4. PDF preview looked transparent on dark UI

Symptom:

- PDF content rendered directly over the dark panel background

Resolution:

- rendered the page with a white transparency remover so the preview looks like an actual page

## 5. Native runtime/policy sensitivity

When native PDFium files were introduced, startup behavior in Windows environments required extra care. Even when builds succeeded, environments with application control rules can behave differently than local developer machines.

Recommended posture:

- keep runtime selection explicit where possible
- validate on target environments early
- document native dependencies clearly

## Design stance going forward

Prefer:

- simple, inspectable dependencies
- open-source and permissive licenses
- predictable runtime behavior
- architecture that can evolve without rewriting the app shell

Avoid:

- premium UI lock-in
- browser-first pivots for a desktop-native tool
- heavy abstractions before the product flow is proven
