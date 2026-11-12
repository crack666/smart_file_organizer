# Library match: the existing order as a target

`sfo library show` reads the target map of a library: what each folder is for, how its
subfolders are named, which period it covers and where its order is weak. `sfo library match`
uses that map to name the best existing folders for a part of a source (a folder, a file or a
bundle), each with reasons and a certainty, and proposes a new folder in the library's style
when nothing fits. Issue: https://github.com/crack666/smart_file_organizer/issues/25

Both commands only read. No file in the source or the library is moved, renamed or written;
the proposals are data for the plan (https://github.com/crack666/smart_file_organizer/issues/11).
The only file written is the patterns file next to the database (below).

The rule part is complete without a model (`--no-model`). The model, when used, gets folder
names, counters and patterns only, never file contents, and is asked for two things: a purpose
for the upper folders (`show`) and a name for a new folder when the rules cannot complete it
(`match`). Its answers are cached in `library_advice` like those of `library review`.

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

The score is the weighted sum. Certainty comes from agreeing signals, not from the score:

- `review`: any signal contradicts. The period contradicts when both periods are known and
  apart and the folder is bound to its time (an occasion, a year, a date in its name); the file
  type contradicts when under 10 % of a folder with at least 3 files is of the unit's kind.
  A kept-whole folder whose content already lies in a differently named folder at that place is
  also `review`.
- `certain`: same content plus at least one more strong signal.
- `likely`: two strong signals, or one strong signal and the file type agreeing.
- `uncertain`: less than that.

When the runner-up scores within `Margin` of the best, the best drops one level: two places
fit about equally well and a person decides. Candidates below `MinScore` are dropped.

Every proposal carries `reason`, one sentence a person can check, built from the agreeing
signals, plus the full `signals` list.

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
code 0 with `status: "no_library"`, a message how to make one (`sfo scan <path> --role library`,
or `sfo library propose` for a structure from scratch), and no proposals. Which library is used:
`--against`, else the one of the latest comparison, else the newest completed library job (with
a note).

## Patterns file

`<database folder>/library-patterns/job-<id>.json`, a target file in the format of
`library propose --out` (`kind: sfo_target_structure`): one target per collection with a naming
habit (`path`, `pattern`, `holds`, `reason`, `examples`). It is written the first time the map is
read and never overwritten: edit a `pattern` by hand and `show` and `match` use it
(`pattern` in the output, new folder names). `--reset-patterns` writes it again from the
rules. A file that does not parse ends the command with `library_patterns_invalid`.

## Settings (`LibraryMatch`)

Starting values, not measured ones: no library with known truth exists yet in the evaluation
corpus.

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
| `SubfolderWeight*` | 0.35 / 0.30 / 0.15 / 0.10 / 0.10 | For a whole folder the neighbours count most after content: a new occasion goes next to occasions. |
| `PatternsDirectory` | `library-patterns` next to the database | One file per library job, beside the data it was made from. |

An invalid value ends `show` and `match` with `settings_invalid`, naming every problem.

Command options and output: [cli.md](cli.md#sfo-library-show-job).
