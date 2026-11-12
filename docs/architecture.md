# Architecture

## Solution structure

The repository is intentionally split into focused layers.

- `src/SmartFileOrganizer.Domain`
  - domain models, enums, and core interfaces
- `src/SmartFileOrganizer.Application`
  - orchestration services and use-case oriented business logic
- `src/SmartFileOrganizer.Infrastructure`
  - persistence and schema migrations, filesystem access, Ollama HTTP integration
  - `Content/`: UI-free file evidence extraction (`FileContentExtractor`) and the model input builder (`AiClassificationInputPreparer`)
- `src/SmartFileOrganizer.Scanning`
  - scan engine, heuristics, file type detection
- `src/SmartFileOrganizer.Hosting`
  - composition root for everything except the GUI; no Avalonia reference
  - resolves database and settings paths, ships the default `appsettings.json`
- `src/SmartFileOrganizer.Desktop`
  - Avalonia UI, MVVM view models, GUI-only services (preview bitmap adapter)
- `src/SmartFileOrganizer.Cli`
  - command line `sfo` on top of the Hosting project; commands and the JSON contract are described in [cli.md](cli.md)
- `tests/*`
  - unit and integration-style validation for the core logic
  - `SmartFileOrganizer.Hosting.Tests` references only the Hosting project and proves the core runs without the GUI
  - `SmartFileOrganizer.Cli.Tests` runs `sfo` in-process against a temp folder and database

## Design intent

The UI must not own scanning, SQL generation, or Ollama HTTP logic. Those responsibilities live behind services and repositories so they remain testable and replaceable.

Everything except views, view models and bitmap conversion must run without Avalonia, so that a CLI or a test can scan and analyse with the same code the GUI uses.

## Composition (Hosting)

`services.AddSmartFileOrganizerCore(configure?)` registers configuration, repositories, scanning, the Ollama client, evidence extraction, `OllamaSettingsService` and all application services. After building the provider, call `provider.InitializeSmartFileOrganizerDatabaseAsync()` (creates the file if needed and applies pending migrations).

The caller adds its own logging providers. To substitute a service (for example a fake `IOllamaService` in tests), register it after `AddSmartFileOrganizerCore`.

The desktop `ServiceConfigurator` calls the same method and only adds console/debug logging, `FilePreviewService` and the view models.

The CLI calls it once per invocation with `ReloadSettingsOnChange = false`, the `--db` option as `DatabasePath`, and a logging provider that writes to stderr only.

### Event-driven vs. awaited runs

The GUI starts work in the background and follows it through events: `ScanJobService.StartAsync` (`JobStateChanged`) and `AiClassificationCoordinator.StartOrResumeAsync` (`StateChanged`, with pause and resume). Hosts without an event loop await the same work instead:

- `ScanJobService.RunAsync(jobId, progress, ct)` returns the persisted job. Cancelling stores the job as `Paused`, so it can be resumed, and throws `OperationCanceledException`.
- `AiClassificationCoordinator.RunAsync(jobId, phases, progress, ct)` runs the selected `AiPhases` and returns one result per phase; `OllamaAvailable = false` means nothing ran. Both paths share the phase loop.
- `JobStatusService.GetAsync(jobId)` returns the job with counters per phase and recent errors (`IJobStatisticsRepository`).

### File locations

Resolved once by `SmartFileOrganizerPaths.Resolve` and registered as `SmartFileOrganizerPaths`:

| | First match wins |
|---|---|
| Database | `SmartFileOrganizerHostOptions.DatabasePath` → `SFO_DB` → `<AppData>/SmartFileOrganizer/data.db` |
| User settings | `SmartFileOrganizerHostOptions.SettingsPath` → `SFO_SETTINGS` → `settings.json` next to the database if the database was redirected → `<AppData>/SmartFileOrganizer/settings.json` |

`<AppData>` is `Environment.SpecialFolder.ApplicationData`. Settings follow a redirected database so that a scratch or test database never reads or overwrites the user's real settings.

Other options: `AppSettingsDirectory` (where the shipped `appsettings.json` defaults are read from; default: application base directory) and `ReloadSettingsOnChange` (default `true`).

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

`FileContentExtractor` (Infrastructure) reads the content once, without UI types; `FilePreviewService` (Desktop) only decodes the returned image bytes into an Avalonia bitmap. Preview and model input therefore come from the same code.

- images: original file bytes
- PDFs: page 1 rendered via Docnet/PDFium with a white background, encoded as PNG by a small built-in encoder
- `.docx`, `.xlsx`, `.odt`, `.ods`: text extracted from the package XML
- text-like files: truncated text excerpt (12,000 characters)

## AI flow (current architecture)

The post-scan AI pipeline runs as three sequential phases coordinated by `AiClassificationCoordinator`:

### Phase 1 — Directory verdict and pre-assessment (`DirectoryPreAssessmentService`)

Every directory of the job gets a verdict: what lies here, and can it be handled as a whole
([#7](https://github.com/crack666/smart_file_organizer/issues/7)). Values are fixed German codes,
defined once on the Domain enums and shared by database and JSON (`VerdictCodes`):

| Verdict | Meaning |
|---|---|
| `einheit` | project, program, game or backup that only makes sense as a whole; never split up |
| `einheitlich` | collection of similar content (photos of one trip, invoices of one year) |
| `gemischt` | storage without a common purpose (desktop, downloads); handled file by file |
| `leer` | no files, neither directly nor below |
| `system` | operating system, tool or cache data, or excluded by the scan |

Retention is `behalten`, `pruefen` or `entbehrlich`; the source is `regel` (a rule decided, no model
call) or `modell`.

1. All `directory_nodes` of the job are loaded, including directories the scan skipped. Directories with
   a verdict in `directory_verdicts` are done (resume); rows with an error are retried.
2. For each pending directory `DirectoryProfileBuilder` computes a profile from scan data only:
   distribution by extension and file type (count and bytes), oldest and newest modification and the
   span in days, naming patterns (`camera`, `date-prefix`, `screenshot`, `copy-suffix`), unit markers
   (`dir:.git`, `dir:node_modules`, `file:package.json`, `ext:.sln`, `program:exe+dll`,
   `game:exe+_Data`, backup images, …) and a hint (`einheitlich` from 80 % of one file type or naming
   series, `gemischt` from three or more types with none above 50 %).
3. `DirectoryVerdictRules` decide the clear cases without the model, in this order: below a directory
   judged `einheit` (`inside-unit`), skipped by the scan (`scan-skip`), name starting with a dot
   (`hidden`), unit markers (`marker`), no files and no subdirectories (`empty`).
4. Directories are processed level by level from the root. What no rule decides goes to Ollama in
   parallel (`PreAssessDirectoryAsync`): file list, subdirectory names and the profile. The model returns
   verdict, purpose, retention and reason, together with the sampling fields used so far
   (`homogeneity`, `anomalous_files`, `sampling_strategy`, `suggested_sample_size`).
   `PreAssessmentResponseParser` reads the answer; a missing or unknown verdict is stored as an error.
   A directory the model judges `einheit` spares all calls below it.
5. Directories without direct files but with subdirectories are decided last, bottom-up, from their
   subdirectories (`container`): all empty → `leer`; only system → `system`; all units, or all
   `einheitlich` with the same dominant file type → `einheitlich`; otherwise `gemischt`.
6. The verdict is stored in `directory_verdicts`; the pre-assessment for sampling in
   `ai_directory_results` (phase=`pre_assessment`) for directories Phase 2 and 3 work on. Units and
   system directories decided by rule get `sampling_strategy = skip`, so their files are marked
   `Skipped`; directories inside a unit get no pre-assessment row and no summary of their own.
7. `ApplySamplingAsync` marks non-sampled `FileNode` rows as `Skipped` (unchanged; reworked in
   [#8](https://github.com/crack666/smart_file_organizer/issues/8)).

`DirectoryQueryService.GetVerdictAsync` and `GetVerdictsAsync` read verdicts with their profile.

### Phase 2 — Per-File Classification (`ClassificationService`)

1. `GetPendingAiAnalysisAsync` selects `Discovered` files with `file_type IN (1, 2, 4)` — Image, Video, Document
2. `IClassificationInputPreparer` (implemented by `AiClassificationInputPreparer` in `Infrastructure/Content`) builds evidence per file type:
   - **Image** (≤ `MaxImageUploadBytes`): base64 image payload
   - **Image** (oversized): text-only note about size
   - **Video**: `[Video file. Classify based on the filename and directory name only.]` — no bytes
   - **Document** (≤ `MaxDocumentUploadBytes`): rendered page image or extracted text preview
3. `ClassifyAsync` sends a structured `/api/chat` request with `think: false`
4. `ParseResponse` handles gemma4's field aliasing (`classification` → `category`, missing `confidence` → 0.85)
5. The schema also asks for `sensitive` (array of marker tokens); parsed into `FileClassification.SensitiveMarkers`
6. A marked file (rule or model) proposed as `TrashCandidate` is stored as `ReviewNeeded`
7. Result stored in `ai_results`; file status updated to `AiAnalyzed` or `Error`

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

## Sensitive files

Spec: [#9](https://github.com/crack666/smart_file_organizer/issues/9); the decision and its limits are in [engineering-decisions.md](engineering-decisions.md#8-sensitive-files-local-and-external-readers).

- `SensitiveMarker` (Domain, flags) with fixed tokens in `SensitiveMarkerNames`: `zugangsdaten`, `schluessel`, `private_nachricht`, `ausweis`, `finanzen`, `gesundheit`. Database and JSON use the tokens, comma-separated, `NULL` for none.
- `SensitiveFileRules` (Application/Sensitivity) runs in `ScanEngine` and `ScopedRefreshService` for every file: exact names, extensions, folders, keywords, and for files up to 256 KB that are not image, video, audio, archive, executable, font or database a header probe of the first 8 KB through `IFileSystemAccessor.ReadPrefix`. Result: `FileNode.SensitiveMarkers` and `SensitiveRule` (rule ids such as `name:id_rsa`, `header:private-key`).
- The local model reports markers in the Phase 2 call (`FileClassification.SensitiveMarkers`).
- `SensitiveOutputGuard` (Application/Sensitivity) is the single exit for file data:

```csharp
var options = new SensitiveOutputOptions { IncludeSensitive = includeSensitiveFlag };
FileOutputRecord record = SensitiveOutputGuard.Project(fileNode, options, textExcerpt);
string json = SensitiveOutputGuard.ToJson(record);   // or ToJson(records) for an array
```

`fileNode` needs `Classification` and `Override` loaded (as `IFileRepository.GetByIdAsync` and `GetByDirectoryAsync` return them). A protected record has `protected: true`, the marker tokens in `sensitive`, a neutral `summary`, and `null` for excerpt, raw response, model summary, target, importance, confidence, model and error. `ScanExportService` formats the same record as CSV (columns `sensitive` and `sensitive_rule` appended at the end).

## Ollama configuration flow

1. default settings come from `src/SmartFileOrganizer.Hosting/appsettings.json` (copied into every referencing app's output)
2. user overrides are saved to the resolved settings file (see "File locations" above)
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
- `file_nodes` — scanned files with metadata, type, AI status, rule-based sensitive markers (`sensitive_markers`, `sensitive_rule`)
- `ai_results` — Phase 2 per-file classification results, including model sensitive markers (`sensitive_markers`)
- `ai_directory_results` — Phase 1 pre-assessment + Phase 3 directory summaries (distinguished by `phase` column)
- `directory_verdicts` — one verdict per directory (step 3): `verdict`, `source` (`regel`/`modell`), `rule`,
  `purpose`, `retention`, `reason`, `unit_directory_node_id` (the directory that is the whole, for `einheit`),
  `profile_json` (snake_case), `model_used`, `raw_response`, `judged_at`, `error`; either `verdict` or `error` is set
- `user_overrides` — manual category/target corrections
- `db_version` — one row per applied schema step (`version`, `applied_at`)

## Schema migrations

The schema is built from numbered steps in `src/SmartFileOrganizer.Infrastructure/Persistence/Migrations/`. `DatabaseContext.InitializeAsync` sets `journal_mode=WAL` (not allowed inside a transaction) and then runs `SchemaMigrator`.

- The current version is `MAX(version)` in `db_version`.
- Each pending step runs in its own transaction together with its `db_version` row; a failing step leaves the database at the previous version.
- A database created before migrations existed has the step-1 tables but no `db_version` row; it is recorded as version 1 without re-running step 1.
- A database whose version is higher than the newest step in the build is refused.

### Adding a step

1. Add one file `Migrations/M000N_ShortName.cs` with a class implementing `ISchemaMigration`, or deriving from `SqlSchemaMigration` when plain SQL is enough. `Version` is `N`.
2. Take the next free number on `master`. Steps are discovered by reflection, so no shared list is edited.
3. Versions must be exactly `1..N`. If two branches picked the same number, startup and `SchemaMigrationTests` fail with a "Duplicate schema migration versions" message; the branch merged second renumbers its step (file name and `Version`).
4. Never change a step that has been merged; add a new one. Step 1 (`M0001_InitialSchema`) is the schema as it existed before migrations. Step 2 (`M0002_SensitiveMarkers`) adds the sensitive-marker columns.
5. Do not put `PRAGMA journal_mode` or other transaction-incompatible statements in a step.

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
