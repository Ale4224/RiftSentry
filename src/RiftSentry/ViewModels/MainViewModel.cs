using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using RiftSentry.Commands;
using RiftSentry.Models;
using RiftSentry.Services;
using RiftSentry.SyncContracts;

namespace RiftSentry.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private const string StaticSyncFingerprint = "RIFTSENTRY_STATIC_FINGERPRINT";
    private readonly DataDragonService _ddragon;
    private readonly AssetCacheService _assets;
    private readonly LiveClientService _liveClient;
    private readonly AppSettingsService _settingsService;
    private readonly SyncClientService _syncClient;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _uiTimer;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<(string RosterKey, SpellSlotNumber Slot), SpellStateDto> _syncedSpellStates = new();
    private readonly Dictionary<string, bool> _syncedCosmicStates = new(StringComparer.Ordinal);
    private GameSnapshot? _currentSnapshot;
    private DateTime _lastHeartbeatUtc = DateTime.MinValue;
    private bool _isInGame;
    private bool _isLobbyConnected;
    private bool _isLobbyOwner;
    private string _statusMessage = "";
    private string _serverUrl = "";
    private string _joinLobbyCodeInput = "";
    private string _currentLobbyCode = "";
    private string _syncStatusMessage = "";
    private bool _isLobbyBusy;

    public MainViewModel(
        DataDragonService ddragon,
        AssetCacheService assets,
        LiveClientService liveClient,
        AppSettingsService settingsService,
        SyncClientService syncClient,
        Dispatcher dispatcher)
    {
        _ddragon = ddragon;
        _assets = assets;
        _liveClient = liveClient;
        _settingsService = settingsService;
        _syncClient = syncClient;
        _dispatcher = dispatcher;
        Enemies = new ObservableCollection<EnemyChampionViewModel>();
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => TickUi();
        SaveServerUrlCommand = new RelayCommand(_ => _ = SaveServerUrlAsync());
        CreateLobbyCommand = new RelayCommand(_ => _ = CreateLobbyAsync());
        JoinLobbyCommand = new RelayCommand(_ => _ = JoinLobbyAsync());
        LeaveLobbyCommand = new RelayCommand(_ => _ = LeaveLobbyAsync());
        _syncClient.LobbySnapshotReceived += snapshot => _ = _dispatcher.InvokeAsync(() => ApplyLobbySnapshot(snapshot));
        _syncClient.SpellCooldownChanged += state => _ = _dispatcher.InvokeAsync(() => ApplySpellState(state));
        _syncClient.CosmicStateChanged += state => _ = _dispatcher.InvokeAsync(() => ApplyCosmicState(state));
        _syncClient.Disconnected += message => _ = _dispatcher.InvokeAsync(() => ClearLobbyState(message.Reason));
    }

    public ObservableCollection<EnemyChampionViewModel> Enemies { get; }

    public RelayCommand SaveServerUrlCommand { get; }

    public RelayCommand CreateLobbyCommand { get; }

    public RelayCommand JoinLobbyCommand { get; }

    public RelayCommand LeaveLobbyCommand { get; }

    public bool IsInGame
    {
        get => _isInGame;
        private set => SetProperty(ref _isInGame, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetProperty(ref _statusMessage, value);
    }

    public string ServerUrl
    {
        get => _serverUrl;
        set => SetProperty(ref _serverUrl, value);
    }

    public string JoinLobbyCodeInput
    {
        get => _joinLobbyCodeInput;
        set
        {
            var sanitized = new string((value ?? "").Where(char.IsDigit).Take(6).ToArray());
            SetProperty(ref _joinLobbyCodeInput, sanitized);
        }
    }

    public bool IsLobbyConnected
    {
        get => _isLobbyConnected;
        private set
        {
            if (_isLobbyConnected == value)
                return;

            _isLobbyConnected = value;
            OnPropertyChanged(nameof(IsLobbyConnected));
            OnPropertyChanged(nameof(SyncConnectionText));
            NotifyLobbyCommands();
        }
    }

    public bool IsLobbyOwner
    {
        get => _isLobbyOwner;
        private set
        {
            if (_isLobbyOwner == value)
                return;

            _isLobbyOwner = value;
            OnPropertyChanged(nameof(IsLobbyOwner));
            OnPropertyChanged(nameof(SyncConnectionText));
        }
    }

    public string CurrentLobbyCode
    {
        get => _currentLobbyCode;
        private set
        {
            if (_currentLobbyCode == value)
                return;

            _currentLobbyCode = value;
            OnPropertyChanged(nameof(CurrentLobbyCode));
            OnPropertyChanged(nameof(CurrentLobbyCodeText));
        }
    }

    public string CurrentLobbyCodeText => string.IsNullOrWhiteSpace(CurrentLobbyCode) ? "Lobby: not connected" : $"Lobby: {CurrentLobbyCode}";

    public string SyncConnectionText => !IsLobbyConnected ? "Not connected" : IsLobbyOwner ? "Hosting sync lobby" : "Connected to sync lobby";

    public string SyncStatusMessage
    {
        get => _syncStatusMessage;
        private set => SetProperty(ref _syncStatusMessage, value);
    }

    public bool IsLobbyBusy
    {
        get => _isLobbyBusy;
        private set
        {
            if (_isLobbyBusy == value)
                return;

            _isLobbyBusy = value;
            OnPropertyChanged(nameof(IsLobbyBusy));
            NotifyLobbyCommands();
        }
    }

    public bool CanCreateLobby => !IsLobbyBusy && !IsLobbyConnected;

    public bool CanJoinLobby => !IsLobbyBusy && !IsLobbyConnected;

    public bool CanLeaveLobby => !IsLobbyBusy && IsLobbyConnected;

    private void NotifyLobbyCommands()
    {
        OnPropertyChanged(nameof(CanCreateLobby));
        OnPropertyChanged(nameof(CanJoinLobby));
        OnPropertyChanged(nameof(CanLeaveLobby));
    }

    public async Task InitializeAsync()
    {
        var settings = await _settingsService.LoadAsync(_cts.Token).ConfigureAwait(false);
        await _dispatcher.InvokeAsync(() =>
        {
            ServerUrl = settings.ServerUrl;
            SyncStatusMessage = string.IsNullOrWhiteSpace(ServerUrl)
                ? "Set a server URL to create or join a lobby."
                : "Sync server configured. Create or join a lobby at any time.";
        });

        try
        {
            await _ddragon.EnsureLoadedAsync(_cts.Token).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => { StatusMessage = "Ready"; });
        }
        catch
        {
            await _dispatcher.InvokeAsync(() => { StatusMessage = "Data Dragon failed"; });
        }

        _ = Task.Run(() => PollLoopAsync(_cts.Token));
        _uiTimer.Start();
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var snap = await _liveClient.TryGetSnapshotAsync(ct).ConfigureAwait(false);
                await _dispatcher.InvokeAsync(() => ApplySnapshot(snap));
            }
            catch
            {
                await _dispatcher.InvokeAsync(() => ApplySnapshot(null));
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void ApplySnapshot(GameSnapshot? snap)
    {
        _currentSnapshot = snap;
        if (snap == null)
        {
            IsInGame = false;
            Enemies.Clear();
            StatusMessage = "Live Client unavailable";
            return;
        }

        if (!snap.InGame)
        {
            IsInGame = false;
            Enemies.Clear();
            StatusMessage = "Not in game";
            return;
        }

        IsInGame = true;
        StatusMessage = "In game";

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in snap.Enemies)
        {
            seen.Add(p.RosterKey);
            EnemyChampionViewModel? vm = null;
            foreach (var e in Enemies)
            {
                if (e.RosterKey == p.RosterKey)
                {
                    vm = e;
                    break;
                }
            }

            if (vm == null)
            {
                vm = new EnemyChampionViewModel();
                WireEnemy(vm);
                Enemies.Add(vm);
            }

            vm.ApplySnapshot(p);
            ApplySyncedState(vm);
            _ = UpdateEnemyAssetsAsync(vm, p);
        }

        for (var i = Enemies.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Enemies[i].RosterKey))
                Enemies.RemoveAt(i);
        }
    }

    private async Task UpdateEnemyAssetsAsync(EnemyChampionViewModel vm, EnemyPlayerSnapshot snap)
    {
        var version = _ddragon.CurrentVersion;
        if (string.IsNullOrEmpty(version)) return;

        var assetKey =
            $"{version}|{snap.ChampionName}|{snap.SpellOneDisplayName}|{snap.SpellTwoDisplayName}|{snap.SpellOneDataKey}|{snap.SpellTwoDataKey}";
        if (vm.LastSyncedAssetKey == assetKey)
            return;

        var champFile = _ddragon.GetChampionImageFileName(snap.ChampionName);
        if (string.IsNullOrEmpty(champFile)) return;

        var champUrl = $"https://ddragon.leagueoflegends.com/cdn/{version}/img/champion/{champFile}";
        var champLocal = await _assets.GetOrDownloadAsync(champUrl, $"{version}_champion_{champFile}", _cts.Token).ConfigureAwait(false);

        var d1 = _ddragon.ResolveSpell(snap.SpellOneDisplayName, snap.SpellOneDataKey);
        var d2 = _ddragon.ResolveSpell(snap.SpellTwoDisplayName, snap.SpellTwoDataKey);

        var p1 = "";
        var p2 = "";
        if (d1 != null)
        {
            var u1 = $"https://ddragon.leagueoflegends.com/cdn/{version}/img/spell/{d1.ImageFileName}";
            p1 = await _assets.GetOrDownloadAsync(u1, $"{version}_spell_{d1.ImageFileName}", _cts.Token).ConfigureAwait(false);
        }

        if (d2 != null)
        {
            var u2 = $"https://ddragon.leagueoflegends.com/cdn/{version}/img/spell/{d2.ImageFileName}";
            p2 = await _assets.GetOrDownloadAsync(u2, $"{version}_spell_{d2.ImageFileName}", _cts.Token).ConfigureAwait(false);
        }

        var l1 = string.IsNullOrEmpty(snap.SpellOneDisplayName) ? d1?.Name ?? "" : snap.SpellOneDisplayName;
        var l2 = string.IsNullOrEmpty(snap.SpellTwoDisplayName) ? d2?.Name ?? "" : snap.SpellTwoDisplayName;

        await _dispatcher.InvokeAsync(() =>
        {
            vm.LastSyncedAssetKey = assetKey;
            vm.PortraitPath = champLocal;
            vm.SpellOne.ApplyDefinition(d1, p1, l1);
            vm.SpellTwo.ApplyDefinition(d2, p2, l2);
            ApplySyncedState(vm);
        });
    }

    private void TickUi()
    {
        foreach (var e in Enemies)
            e.TickSpells();

        if (!IsLobbyConnected)
            return;

        if ((DateTime.UtcNow - _lastHeartbeatUtc) < TimeSpan.FromSeconds(2))
            return;

        _lastHeartbeatUtc = DateTime.UtcNow;
        _ = _syncClient.SendHeartbeatAsync(StaticSyncFingerprint, true, _cts.Token);
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        _cts.Cancel();
        _cts.Dispose();
        _syncClient.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _liveClient.Dispose();
    }

    private void WireEnemy(EnemyChampionViewModel vm)
    {
        vm.SpellCooldownChanged += (slot, startedAtUtc, endsAtUtc) => OnEnemySpellCooldownChanged(vm, slot, startedAtUtc, endsAtUtc);
        vm.CosmicStateChanged += enabled => OnEnemyCosmicStateChanged(vm, enabled);
    }

    private void ApplyLobbySnapshot(LobbySnapshotDto snapshot, string? statusOverride = null)
    {
        ReplaceSyncedState(snapshot);
        CurrentLobbyCode = snapshot.Code;
        IsLobbyConnected = true;
        SyncStatusMessage = statusOverride ?? $"Connected to lobby {snapshot.Code}.";
        ApplySyncedStateToAllEnemies();
    }

    private void ApplySpellState(SpellStateDto state)
    {
        StoreSpellState(state);
        var vm = FindEnemy(state.RosterKey);
        vm?.ApplySpellCooldown(state.Slot, state.StartedAtUtc, state.EndsAtUtc);
    }

    private void ApplyCosmicState(CosmicStateDto state)
    {
        StoreCosmicState(state);
        var vm = FindEnemy(state.RosterKey);
        vm?.ApplyCosmicState(state.Enabled);
    }

    private void ApplySyncedStateToAllEnemies()
    {
        foreach (var enemy in Enemies)
            ApplySyncedState(enemy);
    }

    private void ApplySyncedState(EnemyChampionViewModel vm)
    {
        if (!IsLobbyConnected)
            return;

        vm.ApplyCosmicState(_syncedCosmicStates.TryGetValue(vm.RosterKey, out var cosmicEnabled) && cosmicEnabled);

        foreach (var slot in new[] { SpellSlotNumber.One, SpellSlotNumber.Two })
        {
            if (_syncedSpellStates.TryGetValue((vm.RosterKey, slot), out var spellState))
                vm.ApplySpellCooldown(slot, spellState.StartedAtUtc, spellState.EndsAtUtc);
            else
                vm.ApplySpellCooldown(slot, null, null);
        }
    }

    private void ReplaceSyncedState(LobbySnapshotDto snapshot)
    {
        _syncedSpellStates.Clear();
        foreach (var state in snapshot.SpellStates)
            StoreSpellState(state);

        _syncedCosmicStates.Clear();
        foreach (var state in snapshot.CosmicStates)
            StoreCosmicState(state);
    }

    private void StoreSpellState(SpellStateDto state)
    {
        var key = (state.RosterKey, state.Slot);
        if (state.StartedAtUtc.HasValue && state.EndsAtUtc.HasValue && state.EndsAtUtc.Value > DateTime.UtcNow)
            _syncedSpellStates[key] = state;
        else
            _syncedSpellStates.Remove(key);
    }

    private void StoreCosmicState(CosmicStateDto state)
    {
        if (state.Enabled)
            _syncedCosmicStates[state.RosterKey] = true;
        else
            _syncedCosmicStates.Remove(state.RosterKey);
    }

    private EnemyChampionViewModel? FindEnemy(string rosterKey)
    {
        foreach (var enemy in Enemies)
        {
            if (enemy.RosterKey == rosterKey)
                return enemy;
        }

        return null;
    }

    private async Task SaveServerUrlAsync()
    {
        var trimmed = (ServerUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            ServerUrl = "";
            await _settingsService.SaveAsync(new AppSettings { ServerUrl = "" }, _cts.Token).ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() => SyncStatusMessage = "Server URL cleared.");
            return;
        }

        if (!TryNormalizeServerUrl(trimmed, out var normalizedServerUrl, out var error))
        {
            SyncStatusMessage = error;
            return;
        }

        ServerUrl = normalizedServerUrl;
        await _settingsService.SaveAsync(new AppSettings { ServerUrl = normalizedServerUrl }, _cts.Token).ConfigureAwait(false);
        await _dispatcher.InvokeAsync(() =>
        {
            SyncStatusMessage = string.IsNullOrWhiteSpace(normalizedServerUrl)
                ? "Server URL cleared."
                : "Server URL saved.";
        });
    }

    private async Task CreateLobbyAsync()
    {
        if (IsLobbyBusy)
            return;

        if (!TryGetSyncContext(out var normalizedServerUrl, out var playerName, out var matchFingerprint, out var error))
        {
            SyncStatusMessage = error;
            return;
        }

        IsLobbyBusy = true;
        try
        {
            await _dispatcher.InvokeAsync(() => SyncStatusMessage = "Creating lobby…");
            await _settingsService.SaveAsync(new AppSettings { ServerUrl = normalizedServerUrl }, _cts.Token).ConfigureAwait(false);
            var response = await _syncClient.CreateLobbyAsync(
                normalizedServerUrl,
                new CreateLobbyRequest(matchFingerprint, playerName, BuildSpellStates(), BuildCosmicStates()),
                _cts.Token).ConfigureAwait(false);

            await _dispatcher.InvokeAsync(() =>
            {
                ServerUrl = normalizedServerUrl;
                IsLobbyOwner = true;
                JoinLobbyCodeInput = response.Code;
                ApplyLobbySnapshot(
                    response.Snapshot,
                    $"Lobby created: {response.Code}. Share this code with other players.");
            });
        }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() => SyncStatusMessage = $"Create lobby failed: {GetFriendlyError(ex)}");
        }
        finally
        {
            IsLobbyBusy = false;
        }
    }

    private async Task JoinLobbyAsync()
    {
        if (IsLobbyBusy)
            return;

        if (!TryGetSyncContext(out var normalizedServerUrl, out var playerName, out var matchFingerprint, out var error))
        {
            SyncStatusMessage = error;
            return;
        }

        if (JoinLobbyCodeInput.Length != 6)
        {
            SyncStatusMessage = "Enter a 6-digit lobby code.";
            return;
        }

        IsLobbyBusy = true;
        try
        {
            await _dispatcher.InvokeAsync(() => SyncStatusMessage = $"Joining lobby {JoinLobbyCodeInput}…");
            await _settingsService.SaveAsync(new AppSettings { ServerUrl = normalizedServerUrl }, _cts.Token).ConfigureAwait(false);
            var snapshot = await _syncClient.JoinLobbyAsync(
                normalizedServerUrl,
                new JoinLobbyRequest(JoinLobbyCodeInput, matchFingerprint, playerName),
                _cts.Token).ConfigureAwait(false);

            await _dispatcher.InvokeAsync(() =>
            {
                ServerUrl = normalizedServerUrl;
                IsLobbyOwner = false;
                ApplyLobbySnapshot(snapshot, $"Joined lobby {snapshot.Code}. Sync is active.");
            });
        }
        catch (Exception ex)
        {
            await _dispatcher.InvokeAsync(() => SyncStatusMessage = $"Join lobby failed: {GetFriendlyError(ex)}");
        }
        finally
        {
            IsLobbyBusy = false;
        }
    }

    private async Task LeaveLobbyAsync()
    {
        if (IsLobbyBusy)
            return;

        IsLobbyBusy = true;
        try
        {
            await _dispatcher.InvokeAsync(() => SyncStatusMessage = "Leaving lobby…");
            try
            {
                await _syncClient.LeaveLobbyAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await _dispatcher.InvokeAsync(() => SyncStatusMessage = $"Leave lobby failed: {GetFriendlyError(ex)}");
                return;
            }

            await _dispatcher.InvokeAsync(() => ClearLobbyState("Left the lobby."));
        }
        finally
        {
            IsLobbyBusy = false;
        }
    }

    private void ClearLobbyState(string reason)
    {
        _syncedSpellStates.Clear();
        _syncedCosmicStates.Clear();
        IsLobbyConnected = false;
        IsLobbyOwner = false;
        CurrentLobbyCode = "";
        SyncStatusMessage = reason;
    }

    private void OnEnemySpellCooldownChanged(EnemyChampionViewModel vm, SpellSlotNumber slot, DateTime? startedAtUtc, DateTime? endsAtUtc)
    {
        if (!IsLobbyConnected)
            return;

        var state = new SpellStateDto(vm.RosterKey, slot, startedAtUtc, endsAtUtc);
        StoreSpellState(state);
        _ = _syncClient.SendSpellCooldownAsync(StaticSyncFingerprint, vm.RosterKey, slot, startedAtUtc, endsAtUtc, _cts.Token);
    }

    private void OnEnemyCosmicStateChanged(EnemyChampionViewModel vm, bool enabled)
    {
        if (!IsLobbyConnected)
            return;

        var state = new CosmicStateDto(vm.RosterKey, enabled);
        StoreCosmicState(state);
        _ = _syncClient.SendCosmicStateAsync(StaticSyncFingerprint, vm.RosterKey, enabled, _cts.Token);
    }

    private List<SpellStateDto> BuildSpellStates()
    {
        var states = new List<SpellStateDto>();
        foreach (var enemy in Enemies)
        {
            foreach (var state in new[] { enemy.GetSpellState(SpellSlotNumber.One), enemy.GetSpellState(SpellSlotNumber.Two) })
            {
                if (state.StartedAtUtc.HasValue && state.EndsAtUtc.HasValue && state.EndsAtUtc.Value > DateTime.UtcNow)
                    states.Add(state);
            }
        }

        return states;
    }

    private List<CosmicStateDto> BuildCosmicStates()
    {
        var states = new List<CosmicStateDto>();
        foreach (var enemy in Enemies)
        {
            var state = enemy.GetCosmicState();
            if (state.Enabled)
                states.Add(state);
        }

        return states;
    }

    private bool TryGetSyncContext(
        out string normalizedServerUrl,
        out string playerName,
        out string matchFingerprint,
        out string error)
    {
        if (!TryNormalizeServerUrl(ServerUrl, out normalizedServerUrl, out error))
        {
            playerName = "";
            matchFingerprint = "";
            return false;
        }

        if (_currentSnapshot?.InGame != true)
        {
            matchFingerprint = StaticSyncFingerprint;
            playerName = Environment.UserName;
            error = "";
            return true;
        }

        playerName = string.IsNullOrWhiteSpace(_currentSnapshot.LocalSummonerName)
            ? Environment.UserName
            : _currentSnapshot.LocalSummonerName;
        matchFingerprint = StaticSyncFingerprint;
        error = "";
        return true;
    }

    private static bool TryNormalizeServerUrl(string serverUrl, out string normalizedServerUrl, out string error)
    {
        var trimmed = (serverUrl ?? "").Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            normalizedServerUrl = "";
            error = "Enter a server URL first.";
            return false;
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            normalizedServerUrl = "";
            error = "Server URL must be a valid http or https address.";
            return false;
        }

        normalizedServerUrl = uri.GetLeftPart(UriPartial.Authority).TrimEnd('/');
        error = "";
        return true;
    }

    private static string GetFriendlyError(Exception ex)
    {
        if (ex is Microsoft.AspNetCore.SignalR.HubException hubException && !string.IsNullOrWhiteSpace(hubException.Message))
            return hubException.Message;

        if (!string.IsNullOrWhiteSpace(ex.Message))
            return ex.Message;

        return "Sync request failed.";
    }
}
