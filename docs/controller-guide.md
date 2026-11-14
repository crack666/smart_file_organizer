# Deciding targets as the controller

Where the files of a finding go is decided by a controlling model (or a person) outside sfo, not by the local model
(https://github.com/crack666/smart_file_organizer/issues/95). sfo gives the facts as JSON and records the decision;
it never calls the controller. This page is the workflow and the rules the controller follows. The commands are in
[cli.md](cli.md#sfo-findings-candidates-job), the codes in [glossary.md](glossary.md#who-decided-a-target-targetdecidedby).

## Why

The local model judged target folders badly in the first real runs: it took general features that half a library
shares (indoor, a person, winter) for a fit, and named new folders after a file name. A rule cannot repair that
judgement, and a bigger model belongs in the controller, which reads the facts and can look closer where it is unsure.
The local model stays available for describing single files and folders, and as `sfo findings targets --local-model`.

## Workflow

All commands with `--json`. `<job>` is the source job, `<lib>` a library job.

1. **Prepare.** `sfo scan`, `sfo analyze` (descriptions of the files), `sfo compare <job> --against <lib>`,
   `sfo bundles build <job>`. Optional: `sfo findings targets <job>`; by default the rules only, which stores the
   certain cases (the same files already in a folder) and leaves the rest without a target (`no_model` and the like).
2. **Get the facts.** `sfo findings candidates <job>`. For each finding: kind, size, period, kinds of files, what its
   files show (`described`), the parts of a mixture, and per part the candidate folders of the rules with their score,
   reasons, counts, period, a few file names and the stored description (`described` false when none). Large jobs:
   page with `--offset` (`next_offset`), or narrow with `--key`, `--limit`, `--names`, `--max-kb`.
3. **Look closer where needed.** `sfo library folder <lib> "<relative path>"` lists a folder's subfolders and files.
   `sfo library describe <lib> "<relative path>"` lets the local model describe that one folder from a few of its
   files and stores the description; it is the only step that calls a model. Describe only folders you are about to
   decide on.
4. **Decide.** Per finding (or per part of a mixed one, `part`): an existing folder, a new folder directly below an
   existing one, the inbox of a library, or no target, each with a reason.
5. **Record.** One at a time with `sfo findings place <job> <key> --status ... --reason ...`, or all at once with
   `--file decisions.json` ([format](cli.md#sfo-findings-place-job-key)). A refused decision (`placement_refused`)
   refuses the whole file; fix the named problems and record again. `--withdraw` takes a decision back.
6. **Plan.** `sfo findings accept <job> <key>...`, then `sfo plan create <job>`. The plan moves files to the recorded
   places; `no_target` stays where it is. A recorded decision stays through later `sfo findings targets` runs.

## Rules for the decision

The local model failed at these; the controller should not.

- **General features are no reason.** "Both show people", "both are indoors", "both are photos from winter" fit half
  a library. A fit needs a concrete shared thing: the same occasion, the same place, the same named people, the same
  issuer or contract, the same files already there, or names and period that agree with the folder's own.
- **A file name is no folder name.** `IMG_0815`, `scan_003` or `Document (2)` name no occasion. A new folder is named
  after what the files show (occasion, place, topic) in the style of its siblings (`new_folder.pattern`, the names in
  `subfolders`); when nothing names it, use the inbox.
- **The period alone is weak.** A folder that covers the same days but shows something else is not the place; check
  `described` against the folder's description, or describe the folder.
- **Kept whole stays whole.** A finding with `keep_whole` true is a named folder or device copy: it goes as a whole
  under its own name, so the target is the folder it goes into.
- **Parts are decided apart.** A `split` finding mixes things; decide each part, and leave a part you cannot place
  without a target rather than forcing it next to the others.
- **When in doubt: inbox or no target.** A wrong target buries files in a folder nobody looks into for them. The
  inbox (`_Inbox/<source>` below the library root by default) keeps them findable; `no_target` leaves them where they
  are. Both are decisions with a reason, not failures.
- **Marked things stay closed.** Marked files show path and markers only, marked folders no names or description.
  They are decided by a person; do not try to recover their content from paths.

## What sfo checks when a decision is recorded

The target lies below a root of the libraries of the comparison and is not the root itself; it lies in no protected
place (user profile, system drive, `Plan:ProtectedPlaces`); each level is a valid folder name; an existing folder is
one of the library scan; a new folder does not exist yet and its parent does; a part exists. The problem codes are
listed in [glossary.md](glossary.md#why-a-decision-was-refused-placementproblem). Nothing on disk changes until a
plan is approved and applied.
