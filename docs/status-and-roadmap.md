# Status and Roadmap

_Last updated: April 2026_

---

## ✅ Implemented and working

### Scan and persistence

- Layered solution structure (Domain / Application / Infrastructure / Scanning / Desktop)
- Recursive scan engine with iterative queue (no stack overflow on deep trees)
- SQLite persistence for scan jobs, directory nodes, file nodes, AI results, user overrides
- File type detection (`FileTypeDetector`) by extension
- Heuristic scan pruning: skip dot-dirs, `AppData`, `Windows`, `node_modules`, `$Recycle.Bin`, etc.
- Resume support: already-scanned directories are skipped on restart
- Tests for scan heuristics, file type detection, and scan pipeline persistence

### Desktop UI

- Root folder selection, scan start/pause/cancel
- AI classification start/pause/resume/cancel (independent of scan)
- Folder tree with lazy child loading and expand/collapse
- **Right-click → "Im Explorer öffnen"** on any folder node
- File table for the selected directory (color-coded category strip, confidence, summary)
- **Directory AI summary banner** above the file table (green bar with folder-level description)
- File detail panel with metadata, image preview, text preview, PDF first-page preview
- Resizable splitters for tree / table / detail areas
- Ollama settings (base URL, model, keep-alive) editable and persisted in toolbar
- Local Ollama models loaded from `/api/tags` and selectable in UI
- Manual model warm-up action
- Recent scan history shown on welcome screen

### AI pipeline (3-phase)

**Phase 1 — Directory Pre-Assessment**
- Runs for every directory before per-file analysis
- Sends file list (names, types, sizes) to Ollama
- Determines sampling strategy: `analyze_all`, `random_sample`, or `skip`
- Marks non-sampled files as `Skipped` so Phase 2 ignores them
- Result stored in `ai_directory_results` (phase = `pre_assessment`)

**Phase 2 — Per-File Deep Classification**
- Processes `Discovered` files of type Image / Video / Document in batches
- Images: uploaded as base64 (≤ 10 MB), visual AI analysis
- Documents: text extraction + semantic classification (≤ 5 MB)
- Videos: filename + path heuristics only (no bytes uploaded)
- Returns: `category`, `importance`, `confidence`, `summary`, `suggested_target`
- Handles gemma4's non-standard field aliases (`classification` → `category`)
- Result stored in `ai_results`

**Phase 3 — Directory Summaries**
- Bottom-up: deepest directories first
- Builds `summaryLines` from Phase 2 results (falls back to `name — FileType` if no summary)
- Calls Ollama once per directory for a human-readable folder description
- Result shown as a banner in the UI when the folder is selected
- Result stored in `ai_directory_results` (phase = `summary`)

**Pipeline runtime behavior**
- Single Ollama availability check at pipeline start (not once per phase)
- `think: false` sent in all Ollama requests (prevents gemma4 reasoning tokens from inflating response time)
- Detailed prompt + truncated response logged at `Information` level (visible in VS Output → Debug channel)
- `Microsoft.Extensions.Logging.Debug` provider active in desktop app

### Manual review

- `UserOverride` model and `ReviewService` exist
- Override persistence implemented
- UI for overrides: **done** (category correction visible in file detail panel)

---

## ⚠️ Implemented but limited

### Document understanding

- PDFs: first rendered page only — no multi-page reasoning
- Text extraction: preview-sized only (not full-document ingestion)
- No OCR beyond what the vision model infers from rendered images
- Office formats (`.docx`, `.xlsx`) not deeply parsed

### AI quality / robustness

- Confidence derivation: if model omits `confidence` but gives a valid category, defaults to `0.85`
- Prompt schema field names are often ignored by gemma4 — alias parsing handles this but other models may behave differently
- No adaptive throttling — pipeline is strictly sequential per-file

### Review workflow

- Override UI exists but is minimal
- No confirm/reject bulk workflow
- No review filters
- Approval-driven action planning not yet built

---

## 🔜 Planned next priorities

### 1 — Action pipeline
- Generate file operation plans from approved classifications
- Preview actions (move / copy / archive) before execution
- Execute with per-file logging and rollback-friendly design

### 2 — Richer document understanding
- Multi-page PDF support
- OCR / text extraction for more binary formats
- Improved prompts for "valuable personal data vs. trash" distinction

### 3 — Review UX improvements
- Bulk approve / reject
- Filter by category, confidence, trash candidates
- Surface AI errors and model info per file

### 4 — Performance and scale
- Profile on large datasets (100k+ files)
- Evaluate bounded concurrency for Phase 2
- Improve tree virtualization for very large directory trees

### 5 — Polish and packaging
- Custom skip-pattern editor in UI
- Export scan results (CSV / JSON)
- macOS / Linux packaging

---

## 📋 Known caveats

- Avalonia `DataGrid` requires explicit theme include or renders as a black slab
- Dark theme defaults caused black panels until explicit backgrounds were set
- PDF previews initially rendered transparent — fixed with forced white background
- Large Ollama vision models need time for initial VRAM load — warm-up + `keep_alive` mitigates this
- `think: false` is required for gemma4 — without it, responses take 90–150 s even for trivial prompts
- `%APPDATA%\SmartFileOrganizer\settings.json` overrides `appsettings.json` — check it first when debugging unexpected behavior
- Ollama continues processing after a client-side timeout; the next request queues behind it — this was the root cause of Phase 3 hanging on video files
- PDFium native runtimes may interact with Windows application control policies in enterprise environments

---

## 🏁 MVP definition

MVP is complete when:

- [x] Scan + persist + browse works reliably
- [x] 3-phase AI pipeline runs end-to-end with real results
- [x] Configurable Ollama settings
- [x] Directory summaries visible in UI
- [ ] Review and override workflow fully exposed
- [ ] Basic file action (move/copy) executable after approval


### Scan and persistence

- solution and layered project structure created
- recursive scan engine implemented
- SQLite persistence implemented for:
  - scan jobs
  - directory nodes
  - file nodes
  - AI results
  - user overrides
- file type detection and heuristic scan pruning implemented
- tests exist for core scan heuristics/file type behavior and scan pipeline persistence

### Desktop UI

- root folder selection works
- scan start works
- pause/cancel controls exist
- asynchronous AI classification starts automatically after a completed scan job
- AI classification can be paused, resumed, or cancelled independently of the scanner
- folder tree loads and expands lazily
- file table loads selected directory contents correctly
- file details panel works
- image preview works
- text preview works
- first-page PDF preview works
- bottom details area and inner details/preview area are resizable via splitters
- Ollama base URL, model, and keep-alive are configurable in the toolbar
- local Ollama models can be queried from the running server and selected in the UI
- a manual model warm-up action exists so the chosen model can be preloaded into VRAM before a bigger run

### Ollama and AI classification

- `AiClassificationCoordinator` runs the post-scan classification pipeline separately from the scan engine
- the classification pipeline is sequential/batch-oriented so the scanner is not blocked by long model inference times
- AI status and progress are shown in the UI
- file table/details refresh while AI results arrive
- Ollama settings are persisted to `%AppData%\SmartFileOrganizer\settings.json`
- `keep_alive` is sent to Ollama requests so models stay loaded longer than the default `5m` if configured
- classification now uses real multimodal requests through `/api/chat`
- image files are sent as base64 image payloads
- PDFs are represented by the rendered first page image already used by the preview system
- text-like files contribute extracted preview text
- metadata is still included so the model can reason about path, extension, and file context

### Architectural groundwork

- Domain / Application / Infrastructure / Desktop separation in place
- repositories and services abstract persistence and integrations
- Ollama service contract and implementation exist
- classification pipeline service exists
- manual override service exists

## Implemented but still limited

### Ollama classification quality and document understanding

- model selection, keep-alive, warm-up, and post-scan triggering are implemented
- the current multimodal path is intentionally practical rather than exhaustive:
   - images: original image bytes
   - PDFs: first-page visual render only
   - text-like docs: preview-sized extracted text only

What is still missing:

- deeper multi-page document understanding
- OCR or richer text extraction for more binary document formats
- better model-specific prompting/evaluation for “valuable user data vs trash” decisions
- adaptive throttling/concurrency strategy for large datasets if/when sequential processing becomes too slow
- stronger surfacing of per-file AI error details in the review UX

### Review workflow

The following groundwork exists:

- `UserOverride` model
- `ReviewService`
- override persistence

What is still missing:

- override UI
- confirm/reject workflow
- review filters
- approval-driven file action planning

## Planned next priorities

1. **Wire Ollama into the real job lifecycle**
   - done for the post-scan path
   - next iteration should focus on classification quality, richer evidence, and better review UX

2. **Richer document/vision understanding**
   - improve multi-page PDF handling
   - consider OCR/text extraction for additional document types
   - refine prompts/schema around “valuable user data” vs “trash candidate”

3. **Review UX**
   - edit category/target suggestions
   - show AI summary/confidence inline
   - expose AI errors and model-used information more clearly
   - add manual approval state transitions

4. **Action pipeline**
   - plan file operations from approved items
   - preview actions before execution
   - execute with logging and rollback-friendly design where possible

5. **Performance and scale improvements**
   - larger dataset profiling
   - better virtualization strategy for large tables/trees
   - decide whether the AI path should stay strictly sequential or introduce bounded concurrency/queue workers

## Known caveats from development so far

- Avalonia `DataGrid` requires an explicit theme include; without it the grid can bind data but render like a black slab
- dark theme defaults caused several areas to appear fully black until explicit panel backgrounds/styles were applied
- preview sizing initially used an unconstrained layout; image previews did not respond to pane size until the preview area was converted to a bounded grid-based layout
- PDF previews initially rendered transparent backgrounds; this was fixed by forcing white background rendering
- integrating native PDFium runtimes may interact with Windows application control policies; this should be verified in restricted enterprise environments
- the first request to a large Ollama vision model can take a long time because the model is first loaded into VRAM; this is expected and can be mitigated with warm-up + longer `keep_alive`
- `keep_alive` helps but does not make the model immortal; if it is idle long enough or the server is restarted, Ollama can still unload it
- current PDF understanding is based on the first rendered page, not full document comprehension

## Definition of “MVP complete”

A meaningful MVP for this repository should include:

- scan + persist + browse working reliably
- AI classification actually running in product flow
- configurable Ollama settings and workable multimodal evidence for files/documents
- review and override workflow exposed in UI
- basic settings/configuration
- enough documentation for new contributors to reason about architecture and direction
