# Smart File Organizer

[![CI](https://github.com/crack666/smart_file_organizer/actions/workflows/ci.yml/badge.svg?branch=master)](https://github.com/crack666/smart_file_organizer/actions/workflows/ci.yml)

Smart File Organizer scans old drives, USB sticks, backups and home folders
and records every directory and file in a local SQLite database. A local
model served by [Ollama](https://ollama.com) then judges folders and files
(what they are, whether they matter, where they belong), so nothing leaves
the machine. The goal is a reviewed plan for tidying the data, executed only
after the user confirms it.

Product description, file type support and configuration:
[docs/README.md](docs/README.md). What exists today and the known traps:
[docs/status-and-roadmap.md](docs/status-and-roadmap.md).

## Build and test

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet build SmartFileOrganizer.slnx
dotnet test SmartFileOrganizer.slnx
```

The tests need neither Ollama nor a GPU. CI runs the same build and tests
on Windows and Linux for every push and pull request
([.github/workflows/ci.yml](.github/workflows/ci.yml)).

To run the desktop app (needs a running Ollama for the AI part):

```bash
dotnet run --project src/SmartFileOrganizer.Desktop
```

The same work runs from the command line, with `--json` output for scripts
and models ([docs/cli.md](docs/cli.md)):

```bash
dotnet run --project src/SmartFileOrganizer.Cli -- scan <folder>
dotnet run --project src/SmartFileOrganizer.Cli -- status <job>
dotnet run --project src/SmartFileOrganizer.Cli -- summary <job> --json
# what on an old medium is not yet in the library, by content:
dotnet run --project src/SmartFileOrganizer.Cli -- scan <library> --role library
dotnet run --project src/SmartFileOrganizer.Cli -- compare <job> --against <library-job>
# decide in bundles: phone and computer copies, named folders, events (docs/bundles.md):
dotnet run --project src/SmartFileOrganizer.Cli -- bundles build <job>
dotnet run --project src/SmartFileOrganizer.Cli -- bundles list <job> --json
```

To judge how an existing library is filed, without opening a file and
without changing anything in it ([docs/library-review.md](docs/library-review.md)):

```bash
dotnet run --project src/SmartFileOrganizer.Cli -- scan <library> --role library --structure-only
dotnet run --project src/SmartFileOrganizer.Cli -- library review <job> --no-model
```
How good folder verdicts, categories and sensitive markers are is measured
with `sfo eval` against a generated corpus with a known truth; corrections
made in the review become test cases ([docs/evaluation.md](docs/evaluation.md)).

## Roadmap

Open work lives in GitHub issues, grouped into milestones A to D:
https://github.com/crack666/smart_file_organizer/milestones

Each issue ends with a checkable "done when" section.

## Labels

| Label | Meaning |
|---|---|
| `cloud` | Can be done by an agent without a GPU and without files outside the repository. |
| `lokal` | Needs the local machine: GPU, a running Ollama or real data outside the repository. |
| `mensch` | Needs a human: identity, credentials or a judgement call. |
| `in-arbeit` | Someone is working on it; leave it alone. The claiming comment names branch and owner. |
| `wartet:mensch` | Unfinished, waiting for a human. |
| `wartet:gpu` | Unfinished, waiting for GPU time. |
| `wartet:agent` | Unfinished, waiting for an agent to pick it up. |

## License

MIT
