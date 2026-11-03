# SVN backend regression checks

Run the portable regression checks from the repository root:

```sh
dotnet run --project tests/Svn/SourceGit.Svn.Regression.csproj
```

The project has no package dependencies. It compiles the production `Svn.cs`
and `Svn.Connection.cs` files directly and uses its own apphost as a fake SVN
CLI, so the focused checks do not need Subversion installed.

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
locale-dependent path handling. No user Subversion cache or credentials are
read or modified.
