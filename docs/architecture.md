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

## AI flow (target architecture)

The intended future flow is:

1. scan job identifies AI-eligible files
2. `ClassificationService` consumes eligible files in batches
3. `IOllamaService` classifies content via local Ollama HTTP API
4. results persist to `ai_results`
5. UI refreshes and shows category, confidence, summary, and suggested target

This flow is **partially implemented in code but not yet fully connected to the active UI/job lifecycle**.

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
- configurable scan rules
- additional preview providers
- more advanced AI pipelines
- action planning/execution services
- storage analytics and treemap visualization
