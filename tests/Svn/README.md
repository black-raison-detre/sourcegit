# SVN backend regression checks

Run the portable regression checks from the repository root:

```sh
dotnet run --project tests/Svn/SourceGit.Svn.Regression.csproj
```

The project has no package dependencies. It compiles the production `Svn.cs`,
`Svn.Connection.cs`, `Svn.History.cs`, `Svn.ChangedPaths.cs`,
`Svn.FileDiff.cs`, `Svn.Status.cs`, `Svn.Actions.cs`, and `Svn.Undo.cs` files directly and uses its own apphost as a fake SVN CLI, so
the focused checks do not need Subversion installed.

The fake-client checks cover working-copy root discovery and Git precedence,
local XML info, safe argument handling for paths with spaces and `@`,
copy-aware paged history with revision gaps, multiline Unicode messages,
missing author/date values, scoped selected-revision paths and ancestor copies,
malformed/DTD/oversized XML, cancellation and child termination,
authentication retry, password redaction, transport rejection, and auth-cache
safeguards. File-snapshot checks cover change-action endpoints, copy baselines,
escaped paths, empty versus absent files, binary and special metadata, unknown
deleted kinds, output limits, and cancellation. They also verify that a changed
working-copy URL prevents retained credentials from reaching repository queries.
Metadata-cache checks cover persistent history and selected-path results,
working-copy UUID/BASE, config and username identity separation, password
omission, expiry, corruption recovery, force refresh, and refresh-failure
invalidation. Cache tests use a private temporary directory and fake SVN client.

Working-copy checks cover local XML status, property and changelist rows, copied,
switched, moved, tree-conflict, and file-external flags, filtering unchanged rows,
and rejecting malformed, DTD, oversized, or cancelled status output. Local diff
checks verify bounded internal diffs, special-character paths, property-only
output, copied additions, binary and unversioned content, missing-file BASE
snapshots, symlink boundary checks, FIFO rejection, cancellation, and that the
commands do not request remote status or revisions.

Local action checks cover conservative Add/Remove eligibility, fresh status
revalidation, option-safe paths with spaces, Unicode, `@`, and leading hyphens,
non-recursive directory adds, local-content preservation for modified and
missing removals, and rejection of stale, property-edited, traversing, symlinked,
and nested-working-copy selections. Undo checks cover plain added/deleted rows,
automatic-property metadata on additions, missing deleted paths, safe revert
arguments, and preservation of retained bytes when a failed or cancelled fake
revert first recreates the target. Recovery checks verify that unexpected
temporary data is retained with an error, and that a post-revert symlink leaves
the original bytes and recovery manifest available with their paths reported for
failure and cancellation. Symlink-specific checks run where the test filesystem
supports symlinks. Fake command cancellation checks also verify child termination.

Local text diffs use SVN's internal diff engine and bounded UTF-8 patches;
invalid UTF-8 or NUL-containing patches are reported as binary. Whitespace
preferences use SVN's `-b` extension, which can still show blank-line-only
changes.

The fake-client checks verify that a blank config override omits `--config-dir`
and that unremembered credentials use `--no-auth-cache`. Remembering
credentials without an explicit config directory must fail before starting
SVN. With an explicit directory, the checks verify CLI overrides for the
global and matching server group while confirming the directory's `servers`
file, group mapping, proxy settings, and TLS settings remain unchanged.

These checks validate command arguments and file preservation; they do not
prove native registry/system configuration layering or persistence to an OS
credential provider. Integration mode uses an isolated config with password
stores disabled, so it verifies authentication and non-persistence for
`Remember=false`, not native credential-provider writes for `Remember=true`.

To also exercise an actual authenticated local `svnserve`:

```sh
dotnet run --project tests/Svn/SourceGit.Svn.Regression.csproj -- --integration
```

Integration mode is opt-in and fails with a nonzero exit code if `svn`,
`svnadmin`, or `svnserve` is unavailable, if the SVN client lacks
`--password-from-stdin`, or if a server/repository operation fails. It creates
temporary repositories and an isolated Subversion config directory, binds
only to `127.0.0.1`, disables password stores, and removes only the temporary
directory it created. The client config directory has a Unicode name to catch
locale-dependent path handling. Its private repository contains an unrelated
revision between relevant file changes, then renames the file and copies its
parent directory to a new path with spaces and `@`. The production APIs
authenticate to local `svnserve`, read both history pages with the full
multiline Unicode messages, resolve changed paths through the copied scope,
and load authenticated before/after snapshots for modified, copied, empty, and
binary files, including names with Unicode, `%`, `@`, and backslash. The checks
preserve an existing uncommitted edit and status. Snapshots read `svn cat`
output as bounded bytes and disable keyword expansion; SVN still applies
`svn:eol-style` translation, so text snapshots with that property reflect the
client's EOL form rather than the repository's stored line-ending bytes. No
user Subversion cache or credentials are read or modified.

Integration mode also creates a separate temporary `file://` repository and
working copy to exercise actual local adds and deletes. It checks Unicode and
`@` paths, configured auto-properties, depth-empty directory adds, preservation
of modified and nested file bytes, removal of missing files and directories, and
directory-removal refusal when a missing child has modified properties or is a
file external. It also exercises Undo Add/Remove against the real local
repository, including an added child beneath an added directory, a retained
modified directory with an unversioned child, a nested symlink when supported,
and restoring the BASE file after undoing a missing-file deletion. Action
revalidation reads external boundary rows while the normal working-copy status
view continues to omit them. The
working-copy read-only checks stop the local `svnserve` before reading status or
local diffs; they verify an existing local edit, an unversioned file, and a
missing tracked file without contacting the server or changing working-copy
contents or status.
