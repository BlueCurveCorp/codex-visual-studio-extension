# Codex Feature Parity Roadmap

Global task name: `codex-feature-parity-roadmap`

This plan covers the next Codex-related Visual Studio extension integrations, in the requested priority order:

1. Slash commands
2. Review/diff pane
3. Auto Context plus TODO CodeLens
4. Cloud/worktree delegation

## Agent Operating Protocol

Before implementing any task in this roadmap, the AI agent must:

1. Read the repository root `AGENTS.md`.
2. Read this `README.md`.
3. Read the specific task file linked below.
4. Use codebase-memory-mcp graph tools first for code discovery:
   - `search_graph`
   - `trace_path`
   - `get_code_snippet`
   - `query_graph`
   - `get_architecture`
5. Fall back to text search only for XAML, config, string resources, generated assets, or when graph results are insufficient.
6. Keep Visual Studio theme resources intact. Do not hardcode UI colors.
7. Localize all user-visible strings.
8. Persist any new setting through `%LOCALAPPDATA%\CodexVsix\settings.json` using the existing manual override pattern.
9. Build with full Visual Studio MSBuild, not `dotnet build`.

Preferred verification command:

```powershell
& "C:\Program Files\Microsoft Visual Studio\18\Insiders\MSBuild\Current\Bin\MSBuild.exe" CodexVsix\CodexVsix.csproj /t:Build /p:Configuration=Release /p:BuildVsixPackage=true
```

Fallback if VS 18 Insiders is unavailable:

```powershell
& "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe" CodexVsix\CodexVsix.csproj /t:Build /p:Configuration=Release /p:BuildVsixPackage=true
```

Final hygiene for each task:

```powershell
git diff --check
```

When behavior affects the extension UI, the final implementation note must advise debugging with `F5` to launch the Visual Studio Experimental Instance.

## Task Board

| Priority | Task ID | Status | Plan |
| --- | --- | --- | --- |
| P0 | `CFP-001` | Todo | [Slash Commands](./CFP-001.md) |
| P1 | `CFP-002` | Backlog | [Review and Diff Pane](./CFP-002.md) |
| P2 | `CFP-003` | Backlog | [Auto Context and TODO CodeLens](./CFP-003.md) |
| P3 | `CFP-004` | Backlog | [Cloud and Worktree Delegation](./CFP-004.md) |

Allowed status values, per `AGENTS.md`: `Backlog`, `Todo`, `In Progess`, `Done`.

## Shared Design Principles

- Prefer small, composable services under `CodexVsix/Services` and bindable view models under `CodexVsix/ViewModels`.
- Keep UI behavior in `CodexToolWindowControl.xaml` and `CodexToolWindowControl.xaml.cs` thin; route feature decisions through view models or services.
- Reuse existing `DelegateCommand`, localization, settings, thread, MCP, skill, and process-service patterns.
- Do not introduce a second Codex runtime. The extension must continue to rely on the installed Codex CLI and shared Codex configuration.
- Treat Codex feature availability as dynamic. Probe CLI capabilities or degrade gracefully when a command, JSON field, feature flag, or cloud entitlement is missing.
- Prefer app deep links or explicit CLI subcommands over reverse-engineering private state when Codex exposes a supported route.
- All new long-running work must have cancellation, status text, and error reporting in the existing style.

## Source Basis

This roadmap is based on current Codex documentation sections for:

- IDE extension slash commands and commands
- Codex app review/diff workflow
- IDE context and TODO implementation commands
- Cloud delegation and worktree modes

Implementation agents should refresh current Codex docs before changing CLI integrations if command names or JSON contracts appear to have drifted.
