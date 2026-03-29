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

Current state:

- the product now queries available local models from Ollama
- the chosen model can be warmed explicitly and kept loaded longer via `keep_alive`
- classification uses multimodal `/api/chat` requests instead of metadata-only `/api/generate`

### Important design choice

The AI classification path is intentionally **not** embedded into the scan loop.

Reason:

- scanning local files and calling a large multimodal model have very different performance characteristics
- the scan engine should remain fast and predictable even if model inference is slow
- pause/resume/cancel semantics are easier to reason about in a separate post-scan pipeline

Current posture:

- scan first
- classify afterward in a separate coordinator
- keep the AI path sequential/bounded until profiling proves a more parallel design is necessary

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

## 6. Ollama model warm-up and VRAM residency

Large local vision models can take a long time to load into VRAM on the first request.

Resolution/approach:

- exposed model warm-up in the UI
- pass `keep_alive` to Ollama requests
- allow the operator to choose a longer residency window for local workflows

Trade-off:

- keeping big models resident improves responsiveness
- but it also reserves GPU memory longer and may conflict with other GPU workloads

## 7. Multimodal evidence is intentionally incremental

The current multimodal evidence path is practical rather than complete:

- original image bytes for image files
- first-page rendered image for PDFs
- preview-sized extracted text for text-like files

Reason:

- this gives real vision capability quickly without waiting for a full document/OCR subsystem
- it reuses the preview/rendering work already present in the desktop app

Known limitation:

- long or multi-page documents are not yet fully understood

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
