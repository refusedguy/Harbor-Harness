# Upgrading Harbor

> How to move an existing install to a newer Harbor without losing config or plugins.

Harbor has no installer and no `self-update` command. The update path depends
on the lane you installed from: a source checkout, a tagged release archive,
or a beta artifact. This page lists the commands for each lane. For the
difference between the lanes and where their artifacts live, see
[Daily and pre-release builds](./DAILY_BUILDS.md).

## Check what you have

```bash
harbor version
```

The command prints the Harbor version and the .NET runtime version. In a
source checkout, add `git rev-parse --short HEAD` to name the exact commit.

## Choose your lane

| Installed from | Update command | What you get |
| --- | --- | --- |
| Source checkout | `git pull --ff-only && dotnet build -c Release` | the tip of `dev` |
| Pinned source tag | `git fetch --tags && git checkout <tag> && dotnet build -c Release` | an exact release |
| Tagged release archive | download `harbor-<rid>` from the release lane and replace the old files | a reproducible version |
| Beta artifact | download `harbor-cli-linux-x64` from the beta lane and unpack it over the old directory | the newest `dev` build |

## Source checkout

Pull the branch you are on and rebuild. A plain `dotnet build` is enough for
daily use; `-c Release` matches what the release lane publishes.

```bash
cd Harbor-Harness
git pull --ff-only
dotnet build -c Release
harbor version
```

To pin the checkout to a release instead of tracking `dev`, fetch the tags and
check out one before building:

```bash
git fetch --tags
git checkout <tag>
dotnet build -c Release
```

`git pull` leaves `~/.harbor/config.json` and `~/.harbor/plugins/` untouched.
See [Getting started](./GETTING_STARTED.md) for the first build and
[Build & Publish Guide](./BUILD.md) for publish options.

## Tagged release archive

Release archives are named `harbor-<rid>.tar.gz` or `harbor-<rid>.zip`. They
carry the CLI together with the `providers/` directory next to the executable,
so replace the whole install directory rather than a single binary.

```bash
# Linux and macOS: unpack over the existing install directory
tar -xzf harbor-linux-x64.tar.gz -C /path/to/harbor

# Windows: Expand-Archive -Path harbor-win-x64.zip -DestinationPath C:\harbor -Force

harbor version
```

Keep the previous directory until the new binary passes the version check.
Tagged builds are the only lane with a reproducibility guarantee; use them
when a plugin or a script must see a stable CLI.

## Beta artifact

The beta lane publishes a self-contained `linux-x64` binary on every merge to
`dev`, with a 14-day retention. It is the closest thing to a nightly CLI, but
it tracks `dev` and can break between merges.

```bash
gh run download -R refusedguy/Harbor-Harness -n harbor-cli-linux-x64
sha256sum -c SHA256SUMS.txt
tar -xzf harbor-cli-dev-*-linux-x64.tar.gz -C /path/to/harbor
./Harbor.App.Cli version
```

The checksum file sits next to the tarball in the downloaded artifact. Verify
it before running the binary. A manual `gh run download` grabs the newest
successful beta run; open the
[beta workflow](https://github.com/refusedguy/Harbor-Harness/actions/workflows/beta.yml)
to pick an older one.

## Config and plugins survive an upgrade

Configuration, plugins, and logs live under `~/.harbor/`, outside the install
directory. Updating the binary does not rewrite them. CS-source plugins are
compiled at startup and cached by source hash, so a plugin picks up changes
when its source changes or when its cache entry is invalidated. See
[Plugin development](./PLUGIN_DEVELOPMENT.md) for the plugin paths and the
DLL-versus-CS tradeoff. Environment variables are unchanged; the full list is
in [Environment variables](./ENV_VARS.md).

## Roll back

For a source checkout, check out the previous tag or commit and rebuild:

```bash
git checkout <previous-tag>
dotnet build -c Release
```

For an archive install, keep the previous install directory and point `PATH`
back at it. There is no downgrade flag in the CLI itself.

## See also

- [Getting started](./GETTING_STARTED.md) — first install and first prompt.
- [Daily and pre-release builds](./DAILY_BUILDS.md) — what each lane produces.
- [Build & Publish Guide](./BUILD.md) — building from source and publishing.
- [Plugin development](./PLUGIN_DEVELOPMENT.md) — plugin compatibility notes.
