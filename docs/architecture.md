# Architecture

## Solution structure

The repository is intentionally split into focused layers.

- `src/SmartFileOrganizer.Domain`
  - domain models, enums, and core interfaces
- `src/SmartFileOrganizer.Application`
  - orchestration services and use-case oriented business logic
- `src/SmartFileOrganizer.Infrastructure`
  - persistence, filesystem access, Ollama HTTP integration
- `src/SmartFileOrganizer.Scanning`
  - scan engine, heuristics, file type detection
- `src/SmartFileOrganizer.Desktop`
  - Avalonia UI, MVVM view models, DI composition, preview services
- `tests/*`
  - unit and integration-style validation for the core logic

## Design intent

The UI must not own scanning, SQL generation, or Ollama HTTP logic. Those responsibilities live behind services and repositories so they remain testable and replaceable.

## Runtime flow

## Scan flow

1. user selects a root folder in the desktop UI
2. `MainViewModel` creates a scan job through `ScanJobService`
3. `ScanJobService` starts `IScanEngine`
4. `ScanEngine` walks directories iteratively, applies heuristics, and writes batches to SQLite
5. progress events flow back into the UI
6. tree and file views query persisted state from repositories/services

## Browse flow

1. user selects a directory in the tree
2. `FolderTreeViewModel` raises a selection event
3. `MainViewModel` loads files for that directory through `FileQueryService`
4. `FileTableViewModel` exposes rows for the grid
5. selected file details are loaded through `FileQueryService`
6. `FileDetailViewModel` maps metadata and asks `FilePreviewService` for preview content

## Preview flow

- images load directly as Avalonia bitmaps
- text-like files are read as truncated text previews
- PDFs render page 1 via Docnet/PDFium to an Avalonia bitmap

## AI flow (current architecture)

The post-scan AI pipeline runs as three sequential phases coordinated by `AiClassificationCoordinator`:

### Phase 1 — Directory Pre-Assessment (`DirectoryPreAssessmentService`)

1. All `directory_nodes` for the job are loaded, ordered by depth
2. Directories already in `ai_directory_results` (phase=`pre_assessment`) are skipped (resume support)
3. For each pending directory, `GetFileInfoForDirectoryAsync` returns all direct files (all types)
4. A file list prompt is sent to Ollama via `PreAssessDirectoryAsync`
5. Ollama returns: `sampling_strategy`, `sample_size`, `anomalous_file_ids`, `homogeneity`, `suggested_area`
6. `ApplySamplingAsync` marks non-sampled `FileNode` rows as `Skipped`
7. Result stored in `ai_directory_results` (phase=`pre_assessment`)

### Phase 2 — Per-File Classification (`ClassificationService`)

1. `GetPendingAiAnalysisAsync` selects `Discovered` files with `file_type IN (1, 2, 4)` — Image, Video, Document
2. `IClassificationInputPreparer` builds evidence per file type:
   - **Image** (≤ `MaxImageUploadBytes`): base64 image payload
   - **Image** (oversized): text-only note about size
   - **Video**: `[Video file. Classify based on the filename and directory name only.]` — no bytes
   - **Document** (≤ `MaxDocumentUploadBytes`): rendered page image or extracted text preview
3. `ClassifyAsync` sends a structured `/api/chat` request with `think: false`
4. `ParseResponse` handles gemma4's field aliasing (`classification` → `category`, missing `confidence` → 0.85)
5. Result stored in `ai_results`; file status updated to `AiAnalyzed` or `Error`

### Phase 3 — Directory Summaries (`DirectorySummaryService`)

1. All directories ordered deepest-first (bottom-up synthesis)
2. Directories already in `ai_directory_results` (phase=`summary`) are skipped
3. For each directory: fetch pre-assessment + all file nodes via `GetByDirectoryAsync`
4. Build `summaryLines`: for each file, use `Classification.Summary` if available, else `name — FileType`
5. If `summaryLines` or `anomalyDescriptions` non-empty → call `SummarizeDirectoryAsync`
6. Result stored in `ai_directory_results` (phase=`summary`)
7. Displayed as a banner in the UI via `MainViewModel.SelectedDirSummary`

### Pipeline coordination

- Single `IsAvailableAsync` check at start of `AiClassificationCoordinator.StartOrResumeAsync`
- `think: false` in all three phase request bodies
- Full prompt + truncated response logged at `Information` level per Ollama call
- Pause/resume/cancel supported at any phase boundary via `WaitIfPausedAsync`

## Ollama configuration flow

1. default settings come from `src/SmartFileOrganizer.Desktop/appsettings.json`
2. user overrides are saved to `%AppData%\SmartFileOrganizer\settings.json`
3. `MainViewModel` can refresh the live model list from Ollama via `/api/tags`
4. the selected model, base URL, and `keep_alive` are applied to subsequent requests
5. a warm-up action can preload the selected model via an empty `/api/chat` request

## Key domain entities

### `ScanJob`
Tracks the lifecycle of a scan, including status, counters, and timing.

### `DirectoryNode`
Represents a directory in the scanned tree and stores summary/aggregation information.

### `FileNode`
Represents a scanned file, its metadata, and optional AI/user-derived enrichment.

### `FileClassification`
Stores the AI/heuristic classification result for a file.

### `UserOverride`
Stores a manual correction to category/target decisions.

## Persistence model

SQLite is the local source of truth. It stores both operational state and review-related data so future resume/recovery flows are possible.

Important tables:

- `scan_jobs` — job lifecycle, counters, status
- `directory_nodes` — scanned directories with heuristic metadata and stats
- `file_nodes` — scanned files with metadata, type, AI status
- `ai_results` — Phase 2 per-file classification results
- `ai_directory_results` — Phase 1 pre-assessment + Phase 3 directory summaries (distinguished by `phase` column)
- `user_overrides` — manual category/target corrections

## Architectural rules

- views contain no business logic
- view models contain no SQL and no direct filesystem traversal
- repositories encapsulate storage access
- external systems (filesystem, Ollama) are abstracted behind interfaces
- long-running work must remain off the UI thread
- large scans should process iteratively and in batches

## Extension points

The current architecture leaves room for:

- richer review workflows
- more complete multimodal document understanding (multi-page PDF, OCR, Office formats)
- configurable scan rules
- additional preview providers
- more advanced AI pipelines
- action planning/execution services
- storage analytics and treemap visualization
