# Scan rules and profiles

Which folders a scan enters, records only shallow, or leaves out is decided by a
rule file. The program ships one
([`src/SmartFileOrganizer.Scanning/Rules/default-rules.json`](../src/SmartFileOrganizer.Scanning/Rules/default-rules.json),
embedded in the build); a profile for one drive is a copy of it with other rules.

Issue: https://github.com/crack666/smart_file_organizer/issues/21

The guiding rule: the tool exists to find user data on old drives. A user folder
that is skipped or recorded only shallow is the costliest mistake a scan can
make; a program folder scanned in full only costs time. In doubt, the scan
enters a folder.

## Using a profile

```bash
sfo rules show > my-disk.json                 # start from the shipped rules
sfo rules check /media/disk/Games/Quake --root /media/disk --rules my-disk.json
sfo scan /media/disk --rules my-disk.json
sfo scan --resume 7 --rules my-disk.json      # switch an existing job to other rules
```

The rules are stored at the job (name, source path, hash and the rules
themselves), so a job resumes with exactly its rules even if the file was
edited or moved since. Every `job` in the JSON output carries
`"rules": { "name", "source", "hash" }`. A job created without `--rules`, or by
the desktop app, uses the shipped rules (`source` is `built-in`).

## Format

```json
{
  "schema_version": 1,
  "name": "laptop-2009",
  "description": "Old laptop disk: games are installed under D:/Games.",
  "shallow_from": 6,
  "safety_limit": 32,
  "skip_hidden": true,
  "user_types": ["image", "video", "audio", "document"],
  "program_types": ["executable"],
  "rules": [
    { "match": "node_modules", "action": "skip", "why": "JavaScript packages" },
    { "match": "Games", "action": "shallow", "max_depth": 2, "why": "installed games" },
    { "match": "Games/Saves", "action": "user_data" },
    { "match": "Documents", "action": "user_data" }
  ]
}
```

Every field except `rules` is optional and defaults to the value shown.
Comments (`//`) and trailing commas are allowed. Unknown fields are an error, so a
typo such as `"acton"` is reported instead of being ignored. All problems of a
file are reported at once (`rules_invalid`, exit code 1).

| Field | Meaning |
|---|---|
| `name` | Profile name, shown at the job and in every reason. Default: the file name. |
| `shallow_from` | From this depth below the scan root, a folder no rule covers is decided by its content (below). |
| `safety_limit` | From this depth every folder is recorded shallow, whatever it holds or which rule matches. Guards against loops through links; real trees stay well below it. |
| `skip_hidden` | Skip folders whose name starts with a dot, unless a rule matches them. |
| `user_types` | File types that make a folder user data when found among its own files. |
| `program_types` | File types that mark a program folder, as long as no user file sits next to them. |
| `rules[].match` | One or more whole folder names, compared case-insensitively. `bin` matches `App/bin`, not `Robin`. `ProgramData/Microsoft` (either separator) matches the two names in a row. |
| `rules[].action` | `skip`: not entered, files not listed. `shallow`: files listed, subfolders counted but not entered. `user_data`: always scanned in full, no depth limit and no content check. `follow_link`: enter a link whose own path ends with the match ([below](#links-and-read-errors)); no effect on folders that are no links. |
| `rules[].max_depth` | Only with `shallow`: how many levels below the matched folder are still scanned in full; the folders at that level are recorded shallow. 0 (default) records the matched folder itself shallow. |
| `rules[].why` | Free text, copied into the reason stored at every folder the rule decides. |

File types: `image`, `video`, `audio`, `document`, `archive`, `code`,
`executable`, `database`, `font`, `other`, `unknown` (by extension, see
`FileTypeDetector`).

## How a folder is decided

First that applies wins:

1. The scan root is always scanned.
2. The rule that matches closest to the folder (deepest match; on a tie, the rule
   listed first) if it is a `skip` rule.
3. From `safety_limit` on: shallow.
4. The closest `shallow` or `user_data` rule.
5. A dot-folder: skipped (`skip_hidden`).
6. Above `shallow_from`: scanned.
7. From `shallow_from` on, the folder's own files decide: any user file → scanned;
   program files and no user file → shallow; neither → scanned.

Before every rule: the hold folder of `sfo plan apply` (`.sfo-hold` directly below the root) is skipped, since the
originals in it already left the source. A move notice (`_moved-files.txt`, [move-notice.md](move-notice.md)) is no
file of the scan; its entries are stored as origin
(https://github.com/crack666/smart_file_organizer/issues/86).

A skipped folder is marked sensitive by its name when it typically holds keys,
logins or private messages (`.ssh`, `.gnupg`, `.aws`, `.azure`, `.kube`,
`.docker`, `.password-store`, `.config`, `.mozilla`, `.thunderbird`, wallets,
`AppData`), without being entered. The read commands show the marker
([cli.md](cli.md#sensitive-files)); skips stored before this was introduced are
marked when read.

Skip and shallow rules only look at the part of the path below the scan root:
what lies above it was chosen by the user, so a root inside `AppData` is scanned.
User-data rules look at the whole path, so a root inside `Documents` makes
everything below it user data.

"Closest match wins" lets a profile carve exceptions: `Documents/project/bin` is
skipped although `Documents` is user data, and `Games/Saves` is scanned in full
although `Games` is limited.

The depth limit only applies to folders recognised as program folders. Until
https://github.com/crack666/smart_file_organizer/issues/21 the shipped rules recorded every folder from depth 6 shallow unless its path
contained a user-data name, and skipped everything from depth 12. A photo folder
such as `Urlaubsfotos` at depth 7 was therefore only recorded flat.

## Links and read errors

Issue: https://github.com/crack666/smart_file_organizer/issues/57

An old Windows profile is full of compatibility links: `All Users` and `Default User` point to folders of
the system, `Documents and Settings`, `Eigene Dateien`, `Anwendungsdaten` and `Application Data` point back
into the profile or to itself. On a disk from another computer the absolute targets resolve on the machine
that scans. Following them counts foreign files or the same files twice, and loops run into the safety
limit. Until this was changed, the scan followed every link.

- A folder that is a link (Windows: junction, symbolic link, volume mount point, told apart by the reparse
  tag; Unix: symbolic link) is not entered. It is stored as a row with status `link`, its kind and its
  target, and a reason such as `Link: junction to 'C:\ProgramData'; not followed (no follow_link rule of
  rules 'default' matches)`. Nothing behind it is counted.
- A file that is a link is stored in `file_links` with kind and target, not in the file list: it counts
  neither as a file nor by size, and no comparison or analysis sees it. A hard link is no link in this
  sense: it is a second name of the same file and listed under both names; the execution of a plan tells
  them apart by file identity.
- Reparse points with their own content (OneDrive placeholders, deduplicated files) are no links and are
  scanned as usual.
- The scan root is entered even if it is a link: the user chose it.
- `{ "match": "All Users", "action": "follow_link" }` lets the scan enter that link. Even then it is not
  entered when its target lies inside the scan root (counted there already), holds the link or the root (a
  loop), cannot be resolved, or was already entered through another link; the reason says which. There is
  no setting to follow every link.
- A folder that cannot be read is stored with status `error` and the cause of the error
  (`access_denied`, `not_found`, `path_too_long`, `io_error`), never as an empty folder. A file whose size
  cannot be read stays in the list with `scan_error_cause` and `scan_error`. `sfo status` groups both by
  cause.

`sfo rules check` treats a link on the way like the scan: a link no rule follows blocks everything below it.
Jobs scanned before 13.11.2026 may have followed links; resuming them does not undo that, a new scan does.

## Reasons

Every decision is stored with its reason in `directory_nodes.dir_reason`, for
example:

```
Skip: matches skip rule 'bin' of rules 'default': build output
Shallow: matches shallow rule 'Games' of rules 'laptop-2009', 2 level(s) below the match, max_depth 2: installed games
Descend: depth 7 reaches shallow_from 6, but the folder holds 3 user file(s) (2 image, 1 document)
Shallow: depth 7 reaches shallow_from 6 and the folder looks like a program folder (4 program file(s): 4 executable; no user files)
```

`sfo rules check` shows the same reason for any path without scanning, plus the
matching rule, the `basis` of the decision, and whether a scan reaches the path
at all: a folder below a skipped folder is never seen, whatever its own rule says.

## Resume and rule changes

When a scan resumes, folders already scanned are not read again. Folders stored
as skipped or shallow are evaluated again with the job's current rules:

- a wider decision (skip → shallow → scanned) is applied, and the reason names
  the earlier one: `... (re-evaluated on resume; before: Skip: ...)`;
- a narrower decision keeps the folder as it is: data already collected is never
  thrown away.

`sfo scan --resume <job> --rules <file>` with rules that differ from the stored
ones (by hash) switches the job to them and reopens it even if it was finished,
so a drive can be rescanned with a better profile without starting over. The
result reports `rules_changed: true`. Jobs from before rules were stored get the
shipped rules recorded on their next run; their old skip and shallow decisions
are re-evaluated the same way.
