# Changelog

## Unreleased

### Added

- Added a themed slash-command palette with click and Tab completion for conversation, review, model, permissions, IDE-context, status, skills, MCP, and app actions.
- Added native app-server support for thread fork, compact, and permanent delete operations.
- Added native inline reviews for uncommitted changes, base branches, commits, and custom review instructions.
- Added completed-turn diff summaries from `turn/diff/updated` notifications.

### Changed

- Updated skill invocation and skill chips from the obsolete `/skill-name` form to Codex's current `$skill-name` syntax.
- Updated thread lifecycle notification handling to the current app-server method names.

### Fixed

- Fixed the history Delete action so it permanently deletes a thread instead of merely archiving it.
- Fixed selected skills being displayed as chips but silently omitted from the app-server turn input.
- Removed handling for stale thread notification aliases that are no longer emitted by the current protocol.

## 1.2.1 - 2026-05-01

### Changed

- Updated the VSIX manifests shipped with the extension package.
- Refreshed the packaged extension metadata so the published release carries the new manifest content.

## 1.2.0 - 2026-05-01

### Added

- Inline rename for saved conversations in the history lists, preserving the original Codex/GPT thread ID so existing context resumes normally.
- Keyboard shortcut registration for the main `View.VisualCodexStudio` command, with the command exposed through Visual Studio keyboard settings.
- Shared history item template across recent, visible, and full history lists to keep rename and delete actions consistent.

### Changed

- Renamed the extension branding to `Visual Codex Studio`.
- Updated the extension icon used by the VSIX package, installed extensions list, and Marketplace metadata.
- Updated the `View > Codex` command icon to use the custom ChatGPT/Codex visual asset.
- Refined the history UX so saved conversations can carry meaningful labels without affecting their backing thread identity.
- Reworked chat message presentation to use a virtualized list surface instead of rendering the whole conversation in a single stacked panel.
- Buffered assistant output updates and switched large message list refreshes to batch operations to reduce UI churn in long conversations.

### Fixed

- Restored the rate-limit usage indicator as a proper donut chart instead of a stretched shape.
- Fixed the thread rename UI so it no longer breaks tool window loading at runtime.
- Reduced chat layout jumps during interactions such as copying content while keeping markdown rendering and collapse/expand behavior intact.
