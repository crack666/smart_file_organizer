# Library match: the existing order as a target

`sfo library show` reads the target map of a library: what each folder is for, how its
subfolders are named, which period it covers and where its order is weak. `sfo library match`
uses that map to name the best existing folders for a part of a source (a folder, a file or a
bundle), each with reasons and a certainty, and proposes a new folder in the library's style
when nothing fits. Issue: https://github.com/crack666/smart_file_organizer/issues/25

Both commands only read. No file in the source or the library is moved, renamed or written;
the proposals are data for the plan (https://github.com/crack666/smart_file_organizer/issues/11).
The only file written is the patterns file next to the database (below); the database gets the
folder descriptions of the content check (`folder_descriptions`) and the model's answers.

The rule part is complete without a model (`--no-model`). The model, when used, is asked for
three things: a purpose for the upper folders (`show`), a name for a new folder when the rules
cannot complete it, and whether the content of the unit fits each candidate folder (`match`,
below). The first two get folder names, counters and patterns only. For the content check, a few
files of each candidate folder are described with the same call `sfo analyze` uses for a file
(it reads the file); the comparison itself gets only these descriptions. Answers are cached in
`library_advice` like those of `library review`.

## The map

Built in memory from a completed library job (best scanned with `--structure-only`) and the
findings of [library-review.md](library-review.md). Per folder:

| Field | Meaning |
|---|---|
| `role` | `occasion`, `time`, `collection`, `topic`, `document_type`, `media_title`, `project`, `catch_all`, `mixed` or `dump` (codes of `FolderRole`, shared with `library review`). |
| `children_role` | For a collection: the role most of its subfolders have (at least `RoleShare`). |
| `purpose` | One sentence from the rules, or from the model (`purpose_source`). |
| `files`, `bytes`, `by_file_type` | Live sums of the subtree, dumps not counted. |
| `period` | First and last file time with its source. A library has no capture dates (it is scanned without opening files), so the time is the date in the file name, else the modification time. |
| `name_period` | The year, month or day in the folder's own name. |
| `children_pattern` | How the subfolders are named (`{occasion} {year}`, `{year}`, `{date} {occasion}`, ...), with count and examples. |
| `pattern` | The effective habit: the hand-edited pattern if there is one, else `children_pattern`. |
| `findings`, `weak` | Structure-review findings at this folder (sensitive locations and dominant files are left out). |
| `dump_kind` | For a dump: `device`, `computer`, `package`; its inside is one entry. |

Roles, in this order:

1. `dump` for dumps found by the review.
2. `catch_all` for placeholder names (`Sonstiges`, `New folder`, `misc`, ...).
3. `time` for a name that is only a year or a year and month.
4. `collection` when at least two subfolders with content have meaningful names (or are
   year folders) and the folder's own files are at most half of its subtree.
5. `topic` for a folder with no files.
6. `occasion` when photos and videos make up at least `RoleShare` and the name is not generic.
7. `document_type`, `media_title`, `project` (software share at least half, or unit markers),
   `mixed`, else `topic`.

## `match`: signals and certainty

A match unit is a source folder, a single file or a bundle of `sfo bundles build`. The unit's
time comes from the bundle logic (capture date if analysed, else date in the name, else
modification time), its comparison states and the library folders that already hold the same
content from the latest `sfo compare`.

**Kept whole.** A named source folder (not generic, not a placeholder, not the scan root), and
every `folder` or `dump` bundle, keeps its name and is placed as a whole: it is only offered
`into_existing` a library folder of the same name (same letters, same years), or `as_subfolder`
of a folder with children. It is never renamed and never split. All other units (loose
files, generic folders like `DCIM/100CANON`, events) go `into_existing` folders that hold
files directly.

Signals:

| Signal | Into a folder | As a subfolder | Strong when |
|---|---|---|---|
| `content_neighbours` | 0.40 | 0.35 | at least `MinContentShare` of the unit's files already lie in the folder or below it with the same content; full value at `FullContentShare` |
| `period` | 0.25 | 0.10 | the unit's reliable time overlaps the folder's file period, or a day or month in its name (`PeriodToleranceDays`). A year in the name agrees weakly (0.5), the same file year weaker (0.3). As a subfolder: inside a year folder, or within the years of the neighbours (±1) |
| `name` | 0.20 | 0.10 | at least `MinNameShare` of the unit's words are in the folder's name (prefix of 4 letters, or one letter apart from 5). As a subfolder: the unit's name follows the habit of its new neighbours |
| `file_type` | 0.15 | 0.15 | never strong; agrees when the unit's main kind of files is at least half of the folder |
| `siblings` | | 0.30 | at least `MinSiblingShare` of the subfolders have the unit's role (for dated loose files, year folders count) |

| `content_fit` | 0.40 | 0.40 | the content check (below) says the unit's content fits the folder's; only for checked candidates |

The score is the weighted sum. Time, name and file type say only that a folder *could* fit:
two occasions of the same week share the period and the type. Certainty therefore needs content,
either the same files already at the target or a content check that fits
(https://github.com/crack666/smart_file_organizer/issues/56):

- `review`: any signal contradicts. The period contradicts when both periods are known and
  apart and the folder is bound to its time (an occasion, a year, a date in its name); the file
  type contradicts when under 10 % of a folder with at least 3 files is of the unit's kind.
  A kept-whole folder whose content already lies in a differently named folder at that place is
  also `review`.
- `certain`: same content plus one more strong signal or a fitting content check; or a fitting
  content check plus a strong period.
- `likely`: same content plus the file type agreeing; or a fitting content check plus another
  strong signal or the file type agreeing.
- `uncertain`: everything else, in particular period, name and type without content. No
  proposal without content is better than `uncertain`.

When the runner-up scores within `Margin` of the best, the best drops one level: two places
fit about equally well and a person decides. Candidates below `MinScore` are dropped.

Every proposal carries `reason`, one sentence a person can check, built from the agreeing
signals, plus the full `signals` list. A checked candidate names both descriptions
("the content fits: snowy slope, skiing, holiday; the folder shows ..."); an unchecked one says
why not ("the content was not checked (no model)").

## Content check

Run by `match` per library, after the scores and before the certainty:

1. **The unit's description.** Built from the description facts of `sfo analyze` (scene,
   activity, setting, season, occasion, people, document kind, issuer, year; see
   [glossary.md](glossary.md#fact-key-filefactkey)) of up to `UnitSampleSize` of its files, spread
   over its time. Counted values, the most frequent first; nothing is stored. A unit whose files
   are not described yet is not checked (`no_description`): `sfo analyze` describes them.
2. **The candidates.** Those that reach `MinScore` on time, name and type, at most
   `MaxContentCandidates`, the best first.
3. **The folder's description.** From a folder description source. The default (`sample`) takes
   up to `FolderSampleSize` pictures and videos (for a media folder) or documents, spread over
   time, never a file marked sensitive, and describes them with the model unless a stored
   description of that file exists. The result is stored in `folder_descriptions` per library job,
   folder and source and read again on the next match; `--refresh` describes again. The source is
   an interface (`IFolderDescriptionSource`): an external one (a photo manager that already
   describes the library, found by checksum) can be registered before the sample and wins when
   it has an answer (`source: external`).
4. **The comparison.** One short model call per unit and library: the unit's description and one
   line per candidate (`id | placement | folder | period | description`); the fixed instructions
   are the system message, the answer is limited to a short JSON list of
   `{id, fit, reason}` with `fit` one of `fits`, `contradicts`, `unclear`. Unknown ids and codes
   are dropped.
5. **The result.** `fits` adds the signal `content_fit`. `contradicts` drops the candidate (it is
   listed under `rejected` with the reason), unless the same files already lie there. `unclear`
   leaves it unchanged, which caps it at `uncertain` without the same files.

When every candidate is dropped, the new folder is proposed, and its reason names the folder
that fit only by time or name. Without a model (`--no-model`, Ollama not reachable, no
description) nothing is checked, `content_check.status` says why and a note says that no proposal
without the same files at the target is better than `uncertain`.

| `content_check.status` | Meaning |
|---|---|
| `checked` | The model compared the candidates. |
| `no_model` | `--no-model`, or no comparison is configured. |
| `unavailable` | Ollama does not answer. |
| `no_description` | The unit's files are not described yet. |
| `no_candidates` | No candidate reached `MinScore`; nothing to compare. |
| `failed` | The model call failed or its answer did not parse. |

In `sfo eval --stub` the comparison runs on fixed rules (`description-rules`: other occasion,
document kind or issuer contradicts; shared scene or activity words, or the same occasion and
season, fit), so the measurement runs without a model ([evaluation.md](evaluation.md)).

## Weak places

When the target or the parent of a new folder has review findings, `weak_place` lists them and
the certainty is capped: a high-impact finding (placeholder name, files that do not fit the
name) means `review`; a medium one caps at `uncertain`. Findings about how neighbours are named
only count as a note for a folder that keeps its own name, and cap a new folder's name at
`uncertain`, since the habit to copy is unclear. A catch-all folder is offered at most as a
weak place, so the match does not continue a weak order blindly.

## No fitting target: a new folder

When no candidate reaches `MinScore`, `new_folder` names a parent and a name:

- The parent is the folder whose subfolders are most like the unit (siblings, type, period),
  with 0.10 off for parents whose subfolders are named in several ways. It needs agreeing
  siblings or file type; otherwise the library root.
- A kept-whole unit keeps its name. Other units get the parent's pattern filled from what is
  known: `{year}`, `{date}`, `{month}` from the unit's period, `{occasion}`/`{name}` from a
  meaningful source name. What is not known stays as a placeholder (`{occasion} 2021`) and
  `name_complete` is `false`. A name that exists at the parent gets a number.
- With a model, an incomplete name is offered to the model with the neighbours' names, the
  pattern and the unit's counters. Its answer is refused when it holds a placeholder or an
  invalid character, is longer than 80 characters, repeats a neighbour or lacks the unit's year.
- The certainty is `uncertain` beside fitting siblings, otherwise `review`, capped by a weak
  parent.

## Without a library

When the source has no comparison and no completed library job exists, `match` returns exit
code 0 with `status: "no_library"`, a message that no permanent storage is known and how to make
one (`sfo scan <path> --role library`, or `sfo library propose` for a structure from scratch), and
no proposals. There is no fallback target: never a folder of the user profile (Videos, Pictures,
Documents) or of the system drive, which is often short of room and no safe storage. Which
libraries are used: `--against` (repeatable), else those of the latest comparison, else the newest
completed library job (with a note).

## Several libraries

A permanent storage often has several roots: separate shares for photos, videos and documents,
each scanned as its own library job (https://github.com/crack666/smart_file_organizer/issues/74).
`sfo compare <source> --against <a> --against <b>` compares with all of them; a file is
`backed_up` as soon as one holds it, and the result names which. The comparisons of one run are
read together by the later steps.

`match` with several libraries builds the map of each and matches the unit against each on its
own, with the joined comparison (the signal `content_neighbours` sees the content in every root).
The candidates of all libraries are listed by score; on a tie the library named first wins. Each
proposal carries `library_job_id`, and its `destination` lies inside that library's root. Only
when no library has a fitting folder is a new folder proposed: the one of the library whose
proposal is most certain (first named on a tie), named by the model in that library's style if
the rules cannot complete the name. The `Margin` rule is applied within each library, not across
them.

## Targets only in the permanent storage

Every target (`match`, and the `misplaced` findings, which use the same rules) is a folder of a
library map or a new folder below one, so it lies in a root scanned with the role `library`. The
findings check it once more and drop any place outside every library root, with a note. Without a
library, no target path exists at all.

## Room at the target

`match` names for each library root how much is free there and what the unit needs (`space`): its
files that no library holds yet. The free space is asked from the file system that holds the root
(a local folder, a mounted share or a network path; on Windows `GetDiskFreeSpaceEx`, elsewhere
`statvfs`). When it cannot be told (root unreachable, a share that does not report it) it is
`unknown`, never an error. `not_enough` adds a note. `sfo findings --summary` does the same per
root for all findings with a target there (`storage` in [cli.md](cli.md#sfo-findings-job)).

## Patterns file

`<database folder>/library-patterns/job-<id>.json`, a target file in the format of
`library propose --out` (`kind: sfo_target_structure`): one target per collection with a naming
habit (`path`, `pattern`, `holds`, `reason`, `examples`). It is written the first time the map is
read and never overwritten: edit a `pattern` by hand and `show` and `match` use it
(`pattern` in the output, new folder names). `--reset-patterns` writes it again from the
rules. A file that does not parse ends the command with `library_patterns_invalid`.

## Settings (`LibraryMatch`)

Starting values, not measured against a real library. The evaluation corpus has a small library
with known truth since version 5; with the stub, every target there is hit (measured 13.11.2026,
`sfo eval --stub`), which checks the wiring, not the model.

| Setting | Default | Reason |
|---|---|---|
| `MinScore` | 0.35 | A period and file type alone (0.40) pass; a file type plus a weak year (0.275) does not: better a new folder than a forced hit. |
| `Margin` | 0.10 | About the weight of the weakest signal; closer than that, two places are equal. |
| `MinContentShare` | 0.10 | One copied photo in ten is a hint where the rest belongs. |
| `FullContentShare` | 0.50 | When half already lies there, the folder is the place. |
| `PeriodToleranceDays` | 2 | Time zones, a late evening, a copy the day after. |
| `MinNameShare` | 0.5 | Half of the source's words in the folder's name. |
| `MinSiblingShare` | 0.5 | The majority of the neighbours are of the same kind. |
| `RoleShare` | 0.6 | A clear majority makes the role; below it the folder is `mixed`. |
| `MaxCandidates` | 3 | Enough to choose, few enough to read. |
| `ModelMaxDepth`, `ModelMaxFolders` | 2, 150 | Purposes are for the upper folders; deeper ones are explained by the rules. |
| `Weight*` | 0.40 / 0.25 / 0.20 / 0.15 | Same content is the hardest evidence, then time, then name; the file type separates little. |
| `WeightContentFit` | 0.40 | A fitting description counts like the same files: it is what tells two occasions of one week apart. |
| `MaxContentCandidates` | 5 | One comparison call with a short list; more candidates than that rarely pass `MinScore`. |
| `FolderSampleSize` | 4 | Enough to see what a folder shows; each described file is a model call the first time. |
| `UnitSampleSize` | 12 | The unit's files are described already; more only lengthens the prompt. |
| `SubfolderWeight*` | 0.35 / 0.30 / 0.15 / 0.10 / 0.10 | For a whole folder the neighbours count most after content: a new occasion goes next to occasions. |
| `PatternsDirectory` | `library-patterns` next to the database | One file per library job, beside the data it was made from. |

An invalid value ends `show` and `match` with `settings_invalid`, naming every problem.

Command options and output: [cli.md](cli.md#sfo-library-show-job).
