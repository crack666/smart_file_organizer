# Command line: `sfo`

`sfo` drives the same core as the desktop app: scan a folder into the database, run the
AI phases, and read where a job stands. It is meant for people and for a controlling
model alike; everything a machine needs is in [Machine contract](#machine-contract).

```bash
dotnet run --project src/SmartFileOrganizer.Cli -- <command> [options]
# or, after a build:
dotnet src/SmartFileOrganizer.Cli/bin/Debug/net10.0/sfo.dll <command> [options]
```

Issue: https://github.com/crack666/smart_file_organizer/issues/5

## Global options

Accepted by every command.

| Option | Meaning |
|---|---|
| `--json` | Write exactly one JSON document to stdout. |
| `--progress none\|text\|jsonl` | Progress on stderr. Default `text`, or `none` with `--json`. |
| `--db <path>` | SQLite database. Overrides `SFO_DB`. |
| `-v`, `--verbose` | Log at information level (default: warnings and errors). |
| `-h`, `--help` | Help for the command. |

Database and settings are found as described in
[architecture.md, File locations](architecture.md#file-locations): `--db`, then `SFO_DB`, then
the per-user default. With a redirected database, settings are read from `settings.json` next
to it (or from `SFO_SETTINGS`), so a scratch database never touches the user's settings.
The CLI never writes `settings.json`; `--model` and `--parallel` apply to one run only.

## Commands

### `sfo scan <path>` / `sfo scan --resume <job>`

Creates a job for `<path>` and scans it, or continues an interrupted job. Give exactly one of
the two. Ctrl+C stores the job as `paused`; `--resume` continues it. Resuming a completed job
returns it unchanged.

`--rules <file>` scans with a rule file (profile, see [scan-rules.md](scan-rules.md)) and
stores it at the job; without it the shipped rules are used. With `--resume`, rules that
differ from the stored ones switch the job to them, reopen it even if it was completed, and
re-evaluate the folders stored as skipped or shallow; `rules_changed` is then `true`.

| Option | Meaning |
|---|---|
| `--role quelle\|bestand` | Only with `<path>`. `quelle` (default): a medium to clean up. `bestand`: the user's organised archive; it is only read, never changed and never analysed by a model (`sfo analyze` refuses it with `wrong_role`). The role is fixed when the job is created. |

A directory the scan cannot read (access denied, network path gone) is stored with that error
and counted in `error_count` instead of looking empty; the scan itself continues and completes.

```bash
sfo scan /media/usb-stick --json
sfo scan /mnt/nas/archive --role bestand --json
```

```json
{
  "schema_version": 1,
  "command": "scan",
  "ok": true,
  "exit_code": 0,
  "result": {
    "job": {
      "id": 7,
      "root_path": "/media/usb-stick",
      "role": "quelle",
      "status": "completed",
      "created_at": "2026-11-12T13:36:42.138Z",
      "started_at": "2026-11-12T13:36:42.176Z",
      "completed_at": "2026-11-12T13:36:44.210Z",
      "files_total": 1830,
      "files_processed": 1830,
      "directories_total": 112,
      "directories_processed": 112,
      "error_count": 0,
      "last_error": null,
      "rules": { "name": "default", "source": "built-in", "hash": "3f9c…" }
    },
    "resumed": false,
    "rules_changed": false
  },
  "error": null
}
```

`status` is one of `created`, `running`, `paused`, `completed`, `cancelled`, `failed`.
`rules` is `null` for a job created before rules were stored and not run since.
Errors: `path_not_found` (1, also for a missing rule file), `rules_invalid` (1),
`job_not_found` (1), `scan_failed` (1, with `result`).

### `sfo analyze <job>`

Runs the AI phases for a job whose scan is `completed` and waits until they are done. Each
phase skips work already stored, so running it again after Ctrl+C or an error continues.

| Option | Meaning |
|---|---|
| `--phase <name>` | `pre_assessment`, `classification`, `summary` or `all` (default). Repeatable; phases always run in this order. |
| `--parallel <n>` | Concurrent Ollama requests, 1 to 8. Default: `Ollama.MaxParallelRequests`. |
| `--model <name>` | Ollama model for this run. Default: `Ollama.Model`. |

```bash
sfo analyze 7 --phase classification --parallel 2 --json
```

```json
{
  "schema_version": 1,
  "command": "analyze",
  "ok": true,
  "exit_code": 0,
  "result": {
    "job_id": 7,
    "model": "gemma4:26b",
    "parallel": 2,
    "phases": [
      { "phase": "classification", "total": 940, "processed": 940, "errors": 3,
        "message": "AI classification finished with 3 error(s)." }
    ],
    "error_count": 3
  },
  "error": null
}
```

Errors of single files or directories do not fail the command; they are counted here and
listed by `sfo status`. Failures: `ollama_unavailable` (3), `scan_not_completed` (1),
`job_not_found` (1), `wrong_role` (1, the job is a `bestand`), `cancelled` (130).

### `sfo jobs`

Lists all jobs, newest first.

```bash
sfo jobs --json
```

```json
{
  "schema_version": 1,
  "command": "jobs",
  "ok": true,
  "exit_code": 0,
  "result": { "jobs": [ { "id": 7, "root_path": "/media/usb-stick", "status": "completed", "...": "same fields as scan" } ] },
  "error": null
}
```

### `sfo status <job>`

Counters per phase and the most recent errors. `--max-errors <n>` (default 20) limits the list.

```bash
sfo status 7 --json
```

```json
{
  "schema_version": 1,
  "command": "status",
  "ok": true,
  "exit_code": 0,
  "result": {
    "job": { "id": 7, "status": "completed", "...": "same fields as scan" },
    "phases": {
      "scan": { "status": "completed", "files_total": 1830, "files_processed": 1830,
                "directories_total": 112, "directories_processed": 112, "errors": 0 },
      "pre_assessment": { "total": 104, "done": 104, "errors": 0, "pending": 0, "skipped": 0 },
      "classification": { "total": 940, "done": 610, "errors": 3, "pending": 0, "skipped": 327 },
      "summary": { "total": 104, "done": 0, "errors": 0, "pending": 104, "skipped": 0 }
    },
    "errors": [
      { "phase": "classification", "path": "/media/usb-stick/scan_004.pdf",
        "message": "Ollama request timed out after 180s." }
    ]
  },
  "error": null
}
```

For each AI phase, `total = done + errors + pending + skipped`.

- `pre_assessment` and `summary` count directories the AI phases consider: not skipped by the
  scan rules and not hidden (name starting with a dot).
- `classification` counts files of the types it handles (image, video, document). `skipped`
  are files the pre-assessment chose not to sample.

### `sfo compare <job> --against <job>`

Which files of a source (`quelle`) are already in the archive (`bestand`), by content, not by
name. Both scans must be `completed` and both roots reachable. Issue:
https://github.com/crack666/smart_file_organizer/issues/24

| Option | Meaning |
|---|---|
| `--against <job>` | The `bestand` job. Without it, only duplicates within the source are found and every `state` is `null`. |
| `--files <state>` | List the files with this state (`gesichert`, `neu`, `abweichend`, `fehler`, or `all`). Repeatable. Default: no file list. |
| `--limit <n>` | Maximum entries per list (`directories`, `errors`, `duplicates.groups`, `files`); default 100, `0` lists everything. The `*_total` fields count all. |

State per source file:

| `state` | Meaning |
|---|---|
| `gesichert` | A file with exactly the same content is in the archive (equal SHA-256 over the whole file). `match_path` is one of them (first by path), `match_count` how many. |
| `neu` | No archive file has this content, and none has the same name in a folder of the same name. |
| `abweichend` | Not in the archive by content, but a file with the same name lies in a folder of the same name there (`match_path`): probably another version. |
| `fehler` | The content could not be determined: the source file is unreadable, gone or changed since its scan, or an archive file of the same size that could hold this content cannot be read. `error` says which. Never counted as `neu` or `gesichert`. |

How content is compared, so a large archive on a network drive is read as little as possible:
size first; for files sharing a size, SHA-256 over the size and the first and last 64 KiB; only
where those agree, SHA-256 over the whole file. Files up to 128 KiB are read whole at once.
Checksums are stored per path and reused while size and modification time are unchanged, so a
second run without changes reads no file content (`hashing` shows the counts). A file never
matches itself, even when the source lies inside the archive.

`duplicates` groups source files with identical content (empty files excluded); a file's
`duplicate_group` points to its group. `directories` counts each source directory including its
subdirectories: a directory with `backed_up_share` 1.0 is fully in the archive and needs no model
analysis; otherwise only its files that are not `gesichert` do.

```bash
sfo compare 7 --against 8 --files neu --files fehler --json
```

```json
{
  "schema_version": 1,
  "command": "compare",
  "ok": true,
  "exit_code": 0,
  "result": {
    "comparison_id": 3,
    "source": { "id": 7, "root_path": "/media/usb-stick", "role": "quelle", "...": "same fields as scan" },
    "against": { "id": 8, "root_path": "/mnt/nas/archive", "role": "bestand", "...": "same fields as scan" },
    "compared_at": "2026-11-12T15:02:11.410Z",
    "complete": true,
    "counts": { "files": 1830, "gesichert": 1712, "neu": 101, "abweichend": 17, "fehler": 0 },
    "duplicates": {
      "groups_total": 1, "files": 2,
      "groups": [ { "group": 1, "size": 48211, "paths": [ "/media/usb-stick/a.jpg", "/media/usb-stick/copy/a.jpg" ] } ]
    },
    "directories_total": 112,
    "directories": [
      { "path": "/media/usb-stick", "files": 1830, "gesichert": 1712, "neu": 101, "abweichend": 17, "fehler": 0,
        "backed_up_share": 0.9355 }
    ],
    "errors_total": 0,
    "errors": [],
    "files_total": 101,
    "files": [
      { "path": "/media/usb-stick/notes/2014.txt", "state": "neu", "match_path": null, "match_count": 0,
        "duplicate_group": null, "error": null }
    ],
    "hashing": { "head_tail_computed": 240, "full_computed": 1716, "reused": 0 }
  },
  "error": null
}
```

Errors are listed in `errors` with `role` (`quelle` or `bestand`), `kind` (`scan`: a directory the
scan could not read, so its files are missing from the job; `read`: a file that could not be read
now) and `path`. If there is any, `complete` is `false` and the command exits 1 with
`compare_incomplete`; the result is still returned and stored. Other failures: `job_not_found` (1),
`scan_not_completed` (1), `wrong_role` (1, the first job is not a `quelle` or `--against` not a
`bestand`), `path_not_found` (1, a root is not reachable), `cancelled` (130; checksums computed so
far are kept).

The result is stored per source and archive job (a new run replaces it) and dropped when either
job is deleted or a directory of it is rescanned. Not decided here: a file the archive holds
under the same content but whose scan was skipped by the scan rules counts as `neu`; files that
changed in the archive after its scan are judged by the archive scan's sizes.

### `sfo doctor`

Checks the database (creates and migrates it if missing), whether Ollama answers within
5 seconds, and whether the configured model is installed.

```bash
sfo doctor --json
```

```json
{
  "schema_version": 1,
  "command": "doctor",
  "ok": false,
  "exit_code": 3,
  "result": {
    "ok": false,
    "database": { "path": "/home/me/.config/SmartFileOrganizer/data.db", "existed": true,
                  "schema_version": 1, "ok": true, "error": null },
    "settings_path": "/home/me/.config/SmartFileOrganizer/settings.json",
    "ollama": { "base_url": "http://localhost:11434", "reachable": false },
    "model": { "name": "gemma4:26b", "available": null, "installed": [] }
  },
  "error": { "code": "ollama_unavailable", "message": "Ollama is not reachable at http://localhost:11434." }
}
```

`model.available` is `null` when Ollama could not be asked. Exit code: 1 for a database
error (`database_error`), 3 if Ollama is unreachable, 1 if the model is missing
(`model_missing`), else 0.

### `sfo rules check <path>...`

Shows for each path which rule applies and why, without scanning and without opening the
database. The paths need not exist; if one does, its files are looked at where the decision
depends on them (beyond `shallow_from`), otherwise that check reports `content_unknown`.

| Option | Meaning |
|---|---|
| `--root <dir>` | Scan root the paths are checked against. Default: the drive or file system root of each path. A path outside the root is a usage error (2). |
| `--rules <file>` | Rule file to check with. Default: the shipped rules. |

```bash
sfo rules check /media/disk/app/bin /media/disk/Documents /media/disk/app/bin/Documents --root /media/disk --json
```

```json
{
  "schema_version": 1,
  "command": "rules check",
  "ok": true,
  "exit_code": 0,
  "result": {
    "rules": { "name": "default", "source": "built-in", "hash": "3f9c…" },
    "checks": [
      { "path": "/media/disk/app/bin", "root": "/media/disk", "depth": 2, "exists": true,
        "decision": "skip", "basis": "rule",
        "reason": "Skip: matches skip rule 'bin' of rules 'default': build output",
        "rule": { "index": 4, "match": "bin", "action": "skip", "max_depth": null, "why": "build output" },
        "content": null, "reached": true, "blocked_by": null },
      { "path": "/media/disk/Documents", "...": "decision descend, rule user_data" },
      { "path": "/media/disk/app/bin/Documents", "decision": "descend", "reached": false,
        "blocked_by": { "path": "/media/disk/app/bin", "decision": "skip",
                        "reason": "Skip: matches skip rule 'bin' of rules 'default': build output" },
        "...": "" }
    ]
  },
  "error": null
}
```

- `decision`: `descend`, `shallow` or `skip`.
- `basis`: `scan_root`, `rule`, `safety_limit`, `hidden`, `no_rule`, `user_content`,
  `program_content`, `no_program_content`, `content_unknown`.
- `rule`: the matching rule and its position in the file, or `null`.
- `content`: `{ "files", "user_files", "program_files" }` when the content decided, else `null`.
- `reached`: `false` if a folder between root and path is skipped or shallow; `blocked_by` names
  the first one. The path's own `decision` is then what would apply if it were reached.

Errors: `path_not_found` (1) or `rules_invalid` (1) for the rule file, `usage` (2).

### `sfo rules show`

Prints the rules with every setting spelled out; `--rules <file>` shows that file instead of
the shipped rules. Without `--json` the output is a valid rule file, so
`sfo rules show > my-disk.json` starts a profile. With `--json`, `result` is
`{ "rules": { "name", "source", "hash" }, "content": { ...the rule file... } }`.

## Reading results

Issue: https://github.com/crack666/smart_file_organizer/issues/6

`summary`, `dirs`, `files`, `show` and `export` read a job without changing it. They are
built for a model that reads in small, predictable pieces: start with `sfo summary`, narrow
down with `dirs` and `files`, look at single entries with `show`.

**Paths.** Wherever a command takes a path (`--under`, `--dir`, `show`), give the full path
as listed, or a path relative to the job root (`photos/2024`, `.` for the root).

**Verdict codes.** `--verdict` and `--retention` accept the codes the core defines in
`VerdictCodes` (see [architecture.md](architecture.md), Phase 1), plus `error` (only an error
is stored for the directory) and `none` (not judged yet, or no retention). Output carries the
same codes. When the codes change in the core, the accepted values change with them; `sfo dirs
--help` always lists the current ones.

### Lists and paging

`dirs` and `files` return one page:

```json
{
  "job_id": 7,
  "total": 1830,
  "limit": 200,
  "offset": 0,
  "next_offset": 200,
  "items": [ ... ]
}
```

| Option | Meaning |
|---|---|
| `--limit <n>` | Items per page, 1 to 1000. Default 200. |
| `--offset <n>` | Items to skip. Use `next_offset` from the previous page; it is `null` on the last page. |

Order is stable (ties are broken by path), so paging with `next_offset` sees every item once.

Filters shared by `dirs` and `files` (for `files` they apply to the directory holding the file):

| Option | Meaning |
|---|---|
| `--verdict <code>` | Directory verdict, `error` or `none`. Repeatable; any value matches. |
| `--retention <code>` | Retention code or `none`. Repeatable. |
| `--sensitive yes\|no` | `yes`: only marked files, or directories with marked files in their subtree. `no`: the others. |
| `--include-sensitive` | Lift the protection described below. For a person at this machine only. |

### Sensitive files

Every file passes `SensitiveOutputGuard` (see
[engineering-decisions.md](engineering-decisions.md#8-sensitive-files-local-and-external-readers)).
A marked file shows path, type, markers (`sensitive`), `protected: true`, a neutral summary,
category `review_needed`, and `null` for confidence, importance, target and model output.
Filters see the same values: `--category trash_candidate` never returns a marked file, and
`--max-confidence` leaves marked files out.

Model texts about a directory (purpose, reason, summary, error texts, raw verdict answer,
anomaly reasons) are replaced by a neutral hint when they name a marked file of the subtree
(full name, or the name without extension if it has at least 4 characters). The item then has
`text_withheld: true`. This is a name check: a text can still describe a marked file without
naming it (https://github.com/crack666/smart_file_organizer/issues/33).

`--include-sensitive` turns both protections off for one run and writes a note to stderr. No
setting or environment variable turns it on.

### `sfo summary <job>`

One page as an entry point. Its size does not grow with the number of files: counts,
distributions over fixed value sets and the largest directories. `--top <n>` (0 to 25,
default 10) sets how many directories are listed. With 10,000 files the JSON is about 4 kB at
the default and 8 kB at `--top 25` (measured 12.11.2026 by
`ReadCommandTests.Summary_OfTenThousandFiles_StaysUnder20KB`, which fails above 20 kB).

```bash
sfo summary 7 --json
```

```json
{
  "schema_version": 1,
  "command": "summary",
  "ok": true,
  "exit_code": 0,
  "result": {
    "job": { "id": 7, "status": "completed", "...": "same fields as scan" },
    "directories": 112,
    "files": { "total": 1830, "pending": 330, "errors": 3, "sensitive": 4, "total_bytes": 9126805504 },
    "by_category": { "unclassified": 1220, "photo": 410, "invoice": 120, "review_needed": 80 },
    "by_file_type": { "image": 700, "document": 640, "other": 490 },
    "by_status": { "discovered": 1100, "ai_analyzed": 610, "skipped": 117, "error": 3 },
    "by_sensitive_marker": { "...": "marker code: count" },
    "directories_by_verdict": { "...": "verdict code, error or none: count" },
    "directories_by_retention": { "...": "retention code or none: count" },
    "largest_directories": [
      { "relative_path": "photos", "size_bytes": 7340032000, "files": 900,
        "verdict": "...", "retention": "...", "sensitive_files": 0 }
    ]
  },
  "error": null
}
```

- `files.pending`: image, video and document files still waiting for classification (as in
  `sfo status`). `files.errors`: files whose classification failed.
- `by_category` counts the category `files` reports, so marked files count as `review_needed`;
  `unclassified` are files without a category.
- Distributions are sorted by count, largest first. `largest_directories` leaves out the job
  root; sizes and file counts cover the whole subtree.

### `sfo dirs <job>`

Directories with subtree size, file count, verdict and summary.

| Option | Meaning |
|---|---|
| `--under <path>` | Only this directory and its subtree. |
| `--depth <n>` | Levels below `--under` (or the root); `0` lists only that directory. |
| `--min-size <size>` | Minimum subtree size: bytes or with `K`, `M`, `G`, `T` (powers of 1024), for example `500M`. |
| `--sort size\|files\|path` | Largest or most files first, or by path (default). |

```bash
sfo dirs 7 --sort size --depth 1 --limit 2 --json
```

```json
{
  "job_id": 7, "total": 9, "limit": 2, "offset": 0, "next_offset": 2,
  "items": [
    { "path": "/media/usb-stick", "relative_path": ".", "depth": 0, "status": "scanned",
      "size_bytes": 9126805504, "files": 1830, "direct_files": 12, "subdirs": 8,
      "verdict": "...", "verdict_source": "...", "retention": "...", "sensitive_files": 4,
      "purpose": "...", "summary": "...", "text_withheld": false }
  ]
}
```

`status` is the scan state (`scanned`, `skip`, `shallow`, `error`, `pending`). `verdict` is a
verdict code, `error` or `null`; `verdict_source` tells whether a rule or the model decided.
`size_bytes`, `files` and `sensitive_files` cover the subtree, `direct_files` the directory
itself. `summary` is the Phase 3 summary, `null` until that phase ran.

### `sfo files <job>`

Files with type, category, confidence, summary and status.

| Option | Meaning |
|---|---|
| `--dir <path>` | Only files directly in this directory. |
| `--recursive`, `-r` | With `--dir`: include subdirectories. |
| `--category <value>` | Category as reported (`photo`, `invoice`, `review_needed`, ...). `unknown` also matches unclassified files. Repeatable. |
| `--status <value>` | `discovered`, `ai_analyzed`, `skipped`, `error`, ..., or `pending` (`discovered` or `processing`). Repeatable. |
| `--type <value>` | `image`, `video`, `document`, `archive`, ... Repeatable. |
| `--max-confidence <x>` | Confidence at most `x` (0 to 1, decimal point). Files without a confidence are left out. |
| `--sort path\|size` | By path (default) or largest first. |

`sfo files --help` lists every accepted value. Values are `snake_case`; `ReviewNeeded` is
accepted as well.

```bash
sfo files 7 --dir photos --max-confidence 0.5 --json
```

```json
{
  "job_id": 7, "total": 1, "limit": 200, "offset": 0, "next_offset": null,
  "items": [
    { "path": "/media/usb-stick/photos/scan_004.jpg", "name": "scan_004.jpg",
      "file_type": "image", "size_bytes": 482113, "last_write_time": "2019-07-02T10:14:00.000Z",
      "status": "ai_analyzed", "category": "document_scan", "category_source": "ai",
      "confidence": 0.42, "summary": "Scanned page of a rental contract.",
      "sensitive": [], "protected": false, "dir_verdict": "..." }
  ]
}
```

`category_source` is `ai`, `user` (an override), `sensitive` (forced to review by a marker) or
`null`. `dir_verdict` is the verdict of the directory holding the file.

### `sfo show <job> <path>`

One directory or file with everything that is known about it. `kind` is `directory` or
`file`; the other member is `null`.

- File: every field of `files`, plus `relative_dir`, `extension`, `importance`,
  `suggested_target`, `model_used`, `analyzed_at`, `error`, `raw_response`, `sensitive_rule`
  and `dir_retention`.
- Directory: every field of `dirs`, plus `verdict_rule`, `reason`, `verdict_error`,
  `unit_directory` (for a unit: the directory that is the whole), `verdict_model`,
  `judged_at`, `verdict_raw_response`, `profile` (the facts the verdict was based on, as
  stored), `summary_error` and `analyses` (the stored pre-assessment and summary rows with
  theme, sampling and anomalies).

```bash
sfo show 7 photos/2019 --json
```

A path that is neither a directory nor a file of the job fails with `path_not_found` (1).

### `sfo export <job> --csv <file>`

Writes every file of the job as CSV (UTF-8 with BOM, the columns of `ScanExportService`) and
nothing else. An existing file is replaced. `--include-sensitive` as above.

```json
{ "job_id": 7, "path": "/home/me/usb-stick.csv", "rows": 1830, "include_sensitive": false }
```

Errors: `job_not_found` (1), `path_not_found` (1, the target folder does not exist),
`export_failed` (1, the file could not be written).

## Machine contract

Applies to every command, including the ones added later.

**stdout.** With `--json`, exactly one JSON document, also on failure and on a wrong
invocation. Without `--json`, human-readable text that may change at any time.

**Envelope.**

| Field | Meaning |
|---|---|
| `schema_version` | Version of the document shapes, currently `1`. Raised when a field is removed or changes meaning; new fields can appear without a raise. |
| `command` | The command path, for example `scan`; `sfo` when no command was recognised. |
| `ok` | `true` exactly when `exit_code` is 0. |
| `exit_code` | Same as the process exit code. |
| `result` | Command-specific payload; `null` on most failures, present where it helps (the paused job after Ctrl+C during a scan, the doctor report). |
| `error` | `null`, or `{ "code": "...", "message": "..." }`. `code` is stable; `message` is for people. |

Field names are `snake_case`, enum values are `snake_case` strings, timestamps are ISO 8601
UTC with a trailing `Z`.

**stderr.** Logs and progress, never results. With `--progress jsonl`, every stderr line is
one JSON object with `schema_version` and `type`:

```json
{"schema_version":1,"type":"progress","phase":"scan","processed":120,"total":480,"errors":0,"current_path":"/media/usb-stick/photos","message":null}
{"schema_version":1,"type":"log","level":"warning","category":"ClassificationService","message":"...","exception":null}
```

`phase` is `scan`, `pre_assessment`, `classification`, `summary` or `compare`. Progress is throttled to
one line per phase every 500 ms, plus the final line of a phase.

**Exit codes.**

| Code | Meaning | `error.code` |
|---|---|---|
| 0 | Done | |
| 1 | The command ran but could not do its job | `job_not_found`, `path_not_found`, `scan_failed`, `scan_not_completed`, `wrong_role`, `compare_incomplete`, `database_error`, `model_missing`, `rules_invalid`, `export_failed`, `internal_error` |
| 2 | Wrong invocation: unknown command or option, missing or invalid value | `usage` |
| 3 | Ollama not reachable | `ollama_unavailable` |
| 130 | Interrupted with Ctrl+C; stored work stays and the command can be run again | `cancelled` |

A second Ctrl+C terminates the process immediately.

## Adding a command

The contract is enforced in one place, so a new command does not repeat it:

1. Add `src/SmartFileOrganizer.Cli/Commands/<Name>Command.cs` with a static
   `Create(CliHost host, GlobalOptions globals)` that builds a `System.CommandLine.Command`
   and binds the handler with `CommandRunner.SetHandler`.
2. In the handler, get services from `ctx.GetServicesAsync(ct)`, report progress through
   `ctx.Progress`, and return a `CommandOutcome`: `Success(result, text)` or
   `Fail(exitCode, ErrorCodes.X, message)`. Throwing `CliFailureException` does the same.
   Never write to stdout.
3. Define the result as records in the command class; `CliJson` turns them into `snake_case`.
   Reuse `JobView` for jobs. A command that lists or prints files reuses `ReadOptions` (paging,
   filters, `--include-sensitive`) and gets its files from `JobReadService`, which applies
   `SensitiveOutputGuard`.
4. Register the command in `SfoApp.BuildRoot`, describe it here, and add tests in
   `tests/SmartFileOrganizer.Cli.Tests`.

`CommandRunner` turns the outcome into the envelope or text, maps Ctrl+C to 130 and any
unexpected exception to 1 (`internal_error`). Parse errors never reach the handler; `SfoApp`
answers them with exit code 2.
