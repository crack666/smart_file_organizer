# Status and Roadmap

## Current implementation status

## Implemented and working

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
