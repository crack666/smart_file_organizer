# Code Guidelines

## General principles

- write production-oriented, readable code
- prefer clear naming over clever compression
- keep classes focused and responsibilities narrow
- avoid god objects and hidden side effects

## Layering rules

### Domain

- contains entities, enums, and interfaces only
- must not depend on Avalonia, HTTP, SQLite, or UI concerns

### Application

- coordinates use cases and workflows
- may depend on domain abstractions
- must not contain UI control logic or raw SQL

### Infrastructure

- owns persistence, filesystem access, HTTP integrations, and external service implementations
- should implement interfaces defined in Domain/Application-facing layers

### Desktop/UI

- owns views, view models, UI-specific services, and composition root setup
- must not directly implement file scanning or SQL logic
- should call use-case/application services instead of reaching into infrastructure details

## MVVM rules

- views remain declarative
- view models expose state and commands, not filesystem traversal or SQL
- do not block the UI thread with scan or AI work
- keep event wiring understandable and explicit

## Persistence rules

- SQLite is the persistent source of truth for resumable work
- if a long-running workflow needs recovery, do not keep critical state only in memory
- keep schema changes explicit and easy to reason about
- prefer simple, testable SQL over magical data access patterns

## Scanning rules

- scans must be cancelable
- scanning must be iterative and batch-oriented
- progress should be surfaced through explicit progress reporting
- prune irrelevant directories as early as possible
- avoid deep recursion when a queue/iterative model is simpler and safer

## AI integration rules

- Ollama calls stay behind a dedicated service abstraction
- prompts should remain centralized and maintainable
- parse model output defensively
- AI failures must degrade gracefully without breaking the scan pipeline
- persist AI results and AI errors explicitly

## UI/UX rules

- no HTML reports as the primary UX
- avoid paid/non-free UI dependencies unless there is a compelling and documented exception
- large trees/lists must use lazy loading or virtualization-friendly patterns
- preview and details panes should use available space effectively and remain resizable where helpful

## Dependency rules

- prefer permissive open-source libraries
- consider cross-platform behavior before adding a package
- document why a new dependency exists
- native-runtime dependencies require extra scrutiny and must be called out in docs

## Testing rules

- test core heuristics and persistence behavior first
- prefer narrow, focused unit tests for deterministic logic
- add integration-style tests where persistence and service composition matter
- do not rely on UI-only manual testing for data pipeline correctness

## Documentation rules

- keep `Plan.md` as the aspirational source document
- keep `docs/` focused on reality: architecture, status, decisions, guidelines
- update status docs when major milestones or caveats change
