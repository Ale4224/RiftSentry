using Microsoft.AspNetCore.SignalR.Client;
using RiftSentry.SyncContracts;

namespace RiftSentry.Services;

public sealed class SyncClientService : IAsyncDisposable
{
    private HubConnection? _connection;
    private string _serverUrl = "";
    private string _lobbyCode = "";
    private bool _isStopping;

    public event Action<LobbySnapshotDto>? LobbySnapshotReceived;
    public event Action<SpellStateDto>? SpellCooldownChanged;
    public event Action<CosmicStateDto>? CosmicStateChanged;
    public event Action<DisconnectMessage>? Disconnected;

    public string CurrentLobbyCode => _lobbyCode;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task<CreateLobbyResponse> CreateLobbyAsync(string serverUrl, CreateLobbyRequest request, CancellationToken cancellationToken = default)
    {
        await StopConnectionAsync().ConfigureAwait(false);
        await EnsureConnectionAsync(serverUrl, cancellationToken).ConfigureAwait(false);
        var response = await _connection!.InvokeAsync<CreateLobbyResponse>("CreateLobby", request, cancellationToken).ConfigureAwait(false);
        _lobbyCode = response.Code;
        return response;
    }

    public async Task<LobbySnapshotDto> JoinLobbyAsync(string serverUrl, JoinLobbyRequest request, CancellationToken cancellationToken = default)
    {
        await EnsureConnectionAsync(serverUrl, cancellationToken).ConfigureAwait(false);
        var snapshot = await _connection!.InvokeAsync<LobbySnapshotDto>("JoinLobby", request, cancellationToken).ConfigureAwait(false);
        _lobbyCode = snapshot.Code;
        return snapshot;
    }

    public async Task LeaveLobbyAsync(CancellationToken cancellationToken = default)
    {
        if (_connection != null && !string.IsNullOrWhiteSpace(_lobbyCode))
            await _connection.InvokeAsync("LeaveLobby", new LeaveLobbyRequest(_lobbyCode), cancellationToken).ConfigureAwait(false);

        await StopConnectionAsync().ConfigureAwait(false);
    }

    public async Task SendHeartbeatAsync(string matchFingerprint, bool inGame, CancellationToken cancellationToken = default)
    {
        if (_connection == null || string.IsNullOrWhiteSpace(_lobbyCode) || _connection.State != HubConnectionState.Connected)
            return;

        await _connection.InvokeAsync("Heartbeat", new HeartbeatRequest(_lobbyCode, matchFingerprint, inGame), cancellationToken).ConfigureAwait(false);
    }

    public async Task SendSpellCooldownAsync(
        string matchFingerprint,
        string rosterKey,
        SpellSlotNumber slot,
        DateTime? startedAtUtc,
        DateTime? endsAtUtc,
        CancellationToken cancellationToken = default)
    {
        if (_connection == null || string.IsNullOrWhiteSpace(_lobbyCode) || _connection.State != HubConnectionState.Connected)
            return;

        var request = new SpellCooldownChangeRequest(_lobbyCode, matchFingerprint, rosterKey, slot, startedAtUtc, endsAtUtc);
        await _connection.InvokeAsync("UpdateSpellCooldown", request, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendCosmicStateAsync(
        string matchFingerprint,
        string rosterKey,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        if (_connection == null || string.IsNullOrWhiteSpace(_lobbyCode) || _connection.State != HubConnectionState.Connected)
            return;

        var request = new CosmicStateChangeRequest(_lobbyCode, matchFingerprint, rosterKey, enabled);
        await _connection.InvokeAsync("UpdateCosmicState", request, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureConnectionAsync(string serverUrl, CancellationToken cancellationToken)
    {
        var normalizedServerUrl = NormalizeServerUrl(serverUrl);
        if (_connection != null && string.Equals(_serverUrl, normalizedServerUrl, StringComparison.OrdinalIgnoreCase))
        {
            if (_connection.State == HubConnectionState.Connected)
                return;

            if (_connection.State == HubConnectionState.Disconnected)
            {
                await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
        }

        await StopConnectionAsync().ConfigureAwait(false);

        _serverUrl = normalizedServerUrl;
        _connection = new HubConnectionBuilder()
            .WithUrl(BuildHubUrl(normalizedServerUrl))
            .WithAutomaticReconnect()
            .Build();

        _connection.On<LobbySnapshotDto>(SyncHubMethods.LobbySnapshot, snapshot => LobbySnapshotReceived?.Invoke(snapshot));
        _connection.On<SpellStateDto>(SyncHubMethods.SpellCooldownChanged, state => SpellCooldownChanged?.Invoke(state));
        _connection.On<CosmicStateDto>(SyncHubMethods.CosmicStateChanged, state => CosmicStateChanged?.Invoke(state));
        _connection.On<DisconnectMessage>(SyncHubMethods.Disconnected, async message =>
        {
            Disconnected?.Invoke(message);
            await StopConnectionAsync().ConfigureAwait(false);
        });
        _connection.Closed += OnClosedAsync;

        await _connection.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task OnClosedAsync(Exception? _)
    {
        if (_isStopping)
            return;

        _lobbyCode = "";
        Disconnected?.Invoke(new DisconnectMessage("Connection closed"));
        await StopConnectionAsync().ConfigureAwait(false);
    }

    private async Task StopConnectionAsync()
    {
        if (_connection == null)
        {
            _serverUrl = "";
            _lobbyCode = "";
            return;
        }

        _isStopping = true;
        try
        {
            try
            {
                await _connection.StopAsync().ConfigureAwait(false);
            }
            catch
            {
            }

            await _connection.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _connection = null;
            _serverUrl = "";
            _lobbyCode = "";
            _isStopping = false;
        }
    }

    private static string NormalizeServerUrl(string serverUrl)
    {
        var trimmed = serverUrl.Trim();
        return trimmed.TrimEnd('/');
    }

    private static string BuildHubUrl(string serverUrl) => $"{NormalizeServerUrl(serverUrl)}/syncHub";

    public async ValueTask DisposeAsync() => await StopConnectionAsync().ConfigureAwait(false);
}
