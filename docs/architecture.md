# Architecture

## Solution structure

The repository is intentionally split into focused layers.

- `src/SmartFileOrganizer.Domain`
  - domain models, enums, and core interfaces
- `src/SmartFileOrganizer.Application`
  - orchestration services and use-case oriented business logic
- `src/SmartFileOrganizer.Infrastructure`
  - persistence and schema migrations, filesystem access, Ollama HTTP integration
  - `Content/`: UI-free file evidence extraction (`FileContentExtractor`), metadata readers (`MediaMetadataReader`, `AudioMetadataReader`, `OfficePropertiesReader`, `ArchiveListingReader`, `FileSignature`), optional video frames (`VideoFrameExtractor`), the model input builder (`AiClassificationInputPreparer`) and the checksums for the library comparison (`ContentHasher`)
- `src/SmartFileOrganizer.Scanning`
  - scan engine, heuristics, file type detection
  - `Rules/`: scan rule files (`ScanRulesFile`, `ScanRuleSet`) and the shipped `default-rules.json` (embedded); format in [scan-rules.md](scan-rules.md)
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

- `ScanJobService.RunAsync(jobId, progress, ct)` returns the persisted job; the overload with `ScanRulesProfile rules` first switches the job to other scan rules (reopening a finished job). Cancelling stores the job as `Paused`, so it can be resumed, and throws `OperationCanceledException`.
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
4. `ScanEngine` builds a `HeuristicsEngine` from the rules stored at the job (the shipped rules if none), walks directories iteratively, decides each one with its reason (`dir_reason`), and writes batches to SQLite. On resume, directories stored as skipped or shallow are re-evaluated with the current rules (see [scan-rules.md](scan-rules.md))
   - each directory is listed through `IFileSystemAccessor.ListFiles`/`ListDirectories`, which throw on access or I/O errors; such a directory is stored with `dir_status = Error` and the message in `dir_reason`, and counted in the job's `error_count`, instead of looking empty
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
- `.docx`, `.xlsx`, `.pptx`, `.odt`, `.ods`: text extracted from the package XML
- text-like files: truncated text excerpt (12,000 characters)

## Comparison flow (source against library)

Issue: https://github.com/crack666/smart_file_organizer/issues/24

A scan job has a role (`ScanJob.Role`, column `scan_jobs.role`): `source`, a medium to clean up, or `library`, the user's organised archive (a NAS, a folder on disk). The role is set by `ScanJobService.CreateJobAsync(root, role)` and never changes. A `library` is only scanned and read for checksums; it is never changed and never analysed by a model.

`ContentComparisonService.CompareAsync(sourceJobId, againstJobId?, progress, ct)` (Application):

1. Checks both jobs (exist, roles, `completed`, root reachable); otherwise `ComparisonException`.
2. Loads the source files and only those library files that share a size or a name with a source file (`IContentComparisonRepository`, indexes on `file_nodes(job_id, size)` and `(job_id, name)`).
3. Stats every source file again; one that is gone or changed size since its scan is `error`.
4. Per size, reads the head-and-tail checksum of the library candidates once; per source file compares head-and-tail, then the full checksum (`IContentHasher`, implemented by `Infrastructure/Content/ContentHasher`: SHA-256, files opened read-only and shared). Equal full checksums of both files: `backed_up`, stored with the shared checksum (`match_hash`) and when it was computed from the files (`match_verified_at`, the older of the two). A later deletion step must re-read both files and compare with `match_hash` before it deletes anything. An unreadable candidate that could still hold the content: `error`. Otherwise same name in a folder of the same name: `differs`; else `new`.
5. Groups source files with equal full checksums into duplicate groups.
6. Stores checksums in `content_fingerprints` (by path, reused while size and modification time match, flushed every 256 entries and on cancel) and the result in `comparisons`/`comparison_files`, replacing an earlier result for the same pair.

`ContentComparisonService.GetStoredAsync(sourceJobId, againstJobId)` reads a stored result back, with duplicate groups, per-directory counts (`SummarizeDirectories`, recursive) and errors (file errors plus directories the scans could not read). Later steps (target suggestions, findings) read the comparison through this method.

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
6. The verdict is stored in `directory_verdicts`; the pre-assessment in `ai_directory_results`
   (phase=`pre_assessment`) for directories Phase 2 and 3 work on. Directories inside a unit get no
   pre-assessment row and no summary of their own.
7. The verdict, not the model's sampling fields, decides what happens to the files
   ([#8](https://github.com/crack666/smart_file_organizer/issues/8)): units and system directories
   (by rule or by the model) get `sampling_strategy = skip` and their files are marked `Skipped`;
   uniform directories with more eligible files than the sample size get `stratified_sample`;
   everything else, including a model verdict "empty" for a directory that has files, `analyze_all`.
   Anomalous file names from the model are resolved to ids and later drawn as outliers.

`DirectoryQueryService.GetVerdictAsync` and `GetVerdictsAsync` read verdicts with their profile.
The CLI read commands use `JobReadService` (see [Read side](#read-side-for-the-cli)).

### Phase 2 — Per-File Classification, in stages (`ClassificationService`, `DirectorySamplingService`)

Staged since [#8](https://github.com/crack666/smart_file_organizer/issues/8): sample first, single
judgement only where the sample disagrees or the directory is mixed. Quality comes before throughput:
whenever something is unclear the directory is raised to mixed and every file is judged.

**Plan.** For every directory judged `einheitlich` without a sample, `DirectorySampler.Draw` picks the
members from its eligible files (image, video, document):

- sensitive files (rule markers, `SensitiveOutputGuard.IsSensitive`): always, on top of the sample size;
  they never inherit;
- outliers, at most a third of the sample: files the pre-assessment named anomalous, smallest and
  largest, oldest and newest, sizes ten times off the median;
- strata: every extension group (`.jpeg` counts as `.jpg`, and so on) at least once, the remaining slots
  by largest deficit against its proportional share.

Within a group the order is FNV-1a of seed and file name; the seed is FNV-1a of job id and relative
directory path. The same directory in the same job therefore always draws the same files, whatever the
listing order. A directory with no more files than the sample size is drawn completely (nothing
inherits, but the sample still checks the verdict). More extension groups than the sample size raise the
directory to mixed at once. `IDirectorySampleRepository.CreateAsync` stores the sample (state `open`)
and sets the other eligible files from `Discovered` to `Deferred` in one transaction.

**Judge.** One producer-consumer pass over `Discovered` files, as before:

1. `GetPendingAiAnalysisAsync` selects all `Discovered` files of the job (until 12.11.2026: only Image, Video,
   Document; since then every type, [#22](https://github.com/crack666/smart_file_organizer/issues/22)); `Deferred` files
   are not selected. The sample still draws only images, videos and documents, so the other files of a uniform
   directory are judged one by one (by rule or by the model)
2. `IClassificationInputPreparer` (implemented by `AiClassificationInputPreparer` in `Infrastructure/Content`) builds the
   evidence (`OllamaClassificationInput`): content for the model, **facts** read without the model, and two lists,
   `Contains` and `Missing`, that tell the model what the evidence holds and what it lacks, so it does not guess.
   The first 4 KB decide the format (`FileSignature`, fact `detected_format`); for `Other`/`Unknown` files the format
   routes the file (a `.dat` that is a JPEG is read as an image).

   | Kind | Facts (source `metadata` unless noted) | Content for the model | Typical "missing" notes |
   |---|---|---|---|
   | Image | `width`, `height`, `taken_at`, `device`, `has_location` (true only; coordinates are never read out), `image_origin` (`rule`: `camera`, `screenshot`, `scan`) | the image (JPEG, PNG, GIF, WebP up to `MaxImageUploadBytes`); SVG as source text | capture date, device, origin, pixels of HEIC/TIFF/RAW or oversized files |
   | Video | `duration_seconds`, `width`, `height`, `taken_at`, `device`, `has_location` | two still frames (10 % and 50 %) if `ffmpeg` is on the PATH; the video itself never | frames without the tool, sound and picture content |
   | Audio | `title`, `artist`, `album`, `duration_seconds`, `audio_kind` (`rule`: `speech`, `music`) | genre, year, format, channels, bitrate as text | sound content (no transcription), tags |
   | Archive (ZIP, RAR, 7z, TAR) | `entry_count`, `unpacked_bytes`, `encrypted`, `archive_content` (`rule`: `photos`, `documents`, `media`, `software`, `code`, `mixed`) | listing of the first 60 entries (name, uncompressed size); at most 10,000 entries are read; nothing is unpacked | contents; `.gz`, `.bz2`, `.xz`, `.iso` are not listed |
   | PDF | `page_count`, `has_text` | embedded text of the first pages up to 6,000 characters; page 1 rendered; for a PDF without text (scan) also the middle page (up to `MaxDocumentUploadBytes`) | text of later pages, document properties |
   | Office (OOXML, ODF) | `title`, `author`, `created_at`, `page_count` | document text (12,000 characters) | legacy `.doc`/`.xls`/`.ppt` text |
   | Code, text, text-like `Other` | — | text excerpt | — |
   | Program, library, font, database, unknown binary | `detected_format` | none: a **rule result** (`RuleResult`), no model call | file content |

   Rule results: installers and programs → `SoftwareInstaller`, libraries and fonts → `SystemFile`, `Thumbs.db` →
   `TrashCandidate`, other databases → `ReviewNeeded`, unknown binaries → `Unknown`. They are stored with
   `is_ai_result = 0` and `model_used = rule:<id>` (`rule:program`, `rule:unknown-binary`, ...).
3. Otherwise `ClassifyAsync` sends a structured `/api/chat` request with `think: false`. The prompt lists the facts as
   reliable (`taken_at: 2019-07-14T10:22:33 (metadata)`), then "The evidence contains" and "Not available (do not guess these)"
4. `ParseResponse` handles gemma4's field aliasing (`classification` → `category`, missing `confidence` → 0.85)
5. The schema also asks for `sensitive` (array of marker tokens); parsed into `FileClassification.SensitiveMarkers`
6. For files of mixed and uniform directories the schema also asks for `keep_hint` (`temporary` or
   `worth_keeping`) with `keep_reason`; uniform ones too, so that the hint is there if the sample escalates
7. A marked file (rule or model) proposed as `TrashCandidate` is stored as `ReviewNeeded`, one proposed as
   `temporary` as `worth_keeping` (the model's reason is replaced by a neutral one)
8. Result stored in `ai_results` with `origin = model` (`rule` for a rule result) and `evidence`
   (`{"contains":[...],"missing":[...]}`), even when the run is being stopped; facts replace the file's rows in
   `file_facts`. For a file marked sensitive (rule or model), free-text facts (`title`, `author`, `artist`, `album`)
   are not stored. File status becomes `AiAnalyzed` (rule or model result) or `Error`

**Resolve.** `DirectorySamplingService.ResolveAsync` evaluates every `open` sample whose members are all
done (`DirectorySampler.Evaluate`). Failed members count as deviating. The sample confirms only if

- one category is the clear majority and it is a real category (not `Unknown` or `ReviewNeeded`),
- the share of deviating and failed members is at most `MaxDeviationShare`, and
- the model reported no sensitive marker for a member that the rules had not marked.

Confirmed (`ConfirmAsync`, one transaction): every file still `Deferred` gets an `ai_results` row with
`origin = inherited`, `is_ai_result = 0`, `inherited_from_directory_node_id`, the majority category, the
most frequent importance and target of the agreeing members, `model_used` empty, and the lowest confidence
of the agreeing members capped at `InheritedConfidenceCap`; status `Inherited`.
Contradicted (`EscalateAsync`, one transaction): the verdict becomes `gemischt` with the old one in
`escalated_from` and the reason in `escalation_reason`, the `Deferred` files return to `Discovered`, a
summary written while it was uniform is dropped. Another pass then judges them; the loop ends when a
resolve escalates nothing.

A stopped run leaves samples `open` and their files `Deferred`; the next run skips planning for them,
judges the remaining members and resolves. No directory is ever half inherited.

Thresholds (`DirectoryAnalysisOptions`, configuration section `Sampling`; the sample size falls back to
`Ollama:MaxSamplesPerDirectory`): `MaxSamplesPerDirectory` 15, `MaxDeviationShare` 0.1 (one outlier in
15 is tolerated, two escalate), `InheritedConfidenceCap` 0.6. These are starting values; the measurement
of [#10](https://github.com/crack666/smart_file_organizer/issues/10) should set them.

A focused rescan of a directory forgets its sample, judges it again, draws a new sample if it is uniform,
judges the members and resolves, so it spends the same calls as a run.

### Phase 3 — Directory Summaries (`DirectorySummaryService`)

1. All directories ordered deepest-first (bottom-up synthesis)
2. Directories already in `ai_directory_results` (phase=`summary`) are skipped
3. Directories without a pre-assessment are skipped
4. Mixed directories (also raised ones) and directories without a valid verdict go to the model. For all
   others the verdict already carries purpose and reason, and their files were either not analysed or
   inherited one result: `BuildVerdictSummary` writes the summary from the verdict (plus the outcome of a
   confirmed sample) without a model call, `model_used` empty, `theme` the verdict code
5. For the model: fetch all file nodes via `GetByDirectoryAsync` and build `summaryLines` (per file
   `Classification.Summary` if available, else `name — FileType`)
6. If `summaryLines` or `anomalyDescriptions` non-empty → call `SummarizeDirectoryAsync`
7. Result stored in `ai_directory_results` (phase=`summary`)
8. Displayed as a banner in the UI via `MainViewModel.SelectedDirSummary`

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
- `DirectoryTextGuard` (Application/Sensitivity) withholds model texts about a directory that name a marked file of its subtree (name check only; https://github.com/crack666/smart_file_organizer/issues/33).

## Read side for the CLI

`JobReadService` (Application) answers `sfo summary`, `dirs`, `files` and `show`
([cli.md](cli.md#reading-results)); `IJobReadRepository` / `JobReadRepository` hold its SQL.

- Read-only: the job is read through `IScanJobRepository`, not `ScanJobService.GetJobAsync`, which may store a normalised status.
- Files are streamed (`StreamFilesAsync`, Dapper unbuffered), so a listing holds one page in memory. The database filters job, directory, type and status; everything the output guard can change (category, confidence, markers) is filtered after `SensitiveOutputGuard.Project`, so filters see what the caller sees.
- Directory sizes and file counts are rolled up along the parent chain from `file_nodes`, because `directory_nodes.total_size` holds the direct files only and `recursive_file_count` is not filled by the scan.
- Directory listings and the summary stream every file of the job once to count marked files per subtree (the name rules are code, not SQL).

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

- `scan_jobs` — job lifecycle, counters, status, and the scan rules of the job (step 4: `rules_name`, `rules_source`, `rules_hash`, `rules_json`), role (`source`/`library`)
- `directory_nodes` — scanned directories with heuristic metadata and stats; `dir_status = Error` for directories the scan could not read
- `file_nodes` — scanned files with metadata, type, AI status, rule-based sensitive markers (`sensitive_markers`, `sensitive_rule`)
- `ai_results` — Phase 2 per-file classification results, including model sensitive markers (`sensitive_markers`) and the evidence notes (`evidence`, step 7)
- `file_facts` — facts per file read without the model (step 7): `file_node_id`, `fact`, `value`, `source`
  (`metadata`, `rule`, `model`), `recorded_at`; one row per file and fact. Codes are defined on the Domain enums
  `FileFactKey` and `FactSource` (`FileFactCodes.cs`)
- `ai_directory_results` — Phase 1 pre-assessment + Phase 3 directory summaries (distinguished by `phase` column)
- `directory_verdicts` — one verdict per directory (step 3): `verdict`, `source` (`regel`/`modell`), `rule`,
  `purpose`, `retention`, `reason`, `unit_directory_node_id` (the directory that is the whole, for `einheit`),
  `profile_json` (snake_case), `model_used`, `raw_response`, `judged_at`, `error`; either `verdict` or `error` is set;
  `escalated_from` and `escalation_reason` when a sample raised the directory to `gemischt` (step 6)
- `directory_samples` — one sample per uniform directory (step 6): `seed`, `state` (`open`, `confirmed`,
  `escalated`), `eligible_count`, `members_json` (file id, name, `role` = `stratum`/`size`/`date`/`anomaly`/`sensitive`,
  category, `deviates`, error), `max_deviation_share`, `majority_category`, `deviating_count`, `failed_count`,
  `deviation_share`, `inherited_count`, `inherited_confidence`, `reason`, `drawn_at`, `decided_at`
- `ai_results` columns of step 6: `origin` (`rule`, `model`, `inherited`, `human`, `agent`, `external`; NULL for
  older rows, all judged by the model), `inherited_from_directory_node_id`, `keep_hint` (`temporary`,
  `worth_keeping`), `keep_reason`. File statuses `Deferred` (waits for its sample) and `Inherited` are new
- `user_overrides` — manual category/target corrections
- `content_fingerprints` — head-and-tail and full SHA-256 per path, with the size and modification time (`last_write_ticks`) they belong to and the time the full checksum was computed (`full_hash_at`); shared by all jobs
- `comparisons` — one row per compared pair of source job and library job (`against_job_id` null: duplicates only), with `complete` and `error_count`
- `comparison_files` — per source file: `state` (`backed_up`, `new`, `differs`, `error`; null without library), `match_file_node_id`, `match_count`, `match_hash`, `match_verified_at`, `duplicate_group`, `error`
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
4. Never change a step that has been merged; add a new one. Step 1 (`M0001_InitialSchema`) is the schema as it existed before migrations. Step 2 (`M0002_SensitiveMarkers`) adds the sensitive-marker columns. Step 3 (`M0003_DirectoryVerdicts`) adds `directory_verdicts`. Step 4 (`M0004_ScanJobRules`) adds the scan rules of a job. Step 5 (`M0005_ContentComparison`) adds the comparison with the library. Step 6 (`M0006_DirectorySamples`) adds `directory_samples`, the escalation columns and the origin and keep-hint columns of `ai_results`; its new codes are English.
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
- additional preview providers
- more advanced AI pipelines
- action planning/execution services
- storage analytics and treemap visualization
