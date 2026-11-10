# Repository Guidelines

## Project Structure & Module Organization

SourceGit is a cross-platform Git GUI built with C# and Avalonia on .NET 10. Application code lives in `src/`: `Models/` holds data types, `ViewModels/` and `Views/` implement the UI, `Commands/` wraps Git operations, and `Native/` handles platform integration. Resources and locale AXAML files live in `src/Resources/`. The editor dependency is under `depends/AvaloniaEdit`; packaging scripts and assets are under `build/`, and screenshots are in `screenshots/`.

## Build, Test, and Development Commands

Install the .NET 10 SDK and Git 2.25.1 or newer. Run commands from the repository root:

- `git submodule update --init --recursive` — populate the required AvaloniaEdit dependency before building.
- `dotnet build -c Release` — compile the solution, matching CI's build check.
- `dotnet run --project src/SourceGit.csproj` — launch the app locally in Debug mode.
- `dotnet format --verify-no-changes src/SourceGit.csproj` — check formatting, matching CI.
- `dotnet publish -c Release -r osx-arm64 -o ./publish src/SourceGit.csproj` — publish with Native AOT. See `build/README.md` for other supported runtime identifiers.

On macOS, Native AOT publishing requires Xcode Command Line Tools or full Xcode. The separate `.app` packaging script uses `clang`, `codesign`, and `zip`.

Without local tags, the version step may report `fatal: No names found, cannot describe anything.` This does not fail the build; check the final build result.

## Coding Style & Naming Conventions

Follow `.editorconfig`: four spaces, final newlines, C# opening braces on new lines, and preferred `var` usage. Use PascalCase constants, `s_camelCase` private static fields, and `_camelCase` private/internal instance fields. Match namespaces to folders and follow neighboring C# and AXAML patterns. Keep formatting changes scoped to the files you touch.

## Testing Guidelines

No automated test project or coverage threshold is currently defined. Run the Release build and formatting check, then manually exercise affected workflows against a disposable Git repository. For UI changes, check light/dark themes and record the platform and verification steps. Report compilation, AOT publishing, `.app` packaging, and GUI testing separately; a successful build proves compilation only.

## Commit & Pull Request Guidelines

Follow observed subject prefixes such as `fix:`, `feature:`, `refactor:`, `doc:`, and `code_style:`. Keep commits focused. Codex-authored commits must end with a blank line followed by `Co-authored-by: Codex <noreply@openai.com>`.

In pull requests, describe the problem and resulting behavior, link relevant issues, report validation, and include screenshots for visible UI changes. Call out platform-specific behavior and localization changes.
