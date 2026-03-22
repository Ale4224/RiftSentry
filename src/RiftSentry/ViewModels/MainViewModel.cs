using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using RiftSentry.Models;
using RiftSentry.Services;

namespace RiftSentry.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private readonly DataDragonService _ddragon;
    private readonly AssetCacheService _assets;
    private readonly LiveClientService _liveClient;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _uiTimer;
    private readonly CancellationTokenSource _cts = new();
    private bool _isInGame;
    private string _statusMessage = "";

    public MainViewModel(DataDragonService ddragon, AssetCacheService assets, LiveClientService liveClient, Dispatcher dispatcher)
    {
        _ddragon = ddragon;
        _assets = assets;
        _liveClient = liveClient;
        _dispatcher = dispatcher;
        Enemies = new ObservableCollection<EnemyChampionViewModel>();
        _uiTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _uiTimer.Tick += (_, _) => TickUi();
    }

    public ObservableCollection<EnemyChampionViewModel> Enemies { get; }

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

    public async Task InitializeAsync()
    {
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
                Enemies.Add(vm);
            }

            vm.ApplySnapshot(p);
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
        });
    }

    private void TickUi()
    {
        foreach (var e in Enemies)
            e.TickSpells();
    }

    public void Dispose()
    {
        _uiTimer.Stop();
        _cts.Cancel();
        _cts.Dispose();
        _liveClient.Dispose();
    }
}
