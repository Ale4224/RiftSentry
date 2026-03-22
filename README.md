# RiftSentry

Windows overlay for **manually** tracking enemy **summoner spell** cooldowns in League of Legends. It reads match context from Riot’s **Live Client Data API** (localhost) and spell metadata from **Data Dragon**. Timers start only when **you** click a spell button.

## Requirements

- Windows
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- League of Legends client running; during a match the Live Client API must be available on `https://127.0.0.1:2999`

## Build

```bash
dotnet build RiftSentry.sln -c Release
```

## Run

```bash
dotnet run --project src/RiftSentry/RiftSentry.csproj
```

Or run `RiftSentry.exe` from `src/RiftSentry/bin/Release/net8.0-windows/` after a Release build.

## GitHub release

Push a version tag (for example `v1.0.0`). The [Release](.github/workflows/release.yml) workflow builds a self-contained `win-x64` app, zips it, and attaches `RiftSentry-win-x64.zip` to the GitHub Release for that tag.

## How it works

- **Live Client** (`/liveclientdata/allgamedata`, polled every 2 seconds): detects your team, lists enemy champions, items (Ionian Boots), and summoner spell slots.
- **Data Dragon**: loads the current patch from `versions.json`, then `summoner.json` and `champion.json` for cooldowns and icons.
- **Assets**: downloaded spell and champion PNGs are cached under `assets` next to the executable (`AppContext.BaseDirectory`).
- **Overlay**: borderless, transparent, topmost WPF window. It **shows only while a match is detected**; otherwise the window stays hidden. A **system tray** icon is always available; right-click **Exit** to quit.
- **Haste**: Ionian Boots (+12) is read from items. Cosmic Insight (+18) is detected when the Live Client exposes full enemy rune data; otherwise use the **C** toggle per row.
- **Cooldown formula**: `baseCooldown * (100 / (100 + totalSummonerSpellHaste))`.

## Manual tracking

RiftSentry does **not** detect spell casts automatically. You click a spell when you see it used; click again during the timer to reset.

## Application icon

Place `app.ico` next to [`src/RiftSentry/RiftSentry.csproj`](src/RiftSentry/RiftSentry.csproj). The project references it via `<ApplicationIcon>app.ico</ApplicationIcon>`.

## Compliance note

Use only supported interfaces (Live Client API + public static data). This tool is intended as a manual aid similar to noting timers yourself. Follow Riot’s current policies for third-party tools.
