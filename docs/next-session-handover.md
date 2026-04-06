# Next Session Handover

_Last updated: April 2026_

---

## Current product state

Smart File Organizer has a **fully working end-to-end pipeline**:

- Recursive directory scan → SQLite
- 3-phase AI pipeline (directory recon → per-file classification → directory summaries)
- Interactive folder tree + file table with color-coded categories, confidence, summaries
- Directory AI summary banner above file table
- Manual category override in file detail panel
- Ollama model selection + settings persisted to AppData
- Right-click → "Im Explorer öffnen" on folder tree nodes
- Build is clean: `dotnet build SmartFileOrganizer.slnx` → **0 errors**

---

## What changed in the most recent sessions

### 3-Phase AI Pipeline (implemented)

**Phase 1 — Directory Pre-Assessment** (`DirectoryPreAssessmentService`)
- Sends file list per directory to Ollama before per-file analysis
- Returns sampling strategy: `analyze_all`, `random_sample`, or `skip`
- Non-sampled files marked `Skipped` so Phase 2 ignores them
- Stored in `ai_directory_results` (phase = `pre_assessment`)

**Phase 2 — Per-File Classification** (`ClassificationService` + `OllamaService.ClassifyAsync`)
- `file_type IN (1, 2, 4)` — Image=1, Video=2, Document=4
- Videos get text-only hint: `[Video file. Classify based on the filename and directory name only.]` — no bytes uploaded
- Images: base64 up to `MaxImageUploadBytes` (10 MB)
- Documents: text preview up to `MaxDocumentUploadBytes` (5 MB)
- `think: false` in all request bodies — prevents gemma4 thinking tokens (90s+)
- Parses `classification` as alias for `category` (gemma4 ignores schema field names)
- Derives `confidence = 0.85` when model omits confidence but gives valid category

**Phase 3 — Directory Summaries** (`DirectorySummaryService`)
- Bottom-up (deepest dirs first)
- `summaryLines` includes ALL files now — falls back to `name — FileType` if no AI summary exists (fixes video-only dirs being skipped)
- Stored in `ai_directory_results` (phase = `summary`)
- Displayed as a green banner in `MainWindow.axaml` above the file table, binding to `MainViewModel.SelectedDirSummary`

### AI Pipeline glue
- `AiClassificationCoordinator` — single `IsAvailableAsync` check at start (not 3×)
- `IOllamaService` injected into coordinator (not re-resolved)
- All phase-level availability rechecks removed from individual services
- Debug logging active: `Microsoft.Extensions.Logging.Debug` provider + `AddDebug()` in `ServiceConfigurator`
- Prompt + truncated response logged at `Information` level per Ollama call

### UI additions
- `SelectedDirSummary` observable property in `MainViewModel`
- `GetByDirectoryPathAsync(jobId, fullPath)` added to `IDirectoryClassificationRepository`
- Directory summary loaded in `OnDirectorySelected` → `LoadDirectorySummaryAsync`
- Green `Border` banner in `MainWindow.axaml` bound to `SelectedDirSummary`, visible only when non-empty
- `RevealInExplorerCommand` in `FolderTreeItemViewModel` → `explorer.exe "{path}"`
- Right-click `ContextMenu` with "Im Explorer öffnen" in `TreeView.ItemTemplate`

### Scan exclusions
- `AppData` (entire subtree) added to `HeuristicsOptions.SkipPatterns`
- Replaces the earlier partial entries `AppData\Local\Temp` + `AppData\LocalLow`
- Dot-directories already handled: `name.StartsWith('.')` → `ScanDecision.Skip` (unless UserDataPattern)

---

## Key files

| File | Purpose |
|---|---|
| `src/SmartFileOrganizer.Infrastructure/Ollama/OllamaService.cs` | All Ollama HTTP logic: Phase 1/2/3 prompts, `ParseResponse`, `think=false`, logging |
| `src/SmartFileOrganizer.Application/Services/AiClassificationCoordinator.cs` | Pipeline orchestration, pause/resume/cancel |
| `src/SmartFileOrganizer.Application/Services/DirectoryPreAssessmentService.cs` | Phase 1 implementation + sampling logic |
| `src/SmartFileOrganizer.Application/Services/ClassificationService.cs` | Phase 2 batch loop |
| `src/SmartFileOrganizer.Application/Services/DirectorySummaryService.cs` | Phase 3 bottom-up directory summarization |
| `src/SmartFileOrganizer.Infrastructure/Persistence/FileRepository.cs` | `GetPendingAiAnalysisAsync` (file_type IN 1,2,4), `GetFileInfoForDirectoryAsync` |
| `src/SmartFileOrganizer.Infrastructure/Persistence/DirectoryClassificationRepository.cs` | `UpsertAsync`, `GetByNodeAndPhaseAsync`, `GetByDirectoryPathAsync` |
| `src/SmartFileOrganizer.Desktop/Services/AiClassificationInputPreparer.cs` | Per-file evidence builder (video: text-only, image: base64, doc: text preview) |
| `src/SmartFileOrganizer.Desktop/ViewModels/MainViewModel.cs` | `SelectedDirSummary`, `LoadDirectorySummaryAsync`, `OnDirectorySelected` |
| `src/SmartFileOrganizer.Desktop/ViewModels/FolderTreeViewModel.cs` | `RevealInExplorerCommand` |
| `src/SmartFileOrganizer.Desktop/Views/MainWindow.axaml` | Directory summary banner, right-click context menu on tree nodes |
| `src/SmartFileOrganizer.Scanning/Engine/HeuristicsEngine.cs` | `SkipPatterns` incl. `AppData` |
| `src/SmartFileOrganizer.Desktop/ServiceConfigurator.cs` | DI wiring, `AddDebug()`, explicit `MainViewModel` factory |
| `%APPDATA%\SmartFileOrganizer\settings.json` | **User override file** — check here first when debugging unexpected behavior |

---

## Verified behavior

- Build: 0 errors / 0 warnings
- Phase 1: ~1–6 s per directory
- Phase 2: ~1–3 s per file (image/doc/video)
- Phase 3: ~1–2 s per directory
- `think: false` confirmed working — no more 90s hangs
- Video files classified by filename/path correctly
- Directory summary banner visible after AI scan completes
- gemma4:26b field alias handling confirmed: `classification` → `category`, missing `confidence` → 0.85

---

## Immediate next priorities

### 1. Action pipeline (highest value for usability)
- Add `PlannedAction` model (source path, operation: move/copy/delete, target path)
- UI: "Plan Actions" button → shows planned operations in a review dialog
- Execute with per-file logging
- Start with the simplest case: move to a user-defined target folder

### 2. Bulk review workflow
- Filter file table by: TrashCandidate, ReviewNeeded, low confidence (<50%)
- Bulk approve / reject selected rows
- Show AI error details when `FileNodeStatus.Error`

### 3. Export
- Export current scan results as CSV (filename, category, confidence, summary, suggested_target)

---

## Operational notes

- **Test model**: `gemma4:26b` with `think: false`. Do not remove `think: false` — response times jump to 90s+
- **Settings override**: `%APPDATA%\SmartFileOrganizer\settings.json` wins over `appsettings.json`
- **Logs**: visible in VS → Output → Debuggen (Debug channel) during debug runs
- **New scan needed**: after code changes to AI pipeline, delete the SQLite DB or start a new scan job — old `ai_directory_results` rows have `phase='pre_assessment'`/`'summary'` and will be treated as "already done"
- **Ollama timeout**: keep `TimeoutSeconds` at 180 in settings.json — 90 was the root cause of Phase 3 hanging


- scanning large directory trees into SQLite
- browsing directories/files in the desktop UI
- viewing file details and previews
- automatically starting post-scan AI classification
- pausing/resuming/stopping AI classification independently from the scan
- configuring Ollama base URL, model, and `keep_alive` in the UI
- warming the chosen Ollama model before a longer classification run

## What changed most recently

### Ollama integration

- classification is now really wired into the product flow after scan completion
- local models are loaded from Ollama with `/api/tags`
- the selected model is persisted to `%AppData%\\SmartFileOrganizer\\settings.json`
- the default high-end model remains `qwen3-vl:30b`
- `keep_alive` is supported and exposed in the UI
- a warm-up action can preload the model into VRAM using an empty `/api/chat` request

### Real multimodal classification

- AI requests now use `/api/chat`
- image files are sent as base64 images
- PDFs are represented by the rendered first page image
- text-like files contribute extracted preview text
- metadata remains part of the prompt for path/extension/context clues
- structured JSON output is requested via an explicit schema

## Important files to know

### Ollama and AI pipeline

- `src/SmartFileOrganizer.Infrastructure/Ollama/OllamaService.cs`
  - local model listing
  - warm-up requests
  - multimodal `/api/chat` classification
  - `keep_alive` handling

- `src/SmartFileOrganizer.Application/Services/AiClassificationCoordinator.cs`
  - separate post-scan AI execution flow
  - pause/resume/cancel semantics

- `src/SmartFileOrganizer.Application/Services/ClassificationService.cs`
  - batch iteration over AI-eligible files
  - persistence of results/status

- `src/SmartFileOrganizer.Desktop/Services/AiClassificationInputPreparer.cs`
  - converts files into multimodal evidence for Ollama

- `src/SmartFileOrganizer.Infrastructure/Ollama/OllamaOptions.cs`
  - model/base URL/timeout/keep-alive settings

### UI and settings

- `src/SmartFileOrganizer.Desktop/ViewModels/MainViewModel.cs`
  - toolbar actions for model loading/saving/warm-up
  - scan + AI orchestration and refresh behavior

- `src/SmartFileOrganizer.Desktop/Views/MainWindow.axaml`
  - toolbar layout including AI status and Ollama controls

- `src/SmartFileOrganizer.Desktop/Services/OllamaSettingsService.cs`
  - persists mutable Ollama settings to app data

- `src/SmartFileOrganizer.Desktop/appsettings.json`
  - built-in defaults distributed with the app

## Verified behavior from the last session

- `dotnet build SmartFileOrganizer.slnx` succeeds
- the model list can be read from the running Ollama server
- a live `/api/generate` request with a valid local model succeeded during debugging
- the earlier 404s were caused by a non-existent model name, not by network failure
- the current default model is intentionally a large vision-capable local model because the target machine has sufficient VRAM

## Current known limitations

### AI understanding limitations

- PDFs currently contribute only the first rendered page, not full-document reasoning
- text extraction is preview-sized, not a full content ingestion pipeline
- there is no OCR subsystem yet beyond what the vision model can infer from image/PDF render inputs
- Office formats and more complex binaries are not yet deeply parsed

### Product workflow limitations

- review/override UI is still missing
- action planning/execution (move/copy/archive to NAS or target folders) is still missing
- per-file AI errors are persisted but not yet surfaced in a rich review experience
- the AI pipeline is intentionally conservative and mostly sequential; it has not been aggressively tuned for throughput yet

## Suggested next priorities

1. **Improve document understanding quality**
   - multi-page PDF support
   - richer text extraction / OCR
   - prompt/schema refinement around “valuable user data vs trash”

2. **Build the review workflow**
   - show AI summary/confidence/errors more explicitly
   - allow category/target overrides
   - let the user confirm or reject suggestions

3. **Design the action pipeline**
   - generate safe file operation plans
   - preview changes before execution
   - add logging and rollback-minded behavior

4. **Profile AI throughput**
   - decide whether sequential processing is still the right default
   - consider bounded concurrency only after measuring real gains and UX trade-offs

## Operational notes for the next agent

- do not move AI inference into the scan loop unless there is a very strong reason and hard evidence that it will not damage scan responsiveness
- preserve the OSS-friendly dependency stance
- preserve the current default model unless the user explicitly wants a different default
- if testing Ollama behavior, check `/api/tags` first to verify the exact installed model names
- remember that a large model may need a long initial load into VRAM; `keep_alive` and manual warm-up are expected parts of the workflow