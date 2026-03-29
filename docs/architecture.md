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

The current implemented flow is:

1. scan job identifies AI-eligible files
2. when the scan completes, `MainViewModel` starts `AiClassificationCoordinator`
3. `AiClassificationCoordinator` runs separately from the scan engine and supports pause/resume/cancel
4. `ClassificationService` consumes AI-eligible files in batches and prepares evidence for each file
5. `IClassificationInputPreparer` converts files into multimodal inputs:
  - images → base64 original image
  - PDFs → rendered first-page image
  - text-like files → truncated extracted text
6. `IOllamaService` sends structured multimodal `/api/chat` requests to Ollama
7. results persist to `ai_results`
8. the desktop UI refreshes file rows/details as classifications arrive

This flow is now **real and connected**, but still intentionally conservative in throughput and evidence depth.

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

- `scan_jobs`
- `directory_nodes`
- `file_nodes`
- `ai_results`
- `user_overrides`

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
