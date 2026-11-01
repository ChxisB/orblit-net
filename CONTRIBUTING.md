# Contributing

Multiplayer for Orblit.

Talk about anything large first, in an issue or on the
[Discord](https://discord.gg/5DH7HuDUtJ). Small fixes need no ceremony: just
open the pull request. The engine itself lives in
[ChxisB/orblit](https://github.com/ChxisB/orblit), and its
[CONTRIBUTING](https://github.com/ChxisB/orblit/blob/main/CONTRIBUTING.md) has
the longer version of this.

## Checking your work

```sh
git clone https://github.com/ChxisB/orblit.git   # beside this one
orblit/tool/pack_local.sh                          # packs the engine into ~/.orblit/feed
./tool/check_dotnet.sh
```

`check_dotnet.sh` builds, tests and checks the format. It needs the .NET 10
SDK. `ORBLIT_FEED` names another feed folder.

## Licence

This repository is under MPL-2.0. Opening a pull request means you are
offering your change under that same licence, and that you wrote it or
otherwise have the right to contribute it.

There is no CLA to sign and no copyright to assign. You keep the copyright on
what you write. It is simply licensed the same way as the rest of the
repository.
