# Status and Roadmap

This page records what exists and the traps worth knowing. It does not plan
work: everything open is tracked as issues in the milestones

- [A: Kern ohne GUI und CLI](https://github.com/crack666/smart_file_organizer/milestone/1) (core runnable without the GUI, plus a CLI)
- [B: Ordner-Urteil](https://github.com/crack666/smart_file_organizer/milestone/2) (folder verdict)
- [C: Plan und Ausfuehrung](https://github.com/crack666/smart_file_organizer/milestone/3) (target structure, plan and execution)
- [D: GUI-Anschluss](https://github.com/crack666/smart_file_organizer/milestone/4) (GUI integration)

Every statement about the current state below carries the date it was
checked and how. If you change what a line describes, update the line and
its date, or delete it.

## What exists

_Checked 12.11.2026 by reading the code on `master`. Whether build and tests
pass today is what the CI badge in the [README](../README.md) shows (Windows
and Linux, every push to `master` and every pull request). Until 17.11.2026
this line carried a test count (last 582) that every package had to update and
most did not; dated counts in the sections below are measurements of their day._

### Scan and persistence

- Layered solution: Domain / Application / Infrastructure / Scanning / Desktop.
- Recursive scan engine with an iterative queue, so deep trees do not
  overflow the stack.
- SQLite persistence (Dapper) for scan jobs, directory nodes, file nodes,
  AI results, directory AI results and user overrides.
- File type detection by extension (`FileTypeDetector`).
- Scan pruning (`HeuristicsEngine`) from a rule file (profile): the shipped
  `default-rules.json` or one given with `sfo scan --rules`, stored at the
  job; `skip`, `shallow` (with `max_depth`) and `user_data` rules. Rules match
  whole path segments (case-insensitive, either separator), skip and shallow
  rules only below the scan root. Beyond `shallow_from` (6) the content
  decides: user files → scanned, program files only → shallow. Every
  decision stores rule, profile and reason in `dir_reason`, e.g.
  `Skip: matches skip rule 'bin' of rules 'default': build output`;
  `sfo rules check` shows it for any path. Format: [scan-rules.md](scan-rules.md)
  _(checked 12.11.2026, https://github.com/crack666/smart_file_organizer/issues/21,
  by `HeuristicsEngineTests`, `ScanPipelineIntegrationTests`, `CliTests` and
  `sfo rules check` / `sfo scan --rules` run by hand on a temp folder; until
  then the line read "Each skip stores its rule in `dir_reason`, e.g.
  `Skip: matches skip pattern 'bin'`" and the patterns were fixed in code)_.
- Resume: directories already scanned are not read again; directories stored
  as skipped or shallow are re-evaluated with the job's current rules and
  entered if those let them in (until 12.11.2026 stored shallow decisions
  were kept as they were and re-evaluated skips were not written back).
- `ScanExportService` (CSV export, `sfo export --csv`) exists and is tested. Every row goes through
  `SensitiveOutputGuard`. (Until 12.11.2026 this line said "CSV/JSON
  export"; the service never wrote JSON. Until 22.11.2026 it said the service was "not reachable from the UI";
  `sfo export --csv` had used it for a while by then.)
- Exchange format ([#34](https://github.com/crack666/smart_file_organizer/issues/34), 22.11.2026,
  [data-format.md](data-format.md)): `sfo export --format jsonl` writes `job.json`, `directories.jsonl` and
  `files.jsonl` through the same guard, each line valid against a JSON schema under `schemas/exchange/v1/` (embedded,
  JsonSchema.Net); `sfo import-results` reads judgements of another program in the same line format (origin
  `external`); the answer schemas of the model are published under `schemas/model/`; the views `v_files` and
  `v_directories` are made again from the enums whenever the database opens. Since 23.11.2026
  ([#116](https://github.com/crack666/smart_file_organizer/issues/116)), format 1.1: a source also gets
  `findings.jsonl` and `plan.jsonl` with their schemas; the `result` of `files`, `dirs`, `show` and `summary` has a
  schema under `schemas/cli/v1/` whose code fields refer to the exchange schemas, and the files of
  `sfo findings show` and the CSV export write the same codes (they wrote enum names before). (Until 23.11.2026
  this line said findings, plan actions and the CLI results were not yet described.)
- Sensitive files ([#9](https://github.com/crack666/smart_file_organizer/issues/9)):
  rules without a model mark files during the scan
  (`SensitiveFileRules`: names, extensions, folders, keywords, header probe
  of small non-media files); the local model adds markers in the regular
  classification call. Stored in `file_nodes.sensitive_markers` /
  `sensitive_rule` and `ai_results.sensitive_markers` (schema step 2).
  Since [#99](https://github.com/crack666/smart_file_organizer/issues/99)
  (14.11.2026) the setting `Sensitive:Disclosure` decides what outputs show:
  `open` (default) treats a marker as a hint and withholds only the text of
  access data (`credentials`, `keys`); `restricted` is the earlier default,
  which withheld the content of every marked file and the structure of
  folders holding one (until 14.11.2026 this line said "Outputs withhold
  their content by default"); see
  [engineering-decisions.md](engineering-decisions.md#8-sensitive-files-local-and-external-readers).
  Until 12.11.2026 a stored `private_nachricht` marker made every read of
  its folder fail (Dapper parsed the token as an enum name); fixed with
  issue 36, tested for every marker in `SensitivePersistenceTests`.
  Since [#33](https://github.com/crack666/smart_file_organizer/issues/33) (schema
  step 12) the boundary also covers logs (prompts only in a local debug file),
  directory summaries written before a marker, skipped folders such as `.ssh`,
  the header check of older rows, inherited targets and the `compare` checksum.

### AI pipeline (three phases, run after the scan, never inside it)

1. **Directory verdict and pre-assessment** (`DirectoryPreAssessmentService`,
   since 12.11.2026 by [#7](https://github.com/crack666/smart_file_organizer/issues/7)):
   every directory gets a verdict (`unit`, `uniform`, `mixed`, `empty`,
   `system`, and since 13.11.2026 by
   [#59](https://github.com/crack666/smart_file_organizer/issues/59) the rule-only
   `dump`, whose parts are judged and whose noise is skipped) with purpose, retention, reason and the profile it rests on, stored
   in `directory_verdicts`. Clear cases are decided by rule without Ollama; the
   rest in one Ollama call. Files of units and system directories are marked
   `Skipped` (the directory carries the result). The model is not asked how to
   sample (until 18.11.2026 this said "The model's sampling fields are still
   asked for but no longer followed"; prompt and schema dropped them with
   [#111](https://github.com/crack666/smart_file_organizer/issues/111)). Shown in the GUI since 24.11.2026
   ([#14](https://github.com/crack666/smart_file_organizer/issues/14): tree and folder banner, and a person's own
   verdict in `directory_overrides`, which wins over rule and model; source `human`) and by `sfo dirs`/`sfo show`.
   Until 24.11.2026 this said "Not yet shown in the GUI (#14) or the CLI"; the CLI had shown it since the read
   commands.
2. **Per-file classification, in stages** (`ClassificationService`,
   `DirectorySamplingService`, `OllamaService`; since 12.11.2026 by
   [#8](https://github.com/crack666/smart_file_organizer/issues/8)): a uniform
   directory is judged by a reproducible stratified sample (at most 15 calls
   plus its sensitive files); if the sample agrees, the other files inherit
   category and target (`origin = inherited`, confidence at most 0.6), otherwise
   the directory is raised to mixed (`escalated_from`) and every file is judged.
   Since 12.11.2026 ([#50](https://github.com/crack666/smart_file_organizer/issues/50))
   photos and videos, and the kinds of documents, count as one purpose, one
   outlier never tips a sample, a folder of up to three files keeps its verdict,
   and a marker the model finds marks the file instead of raising the folder
   (its other files are then judged one by one instead of inheriting).
   Mixed directories: every file, with a keep hint (`temporary`,
   `worth_keeping`). Since 12.11.2026 every file type takes part
   ([#22](https://github.com/crack666/smart_file_organizer/issues/22)): the
   evidence holds facts read without the model (capture date, device,
   location present, image origin, page count, document properties, audio
   tags and duration, speech or music, archive listing and content), the
   content the model may look at (image, PDF text and rendered pages, video
   frames if `ffmpeg` is installed, text), and what is missing. Programs,
   fonts, databases and unknown binaries get a rule result (`origin = rule`)
   without a model call. Facts are stored in `file_facts` (schema step 7),
   the evidence notes in `ai_results.evidence`. _(Until then: "images as
   base64, documents as extracted text or first PDF page rendered to an
   image, videos and audio by file name and path only"; checked by
   `FileEvidenceTests` and `Phase2EvidenceTests` with generated files, no
   run against a real model.)_ Returns category,
   importance, confidence, summary, suggested target and sensitive markers.
   Stored in `ai_results`; samples in `directory_samples` (schema step 6).
   A marked file is stored as `ReviewNeeded` instead of `TrashCandidate`
   and as `worth_keeping` instead of `temporary`. The thresholds are starting
   values until the measurement of
   [#10](https://github.com/crack666/smart_file_organizer/issues/10).
   Since 12.11.2026 ([#23](https://github.com/crack666/smart_file_organizer/issues/23))
   every result carries a certainty (`certain`, `likely`, `uncertain`,
   `review`) from agreement between signals; unsure or contradicted answers
   get a second call with more evidence and the directory as context, both
   answers are kept in `result_checks` (schema step 10), disagreement puts the
   file to `review`. `sfo analyze --retry-errors` retries failed files and
   counts the attempts.
   Samples draw only images, videos and documents; the other files of a
   uniform directory are judged one by one. (Until 12.11.2026 this line
   read: "Files of other types (audio, archives, executables, ...) still get
   no result in any directory.") Samples and escalations are not yet shown by
   the CLI (https://github.com/crack666/smart_file_organizer/issues/6).
3. **Directory summaries** (`DirectorySummaryService`): bottom-up. One
   Ollama call per mixed directory or directory without a verdict; the
   others are summarized from verdict and sample outcome without a call
   (until 12.11.2026 every directory with files cost a call). Stored in
   `ai_directory_results` with phase `summary`.

All three phases run with bounded concurrency set by `MaxParallelRequests`
(default 4 in `appsettings.json`; `ClassificationService` clamps it to 1..8).

Since 13.11.2026 ([#63](https://github.com/crack666/smart_file_organizer/issues/63)) a model server
that goes away during a run no longer leaves stored errors behind: single calls are retried on
connection errors, timeouts, 429, 502, 503 and 504; after three failures in a row the run waits for the
server (up to 30 min, shown by `sfo status` and in the GUI), loads the model again and continues; when the
server stays away, `sfo analyze` ends with exit code 4 and the next run finishes the open items. Until
then every call in that window was stored as an error, `Ollama.MaxRetries` was not read, and calls ended
after 100 s instead of the configured 180 s. Checked with a fake server in `ModelServerOutageTests`; not
yet against a real Ollama restart.

### Desktop UI (Avalonia)

- Root folder selection; scan start, pause, cancel.
- AI classification start, pause, resume, cancel, independent of the scan.
- Findings view (since 24.11.2026, [#27](https://github.com/crack666/smart_file_organizer/issues/27)): what a finished
  source job opens with (until then the folder tree; a library and an unfinished scan still open with it). Findings of
  `FindingService` grouped by bundle or by importance, with kind, period, count, size, recommended action with target,
  certainty and state; bundles without a finding come last. Accept, reject, "Falsch", reopen, change or withdraw the
  target and "Ohne Ziel" for one, several (Shift, Ctrl) or a whole bundle, with the keyboard (A, N, O, Z; arrows close
  and open a bundle). Decisions go through `FindingService.RecordAsync` with the origin `human`, targets through
  `FindingPlacementService` with its checks; a sensitive finding is decided only as the one selected row. Filters by
  state, quick filters in the side bar (nicht gesichert, sensibel, prüfen, doppelt), codes in German or English
  (`CodeLabels`). Detail with reason, a strip of up to 8 pictures spread over the entry, first page or text of the
  first document, up to 200 files; a sensitive finding after a click, the content of access data never, a marked file
  in another finding is listed, not shown.
- Side bar with the jobs of the database (role, scan status, judged files) and the quick filters; search over name,
  path and summary of the open job (`JobSearchService`, Ctrl+F), shown in findings and file table alike.
  _Checked 24.11.2026: `FindingsViewModelTests` (13) and one pass with the keyboard in the main window at 1300 px
  ([screenshots](images/17-findings.png)) on Windows; the real window was not driven by hand, and no job of real size
  was opened._
- Lazy folder tree with verdict, retention value, size, file count (since 24.11.2026, #27; sortable by name, size or
  file count at every level) and marked files per folder, read off the UI thread and
  again while the analysis runs (since 24.11.2026,
  [#14](https://github.com/crack666/smart_file_organizer/issues/14)); file table with category colour, state,
  confidence, summary, and marks for sensitive and temporary files.
- Folder banner above the file table: verdict and who decided, retention, purpose, reason, escalation to mixed,
  the model's summary, `analysis_skip`; the person sets the folder's verdict and retention there with a note, and
  takes it back (`DirectoryReviewService`, table `directory_overrides`). Until 24.11.2026 only the model's summary.
- Filters of the file table (category, state, low confidence below `SecondCheck:BelowConfidence` and not yet
  reviewed, sensitive, temporary) that stay from folder to folder; check marks with a common category for the
  checked files (`ReviewService.ApplyCategoryAsync`). A mark counts only while its row is shown.
- Sensitive files: the preview of the detail opens on "Vorschau zeigen", that of access data never; their summary is
  not shown. "CSV exportieren…" in the toolbar writes the job like `sfo export`.
- File detail panel with metadata and image, text and first-page PDF preview.
- Manual review: accept or override category and target per file, in the
  detail panel and in the table row (`ReviewService`, `UserOverride`).
- Ollama settings (base URL, model, keep-alive) editable and persisted;
  installed models listed from `/api/tags`; manual model warm-up.
- Recent scans on the welcome screen.
- Right-click on a folder opens it in the file manager through the system
  shell (Explorer, `open` on macOS, `xdg-open` on Linux). Until 17.11.2026
  this called `explorer.exe` and only worked on Windows.
- Plan view (since 14.11.2026,
  [#15](https://github.com/crack666/smart_file_organizer/issues/15)): plan
  picker per job of the open database, which is named in the window; actions
  grouped by bundle or source folder with kind (fixed colour), target, reason,
  origin, state and an "Agent" mark for decisions an agent made, which can be
  reset to proposed in one go. Detail with preview (image, first page or text
  start, otherwise stored facts), description and marker, "Öffnen" / "Im
  Ordner zeigen" through the system shell (Windows, macOS, `xdg-open`). Kind
  and decision for one action, the ticked rows, a group or all; "Als Ballast"
  sets quarantine or `delete_source` (`PlanService.MarkForRemovalAsync`), with
  the same checks as the CLI. Summary of what moves, is removed and stays.
  Dry run, step 1 (copy and verify) and step 2 (originals to the hold folder,
  with space freed) each ask first, show progress and can be stopped; undo
  takes the last run back. All through `PlanService` and `PlanExecutor`.
  _Checked 14.11.2026: `SmartFileOrganizer.Desktop.Tests` 12 passed on Windows
  and on Linux (Avalonia headless), one pass rendered in the main window
  ([screenshots](images/01-plan-agent.png)); the real window was not driven by
  hand._ Limits: sensitive actions are decided one by one and run only with
  `sfo plan apply --action`; `sfo plan purge` is CLI only; "Als Ballast" picks
  `delete_source` only when the library comparison shows the content is held
  there, otherwise quarantine.
- Plan view, outcomes and release (since 14.11.2026,
  [#102](https://github.com/crack666/smart_file_organizer/issues/102)). Until
  then the buttons were "Bestätigen", "Ablehnen", "Als Ballast", "Belassen",
  "Umziehen", "Zurücksetzen" per row, selection and group, plus "Alle
  bestätigen" / "Alle ablehnen"; "Ablehnen" and "Belassen" both left the file
  in place. Now one question per action, three outcomes in fixed colours:
  "Umziehen" (`move`, `copy`), "In den Papierkorb" (`quarantine`,
  `delete_source`: the original goes into `.sfo-hold` in the job folder in
  step 2 and is deleted only by `sfo plan purge`), "Hier lassen" (`leave`).
  Then "Freigeben" (`approved`) for one action, the ticked rows, a group or
  "Alles freigeben"; "Freigabe zurücknehmen" sets `approved` back to
  `proposed`. Every row (tooltip) and the detail say what executing does,
  where the file is afterwards and how to take it back; step 1 asks with a
  summary in the same words (what moves where, what goes into the bin, what
  stays and why). The GUI never rejects: a `rejected` action (CLI, agent)
  shows as "Hier lassen" with the state "Abgelehnt", is never released by a
  release, and choosing an outcome opens it again. "Genauer" keeps every kind
  reachable. "Ziel ändern" opens a tree of the plan's libraries from the
  library folder view (`LibraryTargetService.FolderAsync`, as `sfo library
  folder`), with the inbox folder and "Neuer Ordner hier"; `PlanService` and
  its checks decide, a refused target keeps the picker open with the reason.
  Videos get one still frame when `ffmpeg` is on the PATH (the extractor of
  the analysis), video and audio their key facts in large type (since #109
  a filmstrip, below).
  _Checked 14.11.2026: `SmartFileOrganizer.Desktop.Tests` 17 passed on Windows
  and on Linux (Avalonia headless), one pass rendered
  ([screenshots](images/01-plan-agent.png)); the real window was not driven by
  hand._ Limits: the folder tree reads the library map anew for each opened
  folder, which may be slow on a large library; no still frame without
  `ffmpeg`; `sfo plan purge` (emptying the bin) is still CLI only (in the GUI
  since #105, below).
- Plan kind `discard` and "Papierkorb leeren" (since 14.11.2026,
  [#105](https://github.com/crack666/smart_file_organizer/issues/105)). Until
  then "In den Papierkorb" set `delete_source` or `quarantine`, so clutter was
  copied into the library first. Now it sets `discard`: step 1 only checks the
  file against the scan, step 2 moves the original into `.sfo-hold` of the
  source with no copy anywhere, "Zurücknehmen" puts it back until the bin is
  emptied. `quarantine` and `delete_source` stay under "Genauer". "Papierkorb
  leeren" runs `sfo plan purge` after a confirmation with count and size; only
  that frees room on the drive of the source, which the summaries say. The
  move notice names a discarded file `removed`.
  _Checked 14.11.2026: `SmartFileOrganizer.Desktop.Tests` 18 passed on Windows
  and on Linux (Avalonia headless), one pass rendered with the confirmation
  of "Papierkorb leeren" ([screenshot](images/09-confirm-empty-bin.png)); the
  real window was not driven by hand._ Limits: a discarded file that was purged cannot be put
  back; an older build that rewrites a move notice drops its `removed` lines.
- Plan view preview: filmstrip, "Abspielen", more formats, "Alles freigeben"
  explained (since 16.11.2026,
  [#109](https://github.com/crack666/smart_file_organizer/issues/109)). Until
  then a video showed one still, `.jfif` and `.doc` (and `.rtf`, `.odp`,
  archives) showed "No built-in preview", and "Alles freigeben" released at
  once. Now the preview stands right under "Öffnen": a video gets up to nine
  pictures with time marks (three by three, made by `ffmpeg` in the
  background, cached under the temp folder of the application), its kind and
  origin from the facts, and "Abspielen" (default program; also Enter or a
  double click in the list, Ctrl+P). Without `ffmpeg` a hint names it. Audio
  gets "Abspielen" beside duration and tags. The reader is chosen from the
  first bytes, then a full extension list
  ([format table](architecture.md#preview-flow)). "Alles freigeben…" asks
  with the counts as set (move, bin, stay) and says no outcome is changed.
  No player is built in ([why](engineering-decisions.md#no-video-player-in-the-gui)).
  _Checked 16.11.2026: `dotnet test` 1,188 passed and 2 skipped on Windows,
  1,190 passed on Linux, `SmartFileOrganizer.Desktop.Tests` 33 of them
  (Avalonia headless), both with `ffmpeg` on the PATH; the case without it is
  forced in the tests. Preview rendered headless
  ([screenshot](images/10-video-filmstrip.png)); the real window was not
  driven by hand._ Limits: `.xls`/`.ppt` show a hint only;
  TIFF, HEIC and raw images are not drawn; the analysis does not read `.doc`
  text yet (only the preview does).

### Command line

_Checked 12.11.2026 on the branch for
https://github.com/crack666/smart_file_organizer/issues/5: `dotnet test`
130 passed after rebasing on the scan-rule change of #20, and `scan`, `status --json`, `analyze` against a closed port
(exit code 3) and `doctor` run by hand against a temp database._

- `sfo scan`, `analyze`, `jobs`, `status`, `doctor` with `--json` output
  and JSONL progress; see [cli.md](cli.md).
- `sfo summary`, `dirs`, `files`, `show`, `export` read a job in pages with
  filters on verdict, retention and the sensitive marker
  (https://github.com/crack666/smart_file_organizer/issues/6, measured
  12.11.2026 by `dotnet test`: 372 passed after rebasing on the scan rules, and every command run by hand
  with `--json` against a temp database). (Until 12.11.2026 this line said
  the read commands were not there yet.)
- `sfo eval` measures directory verdicts, categories and sensitive markers
  against the generated corpus (version 2: 42 folders, 100 files; until
  12.11.2026 this line said 41 and 97, the counts of version 1) and names every
  failure; corrections in the review are kept as test cases
  (`sfo eval --cases`); runs are stored and compared (`sfo eval --compare`).
  See [evaluation.md](evaluation.md). _(checked 12.11.2026 on the branch for
  https://github.com/crack666/smart_file_organizer/issues/36, rebased on the
  staged analysis, the comparison, the read commands, the file evidence and the library review: `dotnet test`
  523 passed, `sfo eval generate` and `sfo eval --stub` run by hand
  against a temp database; CI runs the same with scripted answers. No run with
  a real model yet.)_ What perfect scripted answers still get wrong is listed
  in [evaluation.md](evaluation.md#measured).

### Comparison with the library

_Checked 12.11.2026 on the branch for
https://github.com/crack666/smart_file_organizer/issues/24, rebased on #31,
#32, #41 (comparison migration now step 5) and #44: `dotnet test` 392 passed; `sfo scan`, `scan --role library`,
`compare --json` (twice, the second run computing no checksum; `match_hash`
equal to `sha256sum` of the file) and `analyze` of a `library` job run by hand
against two temp folders and a temp database._

- `sfo scan <path> --role library` marks the organised archive (the library);
  `sfo compare <source-job> --against <library-job>` sets `backed_up`, `new`,
  `differs` or `error` per source file, finds duplicates within the
  source and counts per directory; see [cli.md](cli.md) and
  [architecture.md](architecture.md#comparison-flow-source-against-library).
- Directories a scan cannot read are now recorded as errors (before, they
  looked empty).
- `sfo analyze --against <library>` leaves out files that are `backed_up` and
  folders that are fully backed up
  (https://github.com/crack666/smart_file_organizer/issues/42). Corrected
  13.11.2026; until then this line said analyze did not skip them yet.
- Several library roots (https://github.com/crack666/smart_file_organizer/issues/74):
  `compare`, `analyze`, `findings` and `library match` take `--against` more
  than once; a file is `backed_up` as soon as one root holds it. Targets lie
  only in library roots, the old free-text `suggested_target` is no longer
  shown, and `findings --summary` gives an overview and the free space per root.

### Bundles

_Checked 12.11.2026 on the branch for
https://github.com/crack666/smart_file_organizer/issues/39, rebased on master after the library review, the evaluation corpus and the second check (bundles migration is step 11):
`dotnet test` 582 passed; `sfo scan`, `bundles build`, `bundles list`, `bundles show` and `summary` run by hand
with `--json` on a generated tree (a phone copy nested in two folders, 60 loose photos from three periods, one
named folder) against a temp database, output checked with a JSON parser: one `phone` dump with `camera`,
`chat_media` and noise parts, three events of 20, one folder. On the evaluation corpus (`sfo eval generate`)
the phone backup is one `phone` dump with `DCIM` as `camera` part; the two programs and two projects are
`software`._

- `sfo bundles build <job>` groups a scanned job without a model: dumps (`phone`, `computer`,
  `removable_drive`, `software`) with worth-keeping parts and noise, named folders taken as they are, loose
  photos and videos as events by pauses (capture date from `file_facts`, else date in the name, else
  modification time). `sfo bundles list|show`, `sfo files --bundle`, and `summary` with and without dumps;
  see [bundles.md](bundles.md).
- Capture dates exist only after `sfo analyze` has run Phase 2 on the photos; for a job that was only
  scanned, events rest on names and modification times (`period.time_source` says which).
- Used by `sfo library match --bundle` and by the findings
  (https://github.com/crack666/smart_file_organizer/issues/26), and through the findings by the plan: a finding is
  the unit of a plan (https://github.com/crack666/smart_file_organizer/issues/11). (Until 12.11.2026 evening: "Not
  yet used by findings"; until 13.11.2026: "not yet by the plan".)

### Findings

_Checked 12.11.2026 on the branch for https://github.com/crack666/smart_file_organizer/issues/26 (decisions
migration is step 14): `dotnet test` 722 passed; `sfo eval --stub`, `scan --role library`, `compare`,
`bundles build`, `findings`, `findings --summary` run by hand with `--json` on generated trees against a temp
database, output checked with a JSON parser: the evaluation corpus with 300 loose photos, copies, a photo among
invoices and a document in a program folder (30 findings, every kind, the 200 loose photos not in the library one finding; without a
library 17, `not_backed_up` not computed), and a generated old Windows profile with 2,476 of 2,924 files app data
(11 findings). No run with a real model._

- `sfo findings <job>` lists `not_backed_up`, `in_noise`, `misplaced`, `sensitive`, `duplicate`, summarized per
  bundle or folder, sure before unsure; `--summary` fits one page; `accept|reject|wrong|reopen` store only the
  decision, `wrong` also a test case. Rules: [architecture.md, Findings flow](architecture.md#findings-flow).
- `misplaced` stays `uncertain` at most: no step checks the content of a target folder yet
  (https://github.com/crack666/smart_file_organizer/issues/56).
- Accepted findings become plan actions (`sfo plan create`), which `sfo plan apply` executes once confirmed
  (https://github.com/crack666/smart_file_organizer/issues/12). (Until 13.11.2026: "Nothing executes an accepted
  finding yet", pointing at the plan issue; until 14.11.2026: "nothing executes them yet".)

### Describing and deciding

_Checked 13.11.2026 on the branch for https://github.com/crack666/smart_file_organizer/issues/37 (schema step 20):
`dotnet test` 831 passed, 2 skipped; `scan`, `analyze` and `decide` run in-process against the fake model server
and a temp database (`DecideCommandTests`). No run with a real model: whether real models fill the description
fields well, and what they cost in answer tokens, is not measured._

- The analysis also stores a description as facts with source `model`: scene, activity, setting, season, occasion
  and people of a picture; kind, issuer and year of a document. Never for a marked file.
- `sfo decide <job> [--targets <file>]` computes worth and target of every file by rule from the stored facts and
  results, without a model call, and stores them (`file_decisions`); every decision and target names its inputs.
  Rules: [architecture.md, Decision flow](architecture.md#decision-flow).
- Since 25.11.2026 ([#71](https://github.com/crack666/smart_file_organizer/issues/71), schema step 36) the targets lie in a
  library root (`root` of the structure, `--library`, or the only library known), with full path, needed and free space;
  a root outside every library gives no target. Checked in-process by `DecideCommandTests`, `PlanCommandTests` and
  `PlanChecksTests`; not on a real library. (Until then: relative paths only, bound at `sfo plan create`, no room checked.)
- Importance and keep hint are still asked in the analysis; a model call over facts for what no rule can judge is
  not built; the content check of target folders is https://github.com/crack666/smart_file_organizer/issues/56.

### Plan

_Checked 13.11.2026 on the branch for https://github.com/crack666/smart_file_organizer/issues/11 (schema step 23):
`dotnet test` 871 passed, 2 skipped (after the rebase on the content store); `eval --stub`, `scan --role library`, `compare`, `bundles build`, `decide`,
`findings accept` and every `plan` command run in-process against a temp database (`PlanCommandTests`), the rules
alone in `PlanChecksTests`. No run on a real medium or a real library._

- `sfo plan create <job>` turns accepted findings into actions: a uniform or unit folder one action with its name, a
  mixed folder one action per worth-keeping file, nothing for content a library holds, `leave` for sensitive files and
  for anything without a target in a library. `show`, `summary`, `set`, `import`, `approve`, `reject` read, change and
  confirm; see [cli.md, Planning](cli.md#planning).
- Executing is `sfo plan apply|undo|purge`, see below. (Until 14.11.2026: "Nothing is executed: moving, copying and
  removing is" Issue 12.)
- Targets come from the findings (a place in a library) and from `sfo decide --targets` bound to a library root; the
  content check of target folders (https://github.com/crack666/smart_file_organizer/issues/56) is not part of it.

### Executing a plan

_Checked 14.11.2026 on the branch for https://github.com/crack666/smart_file_organizer/issues/12 (schema step 25):
`dotnet test` 908 passed, 2 skipped; `sfo plan apply|undo|purge|keep-source` run in-process on a generated source and
library in a temp folder (`PlanApplyCommandTests`), with runs cut off between the steps. No run on a real medium, a
second drive or a network drive._

- `sfo plan apply` copies and checks every confirmed action by full checksum (dry run unless `--execute`);
  `--remove-sources`, confirmed on its own, moves originals into a hold folder on their own drive after reading both
  files again; `undo` takes it back, `purge` removes held originals for good while their copy is intact. See
  [cli.md, Executing a plan](cli.md#executing-a-plan).
- Not built: renaming on the same drive instead of copying, the system recycle bin, reading a copy on a network drive
  around the system cache, a name suffix on a collision (the action fails instead).

### Memory: places, refresh, move notices

_Checked 14.11.2026 on the branch for https://github.com/crack666/smart_file_organizer/issues/86 (schema step 27):
`dotnet test` 926 passed, 2 skipped; `sfo scan --refresh`, `sfo places`, `sfo where` and the move notice of
`sfo plan apply --remove-sources` run in-process on generated trees in a temp folder (`RefreshAndPlacesTests`,
`PlanApplyCommandTests`). No run on a real medium._

- Every completed scan makes its root a known place (`sfo places`, with drive, age and the overview of the findings,
  also when the drive is not attached). `sfo scan <path> --refresh` compares with the previous job of the place and
  carries results, facts and corrections of unchanged files over; `sfo where` answers from the move history, the scans
  and found notices. See [cli.md, Memory](cli.md#memory).
- Step 2 of the execution leaves `_moved-files.txt` in emptied folders ([move-notice.md](move-notice.md)).
- After `plan apply` the library job is written forward: the copy is a file of it with its checksum, the source file
  `backed_up`, the undo takes it back (schema step 30). A refresh carries verdict, summary and sample of unchanged
  folders over and lists the folders to judge anew. The dry run names a notice it would remove `would_remove`. (Until
  then this line said: "Not built: updating the library job in place after `plan apply` ...; carrying directory
  verdicts and summaries over in a refresh.")

_Checked 14.11.2026 on the branch for the rest of https://github.com/crack666/smart_file_organizer/issues/86 (schema
step 30): `dotnet test` 952 passed, 2 skipped; `PlanApplyCommandTests` (library written forward, later scan, dry-run
notices) and `RefreshFolderJudgementTests` (no model call for unchanged folders, fake model server) on generated trees
in a temp folder. No run on a real medium._

### Duplicates over a whole holding

_Checked 14.11.2026 on the branch for https://github.com/crack666/smart_file_organizer/issues/87 (schema step 29):
`dotnet test` 949 passed, 2 skipped; `sfo duplicates` runs in-process on generated trees in a temp folder
(`DuplicateServiceTests`, `DuplicatesCommandTests`). No run on a real medium or network drive._

- `sfo duplicates <job> [<job> ...]` reports files and folders with the same content over jobs of any role, reading in
  stages (size, sample checksum, full SHA-256 with `--verify`) and keeping every checksum in `content_fingerprints`.
  See [cli.md](cli.md#sfo-duplicates-job-job-).
- Not built: similar instead of equal files; hashing on the storage device itself; a finding or plan action from the
  report (removal goes through `sfo compare`, `sfo findings`, `sfo plan`).

### Placing into the library

_Checked 12.11.2026 on the branch for
https://github.com/crack666/smart_file_organizer/issues/25, rebased on the sensitive-output work: `dotnet test` 621 passed; `sfo scan --role library
--structure-only`, `scan`, `compare`, `bundles build`, `library show` and `library match` (a folder of loose photos,
a named folder, a camera folder without a fitting target, a single invoice) run by hand with `--json` on generated
trees against a temp database, output checked with a JSON parser. No run with a real model._

- `sfo library show <job>` reads the target map of a library: role, purpose, period and naming habit per
  folder, filtered by subtree, depth, name, period and role; the habits go into a hand-editable patterns file.
- `sfo library match <job> <path>|--bundle <id>` names the best existing folders with reasons and a certainty
  (`certain`, `likely`, `uncertain`, `review`) from agreeing signals, or a new folder in the library's style; a
  named source folder keeps its name and stays whole; see [library-match.md](library-match.md).
- Thresholds are starting values: the evaluation corpus has no library with known truth yet.

_Checked 14.11.2026 on the branch for https://github.com/crack666/smart_file_organizer/issues/95: `dotnet test` 982
passed, 2 skipped; `sfo eval generate` plus `sfo eval --stub` as in CI: target 14 of 14 hit. `sfo findings candidates`
run by hand on a generated tree against a temp database. No run with a real model._

- `sfo findings targets` runs the rules alone unless `--local-model` is given; the target decision belongs to a
  controlling model outside sfo ([controller-guide.md](controller-guide.md)).
- `sfo findings candidates` gives it the facts without a model call, `sfo library folder` and `sfo library describe`
  (the local model, one folder on demand) let it look closer, `sfo findings place` records its decision
  (`existing_folder`, `new_folder`, `inbox`, `no_target`; `decided_by` `controller`), checked like a plan target, all
  or nothing per file, kept by later target runs and taken by `sfo plan create`.
- Not measured yet: how well a controlling model decides on a real library; the evaluation corpus scores the rules and
  the local model, not the controller.

### Known limits

- PDFs: embedded text of the first pages up to 6,000 characters, page 1 as
  image (plus the middle page for PDFs without text); no OCR beyond what the
  vision model reads from rendered pages; document properties are not read.
  (Until 12.11.2026: "only the first page is seen".)
- Not read at all: sound (no transcription), text of `.doc`/`.xls`/`.ppt`,
  listings of `.tar.gz`/`.gz`/`.bz2`/`.xz`/`.iso`, video frames when `ffmpeg`
  is not on the PATH. The evidence names each of these as missing.
- Files of units and system directories stay `Skipped` without a Phase 2
  result of their own; their directory verdict carries it.
- A missing `confidence` is no longer assumed: the result gets a second
  check (until 12.11.2026 this line read "If the model gives a valid
  category but omits `confidence`, the parser sets `0.85`").
- The second check and its thresholds are tested with a scripted model only
  (`SecondCheckTests`); how often the real model agrees with itself, and so
  how many extra calls a run costs, is not measured yet
  (https://github.com/crack666/smart_file_organizer/issues/10).
- `--retry-errors` retries failed files and writes again the summaries that are
  older than a file result or the verdict of their directory. A directory whose
  Phase 1 call failed is judged again by every `sfo analyze`, with or without
  the option (`DirectoryVerdictTests.ModelError_IsStored_AndRetriedOnNextRun`,
  `ModelServerOutageTests.RetryErrors_RetriesTheFile_AndWritesTheSummaryOfItsFolderAgain`).
  Until 18.11.2026 this said "retries files only; directories whose Phase 1 call
  failed and summaries already written are not redone by it", which the code
  had left behind.
- gemma4 ignores schema field names; the parser accepts aliases such as
  `classification` for `category`. Other models may differ.
- No review filters in the file browser yet. Bulk decisions and file
  operations exist for plans (CLI, and the GUI plan view since 14.11.2026);
  until then this line read "No bulk review, no review filters, no file
  operations (move, copy, archive) yet".
- Sensitive-file protection covers what goes through `SensitiveOutputGuard`
  (today: the CSV export). The GUI shows everything, as it is used at the
  machine itself, except that the plan view withholds the text of files
  marked as access data (`Sensitive:Disclosure`, since 14.11.2026). The header probe runs only during a scan or rescan;
  databases scanned before schema step 2 are protected by the name rules
  (re-evaluated on output) and by model markers, not by the header rule.

## Operating notes

_Checked 12.11.2026 against the code: `think = false` in all three request
builders of `OllamaService`, `TimeoutSeconds` default 180 in
`OllamaOptions` and `appsettings.json`. The configuration order was
checked 17.11.2026 in `AddSmartFileOrganizerCore` of the Hosting project
(`ServiceCollectionExtensions`); until then this line named the Desktop
`ServiceConfigurator`, where it lived before #4. The timing figures come from
the development sessions of April 2026 and were not re-measured._

- **`think: false` stays.** Every Ollama request sends it. Without it gemma4
  emits reasoning tokens and trivial prompts took 90 to 150 s (April 2026).
- **Timeout 180 s.** Keep `Ollama:TimeoutSeconds` at 180. With 90 s, Phase 3
  hung: Ollama keeps working after a client-side timeout, so the next
  request queues behind the abandoned one.
- **`settings.json` wins.** The per-user `settings.json` in the
  application data folder (`%APPDATA%\SmartFileOrganizer\` on Windows,
  `~/.config/SmartFileOrganizer/` on Linux) is loaded after the shipped
  `appsettings.json` and overrides it. Check it first when the app does not
  behave as `appsettings.json` suggests. The SQLite database `data.db` lives
  in the same folder.
- **Stale AI results.** Phases treat existing `ai_directory_results` rows as
  done. Since 12.11.2026 Phase 2 takes every `Discovered` file, so the next
  run on an older job also classifies its audio, archives and other files
  that were left out before. After changing the AI pipeline, start a new scan job or delete the
  database, otherwise old results are reused. Exception: Phase 1 decides by
  `directory_verdicts`, so jobs analysed before #7 get their verdicts (and one
  Ollama call per undecided directory) on the next AI run. Files that the old
  random sampling (before #8) marked `Skipped` are judged on the next AI run
  (since 18.11.2026, [#111](https://github.com/crack666/smart_file_organizer/issues/111);
  before, they stayed without a result and needed a new scan job).
- **Model warm-up.** The first request to a large vision model waits for it
  to load into VRAM. Warm-up and a long `KeepAlive` (default `60m`) help;
  Ollama still unloads after idle time or a restart.
- **Check model names.** Query `/api/tags` before testing; 404s seen during
  development came from a model name that was not installed.
- **Logs.** Prompts and truncated responses are logged at `Information`
  through the console and debug providers. The prompt contains the text
  excerpt, also of sensitive files: do not hand console logs of an AI run
  to an external reader.

## Known traps (UI and runtime)

Root causes and fixes are in
[engineering-decisions.md](engineering-decisions.md).

- Avalonia `DataGrid` needs its theme included explicitly or renders as a
  black slab.
- Dark theme defaults left panels black until explicit backgrounds were set.
- PDF previews rendered transparent until a white background was forced.
- Native PDFium runtimes (Docnet.Core) may be blocked by application control
  policies in managed Windows environments.
