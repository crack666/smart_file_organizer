# GitHub Copilot Instructions

## Project overview

This repository contains **Smart File Organizer**, a cross-platform desktop application built with **.NET 10**, **C#**, and **Avalonia**.

The product scans large directory trees, stores metadata in SQLite, helps users review file organization decisions, and is designed to support local AI-assisted classification via **Ollama**.

## What is true today

- the layered solution structure is real and should be preserved
- recursive scanning, SQLite persistence, folder browsing, file listing, and file details are implemented
- image, text, and first-page PDF preview are implemented in the desktop app
- Ollama integration code exists, but it is **not yet fully wired into the active end-to-end scan/review workflow**
- review and override concepts exist in the architecture, but the user-facing workflow is still incomplete

Do not describe unfinished AI workflow pieces as fully shipped.

## Architecture boundaries

Keep the current layering intact:

- `Domain`: entities, enums, interfaces, contracts
- `Application`: use-case orchestration and workflow services
- `Infrastructure`: SQLite, filesystem, HTTP, Ollama, concrete implementations
- `Scanning`: scan engine, heuristics, file type detection
- `Desktop`: Avalonia views, view models, UI services, composition root
- `tests`: unit and integration tests

### Hard rules

- do not put raw SQL in Avalonia view models
- do not put Avalonia/UI types into Domain
- do not bypass application services from the UI unless there is a very strong and explicit reason
- do not mix scanning engine logic into views or view models

## Preferred coding style

- prefer small, focused classes
- prefer explicit names over abbreviations
- keep async flows properly asynchronous
- do not block the UI thread
- favor readability and maintainability over clever abstractions
- preserve existing public APIs unless a change is necessary
- make the smallest safe change that solves the problem

## Persistence and data access

- SQLite is the local source of truth for scan state and resumable workflows
- Dapper is used intentionally; prefer explicit SQL queries over heavy ORM patterns
- remember that snake_case database columns map to PascalCase properties through configured underscore matching
- if adding schema changes, keep them easy to trace and document the intent

## UI guidance

- this is a desktop-first product, not a web wrapper
- maintain a polished native-desktop feel
- keep layouts resizable and practical for large file sets
- be careful with Avalonia theming and DataGrid styling; this project already hit dark-theme rendering issues
- preview features should degrade gracefully when a file type is unsupported

## Dependency guidance

- prefer free and permissive open-source packages
- avoid paid controls or vendor lock-in unless explicitly requested by the user
- cross-platform support matters for dependency selection
- native runtime dependencies require extra care and should be introduced only when justified

## AI integration guidance

When working on Ollama-related features:

- keep model access behind service abstractions
- fail gracefully when Ollama is unavailable
- keep prompts centralized and maintainable
- parse model output defensively
- persist meaningful outcomes instead of leaving important classification state transient

## Testing expectations

- add or update tests when changing scan logic, persistence logic, or classification behavior
- prefer integration tests when validating scan-to-database-to-query flows
- do not rely only on manual UI verification for data pipeline fixes

## Documentation expectations

When you make meaningful changes, update the relevant docs in `docs/`:

- `docs/status-and-roadmap.md` for implementation status
- `docs/architecture.md` for structural changes
- `docs/engineering-decisions.md` for important tradeoffs or dependency decisions
- `docs/code-guidelines.md` for lasting engineering conventions

## Change strategy

When asked to implement or fix something:

1. understand whether the issue belongs to UI, application orchestration, persistence, or scanning
2. fix root causes, not just symptoms
3. keep changes incremental and testable
4. preserve the project’s cross-platform and OSS-friendly direction
5. call out caveats honestly if something is only partially implemented
