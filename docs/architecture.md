# Architecture

## Solution structure

The repository is intentionally split into focused layers.

- `src/SmartFileOrganizer.Domain`
  - domain models, enums, and core interfaces
- `src/SmartFileOrganizer.Application`
  - orchestration services and use-case oriented business logic
  - `Evaluation/`: truth files, scoring against them (`CorpusEvaluator`), test cases from corrections (`EvalCaseService`), stored runs and comparison (`EvalRunService`), scripted answers instead of Ollama; see [evaluation.md](evaluation.md)
  - `Bundles/`: the bundle builder (`BundleBuilder`, `EventGrouper`, `FileNameDates`) and its shipped `bundle-patterns.json` (embedded); see [bundles.md](bundles.md)
- `src/SmartFileOrganizer.Infrastructure`
  - persistence and schema migrations, filesystem access, Ollama HTTP integration
  - `Content/`: UI-free file evidence extraction (`FileContentExtractor`), metadata readers (`MediaMetadataReader`, `AudioMetadataReader`, `OfficePropertiesReader`, `ArchiveListingReader`, `FileSignature`), optional video frames (`VideoFrameExtractor`), the model input builder (`AiClassificationInputPreparer`) and the checksums for the library comparison (`ContentHasher`)
  - `Evaluation/`: generator of the small evaluation corpus (`SmallCorpusGenerator`) with minimal PNG and PDF writers
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

The patterns files of `sfo library show|match` go to `library-patterns/` next to the database unless `LibraryMatch:PatternsDirectory` is set.

`<AppData>` is `Environment.SpecialFolder.ApplicationData`. Settings follow a redirected database so that a scratch or test database never reads or overwrites the user's real settings.

Other options: `AppSettingsDirectory` (where the shipped `appsettings.json` defaults are read from; default: application base directory) and `ReloadSettingsOnChange` (default `true`).

## Runtime flow

## Scan flow

1. user selects a root folder in the desktop UI
2. `MainViewModel` creates a scan job through `ScanJobService`
3. `ScanJobService` starts `IScanEngine`
4. `ScanEngine` builds a `HeuristicsEngine` from the rules stored at the job (the shipped rules if none), walks directories iteratively, decides each one with its reason (`dir_reason`), and writes batches to SQLite. On resume, directories stored as skipped or shallow are re-evaluated with the current rules (see [scan-rules.md](scan-rules.md))
   - each directory is listed through `IFileSystemAccessor.ListFiles`/`ListDirectories`, which throw on access or I/O errors; such a directory is stored with `dir_status = Error` and the message in `dir_reason`, and counted in the job's `error_count`, instead of looking empty; the cause code is in `scan_error_cause`
   - before a directory is read, `IFileSystemAccessor.GetLink` tells whether it is a link (junction, symbolic link, mount point); a link is stored with `dir_status = Link`, `link_kind` and `link_target` and not entered unless a `follow_link` rule allows it, and a file that is a link goes to `file_links` instead of `file_nodes` ([scan-rules.md](scan-rules.md#links-and-read-errors))
   - `IFileSystemAccessor.GetFileSize` throws for an unreadable file; the row keeps `scan_error_cause` and `scan_error`
   - at the end the job counters are recounted from the stored rows, so found and processed agree
   - `IFileSystemAccessor.SameFile` answers `same`, `different` or `unknown` for two paths by volume and file id (Windows `GetFileInformationByHandleEx`, Linux `stat`); it is the check "source and target are not the same file" for the execution of a plan (https://github.com/crack666/smart_file_organizer/issues/12)
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

## Bundle flow

Issue: https://github.com/crack666/smart_file_organizer/issues/39; rules and thresholds in [bundles.md](bundles.md).

`BundleService.BuildAsync(jobId, options?, ct)` (Application) needs a `completed` job. It reads the directories and
files through `IJobReadRepository`, capture date, device and image origin through `ICaptureInfoSource`
(`FileFactCaptureInfoSource` reads the `taken_at`, `device` and `image_origin` rows of `file_facts`), and runs
`BundleBuilder` in memory: dumps by `DumpSignature` (outermost match wins), their parts by `keep_parts` and
`noise_paths`, named folders by `IsGenericName`, events by `EventGrouper`. `IBundleRepository.ReplaceAsync` stores
the tree in one transaction. `ListAsync` and `GetAsync` add, when read, the marked files per bundle
(`SensitiveOutputGuard.GetMatch`, rolled up along `parent_bundle_id`) and the comparison states of the latest
comparison with a library. `JobReadService.ListFilesAsync` filters by a bundle and its descendants
(`FileListFilter.BundleIds`), so files of a bundle pass the output guard like every other listing.

## Findings flow

Issue: https://github.com/crack666/smart_file_organizer/issues/26. Command: [cli.md, `sfo findings`](cli.md#sfo-findings-job);
codes: [glossary.md, Findings](glossary.md#findings).

A job ends in a short list of hints. `FindingService` (Application/Findings) computes them every time they are
read, from what earlier steps stored: scan rows and rule markers, analysis results and folder verdicts (with
`escalated_from`), bundles, the comparisons with and without a library, and the library map. It calls no model,
opens no scanned file and changes nothing on disk. Only decisions are stored (`finding_decisions`, step 14).

| Step | What it gives the findings | Without it |
|---|---|---|
| `sfo scan` | files, folders, rule markers, skipped folders | nothing to find |
| `sfo analyze` | category, keep hint, certainty per file; verdict and retention per folder; model markers | worth rests on bundles, names and file types, never above `likely` |
| `sfo bundles build` | device copies with worth-keeping and noise parts, program folders, named folders, loose photos | files summarized per folder; app data and program folders not recognised |
| `sfo compare --against <library>` | which content is in the library; duplicate groups | `not_backed_up` not computed; `in_noise` does not leave out backed-up files |
| `sfo compare` | duplicate groups | `duplicate` not computed |
| library map | a place for `misplaced` files | no place proposed |

The result carries `coverage` per kind (`complete`, `partial`, `not_computed` with a note) and `missing_steps`.

**Worth of a file** (`FindingRules.ValueOf`). A human override is certain. An analysis result counts with its
certainty (older rows: `likely` from confidence 0.8, else `uncertain`): keep hint `worth_keeping` keeps,
`temporary` drops; photo, video, document scan, document, invoice, personal document and letter are kept unless
importance is low; screenshot, audio, archive and code only with importance high or critical; trash candidate,
installer and system file are disposable. `review_needed` or `unknown` counts as keep (`review`) only for
pictures, videos, documents and audio and never among noise: "look at it" is no sign of worth (a trial on a
generated user profile had a hundred database files the rules sent to review fill a finding). Without a result
programs and fonts are disposable; among noise only camera photos and personal documents (office files and PDFs
not named readme, license, manual and the like) count, as `uncertain`; elsewhere folder retention, then the
bundle decide, never above `likely`.

**Noise** is the noise part of a device copy (`dump_noise`), a program folder (a software copy recognised by
programs and libraries alone), or, outside named folders and worth-keeping parts, a folder judged `mixed` (not
raised from `uniform` by a sample) or `disposable`.

| Kind | Arises from | Certainty | Action |
|---|---|---|---|
| `not_backed_up` | files worth keeping with comparison state `new` or `differs`, not among noise, not marked | weakest file certainty | `move` |
| `in_noise` | files worth keeping in a noise place; backed-up files left out | weakest file certainty | `move` |
| `misplaced` | a folder judged `uniform` (or raised from it), or without analysis a named folder, with at least 5 pictures, documents or audio files of which 80 % are one group; the not disposable files of another group | weakest file certainty, at most `uncertain` until the target content is checked | `move` with a target, else `leave` |
| `sensitive` | files marked by rule or model; skipped folders marked by name or place | header rule, or rule and model: `certain`; name or folder rule: `likely`; model alone: its certainty | `leave`, single decision |
| `duplicate` | identical files (same full SHA-256) of the source, marked files left out | `certain` | `delete_source` if every copy is in the library, `move` one copy with a library, `leave` without |

A file is in at most one of `not_backed_up`, `in_noise`, `sensitive`. `misplaced` may only rest on a target
whose content was checked (https://github.com/crack666/smart_file_organizer/issues/56). Until that check exists
every target says `content_check: not_checked`. The check plugs in at `IFindingTargetAdvisor`
(`LibraryFindingTargetAdvisor` wraps `LibraryTargetService.BestPlacesAsync`): `fits` lifts the cap to the target's
certainty, `contradicts` drops the target; the rules stay as they are. `sensitive` names path, marker and rule
ids, never content; files of `sfo findings show` pass the output guard; no checksum of a marked file is written.

**Summarized, then ranked.** One unit per worth-keeping part of a device copy, device copy without parts, or
named folder; loose files (events, scattered, no bundle) one unit per folder however many events it holds, so
300 loose photos are one finding; noise per noise part or program folder, else per folder; `sensitive` per
folder and marker set, inside noise per noise part and marker set; `duplicate` per set of folders; `misplaced`
per folder and foreign group. `not_backed_up` and `in_noise` split into a sure part (`certain`, `likely`) and an
unsure part (`uncertain`, `review`). Every sure finding comes before every unsure one; within each by kind
(`not_backed_up`, `in_noise`, `sensitive`, `misplaced`, `duplicate`), certainty, files, bytes, key. The key
(`in_noise-4e1f0a9b2c`) is the kind plus ten hex digits of the SHA-256 of a subject built from relative paths and
codes, so it survives `sfo bundles build` and a rescan of the same tree.

**Decisions.** `accept`, `reject`, `wrong`, `reopen` write or delete one row of `finding_decisions` and change no
file. Rejected and wrong findings are not listed again. A wrong finding also becomes a test case
([evaluation.md](evaluation.md#findings-reported-wrong)). The plan (https://github.com/crack666/smart_file_organizer/issues/11)
reads accepted findings with `FindingService.ListAsync(new FindingQuery { JobId = j, States = [FindingState.Accepted] })`:
`Action.Kind` (`PlanActionKind`), `Action.SingleDecision`, `Target` with `ContentCheck`, `FileIds`, `Directories`;
`snapshot_json` keeps what the finding said when accepted. A key that no longer appears rests on changed data.

**Thresholds**, section `Findings`; starting values, not measured ones:

| Setting | Default | Why |
|---|---|---|
| `MinFiles` | 1 | One personal document that is nowhere else is what the person wants to hear about. |
| `MinUncertainFiles` | 1 | Unsure findings are listed last anyway; hiding them would hide the only hint. |
| `MisplacedMinFolderFiles` | 5 | Below five files a folder has no clear purpose to deviate from. |
| `MisplacedDominantShare` | 0.8 | Four of five: a folder that is mostly one thing. |
| `DuplicateMinBytes` | 1 MiB | Smaller groups free nothing worth a decision ... |
| `DuplicateMinFiles` | 10 | ... unless there are many copies. Below both, copies are counted in `below_threshold`. |
| `MaxTargetLookups` | 50 | One rule run per group against the library; the largest groups first. |
| `Examples` | 3 | Enough to check a finding at a glance. |

_Trial, 12.11.2026, built `sfo` of this issue's branch, generated old Windows profile in a temp folder:_ 2,924
files, 2,476 of them app data in `Anwendungsdaten` and `Lokale Einstellungen/Anwendungsdaten` (two browser
profiles, a mail profile, caches, six messenger photos, two personal documents), plus documents, pictures,
desktop and downloads; scripted answers (`sfo eval --stub`), a library with one holiday folder. 11 findings: four
`not_backed_up`, one `in_noise` (9 files in app data), four `sensitive`, one `duplicate`, one unsure `misplaced`.
Before the grouping of sensitive files in noise and the review rule the same tree gave 12, with four sensitive
findings in app data and an unsure `in_noise` of 112 database files. The modern `AppData` is skipped by the
default scan rules and then shows as one skipped folder marked sensitive.

## AI flow (current architecture)

The post-scan AI pipeline runs as three sequential phases coordinated by `AiClassificationCoordinator`:

### Phase 1 — Directory verdict and pre-assessment (`DirectoryPreAssessmentService`)

Every directory of the job gets a verdict: what lies here, and can it be handled as a whole
([#7](https://github.com/crack666/smart_file_organizer/issues/7)). Values are fixed English codes,
defined once on the Domain enums and shared by database, JSON and prompts (`VerdictCodes`; all codes in
[glossary.md](glossary.md)). Until schema step 13 (12.11.2026) they were German (`einheit`, `gemischt`, ...);
the parser still reads those words in a model answer and stores the code. People see words from
`CodeLabels` (English or German, by the summary language), never the code itself:

| Verdict | Meaning |
|---|---|
| `unit` | project, program, game or backup that only makes sense as a whole; never split up |
| `uniform` | collection of similar content (photos of one trip, invoices of one year) |
| `mixed` | storage without a common purpose (desktop, downloads); handled file by file |
| `empty` | no files, neither directly nor below |
| `system` | operating system, tool or cache data, or excluded by the scan |
| `dump` | copy of a phone, computer, user profile or removable drive; only a rule gives it, never the model |

Retention is `keep`, `review` or `disposable`; the source is `rule` (a rule decided, no model
call) or `model`.

1. All `directory_nodes` of the job are loaded, including directories the scan skipped. Directories with
   a verdict in `directory_verdicts` are done (resume); rows with an error are retried.
2. For each pending directory `DirectoryProfileBuilder` computes a profile from scan data only:
   distribution by extension and file type (count and bytes), oldest and newest modification and the
   span in days, naming patterns (`camera`, `date-prefix`, `screenshot`, `copy-suffix`), unit markers
   (`dir:.git`, `dir:node_modules`, `file:package.json`, `ext:.sln`, `program:exe+dll`,
   `game:exe+_Data`, backup images, …) and a hint (`uniform` from 80 % of one file type or naming
   series, `mixed` from three or more types with none above 50 %).
3. `DirectoryVerdictRules` decide the clear cases without the model, in this order: below a directory
   judged `unit` (`inside-unit`), skipped by the scan (`scan-skip`), name starting with a dot
   (`hidden`), inside a dump at or below one of its noise paths (`dump-noise`), unit markers
   (`marker`), a dump (`dump`), no files and no subdirectories (`empty`).
   `dump` uses the one recognition the bundles and the library review use (`BundlePatterns.Recognise`,
   `bundle-patterns.json`, or `Bundles:PatternsFile` when set). Unpacked software is a `unit`, so
   everything inside it is `inside-unit` and none of its files is judged one by one. A copy of a phone,
   a user profile, a system drive or a USB stick gets the verdict `dump` (retention `review`) and is no
   unit ([#59](https://github.com/crack666/smart_file_organizer/issues/59)): below it, a directory at or
   below a `noise_paths` entry of the patterns (`AppData`, `Android/data`, `Lokale Einstellungen`; `Cookies` is not one)
   is `system` by `dump-noise` with retention `review`, and its files are skipped; every other part,
   the worth-keeping ones (`Documents`, `DCIM`) and folders no pattern names, goes on through the rules
   and the model like any directory. Files directly in the dump root (`NTUSER.DAT`) are analysed with
   `analyze_all`. The scan root is recognised as well (only signatures with `at_root`), so a profile
   gets the same verdicts whether it is the scan root or lies below it; a dump inside a dump (a phone
   backup in the documents of a profile) is a dump of its own, and its own noise paths count from it.
   Until Issue 59 (13.11.2026) every dump was a unit and the scan root was never one
   ([#50](https://github.com/crack666/smart_file_organizer/issues/50), measured on the corpus of
   [evaluation.md](evaluation.md)). Verdicts stored before schema step 16 stay as they were; a job
   needs a new scan (or a new job) to get `dump` verdicts. The bundles split a dump the same way, but
   count a folder no pattern names as noise ([bundles.md](bundles.md#worth-keeping-and-noise)); the
   verdict judges it, because a folder of its own name may hold anything.
4. Directories are processed level by level from the root. What no rule decides goes to Ollama in
   parallel (`PreAssessDirectoryAsync`): file list, subdirectory names and the profile. The model returns
   verdict, purpose, retention and reason, together with the sampling fields used so far
   (`homogeneity`, `anomalous_files`, `sampling_strategy`, `suggested_sample_size`).
   `PreAssessmentResponseParser` reads the answer; a missing or unknown verdict is stored as an error.
   A directory the model judges `unit` spares all calls below it.
5. Directories without direct files but with subdirectories are decided last, bottom-up, from their
   subdirectories (`container`): all empty → `empty`; only system → `system`; all units, all dumps
   (a `Users` folder with several profiles), or all `uniform` with the same dominant file type →
   `uniform`; otherwise `mixed`.
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

**Plan.** For every directory judged `uniform` without a sample, `DirectorySampler.Draw` picks the
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
4. `ParseResponse` handles gemma4's field aliasing (`classification` → `category`). A missing `confidence` is not
   assumed (until 12.11.2026 it became 0.85): it stays 0, `ConfidenceMissing` is set and the answer gets a second check
5. The schema also asks for `sensitive` (array of marker tokens); parsed into `FileClassification.SensitiveMarkers`
6. For files of mixed and uniform directories the schema also asks for `keep_hint` (`temporary` or
   `worth_keeping`) with `keep_reason`; uniform ones too, so that the hint is there if the sample escalates
7. A marked file (rule or model) proposed as `TrashCandidate` is stored as `ReviewNeeded`, one proposed as
   `temporary` as `worth_keeping` (the model's reason is replaced by a neutral one)
8. Second check (see below): an unsure or contradicted answer is asked again before anything is stored
9. Result stored in `ai_results` with `origin = model` (`rule` for a rule result), `certainty` and `evidence`
   (`{"contains":[...],"missing":[...]}`), even when the run is being stopped; facts replace the file's rows in
   `file_facts`. For a file marked sensitive (rule or model), free-text facts (`title`, `author`, `artist`, `album`)
   are not stored. File status becomes `AiAnalyzed` (rule or model result) or `Error`

**Second check** ([#23](https://github.com/crack666/smart_file_organizer/issues/23); `ResultCheckRules`,
`SecondCheckOptions`). What the tool calls sure has to be right, and what is unsure has to show as unsure. The
confidence a model states about itself is weakly calibrated, so certainty rests on agreement between independent
signals. `ResultCheckRules.Find` lists what speaks against a first answer (codes in `CheckReason`):

| Reason | When |
|---|---|
| `missing_confidence` | the model gave no confidence |
| `low_confidence` | confidence below `BelowConfidence` (0.7) |
| `unclear_category` | `ReviewNeeded` or `Unknown` |
| `kind_mismatch` | `Photo`/`Screenshot` for a file that is no image, `Video` for no video, `Audio` for no audio; header bytes (`detected_format`) decide, the extension where the header is ambiguous (`mp4` is also HEIC and MOV, `mp3` a weak test) |
| `rule_disagrees` | an unambiguous metadata rule: `image_origin = screenshot` against `Photo`, `image_origin = camera` with a device against `Screenshot`. The rule sets the category, no second call. A scan is not forced: a scanned print may be a photo |
| `verdict_disagrees` | retention of the directory verdict `disposable` and importance High or Critical, or `keep` and `TrashCandidate` or keep hint `temporary` |

Any reason except `rule_disagrees` leads to a second call, made right after the first in the same worker: the
preparer runs again with `EvidenceDepth.Extended` (PDF text up to 24,000 characters plus the middle and last page
rendered, 300 archive names, four video frames, 24,000 characters of text in the prompt), and the prompt gets a
context section with verdict, purpose and retention of the directory and up to `MaxNeighbours` (20) other files
with their category so far. It never sees the first answer. `ResultCheckRules.Combine`:

- same category, same keep hint (when both have one), and the second fits the kind rule: `agreed`, the second
  answer is stored, confidence `1 - (1 - c1)(1 - c2)` (a missing one counts as 0.5), certainty `certain`
  (`review` if the agreed category is `ReviewNeeded`/`Unknown`);
- the first broke the kind rule and the second fits it: `corrected`, the second stands, `likely`;
- otherwise `disagreed`: category `ReviewNeeded`, the higher importance, the lower confidence, no target, keep
  hint `worth_keeping` if either says so, all markers, a summary naming both categories only (no model text);
  certainty `review`;
- second call failed: `failed`, the first answer stays as `uncertain`.

Second check off (`Enabled = false`) or a file a person corrected (`user_overrides`): no call, outcome
`skipped`, `uncertain`. Every check is a row in `result_checks` with reasons, outcome, rule note and both answers
as JSON, written in one transaction with the result (`IClassificationRepository.SaveAsync`); rows are only
added. The sensitive review rule runs on both answers and on the stored result, so a marked file never becomes
`TrashCandidate` or `temporary` after a check either. A stop during either call stores nothing for the file: it
stays `Processing` and is judged anew on resume (also a first call that failed only because the run stopped; until
12.11.2026 that was stored as an error).

Certainty (`ai_results.certainty`, `ResultCertainty`) without a check: `certain` when a rule fact says the same as
the model (`image_origin` camera/screenshot/scan with Photo/Screenshot/DocumentScan, `audio_kind` with Audio),
otherwise `likely`. Rule results: `certain` at rule confidence 0.9 or more, else `uncertain`. Inherited results:
`likely`. When a sample confirms, its agreeing members go from `likely` to `certain` (the file fits the confirmed
sample of its directory). A member left in `review` is `ReviewNeeded` and therefore deviates, so a sample whose
members contradict their verdict escalates instead of passing the contradiction on to inherited files; inherited
results themselves are not checked one by one (they carry the sample's result and were never looked at).

Settings (`SecondCheckOptions`, configuration section `SecondCheck`): `Enabled` true, `BelowConfidence` 0.7,
`CheckUnclearCategory` true, `CheckVerdictContradictions` true, `MaxNeighbours` 20. Starting values; the
measurement of [#10](https://github.com/crack666/smart_file_organizer/issues/10) should set them.

Rule results (`origin = rule`) stay out of samples: they cost no call and would mix other kinds of files into the
majority vote.

**Retry.** `sfo analyze --retry-errors` calls `IFileRepository.ReleaseFailedAsync` before the pass: files with
status `Error` (not those a person corrected) go back to `Discovered`. Every write of a result counts
`ai_results.attempts` up and keeps the error it replaces in `previous_error`; a second call of a check is not an
attempt.

**Resolve.** `DirectorySamplingService.ResolveAsync` evaluates every `open` sample whose members are all
done (`DirectorySampler.Evaluate`). Members are counted by purpose (`DirectorySampler.PurposeOf`): `Photo`
and `Video` are one purpose (the photos and the phone clip of one occasion), and `Document`, `Invoice`,
`Letter`, `PersonalDocument` and `DocumentScan` are one purpose (kinds of documents); every other category
is a purpose of its own. Failed members count as deviating. The sample confirms only if

- one purpose is the clear majority and its most frequent category is a real one (not `Unknown` or
  `ReviewNeeded`), and
- the deviating and failed members are at most `MaxDeviationShare` of the sample, rounded down, but at
  least one (`ToleratedDeviations`): a single outlier never tips a small sample.

Otherwise the directory is raised to mixed, with two exceptions
([#50](https://github.com/crack666/smart_file_organizer/issues/50), from the first measurement with real
models):

- A directory with at most `SmallDirectoryMaxFiles` eligible files keeps the verdict of its
  pre-assessment whatever its sample says (state `confirmed`, reason "Kept: …"). All its files are members
  and judged on their own, nothing inherits, and two or three files make no majority worth the name; the
  pre-assessment saw the whole file list.
- A member the model marks sensitive although the rules had not does not raise the directory: the file
  carries its marker and the verdict stands. Other files may hold more of the same, so none of them
  inherits: `ConfirmAsync` gets no inherited results and returns the `Deferred` files to `Discovered`, and
  another pass judges them one by one. (Until 12.11.2026 such a sample raised the directory to mixed, so a
  folder of letters with one private letter was never uniform.)

Confirmed (`ConfirmAsync`, one transaction): every file still `Deferred` gets an `ai_results` row with
`origin = inherited`, `is_ai_result = 0`, `inherited_from_directory_node_id`, the most frequent category of
the majority purpose (on a tie the general one, `Photo` or `Document`), the most frequent importance and
target of the members in that category, `model_used` empty, and the lowest confidence of those members
capped at `InheritedConfidenceCap`; status `Inherited`. A `Deferred` file that gets no inherited result
returns to `Discovered`.
Contradicted (`EscalateAsync`, one transaction): the verdict becomes `mixed` with the old one in
`escalated_from` and the reason in `escalation_reason`, the `Deferred` files return to `Discovered`, a
summary written while it was uniform is dropped. Another pass then judges them; the loop ends when a
resolve escalates and releases nothing.

A stopped run leaves samples `open` and their files `Deferred`; the next run skips planning for them,
judges the remaining members and resolves. No directory is ever half inherited.

Thresholds (`DirectoryAnalysisOptions`, configuration section `Sampling`; the sample size falls back to
`Ollama:MaxSamplesPerDirectory`): `MaxSamplesPerDirectory` 15, `MaxDeviationShare` 0.1 (one outlier in
15 is tolerated, two escalate; one outlier in a sample of six is tolerated too), `SmallDirectoryMaxFiles` 3,
`InheritedConfidenceCap` 0.6. These are starting values; the measurement
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
- Full prompt + truncated response logged at `Information` level per Ollama call; the second call of a check
  (more content, names of neighbouring files) at `Debug` only
- Pause/resume/cancel supported at any phase boundary via `WaitIfPausedAsync`

## Sensitive files

Spec: [#9](https://github.com/crack666/smart_file_organizer/issues/9); the decision and its limits are in [engineering-decisions.md](engineering-decisions.md#8-sensitive-files-local-and-external-readers).

- `SensitiveMarker` (Domain, flags) with fixed tokens in `SensitiveMarkerNames`: `credentials`, `keys`, `private_message`, `identity`, `finance`, `health`. Database and JSON use the tokens, comma-separated, `NULL` for none. Dapper cannot read an enum through a type handler (it calls `Enum.Parse`), so `SensitiveMarkerColumnMap` maps the column `sensitive_markers` of `file_nodes` to a private string property that parses the tokens, and `ClassificationRepository` reads it from `ai_results` under another name and parses it in `Row.Finish`; until 12.11.2026 every read of a folder with a private-message marker failed (then stored as `private_nachricht`). Until step 13 (12.11.2026) the codes were German (`zugangsdaten`, ...); see [glossary.md](glossary.md).
- `SensitiveFileRules` (Application/Sensitivity) runs in `ScanEngine` and `ScopedRefreshService` for every file: exact names, extensions, folders, keywords, and for files up to 256 KB that are not image, video, audio, archive, executable, font or database a header probe of the first 8 KB through `IFileSystemAccessor.ReadPrefix`. Result: `FileNode.SensitiveMarkers` and `SensitiveRule` (rule ids such as `name:id_rsa`, `header:private-key`).
- The local model reports markers in the Phase 2 call (`FileClassification.SensitiveMarkers`).
- `SensitiveOutputGuard` (Application/Sensitivity) is the single exit for file data:

```csharp
var options = new SensitiveOutputOptions { IncludeSensitive = includeSensitiveFlag };
FileOutputRecord record = SensitiveOutputGuard.Project(fileNode, options, textExcerpt);
string json = SensitiveOutputGuard.ToJson(record);   // or ToJson(records) for an array
```

`fileNode` needs `Classification` and `Override` loaded (as `IFileRepository.GetByIdAsync` and `GetByDirectoryAsync` return them). A protected record has `protected: true`, the marker tokens in `sensitive`, a neutral `summary`, and `null` for excerpt, raw response, model summary, target, importance, confidence, model and error. `ScanExportService` formats the same record as CSV (columns `sensitive` and `sensitive_rule` appended at the end).
- `DirectoryTextGuard` (Application/Sensitivity) withholds model texts about a directory that name a marked file of its subtree (name check, a second net).
- `DirectorySummaryGuard` (Application/Sensitivity) decides whether a Phase 3 summary may be shown: `ai_directory_results.marked_inputs` lists the marked files that entered it as their neutral line; a summary that misses a file marked since then is withheld and rewritten by the next analysis.
- `SensitiveRulesBackfill` (Application/Sensitivity) runs the header rules again, before Phase 1, for files whose `file_nodes.sensitive_rules_version` is missing or lower than `SensitiveFileRules.Version`.
- Prompts and model answers are logged under the category `ModelTraffic` (Debug). The CLI drops that category on stderr and writes it, with `--verbose`, to `sfo-debug.log` next to the database (`DebugFileLoggerProvider`); for a marked file the prompt and answer are replaced by a placeholder. https://github.com/crack666/smart_file_organizer/issues/33

## Read side for the CLI

`JobReadService` (Application) answers `sfo summary`, `dirs`, `files` and `show`
([cli.md](cli.md#reading-results)); `IJobReadRepository` / `JobReadRepository` hold its SQL.

- Read-only: the job is read through `IScanJobRepository`, not `ScanJobService.GetJobAsync`, which may store a normalised status.
- Files are streamed (`StreamFilesAsync`, Dapper unbuffered), so a listing holds one page in memory. The database filters job, directory, type and status; everything the output guard can change (category, confidence, markers) is filtered after `SensitiveOutputGuard.Project`, so filters see what the caller sees.
- Directory sizes and file counts are rolled up along the parent chain from `file_nodes`, because `directory_nodes.total_size` holds the direct files only and `recursive_file_count` is not filled by the scan.
- Directory listings and the summary stream every file of the job once to count marked files per subtree (the name rules are code, not SQL).

## Library review

`LibraryService` (Application/Library) answers `sfo library review` and `propose`
([library-review.md](library-review.md)). It reads only the database; nothing touches the judged folder.

- `LibraryTree.BuildAsync` builds the tree in memory from `IJobReadRepository.GetDirectoriesAsync` and `ILibraryRepository.StreamStructureFilesAsync` (name, size, type, date, markers; unbuffered) and rolls up subtree sums.
- `DumpDetector` marks device, computer and package dumps first; `LibraryRules` then runs the rules on the rest and bundles findings per parent. Word lists and exceptions come from `LibraryWords` (embedded `library-words.json`, extendable with `--words`).
- `LibraryFacts` turns the tree into the text the model sees (outline and findings, no file names). `ILibraryAdvisor` (`OllamaLibraryAdvisor`) asks the model with a JSON schema; `LibraryAdviceParser` drops unknown paths and codes. Answers are cached in `library_advice` by a hash of task, model and facts.
- `TargetProposer` builds the target structure from the rules or the model answer; `TargetStructureFile` writes, reads and validates it.
- `sfo scan --structure-only` stores `scan_jobs.structure_only`; `ScanEngine` and `ScopedRefreshService` then call `SensitiveFileRules.Apply` without a header probe, so no file is opened.

`LibraryTargetService` answers `sfo library show` and `match` ([library-match.md](library-match.md)), on top of the same tree and rules:

- `LibraryMap.Build` turns tree and findings into one entry per folder (role, period, naming habit, findings); dumps are one entry each. `LibraryMap.ToPatterns` writes the habits as a target file (`LibraryMatch:PatternsDirectory`, by default `library-patterns/` next to the database); a hand-edited file wins over the rules.
- The match unit (folder, file or bundle) is built from `IJobReadRepository`, `IBundleRepository` and the latest comparison (`IContentComparisonRepository.GetLatestWithLibraryAsync`); its time follows `EventGrouper.TimeOf`.
- `LibraryMatcher` scores existing folders and, when none fits, a new folder; it has no I/O and is tested on its own. The model is asked only for folder purposes (task `map`) and a new folder name (task `name`), through `ILibraryAdvisor` and the same `library_advice` cache.

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
Stores a manual correction to category/target decisions. A category that differs from the model's answer is also kept as an `EvalCase`.

### `EvalCase` and `EvalRun`
A correction kept as a test case with the evidence the model saw, and a stored evaluation run with model, prompt fingerprint, settings and report.

## Persistence model

SQLite is the local source of truth. It stores both operational state and review-related data so future resume/recovery flows are possible.

Important tables:

- `scan_jobs` — job lifecycle, counters, status, and the scan rules of the job (step 4: `rules_name`, `rules_source`, `rules_hash`, `rules_json`), role (`source`/`library`), `structure_only` (step 8)
- `directory_nodes` — scanned directories with heuristic metadata and stats; `dir_status = Error` for directories the scan could not read; `sensitive_markers` / `sensitive_rule` for skipped folders marked by their name (step 12)
- `file_nodes` — scanned files with metadata, type, AI status, rule-based sensitive markers (`sensitive_markers`, `sensitive_rule`) and the version of the file rules that ran (`sensitive_rules_version`, step 12)
- `ai_results` — Phase 2 per-file classification results, including model sensitive markers (`sensitive_markers`) and the evidence notes (`evidence`, step 7)
- `file_facts` — facts per file read without the model (step 7): `file_node_id`, `fact`, `value`, `source`
  (`metadata`, `rule`, `model`), `recorded_at`; one row per file and fact. Codes are defined on the Domain enums
  `FileFactKey` and `FactSource` (`FileFactCodes.cs`)
- `ai_directory_results` — Phase 1 pre-assessment + Phase 3 directory summaries (distinguished by `phase` column); `marked_inputs` (step 12) lists the marked files a model summary saw as their neutral line
- `directory_verdicts` — one verdict per directory (step 3): `verdict`, `source` (`rule`/`model`), `rule`,
  `purpose`, `retention`, `reason`, `unit_directory_node_id` (the directory that is the whole, for `unit`),
  `profile_json` (snake_case), `model_used`, `raw_response`, `judged_at`, `error`; either `verdict` or `error` is set;
  step 16 widens the check of `verdict` by `dump` and changes no stored row;
  `escalated_from` and `escalation_reason` when a sample raised the directory to `mixed` (step 6)
- `directory_samples` — one sample per uniform directory (step 6): `seed`, `state` (`open`, `confirmed`,
  `escalated`), `eligible_count`, `members_json` (file id, name, `role` = `stratum`/`size`/`date`/`anomaly`/`sensitive`,
  category, `deviates`, error), `max_deviation_share`, `majority_category`, `deviating_count`, `failed_count`,
  `deviation_share`, `inherited_count`, `inherited_confidence`, `reason`, `drawn_at`, `decided_at`
- `ai_results` columns of step 6: `origin` (`rule`, `model`, `inherited`, `human`, `agent`, `external`; NULL for
  older rows, all judged by the model), `inherited_from_directory_node_id`, `keep_hint` (`temporary`,
  `worth_keeping`), `keep_reason`. File statuses `Deferred` (waits for its sample) and `Inherited` are new
- `ai_results` columns of step 10: `certainty` (`certain`, `likely`, `uncertain`, `review`; NULL for older rows),
  `attempts`, `previous_error`
- `result_checks` — one row per second check (step 10): `file_node_id`, `reasons` (comma-separated `CheckReason`
  codes), `outcome` (`agreed`, `disagreed`, `corrected`, `rule_wins`, `failed`, `skipped`), `rule_note`,
  `first_answer` and `second_answer` (JSON, snake_case: category, importance, confidence or null, summary, target,
  keep hint, markers, model, raw response, evidence, error), `checked_at`
- `user_overrides` — manual category/target corrections
- `content_fingerprints` — head-and-tail and full SHA-256 per path, with the size and modification time (`last_write_ticks`) they belong to and the time the full checksum was computed (`full_hash_at`); shared by all jobs
- `comparisons` — one row per compared pair of source job and library job (`against_job_id` null: duplicates only), with `complete` and `error_count`
- `comparison_files` — per source file: `state` (`backed_up`, `new`, `differs`, `error`; null without library), `match_file_node_id`, `match_count`, `match_hash`, `match_verified_at`, `duplicate_group`, `error`
- `library_advice` — model answers of `sfo library` (step 8): `job_id`, `task` (`review`/`propose`; since
  https://github.com/crack666/smart_file_organizer/issues/25 also `map` for folder purposes and `name` for a new
  folder, no schema change), `input_hash`, `model`, `created_at`, `content`, `raw_response`, `error`
- `eval_cases` — test cases from corrections (step 9): `kind`, `origin`, `job_id`, `file_node_id`,
  `subject_path`, `relative_path`, `expected`, `observed`, `observed_model`, `sensitive_markers`,
  `evidence_json` (what the model saw; `NULL` for sensitive files), `created_at`, `updated_at`; one
  row per kind, job and path. Local only, never exported ([evaluation.md](evaluation.md))
- `eval_runs` — stored evaluation runs (step 9): `mode`, `subject`, `job_id`, `model`, `stub`,
  `prompt_fingerprint`, `app_version`, `settings_json`, `report_json`, start and end
- `bundles` — one row per bundle (step 11): `job_id`, `parent_bundle_id`, `kind` (`dump`, `dump_keep`, `dump_noise`,
  `folder`, `event`, `scattered`), `origin`, `part`, `root_path`, `root_directory_node_id`, `suggested_name`,
  `styled_name` (empty; names in the library's style come per match from `sfo library match`), `first_time`/`last_time` (local wall-clock time), `time_source`,
  `file_count`, `total_bytes`, `directory_count`, `empty_directory_count`, `reason`, `facts_json`, `built_at`
- `bundle_files` — the bundle each file belongs to directly (step 11); a file is in at most one
- `bundle_builds` — per job the last build (step 11): `built_at`, `settings_json`, `stats_json` (with and without dumps).
  Bundles of a job are dropped with a rescan of one of its directories and with the job
- `finding_decisions` — decisions about findings (step 14): `job_id`, `finding_key`, `kind`, `state` (`accepted`,
  `rejected`, `wrong`; no row means open), `origin` (`human`, `agent`), `note`, `decided_at`, `snapshot_json`.
  Findings themselves are computed when read; deleted with the job
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
4. Never change a step that has been merged; add a new one. Step 1 (`M0001_InitialSchema`) is the schema as it existed before migrations. Step 2 (`M0002_SensitiveMarkers`) adds the sensitive-marker columns. Step 3 (`M0003_DirectoryVerdicts`) adds `directory_verdicts`. Step 4 (`M0004_ScanJobRules`) adds the scan rules of a job. Step 5 (`M0005_ContentComparison`) adds the comparison with the library. Step 6 (`M0006_DirectorySamples`) adds `directory_samples`, the escalation columns and the origin and keep-hint columns of `ai_results`; its new codes are English. Step 7 (`M0007_FileFacts`) adds `file_facts` and `ai_results.evidence`. Step 8 (`M0008_LibraryReview`) adds `scan_jobs.structure_only` and `library_advice`. Step 9 (`M0009_Evaluation`) adds `eval_cases` and `eval_runs`. Step 10 (`M0010_ResultChecks`) adds certainty, attempts and previous error to `ai_results` and the table `result_checks`. Step 11 (`M0011_Bundles`) adds `bundles`, `bundle_files` and `bundle_builds`. Step 12 (`M0012_SensitiveGaps`) adds the markers of skipped folders, the rule version per file and the marked inputs of directory summaries. Step 13 (`M0013_EnglishCodes`) switches the German codes of steps 2 and 3 to the English codes of [glossary.md](glossary.md): it rebuilds `directory_verdicts` (its CHECK constraints named the old codes), rewrites marker lists token by token, and the texts the program wrote from codes (keep reason, container reason, profile hint, `dominant_type` of the pre-assessment, stored check answers, stored evaluation reports); raw model answers stay as received. The old codes are spelled out in that step, and in step 3, and nowhere else in code. Step 14 (`M0014_FindingDecisions`) adds `finding_decisions`.
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
