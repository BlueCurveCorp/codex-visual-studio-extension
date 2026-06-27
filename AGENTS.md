# Visual Codex Studio - Agent Guidelines

## Project Overview

This project, **Visual Codex Studio**, is a Visual Studio 2022 and 2026 extension (`CodexVsix`) that integrates a docked Codex AI chat window directly into the IDE. It relies on a local Codex CLI installation rather than bundling its own runtime, allowing developers to keep their interactions within Visual Studio while reusing their existing Codex configurations (e.g., `~/.codex/config.toml`, `OPENAI_API_KEY`).

Key features include:

- A chat-style Codex panel with Normal and Plan modes.
- Support for selecting models, reasoning effort (`minimal` to `xhigh`), and verbosity.
- Solution-aware `@file` search while typing.
- Image attachments from clipboard or files (relies on `--image` support in the Codex CLI).
- Local settings persistence in `%LOCALAPPDATA%\CodexVsix\settings.json`.

## Important Details & Constraints

- **Building the Project**: Do NOT use `dotnet build` to create the VSIX package. The project must be compiled using the full Visual Studio MSBuild.
  Example:
  ```powershell
  & "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe" CodexVsix\CodexVsix.csproj /t:Build /p:Configuration=Release /p:BuildVsixPackage=true
  ```
- **Local Settings**: The extension stores runtime configurations in `%LOCALAPPDATA%\CodexVsix\settings.json`. Manual future-proofing handles extra models or values via keys like `CustomModels` or `CustomReasoningEfforts`.
- **UI & Theming**: The extension supports VS light/dark themes natively using Visual Studio theme resources. UI strings are localized.

## Relevant Documentation

- [Create your first Visual Studio extension (VisualStudio.Extensibility)](https://learn.microsoft.com/en-us/visualstudio/extensibility/visualstudio.extensibility/get-started/create-your-first-extension?view=visualstudio)
  - *Note*: This guide explains the modern out-of-process VisualStudio.Extensibility model (using `ExtensionEntrypoint`, `InitializeServices`, and `[VisualStudioContribution]`). Use this as a reference for modern extension components, dependency injection, command handling (`ExecuteCommandAsync`), and prompts (`ShowPromptAsync`).

## Agent Instructions

- **Modifying UI**: Always ensure Visual Studio theme resources are respected to support both light and dark themes. Do not hardcode colors.
- **Modifying Settings**: If adding a new setting, ensure it is serialized to the local settings JSON and respects the manual override format.
- **Debugging**: When making changes to extension commands or UI components, advise the user to debug by pressing `F5` to build and launch the Visual Studio Experimental Instance.

## Progress tracking

- **ALWAYS** write a detailed plan before proceeding in a directory named `./plans/<GLOBAL_TASK_NAME>`
- **ALWAYS** each task is prioritized and have a unique identifier and a link to the target `./plans/<GLOBAL_TASK_NAME>/<TASK_ID>.md`
- **ALWAYS** keep track of your work for each task using a memory file `./plans/<GLOBAL_TASK_NAME>/<TASK_ID>.md`
- **ALWAYS** update the task status **Backlog** → **Todo** → **In Progess** → **Done**

## Changelog

- **ALWAYS** update the `CHANGELOG` and `Readme.md` files