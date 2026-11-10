#!/usr/bin/env bash
set -euo pipefail

# Run from the checkout so SDK selection and relative app arguments are consistent.
repo_root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
cd -- "$repo_root"

printf '%s\n' 'Launching SourceGit (Debug). Quit any running SourceGit instance first to load this build.'
exec dotnet run --project "$repo_root/src/SourceGit.csproj" --configuration Debug "$@"
