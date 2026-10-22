> [!WARNING]
> **This tool was Vibecoded.** The code, tests and documentation were written by Claude
> (an AI model by Anthropic) and reviewed and tested only as described below. Use it at your own
> risk and keep backups of your saves.

# Azimuth Save Importer

Brings your **Forza Horizon 2 Xbox saves** into **Azimuth** on PC: your profile and progress,
liveries, tuning setups, photos and garage thumbnails.

It is the second half of a two-step move:

1. **[XBsync](https://github.com/d3fekt/xbsync)** downloads your saves from the Xbox cloud.
2. **Azimuth Save Importer** converts that download into Azimuth's save format and puts it in place,
   with a backup first.

Simply copying the Xbox files into Azimuth's save folder does not work (the game reports
*"storage full"*): Azimuth keeps every save container in its own `revisions` folder with a
`commit` manifest that lists the SHA-256 and size of each file. This tool writes exactly that.

## Download

Get `AzimuthSaveImporter.exe` from [Releases](../../releases). It is a single file, nothing to install.
Requires Windows 10 or 11 (it runs on the .NET Framework 4.8 that comes with Windows).

## How to use it

1. **Download your saves with **[XBsync](https://github.com/d3fekt/xbsync)**.** Sign in, pick *Forza Horizon 2*, then use *Download all*
   (a folder) or *Download ZIP*.
2. **Start the game in Azimuth once, let it save, and close it.** The importer checks its output
   against the saves Azimuth wrote itself before it changes anything (see *Safety*), so it needs
   at least one.
3. **Run `AzimuthSaveImporter.exe`.**
   - *Xbox saves*: pick the XBsync folder or ZIP.
   - *Azimuth saves*: pick the folder Azimuth is installed in. The save folder
     (`data\titles\<title>\saves\player-1`) is found automatically.
   - *Review* lists every item and what will happen to it.
4. **Click _Import into Azimuth_.** If Azimuth already has saves, you choose:
   - **Back up and replace everything** (recommended for a clean move from Xbox), or
   - **Back up and merge**: replace only items with the same name and keep the rest.
5. Start the game and check your profile, liveries and photos.

Backups go to `Documents\Azimuth Save Importer\Backups` (you can change this). **Restore a backup...**
puts any of them back, after backing up the current state first.

## What it does

| Xbox (XBsync download)                         | Azimuth                                                       |
| ---------------------------------------------- | ------------------------------------------------------------- |
| `User_<XUID>,savedgame\*` (profile)            | `User_9000000000000\revisions\0000000000000001\data\*`, or the name of the profile Azimuth already has |
| `Livery_0289_20150618180709,savedgame\*`       | `Livery_0289_20150618180709\revisions\0000000000000001\data\*` |
| ... every other container the same way         |                                                               |
| `TransactionLogFile` (Xbox only)               | left out                                                      |

Every revision gets a `commit` file:

```
Azimuth complete save revision v2
blobs <count>
<sha256> <size> <name>        (one line per file, ordinal name order)
```

File contents are copied byte for byte; nothing inside the saves is modified.

## Safety

- **Format check.** Before importing, the tool regenerates the `commit` of every save Azimuth
  already wrote and compares it byte for byte. If any differs (for example because a newer
  Azimuth changed its format), it refuses to import and changes nothing.
- **Backup first.** The whole player folder is zipped, and the ZIP is checked file by file,
  before anything is replaced.
- **Staged and verified.** Everything is written to a temporary folder next to the save folder
  and verified, then moved into place and verified again.
- **Automatic rollback.** If anything fails while saves are being replaced, the backup is
  restored automatically.
- **The game should be closed.** The tool warns if it looks like the game is running.

## Known limitations

- The save format was worked out from saves Azimuth wrote, not from Azimuth's code. The format
  check above guards against changes, but treat this as a community tool and keep your backups.
- Tested with Forza Horizon 2 only.
- Liveries, tunes and photos keep the Xbox owner (your gamertag) in their headers; they are not
  rewritten. If an item does not show up in the game, that is the most likely reason. Please open
  an issue.
- Photos are numbered (`Photo_0000_...`). If Azimuth already has a photo with the same number,
  choose *replace everything* or delete that photo, or one of them may not show up.

## Command line

The same exe works from a terminal:

```
AzimuthSaveImporter import  --source <folder|zip> --target <folder> [--mode replace|merge] [--dry-run] [--yes]
AzimuthSaveImporter verify  --target <folder>
AzimuthSaveImporter restore --backup <zip> --target <folder> [--yes]
AzimuthSaveImporter --help
```

## Building

The release build uses Mono's C# compiler, so it builds on Linux (this is what CI does):

```
sudo apt-get install mono-devel
./build.sh                # dist/AzimuthSaveImporter.exe
./tests/run-tests.sh      # end-to-end tests of the command line
```

On Windows you can also open `AzimuthSaveImporter.csproj` in Visual Studio 2022 or run
`dotnet build -c Release`.

Pushing a tag such as `v1.0.0` builds the exe and attaches it to a GitHub release.

## Credits and license

- [XBsync](https://github.com/d3fekt/xbsync) by d3fekt does the Xbox download. It is a separate
  project (GPL-3.0); no code from it is included here.
- Not affiliated with Microsoft, Xbox, Playground Games or the Azimuth project.

MIT License, see [LICENSE](LICENSE).
