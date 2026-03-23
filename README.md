# RiftSentry

Windows overlay for **manually** tracking enemy **summoner spell** cooldowns in League of Legends. It reads match context from Riot’s **Live Client Data API** (localhost) and spell metadata from **Data Dragon**. Timers start only when **you** click a spell button.

The app also supports an optional **sync lobby** mode. Players can point the overlay at a self-hosted sync server, create a lobby with a 6-digit code, or join an existing lobby to share cooldown and Cosmic Insight state in real time.

## Requirements

- Windows
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- League of Legends client running; during a match the Live Client API must be available on `https://127.0.0.1:2999`

## Build

```bash
dotnet build RiftSentry.sln -c Release
```

## Build sync server

```bash
dotnet build src/RiftSentry.SyncServer/RiftSentry.SyncServer.csproj -c Release
```

## Run

```bash
dotnet run --project src/RiftSentry/RiftSentry.csproj
```

Or run `RiftSentry.exe` from `src/RiftSentry/bin/Release/net8.0-windows/` after a Release build.

## Run sync server locally

```bash
dotnet run --project src/RiftSentry.SyncServer/RiftSentry.SyncServer.csproj
```

By default the SignalR hub is exposed at `http://localhost:5012/syncHub` when running from `dotnet run`, or at `http://localhost:8080/syncHub` when running in Docker.

Health endpoint:

```text
GET /health
```

## Run sync server with Docker

```bash
docker compose up --build -d
```

This starts the self-hosted sync server on port `8080`.

## GitHub release

Push a version tag (for example `v1.0.0`). The [Release](.github/workflows/release.yml) workflow attaches two zips:

- **`RiftSentry-win-x64-self-contained.zip`** — single `.exe`, no .NET install. Smaller than an uncompressed self-contained build thanks to **compressed single-file** and **invariant globalization** (no bundled ICU data). If you need full culture-specific formatting for every locale, build locally without `-p:InvariantGlobalization=true`.
- **`RiftSentry-win-x64-framework-dependent.zip`** — tiny footprint; requires the [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0) (`Windows x64` under **Run desktop apps**).

The large size you see on a plain self-contained publish is mostly the **.NET runtime** embedded in the app. **Publish trimming** is not a good fit for WPF in most cases.

## How it works

- **Live Client** (`/liveclientdata/allgamedata`, polled every 2 seconds): detects your team, lists enemy champions, items (Ionian Boots), and summoner spell slots.
- **Data Dragon**: loads the current patch from `versions.json`, then `summoner.json` and `champion.json` for cooldowns and icons.
- **Assets**: downloaded spell and champion PNGs are cached under `assets` next to the executable (`AppContext.BaseDirectory`).
- **Overlay**: borderless, transparent, topmost WPF window. It **shows only while a match is detected**; otherwise the window stays hidden. A **system tray** icon is always available; right-click **Exit** to quit.
- **Settings**: a tray **Settings** window lets you save the sync server URL, create a lobby, join by 6-digit code, and leave the current lobby.
- **Haste**: Ionian Boots (+12) is read from items. Cosmic Insight (+18) is detected when the Live Client exposes full enemy rune data; otherwise use the **C** toggle per row.
- **Cooldown formula**: `baseCooldown * (100 / (100 + totalSummonerSpellHaste))`.

## Sync lobbies

- The saved **server URL** is persisted under the current Windows user profile.
- The active **lobby code** is never persisted.
- The lobby owner creates a room and shares the generated 6-digit code.
- Other players join with the same server URL and code.
- Spell cooldown clicks and manual Cosmic Insight toggles are broadcast to every connected player in that lobby.
- Late joiners receive the current synced state, including active cooldown timers.
- If a player is no longer in the same game as the lobby owner, the sync server disconnects that player from the lobby.
- If the owner leaves the game or disconnects, the lobby closes for everyone.

## Manual tracking

RiftSentry does **not** detect spell casts automatically. You click a spell when you see it used; click again during the timer to reset.

## Application icon

Place `app.ico` next to [`src/RiftSentry/RiftSentry.csproj`](src/RiftSentry/RiftSentry.csproj). The project references it via `<ApplicationIcon>app.ico</ApplicationIcon>`.

## Compliance note

Use only supported interfaces (Live Client API + public static data). This tool is intended as a manual aid similar to noting timers yourself. Follow Riot’s current policies for third-party tools.
