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
| `-v`, `--verbose` | Log at information level on stderr (default: warnings and errors). Prompts and model answers never go to stderr; with `--verbose` they are written at debug level to `sfo-debug.log` next to the database (appended, rotated to `sfo-debug.log.1` above 50 MB). For a marked file even that file holds a placeholder instead of prompt and answer. |
| `-h`, `--help` | Help for the command. |

Database and settings are found as described in
[architecture.md, File locations](architecture.md#file-locations): `--db`, then `SFO_DB`, then
the per-user default. With a redirected database, settings are read from `settings.json` next
to it (or from `SFO_SETTINGS`), so a scratch database never touches the user's settings.
The CLI never writes `settings.json`; `--model` and `--parallel` apply to one run only.
`SFO_OLLAMA_URL` replaces the shipped default address of the model server (`http://localhost:11434`) when
`settings.json` names none; an address in `settings.json` always wins. The test projects set it to a dead port.

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
| `--role source\|library` | Only with `<path>`. `source` (default): a medium to clean up. `library`: the user's organised archive (NAS, a folder on disk); it is only read, never changed and never analysed by a model (`sfo analyze` refuses it with `wrong_role`). The role is fixed when the job is created. |
| `--structure-only` | Only with `<path>`. Records names, sizes and dates and opens no file: sensitive files are marked by name and extension only, without looking at their first bytes. The job keeps the flag (`structure_only` in the job view), so a later `--resume` scans the same way. Use it for `sfo library review`. |
| `--refresh` | Only with `<path>`. Scan a known place again and compare with its previous completed job (same root and role; without `--role` a library job of the root is found too). See below. |

Every completed scan makes its root a known place (`sfo places`). The scan leaves out the hold folder of
`sfo plan apply` (`<root>/.sfo-hold`, recorded as a skipped folder) and every move notice (`_moved-files.txt`, see
[move-notice.md](move-notice.md)); the entries of a notice are stored as origin and answered by `sfo where`.

**Refresh** (https://github.com/crack666/smart_file_organizer/issues/86). `--refresh` creates a new job with the rules
and the structure-only setting of the previous job of the place (unless `--rules` is given), scans it, and then
compares the two by path, size and modification time without reading a file. An unchanged file keeps its analysis
result, its facts and a person's correction (copied to the new job), and its stored checksum still holds, so no model
is asked and no checksum is computed again for it; `sfo compare` of the new job only reads new and changed files.
Directory verdicts and summaries are not carried over. New, changed and missing files are listed; a missing file that
a plan moved (see the move history of `sfo where`) is `moved` and names its target. Without an earlier completed scan
of the root the scan runs in full and `refresh` is `null`.

```json
"refresh": {
  "previous_job_id": 7, "unchanged": 1826, "new": 3, "changed": 1, "missing": 0, "moved": 6,
  "carried": { "checksums": 412, "results": 1790, "facts": 2201, "corrections": 2 },
  "changes": [ { "path": "/media/usb-stick/DCIM/IMG_0001.jpg", "change": "moved", "size": 18234,
                 "target": "/mnt/nas/library/Fotos/IMG_0001.jpg", "plan_id": 1, "moved_at": "2026-11-14T10:00:00Z" } ],
  "changes_truncated": false
}
```

`change` is a `FileChange` code ([glossary.md, Places](glossary.md#places)); `changes` lists at most 200 files by path,
`changes_truncated` says whether there are more. `carried.checksums` counts unchanged files with a stored checksum,
`carried.results` those whose analysis result was taken over.

A directory the scan cannot read (access denied, network path gone) is stored with that error
and counted in `error_count` instead of looking empty; the scan itself continues and completes.

```bash
sfo scan /media/usb-stick --json
sfo scan /mnt/nas/library --role library --json
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
      "role": "source",
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
    "rules_changed": false,
    "refresh": null
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
| `--against <job>` | A `library` job: the stored comparison of this job against it (`sfo compare <job> --against <library>`) decides what is left out. Repeatable for a library with several roots: a file `backed_up` in any of them is left out. Files with `backed_up` are not classified; folders whose every file below is `backed_up` (`backed_up_share` 1.0) get no pre-assessment and no summary. `new`, `differs` and `error` are judged as usual. Without the option the whole job is analysed, and the marks of an earlier run with `--against` are released, so the files are judged again. Fails with `comparison_not_found` (1) when the comparison with one of them is not stored. |
| `--retry-errors` | Classification: try files that failed in an earlier run again. Files with a result are never called again; files still pending are judged as in any run; files a person corrected are left alone. Summary: write again the summaries that failed and those older than the results they rest on, so the folder of a retried file gets a summary that knows it. Failed directory verdicts (phase 1) are asked again in every run, with or without this option. |

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
        "message": "AI classification finished with 3 error(s). 41 result(s) checked a second time.",
        "retried": 0, "second_checks": 41, "reused": 0 }
    ],
    "error_count": 3,
    "model_server_lost": null,
    "model_calls": [
      { "kind": "classification", "calls": 899, "prompt_tokens": 2037963, "cached_prompt_tokens": 1650000,
        "answer_tokens": 54000, "mean_seconds": 0.95, "cut_off": 0 },
      { "kind": "second_check", "calls": 41, "prompt_tokens": 98000, "cached_prompt_tokens": 70000,
        "answer_tokens": 2500, "mean_seconds": 1.02, "cut_off": 0 }
    ]
  },
  "error": null
}
```

`model_calls` has one entry per kind of call made in this run (`pre_assessment`, `classification`,
`second_check`, `summary`; library questions as `content_fit`, `folder_name`, `library_advice` with calls and time
only, see [glossary.md](glossary.md#kind-of-a-model-call-modelcallkinds)), with the
counts the model server returns with every answer: `prompt_tokens` (`prompt_eval_count`), of them
`cached_prompt_tokens` taken from the server prompt cache (`prompt_eval_cached_count`), `answer_tokens`
(`eval_count`), `mean_seconds` (prompt and answer time per call as the server measured it) and `cut_off`
(answers that stopped at the token limit and were asked again). A repeated call counts again. The numbers
in the example are illustrative. The text output prints the same as one line per kind.

With `--against`, `result.backed_up` says what was left out:
`{ "against": 8, "comparison_id": 3, "files": 411, "directories": 12, "libraries": [8, 9] }` (files still waiting
for the analysis that are `backed_up` in any of the libraries, and fully backed-up folders; `against` and
`comparison_id` are those of the first library named, `libraries` all of them); without it, `backed_up` is `null`. A left-out
file keeps the sensitive markers of the scan rules, so it is still listed as sensitive, and `sfo findings`
reads the comparison itself: it never counts such a file as not backed up. Issue:
https://github.com/crack666/smart_file_organizer/issues/42

`retried` is the number of failed files `--retry-errors` released, `second_checks` the number of
second calls made for unsure or contradicted results (see
[architecture.md](architecture.md#phase-2--per-file-classification-in-stages-classificationservice-directorysamplingservice));
both are 0 for the other phases. `reused` is the number of files whose result was taken over from the content store
instead of a model call ([`sfo cache`](#sfo-cache-stats)); 0 for the other phases. Errors of single files or directories do not fail the command; they are counted here and
listed by `sfo status`. Failures: `ollama_unavailable` (3), `model_server_lost` (4),
`scan_not_completed` (1), `job_not_found` (1), `wrong_role` (1, the job is a `library`),
`comparison_not_found` (1, `--against` without a stored comparison), `cancelled` (130).

**When the model server goes away during the run.** `ollama_unavailable` (3) is only the check at
the start: nothing has been asked yet. Once the run has started, a server that stops answering is
waited for, not counted as errors of the files
([architecture.md](architecture.md#when-the-model-server-goes-away)). With `--progress jsonl` each
change is one `wait` line on stderr:

```json
{"schema_version":1,"type":"wait","phase":"classification","state":"waiting","since":"2026-11-13T09:12:04Z","cause":"connection_failed","waited_seconds":0,"message":"waiting for the model server since ..."}
{"schema_version":1,"type":"wait","phase":"classification","state":"resumed","since":"2026-11-13T09:12:04Z","cause":"connection_failed","waited_seconds":41.5,"message":"model server answers again after 42s; continuing"}
```

`state` and `cause` are codes from the [glossary](glossary.md#model-server). When the server stays
away longer than `ModelServer.MaxWaitMinutes`, a `given_up` line follows and the command ends with
exit code 4, `model_server_lost`. `result` is then filled as usual plus `model_server_lost`:
`{ "phase": "classification", "since": "...Z", "cause": "connection_failed", "waited_seconds": 1800 }`.
The files and folders not judged stay pending, none is stored as an error; `sfo analyze <job>` again
finishes them.

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
      "classification": { "total": 940, "done": 610, "errors": 3, "pending": 0, "skipped": 327,
                          "rate_per_minute": null, "remaining_seconds": null, "by_model": 402, "reused": 120 },
      "summary": { "total": 104, "done": 0, "errors": 0, "pending": 104, "skipped": 0 }
    },
    "errors": [
      { "phase": "classification", "path": "/media/usb-stick/scan_004.pdf",
        "message": "Ollama request timed out after 180s." }
    ],
    "scan_errors": [
      { "cause": "access_denied", "directories": 97, "files": 0,
        "examples": [ { "path": "C:/Users/Old/Local Settings", "message": "Read error: Access to the path ... is denied." } ] }
    ],
    "links": [
      { "kind": "junction", "not_followed": 12, "followed": 0, "files": 0,
        "examples": [ { "path": "C:/Users/All Users", "target": "C:/ProgramData", "followed": false } ] }
    ]
  },
  "error": null
}
```

`phases.scan.errors` counts the folders and files the scan could not read. `scan_errors` groups them by
cause (`access_denied`, `not_found`, `path_too_long`, `io_error`) with counts and the first three paths of
each; `errors` lists the AI phases only. `links` lists the links the scan found, by kind (`junction`,
`symlink`, `mount_point`): folders not entered, folders a `follow_link` rule entered, and files that are
links; none of them counts as content ([scan-rules.md](scan-rules.md#links-and-read-errors)). After a
finished scan `directories_total` equals `directories_processed` and `files_total` equals `files_processed`:
both are counted from the stored rows (since 13.11.2026; before, the scan root was missing from the total).

`model_server_wait` is present only while an analysis of the job waits for the model server:
`{ "since": "...Z", "checked_at": "...Z", "cause": "connection_failed", "stale": false }`. `checked_at`
is the last probe. `stale` is `true` when no probe was recorded for longer than three probe intervals
(at least one minute): the waiting run was killed, and `sfo analyze <job>` continues it.

For each AI phase, `total = done + errors + pending + skipped`. `backed_up` (every phase, since 13.11.2026) is
the part of `skipped` that `sfo analyze --against` left out because the library holds it: files with `backed_up`
for `classification`, fully backed-up folders without a verdict or summary for the two folder phases. The text
output adds it as `(n already in the library)`.

- `classification` also says where `done` came from (since Issue 38): `by_model` files the model judged in a call of
  their own, `reused` files whose result was taken over from the content store without a call (origin `reused`,
  [`sfo cache`](#sfo-cache-stats)). The rest of `done` are rule results and inherited results. Both are `null` for the
  folder phases. The text output adds `(n by the model, m taken over from the content store)` when something was taken
  over. `rate_per_minute` counts model calls only.
- `pre_assessment` and `summary` count directories the AI phases consider: not skipped by the
  scan rules and not hidden (name starting with a dot).
- `classification` counts every file (since 12.11.2026; before: only image, video, document).
  Files a rule decides (programs, fonts, unknown binaries) count as `done` without a model call. `done`
  includes files that inherited the result of their directory's confirmed sample; `pending`
  includes files waiting for that sample. `skipped` are files of units and system directories,
  which are not analysed one by one. While a run goes on (the newest model result is less than 10 minutes old),
  `rate_per_minute` is how many files the model judged per minute over the last 50 results and
  `remaining_seconds` the time left for the files still waiting for the model (`pending` without those waiting for
  a sample); both are `null` otherwise. Every phase carries the two fields; only `classification` fills them.
- `summary`: folders that need no summary count as `skipped`, not `pending`: folders judged with their unit or by
  rule without a pre-assessment, and pre-assessed folders without a file of their own. After a complete
  `sfo analyze` nothing is `pending` (before 13.11.2026 they stayed `pending` for ever).

### `sfo compare <job> --against <job> [--against <job> ...]`

Which files of a `source` scan are already in the `library` (the user's organised archive), by
content, not by name. Codes in JSON and on the command line are English; this command never
deletes or changes anything. Both scans must be `completed` and both roots reachable. Issue:
https://github.com/crack666/smart_file_organizer/issues/24

| Option | Meaning |
|---|---|
| `--against <job>` | The `library` job. Repeatable: a library often has several roots (separate shares for photos, videos, documents), each scanned as its own library job. Without it, only duplicates within the source are found and every `state` is `null`. |
| `--files <state>` | List the files with this state (`backed_up`, `new`, `differs`, `error`, or `all`). Repeatable. Default: no file list. |
| `--limit <n>` | Maximum entries per list (`directories`, `errors`, `duplicates.groups`, `files`); default 100, `0` lists everything. The `*_total` fields count all. |

State per source file:

| `state` | Meaning |
|---|---|
| `backed_up` | A file with exactly the same content is in the library: the full SHA-256 of both files is equal (the sample checksum alone never counts). `match_path` is one of them (first by path), `match_count` how many, `match_hash` the shared full SHA-256 and `match_verified_at` the older of the two times that checksum was computed from the files (possibly in an earlier run, see the cache below). A step that deletes the source file must read both files again and compare with `match_hash` first. For a marked file, or a file whose match is marked, `match_hash` is `null`: for a short secret the checksum would confirm a guess of its content. |
| `new` | No library file has this content, and none has the same name in a folder of the same name. |
| `differs` | Not in the library by content, but a file with the same name lies in a folder of the same name there (`match_path`): probably another version. |
| `error` | The content could not be determined: the source file is unreadable, gone or changed since its scan, or a library file of the same size that could hold this content cannot be read. `error` says which. Never counted as `new` or `backed_up`. |

How content is compared, so a large library on a network drive is read as little as possible:
size first; for files sharing a size, the sample checksum (SHA-256 over the size, the first and
last 64 KiB and six places of 4 KiB in between whose offsets follow from the size); only where
those agree, SHA-256 over the whole file. Files up to 128 KiB are read whole at once.
Checksums are stored per path and reused while size and modification time are unchanged, so a
second run without changes reads no file content (`hashing` shows the counts; `head_tail_computed`
counts sample checksums, the key stays from the time the sample was head and tail only, before
https://github.com/crack666/smart_file_organizer/issues/87). The same checksums serve
[`sfo duplicates`](#sfo-duplicates-job-job-). A file never matches itself, even when the source lies inside the library.
Folders a scan skipped by rule (`dir_status` skip) are not part of the comparison, on either side: no file
below them is compared and no read error below them is reported, even when the folder is gone by now.

`duplicates` groups source files with identical content (empty files excluded); a file's
`duplicate_group` points to its group. `directories` counts each source directory including its
subdirectories: a directory with `backed_up_share` 1.0 is fully in the library and needs no model
analysis; otherwise only its files that are not `backed_up` do. `sfo analyze <job> --against <library>`
acts on this.

```bash
sfo compare 7 --against 8 --files new --files error --json
```

```json
{
  "schema_version": 1,
  "command": "compare",
  "ok": true,
  "exit_code": 0,
  "result": {
    "comparison_id": 3,
    "source": { "id": 7, "root_path": "/media/usb-stick", "role": "source", "...": "same fields as scan" },
    "against": { "id": 8, "root_path": "/mnt/nas/library", "role": "library", "...": "same fields as scan" },
    "compared_at": "2026-11-12T15:02:11.410Z",
    "complete": true,
    "counts": { "files": 1830, "backed_up": 1712, "new": 101, "differs": 17, "errors": 0 },
    "duplicates": {
      "groups_total": 1, "files": 2,
      "groups": [ { "group": 1, "size": 48211, "paths": [ "/media/usb-stick/a.jpg", "/media/usb-stick/copy/a.jpg" ] } ]
    },
    "directories_total": 112,
    "directories": [
      { "path": "/media/usb-stick", "files": 1830, "backed_up": 1712, "new": 101, "differs": 17, "errors": 0,
        "backed_up_share": 0.9355 }
    ],
    "errors_total": 0,
    "errors": [],
    "files_total": 101,
    "files": [
      { "path": "/media/usb-stick/notes/2014.txt", "state": "new", "match_path": null, "match_count": 0,
        "match_hash": null, "match_verified_at": null, "duplicate_group": null, "error": null,
        "match_library_job_id": null }
    ],
    "hashing": { "head_tail_computed": 240, "full_computed": 1716, "reused": 0 },
    "libraries": [
      { "job": { "id": 8, "root_path": "/mnt/nas/library", "...": "" }, "comparison_id": 3,
        "compared_at": "2026-11-12T15:02:11.410Z", "complete": true, "backed_up": 1712, "backed_up_bytes": 5120000000 }
    ]
  },
  "error": null
}
```

**Several libraries** (https://github.com/crack666/smart_file_organizer/issues/74). Every job is checked first;
then the source is compared with each library in the order named, and each pair is stored on its own as with one
library. The result joins them per file: `backed_up` as soon as one library holds the content (the first one named
that does), else `error` when one could not tell (another root might hold it), else `differs`, else `new`.
`match_library_job_id` per file names the library of `match_path`. `against` is the first library, `libraries`
lists each with `job`, `comparison_id`, `compared_at`, `complete` and `backed_up` / `backed_up_bytes` (source files
it holds; a file held by several counts at the first). `counts`, `directories` and `errors` cover all of them,
`compared_at` is the oldest of the comparisons. The comparisons of one run share a run id; `sfo analyze`,
`sfo findings` and `sfo library match` without `--against` read the libraries of the newest run together. A
comparison stored before is a run with one library.

Errors are listed in `errors` with `role` (`source` or `library`), `kind` (`scan`: a directory the
scan could not read, so its files are missing from the job; `read`: a file that could not be read
now) and `path`. If there is any, `complete` is `false` and the command exits 1 with
`compare_incomplete`; the result is still returned and stored. Other failures: `job_not_found` (1),
`scan_not_completed` (1), `wrong_role` (1, the first job is not a `source` or `--against` not a
`library`), `path_not_found` (1, a root is not reachable), `cancelled` (130; checksums computed so
far are kept).

The result is stored per source and library job (a new run replaces it) and dropped when either
job is deleted or a directory of it is rescanned. Not decided here: a file the library holds
under the same content but whose scan was skipped by the scan rules counts as `new`; files that
changed in the library after its scan are judged by the library scan's sizes.

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
    "model": { "name": "gemma4:26b", "available": null, "installed": [] },
    "libraries": [ { "job_id": 4, "root_path": "/media/archive", "readable": true, "problem": null } ]
  },
  "error": { "code": "ollama_unavailable", "message": "Ollama is not reachable at http://localhost:11434." }
}
```

`model.available` is `null` when Ollama could not be asked. `libraries` checks the root of every completed library
job: it exists and one entry of it can be listed; `problem` says why not (a share that is not connected, a drive that is
not plugged in, no access). Exit code: 1 for a database error (`database_error`), 3 if Ollama is unreachable, 1 if the
model is missing (`model_missing`), 1 if a library root cannot be read (`library_unreachable`), else 0.

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

### `sfo eval <corpus>`

Scans `<corpus>/<root>` (from `<corpus>/truth.json`) into a new job, runs the pre-assessment and
the classification, and scores directory verdicts, categories and sensitive markers against the
truth. The run is stored for `--compare`. Corpus, truth format and what counts:
[evaluation.md](evaluation.md). Phase 3 (directory summaries) is left out on purpose: nothing it
writes is measured, and it would cost a model call per mixed folder. `sfo status <job>` therefore
shows the summary phase as pending; `sfo analyze <job>` writes the summaries if you want them.

| Option | Meaning |
|---|---|
| `--stub <answers.json>` | Answer from this file instead of Ollama. Same shape as `truth.json`; the truth itself is a perfect answer sheet. |
| `--model <name>` | Ollama model for this run. Not saved. |
| `--parallel <n>` | Concurrent Ollama requests for this run, 1 to 8. |

```bash
sfo eval ~/sfo-eval --db ~/sfo-eval.db --json
```

```json
{
  "schema_version": 1,
  "command": "eval",
  "ok": true,
  "exit_code": 0,
  "result": {
    "run_id": 4,
    "mode": "corpus",
    "subject": "small v1 seed 36",
    "job_id": 9,
    "model": "gemma4:26b",
    "stub": null,
    "prompt_fingerprint": "c5304d1e2d5295a4",
    "app_version": "1.0.0+525c535…",
    "settings": { "ollama.max_parallel_requests": "4", "scan.rules": "default 3f9c…", "...": "" },
    "scan_seconds": 0.41,
    "analyze_seconds": 212.7,
    "scores": [
      { "kind": "directory_verdict", "total": 41, "hits": 35, "wrong": 6, "missing": 0, "accuracy": 0.8537, "sensitive": null },
      { "kind": "file_category", "total": 49, "hits": 44, "wrong": 5, "missing": 0, "accuracy": 0.898, "sensitive": null },
      { "kind": "sensitive", "total": 97, "hits": 93, "wrong": 3, "missing": 1, "accuracy": 0.9588,
        "sensitive": { "expected_marked": 13, "fully_found": 10, "missed": 3, "false_alarms": 1 } }
    ],
    "failures": [
      { "kind": "file_category", "key": "file_category:Fotos/2019 Skifahrt/IMG_20190212_101530.jpg",
        "path": "Fotos/2019 Skifahrt/IMG_20190212_101530.jpg", "outcome": "wrong",
        "expected": "photo", "actual": "screenshot", "decided_by": "model gemma4:26b", "detail": null }
    ],
    "untracked_count": 0,
    "untracked": [],
    "cases": null,
    "model_calls": []
  },
  "error": null
}
```

`model_calls` counts the calls and tokens of this run as for
[`sfo analyze`](#sfo-analyze-job); it is empty with `--stub`, which makes no model call.

(Numbers illustrative.) `kind` is `directory_verdict`, `file_category` or `sensitive`; `outcome`
is `wrong` or `missing` (`hit` appears only in `cases`). `expected` and `actual` are codes; markers
are comma-separated, `none` for no marker. `decided_by` is `rule <id>`, `model <name>`, both
for markers, or `sample` when the sample check of the staged analysis raised a uniform folder to
mixed or a file took its category from a confirmed sample. `key` is stable across runs. `untracked` lists (up to 50) paths the scan found that the
truth does not list.

Errors: `path_not_found` (1, corpus, truth or answer file), `truth_invalid` (1, with every problem
in the message), `scan_failed` (1), `ollama_unavailable` (3), `cancelled` (130).

### `sfo eval --cases`

Runs every stored test case (corrections made in the review, see
[evaluation.md](evaluation.md#test-cases-from-corrections)) against the current model and prompts,
and stores the run. Accepts `--stub`, `--model`, `--parallel`. `result` has the shape above with
`mode` `cases`, `job_id` and `scan_seconds` `null`, one score (`file_category`), and `cases`:

```json
"cases": [
  { "case_id": 3, "kind": "file_category", "path": "/media/usb-stick/Fotos/IMG_0001.jpg",
    "expected": "photo", "corrected_from": "screenshot", "actual": "photo", "outcome": "hit",
    "sensitive": false, "detail": null }
]
```

`corrected_from` is the model's answer when the human corrected it; `actual` is the answer now.
No output ever contains the stored evidence. Progress phase: `eval_cases`.

### `sfo eval --compare [<run> [<run>]]`

Sets two stored runs side by side. No id: the newest run against the newest earlier run with the
same mode and subject. One id: that run against the one before it. Two ids: the first as before,
the second as after.

```json
"result": {
  "before": { "id": 3, "mode": "corpus", "subject": "small v1 seed 36", "model": "gemma4:26b", "...": "" },
  "after": { "id": 4, "...": "" },
  "same_subject": true,
  "changes": [ { "name": "model", "before": "gemma4:26b", "after": "other-model" } ],
  "scores": [ { "kind": "file_category", "before_hits": 44, "before_total": 49, "before_accuracy": 0.898,
                "after_hits": 46, "after_total": 49, "after_accuracy": 0.9388, "change": 0.0408 } ],
  "fixed": [ { "kind": "file_category", "key": "...", "...": "failure of the earlier run" } ],
  "new_failures": [ { "...": "failure of the later run" } ],
  "still_failing": 3
}
```

`changes` covers model, stub, prompt fingerprint, app version and every stored setting.
Errors: `run_not_found` (1, unknown id or no earlier run to compare with).

### `sfo eval generate <dir>`

Writes the small built-in corpus to `<dir>` (must be empty or missing): `<dir>/tree/` and
`<dir>/truth.json`. `--seed <n>` (default 36) selects the variant; the same seed gives the same
corpus. `result`: `{ "directory", "truth", "root", "name", "generator_version", "seed",
"directories", "files" }`. Errors: `output_not_empty` (1).

## Reading results

Issue: https://github.com/crack666/smart_file_organizer/issues/6

`summary`, `dirs`, `files`, `show` and `export` read a job without changing it. They are
built for a model that reads in small, predictable pieces: start with `sfo summary`, narrow
down with `dirs` and `files`, look at single entries with `show`.

**Paths.** Wherever a command takes a path (`--under`, `--dir`, `show`), give the full path
as listed, or a path relative to the job root (`photos/2024`, `.` for the root).

**Verdict codes.** `--verdict` and `--retention` accept the codes the core defines in
`VerdictCodes` (`--verdict mixed`, `--retention keep`; all codes and their meaning in
[glossary.md](glossary.md)), plus `error` (only an error is stored for the directory) and `none`
(not judged yet, or no retention). JSON output carries the same codes; the German words used
before 12.11.2026 (`gemischt`, `behalten`) are no longer accepted. `sfo dirs --help` always lists
the current codes.

**Words in the text output.** Without `--json`, verdict, retention, verdict source, result origin
and sensitive markers are shown as words for people, in German when the summary language in the
settings is German (`"SummaryLanguage": "Deutsch"`), in English otherwise (`mixed` / `gemischt`,
`credentials` / `Zugangsdaten`). Programs read the JSON, which never changes with the language.

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
`text_withheld: true`. This name check is a second net; the protection is at the source: the
verdict model sees names and the folder profile only, and the summary model sees a marked file
as its neutral line. A summary written before a file of its directory was marked is shown as
a stale hint (summary, theme, anomaly reasons and error withheld, `text_withheld: true`) until
the next `sfo analyze` writes it again
(https://github.com/crack666/smart_file_organizer/issues/33).

Folders the scan skipped without entering are marked by their name (`.ssh`, `.gnupg`,
`.aws`, wallets, `AppData`, browser and mail profiles). `dirs` and `show` give such a folder
`sensitive` (marker tokens) and `sensitive_rule` (for example `dir:ssh`); `sensitive_dirs`
counts marked folders in the subtree, this one included, and `--sensitive yes` lists them.
`summary` has `sensitive_directories`.

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
    "sensitive_directories": 2,
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

- `files.pending`: files of any type still waiting for classification (as in
  `sfo status`). `files.errors`: files whose classification failed.
- `by_category` counts the category `files` reports, so marked files count as `review_needed`;
  `unclassified` are files without a category.
- Distributions are sorted by count, largest first. `largest_directories` leaves out the job
  root; sizes and file counts cover the whole subtree.
- `bundles`: the job with and without dumps, from the last `sfo bundles build`; `null` when bundles
  were never built or a rescan dropped them. `{ "built_at", "dump_count", "bundle_count",
  "unbundled_files", "dumps": {...}, "without_dumps": {...} }`, each part with `directories`,
  `empty_directories`, `files`, `total_bytes` and `by_file_type`. The other numbers of the summary
  include dumps.
- `findings`: counts of [`sfo findings`](#sfo-findings-job): `{ "listed", "sure", "unsure", "by_kind",
  "by_certainty", "by_state", "missing_steps" }` (listed are open and accepted findings); `null` for a library.

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
      "sensitive": [], "sensitive_rule": null, "sensitive_dirs": 1,
      "purpose": "...", "summary": "...", "text_withheld": false,
      "scan_reason": "Descend: scan root", "link_kind": null, "link_target": null, "error_cause": null }
  ]
}
```

`status` is the scan state (`scanned`, `skip`, `shallow`, `error`, `link`, `pending`; see the
[glossary](glossary.md#what-the-scan-did-with-a-folder-directorystatus)). `scan_reason` says why the scan
treated the folder as it did. A link that was not followed has status `link`, `link_kind` and
`link_target`, and no files; a link a `follow_link` rule entered keeps `link_kind` and `link_target` with its
normal status. A folder that could not be read has status `error` and `error_cause` (never `scanned` with 0
files). `verdict` is a
verdict code, `error` or `null`; `verdict_source` tells whether a rule or the model decided.
`size_bytes`, `files`, `sensitive_files` and `sensitive_dirs` cover the subtree, `direct_files`
the directory itself. `sensitive` and `sensitive_rule` are the markers of the folder itself
(a skipped folder such as `.ssh`). `summary` is the Phase 3 summary, `null` until that phase ran.

### `sfo files <job>`

Files with type, category, confidence, summary and status.

| Option | Meaning |
|---|---|
| `--dir <path>` | Only files directly in this directory. |
| `--recursive`, `-r` | With `--dir`: include subdirectories. |
| `--bundle <id>` | Only files of this bundle and its child bundles (see [`sfo bundles`](#sfo-bundles-build-job)); `bundle_not_found` (1) for an unknown id. |
| `--category <value>` | Category as reported (`photo`, `invoice`, `review_needed`, ...). `unknown` also matches unclassified files. Repeatable. |
| `--status <value>` | `discovered`, `ai_analyzed`, `skipped`, `error`, ..., or `pending` (`discovered` or `processing`). Repeatable. |
| `--type <value>` | `image`, `video`, `document`, `archive`, ... Repeatable. |
| `--max-confidence <x>` | Confidence at most `x` (0 to 1, decimal point). Files without a confidence are left out. |
| `--certainty <value>` | `certain`, `likely`, `uncertain` or `review`. Repeatable. `review` is the work list: two answers disagree or the model could not say what the file is. Files without a certainty (no result, or analysed before 12.11.2026) are left out. |
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
      "status": "ai_analyzed", "category": "document_scan", "category_source": "model",
      "confidence": 0.42, "summary": "Scanned page of a rental contract.",
      "sensitive": [], "protected": false, "dir_verdict": "...", "certainty": "likely" }
  ]
}
```

`category_source` is an origin code (see [glossary.md](glossary.md)): `model`, `human` (an override), `rule` (forced to review by a marker; until 12.11.2026 `ai`, `user` and `sensitive`) or
`null`. `dir_verdict` is the verdict of the directory holding the file. `certainty` says how far the
result can be relied on: `certain` (two independent signals agree), `likely` (one answer nothing
contradicts, or inherited), `uncertain` (a due second check did not happen), `review` (a person should
look); `null` without a result or for results written before 12.11.2026.

### `sfo show <job> <path>`

One directory or file with everything that is known about it. `kind` is `directory` or
`file`; the other member is `null`.

- File: every field of `files`, plus `relative_dir`, `extension`, `importance`,
  `suggested_target` (deprecated, always `null`, see below), `model_used`, `analyzed_at`, `error`, `raw_response`, `sensitive_rule`,
  `dir_retention`, `attempts` (how often the file went through classification), `previous_error`
  (the error a retry replaced) and `checks`: every second check with `reasons`, `outcome`
  (`agreed`, `disagreed`, `corrected`, `rule_wins`, `failed`, `skipped`), `rule_note`, `first` and
  `second` (category, importance, confidence, keep hint, summary, target (always `null`), keep reason, model, error,
  raw response; for a protected file only the codes, model texts are `null`) and `checked_at`.

`suggested_target` is deprecated (https://github.com/crack666/smart_file_organizer/issues/74): the model is no
longer asked for a target per file, and older runs stored paths it made up. The key stays in the JSON of `sfo show`
and `sfo files` and the column in `sfo export`, always empty, and the text output and the desktop app show no
target from it. Targets come from `sfo library match` and the findings and always lie in a library root.
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
nothing else. An existing file is replaced. `--include-sensitive` as above. The column `suggested_target`
stays for sheets that address columns by position and is always empty (deprecated, see `sfo show`).

```json
{ "job_id": 7, "path": "/home/me/usb-stick.csv", "rows": 1830, "include_sensitive": false }
```

Errors: `job_not_found` (1), `path_not_found` (1, the target folder does not exist),
`export_failed` (1, the file could not be written).

## Judging the structure

Both commands judge how a library is filed and change nothing in it. Finding kinds,
thresholds, the word file and the target file are described in
[library-review.md](library-review.md).

### `sfo library review <job>`

Runs the structure rules on a completed job and, unless `--no-model` is given, asks the
local model about roles and a few changes. It is meant for a job with the role `library`;
a `source` job is judged too, with a note on stderr and in `role_note` (otherwise `null`).

| Option | Meaning |
|---|---|
| `--no-model` | Rules only. |
| `--model <name>` | Ollama model for this run. Default: `Ollama.Model`. |
| `--refresh` | Ask the model again even if an answer to the same facts is stored. |
| `--kind <code>` | Only findings of this kind. Repeatable. |
| `--limit <n>`, `--offset <n>` | Paging over the findings, as in [Lists and paging](#lists-and-paging). Default limit 50. |
| `--words <file>` | Word file with extra words and exceptions. |

```bash
sfo scan /media/archive --role library --structure-only --json
sfo library review 7 --no-model --json
```

```json
{
  "job": { "id": 7, "status": "completed", "structure_only": true, "...": "..." },
  "structure": { "directories": 80, "files": 374, "bytes": 3669301, "system_files": 1,
                 "max_depth": 11, "median_depth": 2, "complete": false,
                 "dumps": 2, "dump_directories": 24, "dump_files": 5, "dump_bytes": 17009 },
  "by_kind": { "dump": 2, "naming_inconsistent": 2, "...": 1 },
  "excluded_by_exceptions": 0,
  "words": "built-in",
  "role_note": null,
  "job_id": 7, "total": 21, "limit": 50, "offset": 0, "next_offset": null,
  "items": [
    {
      "kind": "dump", "impact": "high",
      "path": "/media/archive/Handy", "relative_path": "Handy",
      "reason": "device dump (holds Android, DCIM, LOST.DIR): ...",
      "items": [], "more": 0, "directories": 21, "files": 3, "bytes": 15006,
      "details": { "dump_kind": "device", "empty_dirs": 4, "levels": 7, "share_of_dirs": 0.262,
                   "evidence": "holds Android, DCIM, LOST.DIR" }
    }
  ],
  "advice": { "status": "skipped", "model": null, "created_at": null, "good": [], "roles": [],
              "changes": [], "outline": [], "dropped": 0, "error": null }
}
```

`structure.complete` is `false` when folders were skipped or recorded shallow by the scan
rules; their contents are then unknown. `items[].items` names up to five members of a
bundled finding (relative paths), `more` counts the rest. `sensitive_location` findings
show path and marker only.

Errors: `job_not_found` (1), `scan_not_completed` (1), `path_not_found` (1, word file),
`words_invalid` (1), `ollama_unavailable` (3, with `result`: the rule findings are complete,
`advice.status` is `unavailable`).

### `sfo library propose <job>`

Suggests a target structure and, with `--out`, writes it as a target file.

| Option | Meaning |
|---|---|
| `--out <file>` | Write the target file here (replaced if it exists). Refused inside the job's folder. |
| `--root <folder>` | Folder the targets go under, written into the file. Default: empty. |
| `--lang <en\|de>` | Language of folder names when the rules build the structure. Default `en`. |
| `--no-model`, `--model`, `--refresh`, `--words` | As for `review`. |

The result holds `job`, `status` (as `advice.status`), `error`, `dropped`, `path` (the
written file or `null`) and `target` (the target structure). When the model is unreachable
(`status: "unavailable"`) or its answer cannot be used (`status: "failed"`, reason in
`error`), the rule-built structure is returned with exit code 0 and a note on stderr.

Errors: `job_not_found` (1), `scan_not_completed` (1), `output_inside_job` (1),
`path_not_found` (1, missing target folder or word file), `export_failed` (1),
`words_invalid` (1).

## Placing into the library

Both commands read the target map of a library job; neither moves or renames anything. Map,
signals, certainty, new folder names, the patterns file and the settings are described in
[library-match.md](library-match.md). Issue:
https://github.com/crack666/smart_file_organizer/issues/25

### `sfo library show <job>`

The target map of a `library` job, one page of folders in tree order.

| Option | Meaning |
|---|---|
| `--under <path>` | Only this folder and its subtree (relative to the library root, or a full path). |
| `--depth <n>` | Levels below `--under` (or the root); `0` lists only that folder. |
| `--search <text>` | Only folders whose name contains the text (case and umlauts aside). |
| `--from <date>`, `--to <date>` | Only folders whose period overlaps: a year (`2015`), month (`2015-07`) or day (`2015-07-12`). |
| `--role <code>` | Only folders with this role. Repeatable. |
| `--limit <n>`, `--offset <n>` | Paging as in [Lists and paging](#lists-and-paging). Default limit 100. |
| `--reset-patterns` | Write the patterns file anew from the folders, replacing hand edits. |
| `--no-model`, `--model`, `--refresh`, `--words` | As for `review`. The model writes the `purpose` of the upper folders. |

```json
{
  "job": { "id": 8, "role": "library", "...": "..." },
  "patterns": { "file": "/home/me/.local/share/SmartFileOrganizer/library-patterns/job-8.json",
                "created": true, "ignored": [], "note": null, "targets": [ { "path": "Fotos", "pattern": "{occasion} {year}", "...": "..." } ] },
  "job_id": 8, "total": 14, "limit": 100, "offset": 0, "next_offset": null,
  "items": [
    { "path": "/media/archive/Fotos", "relative_path": "Fotos", "name": "Fotos", "depth": 1, "parent": ".",
      "role": "collection", "children_role": "occasion",
      "purpose": "3 folders, mostly occasions named like '{occasion} {year}' (Grill 2016, Ostern 2018, Ski 2015): 15 image, taken 2015-02-07 to 2018-04-01.",
      "purpose_source": "rule", "files": 15, "direct_files": 0, "subdirs": 3, "bytes": 82, "by_file_type": { "image": 15 },
      "period": { "from": "2015-02-07T10:00:00", "to": "2018-04-01T12:30:00", "time_source": "file_name" },
      "name_period": null,
      "children_pattern": { "template": "{occasion} {year}", "name_pattern": "year_suffix", "count": 3, "of": 3,
                            "examples": [ "Grill 2016", "Ostern 2018", "Ski 2015" ] },
      "pattern": "{occasion} {year}", "findings": [], "weak": false, "dump_kind": null, "closed": null, "closed_at": null }
  ],
  "advice": { "status": "skipped", "model": null, "error": null, "dropped": 0 }
}
```

`closed` marks a closed unit that takes no loose files and is no parent of a new folder (`program`, `technical`,
`single_title`, `unit`; [library-match.md](library-match.md#the-map)), `closed_at` the folder that makes it one; the
text output shows `closed` on the line. `patterns.ignored` lists hand-edited targets whose folder no longer exists. Errors:
`job_not_found` (1), `scan_not_completed` (1), `wrong_role` (1, not a library job),
`path_not_found` (1, `--under` or word file), `words_invalid` (1), `library_patterns_invalid` (1),
`settings_invalid` (1, a `LibraryMatch` value), `ollama_unavailable` (3, with `result`: the map is
complete with rule purposes).

### `sfo library match <job> [path]`

Where a part of a `source` job belongs in the library: a file or folder (`path`, relative to the
source root or full) or a bundle (`--bundle <id>`), exactly one of them.

| Option | Meaning |
|---|---|
| `--bundle <id>` | Match a bundle of `sfo bundles build` as a whole. |
| `--against <job>` | Library job. Repeatable: each library is matched on its own and the best places of all of them are listed by score (the first named wins a tie). Default: the libraries of the latest `sfo compare`, else the newest completed library job. |
| `--limit <n>` | Proposals to list, 1 to 50. Default `LibraryMatch.MaxCandidates` (3). |
| `--no-model`, `--model`, `--refresh`, `--words` | As for `review`. The model names a new folder the rules cannot complete, describes a few files of each candidate folder (stored, `--refresh` describes again) and compares the unit's content with each candidate. With `--no-model` nothing is described or compared. |

```json
{
  "source": { "id": 9, "role": "source", "...": "..." },
  "library": { "id": 8, "role": "library", "...": "..." },
  "library_from": "comparison",
  "status": "ok", "message": null,
  "unit": { "kind": "folder", "path": "/media/usb/Neuer Ordner", "bundle_id": null, "bundle_kind": null,
            "name": "Neuer Ordner", "keep_whole": false,
            "keep_reason": "'Neuer Ordner' says nothing about its content; its files need a place.",
            "role": "occasion", "files": 5, "by_file_type": { "image": 5 },
            "period": { "from": "2015-02-07T10:00:00", "to": "2015-02-08T10:30:00", "time_source": "file_name" },
            "words": [], "comparison": { "backed_up": 1, "new": 4 } },
  "comparison": { "comparison_id": 3, "compared_at": "2026-11-12T20:18:51.998Z", "complete": true },
  "considered": 9,
  "candidates": [
    { "rank": 1, "placement": "into_existing",
      "target": "/media/archive/Fotos/Ski 2015", "target_relative": "Fotos/Ski 2015",
      "target_role": "occasion", "target_purpose": "Photos and videos of one occasion: 6 image, taken 2015-02-07.",
      "destination": "/media/archive/Fotos/Ski 2015", "destination_relative": "Fotos/Ski 2015",
      "keeps_name": false, "score": 0.56, "certainty": "certain",
      "reason": "Into Fotos/Ski 2015: 1 of 5 files already lie here with the same content; taken 2015-02-07 to 2015-02-08, within the folder's 2015-02-07; 100% of its files are photos and videos.",
      "signals": [ { "signal": "content_neighbours", "value": 0.4, "weight": 0.4, "agrees": true, "contradicts": false,
                     "detail": "1 of 5 files already lie here with the same content" } ],
      "weak_place": null }
  ],
  "new_folder": null,
  "patterns": { "file": "...", "created": false, "...": "..." },
  "advice": { "status": "skipped", "model": null, "error": null, "dropped": 0 },
  "notes": [],
  "libraries": [ { "id": 8, "role": "library", "...": "..." } ],
  "space": [ { "library_job_id": 8, "root_path": "/media/archive", "free_bytes": 912000000000,
               "needed_bytes": 20480, "space": "enough" } ]
}
```

- `library` is the library of the best proposal (or of the new folder); `libraries` lists every library matched.
  Each candidate and `new_folder` carry `library_job_id`; `destination` is a full path inside that library's root.
  A proposal never lies outside a library root.
- `space`: per library root the free space (`free_bytes`, `null` when it cannot be determined) and what the unit
  needs there (`needed_bytes`: its files not yet `backed_up` in any library), with `space` `enough`, `not_enough`
  (also a note) or `unknown` ([glossary](glossary.md#room-at-a-target-root-targetspace)).
- `placement`: `into_existing` (the files go into the folder), `as_subfolder` (a named source
  folder goes under the target as a whole, `keeps_name: true`) or, in `new_folder`, `new_folder`.
- `certainty`: `certain`, `likely`, `uncertain` or `review`, from agreeing signals;
  `signal`: `content_neighbours`, `period`, `name`, `file_type`, `siblings`, `content_fit`.
- `weak_place`: `null`, or `{ "findings": [ { "kind", "impact", "relative_path" } ], "note" }`
  when the review found the order at the target weak; the certainty is capped then.
- `new_folder` when no candidate fits: `parent`, `parent_relative`, `name`, `name_source`
  (`rule` or `model`), `name_complete` (`false` while a placeholder such as `{occasion}` is left),
  `pattern`, `destination`, `destination_relative`, `certainty`, `reason`, `weak_place`.
- Content check ([library-match.md](library-match.md#content-check)): `unit.description` is what the unit's
  described files show, `{ "sampled", "described", "values": { "<fact key>": [ { "value", "count" } ] }, "text" }`,
  or `null`. `content_check` is `{ "status", "model", "note" }` with `status` `checked`, `no_model`, `unavailable`,
  `no_description`, `no_candidates` or `failed`. A checked candidate has `content`:
  `{ "fit": "fits"|"unclear", "reason", "folder": <description of the folder> }`, and the signal `content_fit` when
  it fits; an unchecked one has `content: null`. `rejected` lists the candidates whose content contradicts the unit:
  `{ "target_relative", "reason", "folder" }`. Without a check (`--no-model`, no model, no description) no existing
  folder is proposed without the same files at the target, and `notes` says so.
- Rather no target than a wrong one (Issue 85, [library-match.md](library-match.md#match-signals-and-certainty)):
  `unconfirmed` (`null` when empty) lists, like `candidates`, the existing folders loose files would go into that only
  time, name or kind of files support, without the same files there and without `fits`; they are never the proposal.
  `no_target` (`null` otherwise) is the reason why not even a new folder is proposed: no library holds this kind of
  content. Then `candidates` is empty and `new_folder` is `null`; the text output shows both as lines.
- `status: "no_library"` with a `message` and no proposals, exit code 0, when there is no library
  to match against: no permanent storage is known, so there is no target path at all, never a folder of the
  user profile or the system drive instead. `notes` says when there is no or an incomplete comparison, when the newest
  library job was taken, or when every file is already backed up.

Errors: `job_not_found` (1, source or `--against`), `scan_not_completed` (1), `wrong_role` (1, the
source is a library or `--against` is not), `path_not_found` (1), `bundle_not_found` (1),
`words_invalid` (1), `library_patterns_invalid` (1), `settings_invalid` (1), `library_unreachable` (1, a library root
cannot be read; the message names each library, its root and why), `usage` (2, both or
neither of `path` and `--bundle`). An unreachable model is not an error here: the new folder keeps
the name of the rules, `advice.status` says `unavailable`.

## Bundles

### `sfo bundles build <job>`

Groups a completed job into bundles the person decides once: dumps (phone, computer, USB stick or
software copies) with their worth-keeping parts and noise, named folders, and events of loose photos
and videos. Replaces earlier bundles of the job. Reads only the database: no file is opened, no model
called. How bundles are formed and why: [bundles.md](bundles.md). Issue:
https://github.com/crack666/smart_file_organizer/issues/39

| Option | Meaning |
|---|---|
| `--patterns <file>` | Pattern file for this run (default: `Bundles:PatternsFile`, else the shipped patterns). |
| `--gap-hours <h>` | Pause that starts a new event (default `Bundles:EventGapHours`, 8). |
| `--max-event-days <d>` | Longer events are cut at their largest pauses (default 14). |
| `--min-event-files <n>` | Smaller groups go to the year's `scattered` bundle (default 3). |

```json
{
  "job_id": 7,
  "built_at": "2026-11-12T19:25:52.350Z",
  "settings": { "event_gap_hours": 8, "max_event_days": 14, "min_event_files": 3,
                "patterns": { "name": "default", "source": "built-in", "hash": "…" } },
  "stats": {
    "all": { "directories": 15, "empty_directories": 1, "files": 71, "total_bytes": 229, "by_file_type": { "image": 70, "other": 1 } },
    "dumps": { "...": "same fields" },
    "without_dumps": { "...": "same fields" },
    "dump_count": 1, "bundle_count": 10, "unbundled_files": 0
  },
  "top_level_by_kind": { "event": 3, "dump": 1, "folder": 1 },
  "comparison": { "comparison_id": 3, "against_job_id": 8, "compared_at": "2026-11-12T15:02:11.410Z",
                  "library_job_ids": [ 8, 9 ] }
}
```

`comparison` is the newest `sfo compare` run of the job against libraries, or `null`: `library_job_ids` lists all of
them, `comparison_id` and `against_job_id` name the first, `compared_at` is the oldest of the run. The `comparison`
counts of each bundle (`sfo bundles list`) join all of them: a file is `backed_up` as soon as one library holds it.
Errors:
`job_not_found` (1), `scan_not_completed` (1), `patterns_invalid` (1, the pattern file or a threshold).

### `sfo bundles list <job>`

One page of bundles (paging as in [Lists and paging](#lists-and-paging)): the top level by default.

| Option | Meaning |
|---|---|
| `--parent <id>` | The children of this bundle: a dump's parts, a camera part's events. |
| `--all` | Every bundle, whatever its parent. |
| `--kind <code>` | `dump`, `dump_keep`, `dump_noise`, `folder`, `event`, `scattered`. Repeatable. |

```json
{
  "job_id": 7, "total": 5, "limit": 200, "offset": 0, "next_offset": null,
  "items": [
    { "id": 1, "parent_id": null, "kind": "dump", "origin": "phone", "part": null,
      "suggested_name": "Handy (phone 2013)", "styled_name": null, "root_path": "/media/disk/Handy",
      "period": { "from": "2013-06-10T10:00:00", "to": "2013-06-20T12:00:00", "time_source": "file_name" },
      "files": 10, "total_bytes": 18, "directories": 12, "empty_directories": 1, "children": 3,
      "sensitive_files": 0, "comparison": { "backed_up": 4, "new": 6 },
      "reason": "Recognised as phone dump by signature 'phone-storage' (score 5 of at least 3): dir:DCIM, dir:Android, dir:LOST.DIR.",
      "facts": { "by_file_type": { "image": 9, "other": 1 }, "time_sources": { "file_name": 9, "modified_date": 1 },
                 "markers": [ "dir:DCIM", "dir:Android", "dir:LOST.DIR" ], "directories": [], "directories_total": 0,
                 "device": null, "copy_time_suspected": false, "direct_files_only": false } }
  ]
}
```

- `origin` for `dump`, `part` for `dump_keep`, otherwise `null`. `root_path` is the directory that is
  the bundle (dump, folder, first folder of a part); `null` for events.
- `period.from`/`to` are local wall-clock times **without** a zone and without `Z`: capture dates and
  dates in names carry none. `time_source`: `capture_date`, `file_name`, `modified_date` or `mixed`.
- `files`, `total_bytes`, `directories`, `sensitive_files` and `comparison` cover the bundle with its
  children. `sensitive_files` and `comparison` (counts per state of the latest comparison with a
  library, `null` without one) are computed when read.
- `styled_name` is always `null`. A name in the style of the library is proposed per match by
  `sfo library match` (`new_folder.name`), not stored on the bundle.
- `facts.directories` lists at most 20 directories (relative to the root) of a part or an event;
  `directories_total` counts all.

### `sfo bundles show <job> <bundle>`

`{ "job_id", "bundle": {...}, "children": [ {...} ] }` with the fields of `list`. The files:
`sfo files <job> --bundle <bundle>`. Errors: `job_not_found` (1), `bundle_not_found` (1).

## Findings

The short list a job ends in (https://github.com/crack666/smart_file_organizer/issues/26). Rules, certainty,
ranking and thresholds: [architecture.md, Findings flow](architecture.md#findings-flow). No model call, no
scanned file opened; decisions change only their row. The one exception is `sfo findings targets`, the target match,
which asks the model as `sfo library match` does and stores its answer per finding.

### `sfo findings <job>`

| Option | Meaning |
|---|---|
| `--summary` | One page: counts per kind, certainty and state, coverage, missing steps and the first findings. Stays under 20 kB for a 10,000-file job. |
| `--top <n>` | With `--summary`: findings listed, 0 to 20, default 10. |
| `--kind <code>` | `not_backed_up`, `in_noise`, `misplaced`, `sensitive`, `duplicate`. Repeatable. |
| `--under <path>` | Only findings about files or folders at or below this folder (full, or relative to the job root). |
| `--min-certainty <code>` | `certain`, `likely`, `uncertain`, `review`: only findings at least this certain. |
| `--state <code>` | `open`, `accepted`, `rejected`, `wrong`. Repeatable. Default: open and accepted. |
| `--against <job>` | Library job whose stored comparison counts. Repeatable: a file is backed up as soon as one of them holds it, and targets are looked up in all of them. Default: the libraries of the newest `sfo compare`. |
| `--limit`, `--offset` | Paging as in every list. |

```json
{
  "job_id": 1, "total": 12, "limit": 200, "offset": 0, "next_offset": null,
  "items": [
    { "rank": 1, "key": "not_backed_up-880444df56", "kind": "not_backed_up", "certainty": "likely",
      "section": "sure", "state": "open",
      "title": "300 images (60 KB) worth keeping, not in the library: loose files in Neuer Ordner",
      "reason": "300 images of the 300 in Neuer Ordner are worth keeping by the model, and the comparison with library job 2 on 2026-11-12 found no file with the same content.",
      "path": "...", "relative_path": "Neuer Ordner", "bundle_id": null, "bundle_kind": null,
      "files": 300, "bytes": 61200, "group": "images", "by_file_type": { "image": 300 },
      "period": { "from": "2018-08-05T10:00:00", "to": "2018-08-05T14:59:00", "time_source": "file_name" },
      "comparison": { "new": 300 }, "markers": [], "sensitive_rules": [],
      "action": { "kind": "move", "reason": "...", "single_decision": false },
      "target": null, "duplicates": null,
      "examples": [ "Neuer Ordner/IMG_20180805_100000.jpg", "..." ], "notes": [ "..." ],
      "decided_by": null, "decided_at": null, "decision_note": null }
  ],
  "inputs": { "files", "files_judged", "files_pending", "directories", "directories_judged",
              "bundles_built_at", "library_job_id", "library_compared_at", "duplicates_compared_at",
              "library_job_ids" },
  "coverage": { "not_backed_up": { "coverage": "complete", "note": null }, "...": "" },
  "missing_steps": []
}
```

- `section` is `sure` (certainty `certain` or `likely`) or `unsure`; unsure findings come last.
- `key` is stable across `sfo bundles build` and a rescan of the same tree.
- `bytes` of a `duplicate` is what removing the redundant copies frees; `duplicates` gives `groups`,
  `redundant_files`, `redundant_bytes`, `directories` (at most 10), `directories_total`, `keep` and `scope`
  (`files`, or `folders` when a whole program or system folder exists twice; `directories` are then the copied
  folders). The action keeps the copy in `keep`: `delete_source` for the others with a library, `leave` without.
- `group` (for `not_backed_up` and `in_noise`): the kind of files, one finding per place and kind
  ([glossary](glossary.md#kind-of-files-of-a-finding-group)); `null` for the other kinds.
- `target`: `path` (the target folder), `relative_path` (below the library root), `certainty`, `reason`,
  `same_content_files`, `content_check` (`not_checked`, `fits`, `contradicts`, `unclear`), `library_job_id` and
  `library_root`: the library the place lies in. For `not_backed_up`, `in_noise` and `misplaced` it comes from the
  target match ([`sfo findings targets`](#sfo-findings-targets-job)); a `misplaced` finding not matched yet may carry a
  target of the rules alone (`not_checked`). A target lies inside a library root or there is none: a proposed place
  outside every root is dropped with a note, and without a library no finding has a target.
- `target_status` (for `not_backed_up`, `in_noise`, `misplaced`; `null` for the others): what the target match found,
  `existing_folder`, `new_folder`, or why there is no target: `not_matched`, `no_fitting_folder`, `content_unclear`,
  `no_model` ([glossary](glossary.md#target-match-of-a-finding-findingtargetstatus)). The text list ends a line with
  `-> <target>` or `-> none (<code>)`.
- `inputs.library_job_ids` lists every library the findings rest on; `library_job_id` and `library_compared_at` are
  those of the first one.
- `sensitive` findings name path, marker and rule ids, never content.
- `--summary` gives `{ "job_id", "root_path", "inputs", "coverage", "missing_steps", "listed", "sure", "unsure",
  "by_kind": { "<kind>": { "findings", "sure", "unsure", "files", "bytes" } }, "by_certainty", "by_state",
  "below_threshold", "top": [ { "rank", "key", "kind", "certainty", "state", "title", "relative_path", "files",
  "bytes", "action", "target", "target_status" } ], "overview", "storage" }`. `target` is the place with its reason as
  in the list; the text names it under the finding (`-> Fotos/Skifahrt 2015 (library job 8, certain, content fits): ...`).
- `missing_steps` names `sfo findings targets <job>` while a finding that should move has no target and was not
  matched yet.

**Overview first** (https://github.com/crack666/smart_file_organizer/issues/74). `overview` answers file by file,
before any finding: what lies on the source, what of it is worth keeping and not safe, what is safe and where.

```json
"overview": {
  "files": { "files": 2400, "bytes": 3100000000 },
  "worth_keeping": { "files": 900, "bytes": 2000000000,
                     "not_backed_up": { "files": 310, "bytes": 640000000 },
                     "backed_up": { "files": 560, "bytes": 1300000000 },
                     "unknown": { "files": 30, "bytes": 60000000 } },
  "backed_up": { "files": 1700, "bytes": 2500000000,
                 "by_library": [ { "library_job_id": 8, "root_path": "/mnt/nas/fotos", "files": 1500, "bytes": 2200000000 },
                                 { "library_job_id": 9, "root_path": "/mnt/nas/videos", "files": 200, "bytes": 300000000 } ] },
  "disposable": { "files": 1100, "bytes": 400000000 },
  "sensitive": { "files": 12, "bytes": 90000 },
  "not_compared": { "files": 30, "bytes": 60000000 }
}
```

A marked file counts under `sensitive` only, never under `worth_keeping` or `disposable`; `backed_up` counts every
file a library holds, marked ones included, each at the first library named that holds it. `worth_keeping.unknown`
is worth keeping but not compared or unreadable: nobody knows whether it is safe. `not_compared` is every file
without a comparison state (no library, or a read error). The by-kind counts of the findings stay as before; the
overview counts files, the findings count what passed their thresholds.

**Where the proposals go.** `storage` says whether a permanent storage is known and how much room it has:

```json
"storage": {
  "status": "known", "message": null,
  "roots": [ { "library_job_id": 8, "root_path": "/mnt/nas/fotos", "free_bytes": 52000000000,
               "needed_bytes": 2048, "findings": 1, "space": "enough" },
             { "library_job_id": 9, "root_path": "/mnt/nas/videos", "free_bytes": null,
               "needed_bytes": 0, "findings": 0, "space": "unknown" } ],
  "unplaced_findings": 4, "unplaced": { "files": 330, "bytes": 700000000 },
  "warnings": [],
  "unplaced_by_reason": { "content_unclear": 1, "no_fitting_folder": 1, "not_matched": 2 }
}
```

- `status` is `known` or `no_library` ([glossary](glossary.md#permanent-storage-for-targets-storagestatus)). Without a
  library, `roots` is empty and `message` says that no permanent storage is known: no target is proposed.
- `roots`: one entry per library. `needed_bytes` sums the listed (open or accepted) findings that move into this root;
  `free_bytes` is what the file system lets the user write there (local folder, mounted share or network path;
  `null` when it cannot be told). `space` is `enough`, `not_enough` or `unknown`
  ([glossary](glossary.md#room-at-a-target-root-targetspace)); never an error.
- `unplaced`: findings that should move (`not_backed_up`, `in_noise`, `misplaced` with a move) but have no target;
  they need room in one of the roots too. `unplaced_by_reason` counts them per `target_status`: `not_matched` until
  `sfo findings targets` ran, then only those that really got none, with the reason (`no_fitting_folder`,
  `content_unclear`, `no_model`). The text line reads `without target yet: 4 findings, 668 MB (content_unclear 1, ...)`.
- `warnings`: one line per root that has not enough room, and one when the unplaced findings need more than is left
  free in all roots together (only when every root's free space is known).

Errors: `job_not_found` (1), `wrong_role` (1, a library), `comparison_not_found` (1, `--against` names a library
the job was not compared with), `settings_invalid` (1, a `Findings` value), `usage` (2).

### `sfo findings show <job> <key>`

`{ "job_id", "finding": {...}, "total", "limit", "offset", "next_offset", "files": [ ... ] }`: the finding and
its files, one page at a time, as `sfo files` writes them (marked files protected; `--include-sensitive` as
there). Errors: `job_not_found` (1), `finding_not_found` (1).

### `sfo findings targets <job>`

The step between overview and recommendation: where each finding should go
(https://github.com/crack666/smart_file_organizer/issues/83). For every open or accepted finding of the kinds
`not_backed_up`, `in_noise` and `misplaced` it runs the match of [`sfo library match`](#sfo-library-match-job-path)
with exactly the files of the finding, over all libraries of the comparison: time, names, kinds of files, the files
already there, and the content check (unit description against a sample of each candidate folder,
`fits|contradicts|unclear`). A named folder or device copy keeps its name, as with `library match --bundle`. The answer
is stored per finding (`finding_targets`): the best existing folder, else a new folder in the style of the library with
a complete name, else why there is none. `sfo findings`, `findings show`, `--summary` and `sfo plan create` read it from
there. Sensitive findings never get a target. Nothing on disk is changed.

| Option | Meaning |
|---|---|
| `--against <job>` | Library job to look in. Repeatable. Default: the libraries of the newest `sfo compare`. |
| `--key <key>` | Only this finding. Repeatable. Default: every open or accepted finding of the three kinds. |
| `--no-model` | Rules only: no content check, no model name for a new folder. An existing folder is then the target only when the same files already lie there (Issue 85), as with `library match --no-model`. |
| `--model <name>` | Ollama model for this run. |
| `--refresh` | Match every finding again and ask the model again, even where a stored match still fits. |

**Resumable and cheap the second time.** Each answer is stored as soon as it is there; a run cut off (Ctrl+C) goes on
where it stopped. A finding whose stored match rests on the same files, the same descriptions of them and the same
libraries is not matched again (`reused`), unless this run may ask the model and the stored match could not
(`--no-model` before, model away). Below that, folder descriptions (`folder_descriptions`) and the model's answers
(`library_advice`) are stored as in `library match`. Progress goes to stderr, one line per finding.

```json
{
  "job_id": 1, "status": "known", "message": null, "library_job_ids": [ 8, 9 ],
  "considered": 3, "matched": 3, "reused": 0,
  "by_status": { "existing_folder": 1, "new_folder": 1, "no_fitting_folder": 1 },
  "items": [
    { "key": "not_backed_up-880444df56", "kind": "not_backed_up",
      "title": "4 images (72 B) worth keeping, not in the library: loose files in Neuer Ordner",
      "status": "existing_folder", "reused": false, "library_job_id": 8, "path": "/nas/photos/Fotos/Skifahrt 2015",
      "relative_path": "Fotos/Skifahrt 2015", "certainty": "certain", "content_check": "fits", "content_status": "checked",
      "reason": "Into Fotos/Skifahrt 2015: the content fits: snowy slope, skiing, holiday; ..." }
  ],
  "model_calls": [ { "kind": "content_fit", "calls": 2, "prompt_tokens": 0, "...": "" } ]
}
```

- `status` is `known`, or `no_library` with a `message` and no items: without a library there is no target.
- `items[].status` is a `FindingTargetStatus` code ([glossary](glossary.md#target-match-of-a-finding-findingtargetstatus));
  `content_status` the `ContentCheckStatus` of the match ([library-match.md](library-match.md#content-check)).
- `model_calls` counts the calls of this run per kind (`classification` for describing a library folder's sample,
  `content_fit`, `folder_name`); empty when every answer was stored already.
- Since Issue 85 an existing folder is stored only when content confirms it (the same files there, or `fits`); else a
  new folder in the library that holds this kind of content, else `no_target` with the reason
  ([library-match.md](library-match.md#for-every-finding-sfo-findings-targets)). With `--no-model` that means: no
  existing folder without the same files there.

Errors: `job_not_found` (1), `wrong_role` (1, a library), `comparison_not_found` (1, `--against` names a library the
job was not compared with), `finding_not_found` (1, `--key`), `library_patterns_invalid` (1), `settings_invalid` (1),
`library_unreachable` (1, a library root cannot be read: checked before any finding is matched, nothing is stored),
`usage` (2).

### `sfo findings accept|reject|wrong|reopen <job> <key>...`

`accept`: the plan turns the finding into an action. `reject`: not listed again. `wrong`: not listed again and
stored as a test case ([evaluation.md](evaluation.md#findings-reported-wrong)). `reopen`: the decision is
withdrawn. `--note <text>` is kept with the decision; `--by human|agent` says who decided (default `human`).
Result: `{ "job_id", "items": [ { "key", "state", "decided_by", "decided_at", "note", "eval_case_id",
"finding": {...} } ] }`. Changes no file. Errors: `job_not_found` (1), `finding_not_found` (1, the key is not
part of the job, or the data it rested on changed).

## Deciding

### `sfo decide <job>`

Stage 2 of https://github.com/crack666/smart_file_organizer/issues/37: worth and target of every file, computed anew
by rule from what the analysis stored (facts, results, folder verdicts) and from bundles and comparison, and stored
(`file_decisions`). No model call, no scanned file opened, nothing changed on disk. After a changed target structure
or changed rules this is all that has to run; the description of stage 1 stays as it is. Rules and inputs:
[architecture.md, Decision flow](architecture.md#decision-flow).

| Option | Meaning |
|---|---|
| `--targets <file>` | Target structure (`sfo library propose --out`, edited by hand; format in [library-review.md](library-review.md#target-structure-file-library-propose---out-file)): kept files get a target from it. Without it, worth only. |
| `--changed` | List only files whose worth, certainty or target changed against the decisions stored before. |
| `--limit`, `--offset` | Paging of `items`, as in every list. |

```json
{
  "job_id": 1, "decided_at": "2026-11-13T10:00:00Z", "previous_decided_at": "2026-11-13T09:00:00Z",
  "files": 5, "by_worth": { "keep": 5, "disposable": 0, "none": 0 },
  "by_certainty": { "certain": 0, "likely": 5, "uncertain": 0, "review": 0 },
  "targets": { "structure": "/path/targets.json", "proposed": 4, "unresolved": 1, "kept_without_target": 1 },
  "changed": 4, "findings": { "not_backed_up": 0, "in_noise": 0, "sensitive": 1, "misplaced": 0, "duplicate": 0 },
  "total": 5, "limit": 200, "offset": 0, "next_offset": null,
  "items": [
    { "file_id": 3, "relative_path": "rechnungen/strom.txt", "worth": "keep", "certainty": "likely", "rule": "model",
      "basis": [ { "key": "category", "value": "invoice", "source": "model" },
                 { "key": "keep_hint", "value": "worth_keeping", "source": "model" } ],
      "target": { "id": "papiere", "path": "Archiv/Papiere/Stadtwerke/2019", "rule": "target_structure",
                  "basis": [ { "key": "file_type", "value": "document", "source": "scan" },
                             { "key": "issuer", "value": "Stadtwerke", "source": "model" },
                             { "key": "year", "value": "2019", "source": "model" } ],
                  "unresolved": [] },
      "previous_target": "Documents/invoice/2019", "changed": true }
  ]
}
```

- `worth` is `keep`, `disposable` or `null` (the stored results say nothing about the file); `rule` says what decided
  it and `basis` the inputs it rests on ([glossary](glossary.md#decisions)). The findings use the same worth.
- A target is proposed for kept files only: the first target whose `holds` names the file type, its `pattern` filled
  from the facts. A placeholder no fact fills (`unresolved`) leaves the folder level of the pattern out. `basis` names
  the file type and one input per filled placeholder.
- A marked file rests on its marker alone (`rule` `sensitive`, `basis` key `sensitive` with the marker and the rule
  ids), is kept and gets no target; its description is never stored.
- `previous_target` and `changed` compare with the decisions stored before this run.

Errors: `job_not_found` (1), `wrong_role` (1, a library), `path_not_found` (1, the target file), `targets_invalid` (1,
the target file is not a valid target structure), `settings_invalid` (1, a `Findings` value), `usage` (2).

## Content store

Issue: https://github.com/crack666/smart_file_organizer/issues/38; how it works:
[architecture.md](architecture.md#content-store). Before every classification call `sfo analyze` looks for an
assessment of the same content (full SHA-256 and size) made with the same model and the same prompt version, and
takes it over instead of asking the model (origin `reused`). The same file on the next old disk, renamed or in a new
scan costs no call. Only what belongs to the content is taken over; folder verdict, findings, target and decision
are made anew in every job. A person's correction of a category follows the content into every job as well. The
store is kept when a job is deleted. `sfo eval` neither reads nor writes it.

### `sfo cache stats`

Counts the store; no model call, the current model and prompt version come from the settings.

```json
{
  "schema_version": 1,
  "command": "cache stats",
  "ok": true,
  "exit_code": 0,
  "result": {
    "model": "gemma4:26b", "prompt_version": "3f9c0a1e22b74d58",
    "entries": 1250, "current": 1180, "stale": 70, "reuses": 640, "corrections": 12, "fingerprints": 4100,
    "groups": [
      { "model": "gemma4:26b", "prompt_version": "3f9c0a1e22b74d58", "current": true, "entries": 1180, "reuses": 640,
        "last_used_at": "2026-11-13T09:40:00Z" },
      { "model": "gemma4:26b", "prompt_version": "0b77e1d0c9a35f12", "current": false, "entries": 70, "reuses": 0,
        "last_used_at": null }
    ]
  },
  "error": null
}
```

`current` entries are taken over by the next analysis; `stale` ones were made with another model or another prompt
version and are never taken over, but kept until `sfo cache clear`. The prompt version is a hash of the classification
prompt (system prompt, language, instructions, answer schemas): a changed prompt is a new version by itself.
`reuses` counts take-overs, `corrections` the categories a person set by content, `fingerprints` the files with a
full checksum (shared with `sfo compare`).

### `sfo cache clear`

| Option | Meaning |
|---|---|
| `--stale` | Only remove entries of another model or prompt version; the current ones stay. |
| `--corrections` | Also remove the corrections by content. The corrections of single files (`user_overrides`) stay. |

Result: `{ "only_stale": false, "entries_removed": 1250, "corrections_removed": 0, "entries_left": 0, "corrections_left": 12 }`.
Checksums are not removed. The next analysis asks the model again for every file.

## Planning

https://github.com/crack666/smart_file_organizer/issues/11: the accepted findings of a source become a plan, one
action per whole folder or per worth-keeping file, which a person or a steering model reads, changes and confirms.
**A plan executes nothing**: the commands below up to `approve|reject` move, copy or remove no file; only
[Executing a plan](#executing-a-plan) does, with `--execute`, and it runs the same checks again. Rules:
[architecture.md, Plan flow](architecture.md#plan-flow); codes: [glossary.md, Plans](glossary.md#plans).

Order of work: overview (`sfo findings --summary`), where to (`sfo findings targets`), recommendation
(`sfo findings`, `sfo plan create`), decision (`sfo findings accept`, `sfo plan approve`).

**Hard rules**, checked when an action is created, set, imported and confirmed (`problems`, see the glossary):

- Every target lies below the root of a library of the plan (a job scanned with `--role library`), never in the
  source, never in the user profile, on the system drive or in a place of `Plan:ProtectedPlaces`.
- Content any library holds already gets no move or copy; at most the note that it lies there twice.
- `leave` is the default: an action without a usable target is `leave` with a `note`; `recommended` keeps what the
  finding recommended.
- Sensitive files are `leave`, never `quarantine`, never removed unless a person decides it, and confirmed only by
  `--action`, never by `--dir` or `--all`.
- A library is never a source. Nothing in a managed area (`Plan:ManagedAreas`) is moved, renamed or removed; an action
  whose target lies in one is marked `into_managed_area`: the program that manages it must read it in again.

Settings, section `Plan` of the settings file:

```json
{ "Plan": { "ManagedAreas": [ "\\\\nas\\photos\\Lightroom" ], "ProtectedPlaces": [ "D:\\Scratch" ] } }
```

### `sfo plan create <job>`

| Option | Meaning |
|---|---|
| `--against <job>` | Library job whose stored comparison counts, as in `sfo findings`; repeatable. Default: the libraries of the newest `sfo compare`. They are the libraries of the plan. |
| `--library <job>` | Library whose root the targets of `sfo decide --targets` (relative paths) go under. Default: the only library, when there is exactly one; with several and none named, such targets are not used and the action is `leave` with a note. |

How actions arise from accepted findings:

- A folder judged `unit` or `uniform` whose files all go (worth keeping, not in a library, not marked, no misplaced
  file among them) is one action with `source_kind` `folder`; the target keeps the folder name (a target folder that
  already carries that name, from `sfo findings targets`, is taken as it is). The topmost such folder wins.
- Everything else of a finding to move is one action per worth-keeping file; the rest of a `mixed` folder stays.
- The target is the place the finding proposes in a library (from [`sfo findings targets`](#sfo-findings-targets-job),
  or for `misplaced` from the rules), else the target of `sfo decide` below the root of the `--library`. Without one:
  `leave`. The checks below apply to every target alike.
- `sensitive`: one `leave` per file (`sensitive`, `single_decision` true). `duplicate`: `delete_source` for each copy
  outside the kept folder whose content a library holds (the target is the place in the library); else one `leave`.
  Any other finding that recommends nothing to move is one `leave`.
- An action that fails a check is stored as `leave` with the problems in its note.

Result: the summary of the new plan, as `sfo plan summary`. Changes no file.

Errors: `job_not_found` (1), `wrong_role` (1, the job is a library), `comparison_not_found` (1), `library_invalid` (1,
`--library` is no library job), `usage` (2).

### `sfo plan show <plan>`

The actions, one page at a time (`--limit`, `--offset`), filtered by `--kind`, `--state`, `--finding <key>`,
`--under <folder>` (full, or relative to the job root) and `--problems` (only actions with problems). The problems
are computed when read, so a file that appeared at a target since shows up here.

```json
{ "plan_id": 1, "job_id": 1, "total": 5, "limit": 200, "offset": 0, "next_offset": null,
  "items": [
    { "id": 1, "finding_key": "not_backed_up-0a1b2c3d4e", "finding_kind": "not_backed_up",
      "unit": "4 photos not in the library in Urlaub Kroatien 2021", "source_kind": "folder",
      "source_path": "/media/stick/Urlaub Kroatien 2021", "relative_path": "Urlaub Kroatien 2021",
      "files": 4, "bytes": 8123456, "kind": "move", "recommended": "move",
      "target": "/nas/photos/Fotos/Urlaub Kroatien 2021", "library_job_id": 2,
      "reason": "The folder is one whole (uniform): it moves as it is, its name stays. ...", "note": null,
      "origin": "rule", "state": "proposed", "sensitive": false, "single_decision": false,
      "into_managed_area": false, "problems": [], "decided_by": null, "decided_at": null } ] }
```

Errors: `plan_not_found` (1), `usage` (2).

### `sfo plan summary <plan>`

One page: `actions`, `by_kind` (actions, files, bytes), `by_state`, `changed_from_recommendation`,
`with_problems`, `sensitive`, `into_managed_area`, per library of the plan the bytes the writing actions need
there against its free space (`space`: `enough`, `not_enough`, `unknown`), the units (one per finding) with the
largest first (`--top`, default 10, at most 50), and the notes of the creation (files left out because a library
holds them, no library, no accepted finding). Errors: `plan_not_found` (1).

### `sfo plan set <plan> <action>`

| Option | Meaning |
|---|---|
| `--kind <code>` | `move`, `copy`, `quarantine`, `delete_source` or `leave`. |
| `--target <path>` | Full path of the file or folder at the target; a relative path lies below the only library of the plan. |
| `--clear-target` | Remove the target. |
| `--reason <text>` | Why; replaces the reason. |
| `--by human\|agent` | Who changes it (`origin`); default `human`. |

The change is checked at once; a refused change changes nothing and ends with `plan_change_refused` (1) and
`{ "plan_id", "refused": [ { "action_id", "problems", "message" } ] }` as result. A changed action is `proposed`
again. Verified, executed, failed and undone actions are not changed. Result: `{ "plan_id", "items": [ action ] }`.

### `sfo plan import <plan> <file>`

Many changes at once, as a steering model writes them; all or none, checked as `set`:

```json
{ "actions": [ { "id": 3, "target": "/nas/docs/Papiere/Strom/Rechnung_Strom.pdf" },
               { "id": 7, "kind": "leave", "reason": "keep it on the stick" },
               { "id": 9, "target": null } ] }
```

A missing field stays as it is; `"target": null` removes the target. `--by human|agent` as in `set`. Errors:
`path_not_found` (1, the file), `import_invalid` (1, not JSON, no `actions` list, an entry without `id` or with an
unknown kind), `plan_change_refused` (1, as in `set`), `plan_not_found` (1).

### `sfo plan approve|reject <plan>`

`--action <id>` (repeatable), `--dir <folder>` (every action whose source lies at or below it) or `--all`; one of
them is needed (else `usage`, 2). `--by human|agent`, default `human`. `approve` confirms only actions without
problems, and a sensitive action only when named by `--action`; `reject` marks them not to be executed. Verified,
executed, failed and undone actions are skipped. Result: `{ "plan_id", "state", "changed": [ ids ], "skipped": [ {
"action_id", "reasons" } ], "by_state" }`; a reason is a problem code, `single_decision` or the state of the action.
Errors: `plan_not_found` (1), `action_not_found` (1).

## Executing a plan

https://github.com/crack666/smart_file_organizer/issues/12: confirmed actions are carried out and can be taken back.
How it works inside: [architecture.md, Plan execution](architecture.md#plan-execution); codes:
[glossary.md, Plans](glossary.md#plans).

**Always look first.** Every command here is a dry run unless `--execute` is given: it reads and checks and reports
what would happen, and changes nothing. Run it without `--execute`, read the result, then run the same line with
`--execute`.

```bash
sfo plan apply 1                              # step 1, dry run: what would be copied and checked
sfo plan apply 1 --execute                    # step 1: copy and check; no original is touched
sfo plan apply 1 --remove-sources             # step 2, dry run: which originals would go, how much room it frees
sfo plan apply 1 --remove-sources --execute   # step 2: originals into the hold folder
sfo plan undo 1                               # dry run of taking it all back
sfo plan purge 1                              # dry run of removing the held originals for good
```

**What this can remove, and what guards it.**

- Only confirmed actions run (`sfo plan approve`); before each one the checks of the plan run again. A sensitive
  action runs only when named by `--action`, never in a run over the whole plan.
- An original is never removed in the same step as its copy is written. Step 1 copies under a temporary name, reads
  the copy back whole and compares its full checksum with the original's, then names it; nothing is ever overwritten.
  Step 2 is its own command.
- Immediately before an original goes, both files are read again whole; the original must also be as the scan saw it
  (size, modification time) and source and target must be shown to be two files (not a link, a hard link or one
  volume reached twice; "cannot tell" refuses too). Otherwise the original stays and the action is `failed` with the
  reason.
- An original is only renamed into the hold folder `<job root>/.sfo-hold/plan-<id>/action-<id>/` on its own drive, from
  where `undo` puts it back. Only `purge` removes it for good, and only while the copy at the target still has its
  checksum: the last copy of anything is never removed. The hold folder lies in the job root; a new scan of the source
  leaves it out (since https://github.com/crack666/smart_file_organizer/issues/86; until then a new scan counted the
  held originals as content). Deleting the job drops the journal but not the hold folder; the move history of
  `sfo where` stays.
- Links and junctions are never followed; a source that is or holds one is skipped. Empty source folders stay and are
  reported (`empty_folders`).
- Step 2 leaves a move notice `_moved-files.txt` in every folder whose originals went: plain text, readable without sfo,
  with the file names, their new places, the time and the plan ([move-notice.md](move-notice.md)). `undo` removes the
  lines of the files that came back and the notice when none is left. A file of that name that is not a notice is never
  touched. `--no-notice` writes none.
- `sfo plan keep-source <job>` leaves every source of the job untouched: a move only copies, nothing is removed.

Every step is journalled before it touches a file (`plan_steps`); a run cut off by a crash or Ctrl+C is finished by
running the same command again, which first settles the step it finds open.

### `sfo plan apply <plan>`

| Option | Meaning |
|---|---|
| `--execute` | Really change files. Without it: dry run. |
| `--remove-sources` | Step 2 instead of step 1: the originals of `verified` actions go into the hold folder. |
| `--action <id>` | Only this action; repeatable. Needed for a sensitive action. Default: every action of the plan. |
| `--no-notice` | With `--remove-sources`: leave no move notice in the source folders in this run. |

Step 1 runs `approved` actions: `move`, `quarantine` and `copy` copy and check; `delete_source` reads the library file
and the original whole and compares them. The action becomes `verified` (its original waits for step 2), or
`executed` for a `copy` and for every action of a job with `keep_source`. A file already at the target with the same
content counts as `already_present`; with other content the action fails (`target_collision`). A source changed since
the scan is skipped (`source_changed`). Step 2 runs `verified` actions and makes them `executed`.

```json
{ "plan_id": 1, "mode": "apply", "executed": true, "keep_source": false,
  "items": [ { "action_id": 3, "kind": "move", "outcome": "done", "state": "verified", "reasons": [],
               "files": 1, "bytes": 18234, "detail": null } ],
  "removal": { "actions": 4, "files": 6, "bytes": 81234 },
  "empty_folders": [],
  "notices": [ { "path": "/media/usb-stick/DCIM/_moved-files.txt", "outcome": "written", "entries": 6, "detail": null } ] }
```

`outcome` is `done`, `would_run` or `would_fail` (dry run), `skipped` (left as it was), `failed` (state `failed`, the
original stays) or `partial` (undo and purge: not all of it). `reasons` are `PlanRunReason` codes, `PlanProblem` codes
or the state of an action named by `--action` that cannot run. `removal` is what step 2 would take away now: the
originals of every `verified` action and the bytes they free. `mode` is `apply`, `remove_sources`, `undo` or `purge`.
`notices` (step 2 and undo) lists the move notices the run wrote (`written`), removed (`removed`) or left alone because
a file that is not a notice carries the name (`skipped`), or could not write (`failed`); a folder whose notice needed
no change is not listed. A folder that holds only its notice counts as left empty.

Errors: `plan_run_incomplete` (1, with the result: an action failed or an undo or purge was partial), `plan_not_found`
(1), `action_not_found` (1).

### `sfo plan undo <plan>`

`--execute`, `--action <id>` as in `apply`. Takes back, newest action first: originals come back from the hold folder
(after a `purge`, they are copied back from the checked file at the target, for `delete_source` from the library);
then the copies this tool wrote are removed, but only once their original is back with its checksum and the copy is
unchanged; then the target folders it created, when empty. A file it did not write is never removed. A fully taken
back action is `undone`; otherwise it keeps its state and the reasons (`target_changed`: the copy was edited and
stays; `original_occupied`: something lies at the place of the original; `not_undoable`). Result and errors as
`apply`.

### `sfo plan purge <plan>`

`--execute`, `--action <id>` as in `apply`. Removes the held originals of the plan for good, each only while the file at
its target still has its checksum and is another file; the rest stay held (`target_changed`, `target_missing`,
`same_file`). This cannot be undone from the hold folder, but `undo` can still copy an original back from its target.
Result and errors as `apply`.

### `sfo plan keep-source <job>`

The person's switch "leave the source untouched" for a whole job: a move then only copies and becomes `executed`,
`delete_source` and `quarantine` are skipped (`keep_source`), step 2 removes nothing. `--off` lifts it. Result:
`{ "job_id", "keep_source" }`. Errors: `job_not_found` (1).

## Memory

https://github.com/crack666/smart_file_organizer/issues/86: what the tool remembers about places and moves. Both
commands read the database only (and whether a path exists): no model, and the drive need not be attached. Codes:
[glossary.md, Places](glossary.md#places).

### `sfo places`

Every known place: the root of a completed scan, with its drive and its last scan. A place outlives its jobs; after the
job is deleted it points at its newest other job, or at none, and keeps its last numbers.

```json
{ "places": [ {
    "id": 1, "root_path": "/media/usb-stick", "role": "source",
    "volume": { "label": "USB-STICK", "id": "4A3B1C2D", "format": "exFAT" },
    "last_job_id": 9, "last_scan_at": "2026-11-14T10:05:00Z", "age_days": 0, "reachable": false,
    "files": 1830, "bytes": 5234000000,
    "report": { "worth_keeping_not_backed_up": { "files": 212, "bytes": 912000000 },
                "backed_up": { "files": 1400, "bytes": 4100000000 },
                "disposable": { "files": 180, "bytes": 22000000 },
                "sensitive": { "files": 3, "bytes": 12000 } },
    "report_note": null, "first_seen_at": "2026-11-12T13:36:44Z" } ] }
```

`volume` holds what the system tells about the drive (Windows: label, serial number, file system; elsewhere label and
file system); every part may be `null`. `reachable` says whether the root can be reached now. `report` is the overview
of the findings of the last job (as `sfo findings --summary` counts it: a marked file counts under `sensitive` only);
`null` for a library or without a job, with the reason in `report_note`.

### `sfo where <path-or-hash>`

Where a file lies today, where it lay, and when and by which plan it moved. The argument is a path (a file, or a
folder for everything below it) or a full SHA-256 checksum (64 hex digits).

```json
{ "query": "/media/usb-stick/DCIM/IMG_0001.jpg", "kind": "path", "hash": "9f2c…",
  "now": [ { "path": "/mnt/nas/library/Fotos/IMG_0001.jpg", "source": "journal", "exists": true, "job_id": null,
             "seen_at": "2026-11-14T10:00:00Z" } ],
  "history": [ { "from": "/media/usb-stick/DCIM/IMG_0001.jpg", "to": "/mnt/nas/library/Fotos/IMG_0001.jpg",
                 "moved_at": "2026-11-14T10:00:00Z", "plan_id": 1, "action_id": 3, "state": "moved",
                 "returned_at": null, "purged": false, "source": "journal" } ] }
```

`now` lists the places of today, newest first: the target of a move that did not come back, else where scans saw the
file (`scan`), or for a checksum every path with that stored checksum (`checksum`). `exists` says whether the path is
there now (`false` also when its drive is not attached). `history` lists the moves, oldest first, from the move
history (`journal`) and from move notices a scan found (`notice`); `state` is `moved` or `returned` (put back by
`undo`), `purged` says the held original was removed for good. Both lists are empty when nothing is known.

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
{"schema_version":1,"type":"progress","phase":"scan","processed":120,"total":480,"errors":0,"current_path":"/media/usb-stick/photos","message":null,"rate_per_minute":null,"remaining_seconds":null}
{"schema_version":1,"type":"log","level":"warning","category":"ClassificationService","message":"...","exception":null}
```

`phase` is `scan`, `pre_assessment`, `classification`, `summary`, `compare` or `eval_cases`. Progress is throttled to
one line per phase every 500 ms, plus the final line of a phase. `rate_per_minute` and `remaining_seconds` are
filled after the first 5 seconds of a phase: the rate counts only what this run got through since its first line
(files counted as done before, such as those skipped by rule, are left out), the time left rests on the open rest.
Text lines then end in `, 277 open, 12.5/min, about 23 min left`. `sfo analyze` also writes `"type":"wait"`
lines when it starts or stops waiting for the model server (see `sfo analyze`); they are never throttled.

**Exit codes.**

| Code | Meaning | `error.code` |
|---|---|---|
| 0 | Done | |
| 1 | The command ran but could not do its job | `job_not_found`, `path_not_found`, `scan_failed`, `scan_not_completed`, `wrong_role`, `compare_incomplete`, `comparison_not_found`, `database_error`, `model_missing`, `rules_invalid`, `words_invalid`, `output_inside_job`, `export_failed`, `truth_invalid`, `run_not_found`, `output_not_empty`, `bundle_not_found`, `patterns_invalid`, `library_patterns_invalid`, `settings_invalid`, `finding_not_found`, `targets_invalid`, `plan_not_found`, `action_not_found`, `plan_change_refused`, `library_invalid`, `import_invalid`, `plan_run_incomplete`, `library_unreachable`, `internal_error` |
| 2 | Wrong invocation: unknown command or option, missing or invalid value | `usage` |
| 3 | Ollama not reachable | `ollama_unavailable` |
| 4 | The model server went away during `sfo analyze` and did not come back within `ModelServer.MaxWaitMinutes`; open items stay open, run the command again | `model_server_lost` |
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
   Never write to stdout. A command that needs another service for one run (for example
   `sfo eval --stub` replacing Ollama) calls `ctx.ConfigureServices` before the first
   `GetServicesAsync`.
3. Define the result as records in the command class; `CliJson` turns them into `snake_case`.
   Reuse `JobView` for jobs. A command that lists or prints files reuses `ReadOptions` (paging,
   filters, `--include-sensitive`) and gets its files from `JobReadService`, which applies
   `SensitiveOutputGuard`.
4. Register the command in `SfoApp.BuildRoot`, describe it here, and add tests in
   `tests/SmartFileOrganizer.Cli.Tests`.

`CommandRunner` turns the outcome into the envelope or text, maps Ctrl+C to 130 and any
unexpected exception to 1 (`internal_error`). Parse errors never reach the handler; `SfoApp`
answers them with exit code 2.
