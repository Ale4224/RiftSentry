using Microsoft.AspNetCore.SignalR;
using RiftSentry.SyncContracts;
using RiftSentry.SyncServer.Services;

namespace RiftSentry.SyncServer.Hubs;

public sealed class SyncHub : Hub
{
    private readonly LobbyRegistry _registry;
    private readonly ILogger<SyncHub> _logger;

    public SyncHub(LobbyRegistry registry, ILogger<SyncHub> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public override Task OnConnectedAsync()
    {
        _logger.LogInformation("Client connected {ConnectionId}", Context.ConnectionId);
        return base.OnConnectedAsync();
    }

    public async Task<CreateLobbyResponse> CreateLobby(CreateLobbyRequest request)
    {
        try
        {
            var response = _registry.CreateLobby(Context.ConnectionId, request);
            _logger.LogInformation(
                "CreateLobby {ConnectionId} code {Code} player {PlayerName} fingerprint {FingerprintLength}",
                Context.ConnectionId,
                response.Code,
                request.PlayerName,
                request.MatchFingerprint.Length);
            await Groups.AddToGroupAsync(Context.ConnectionId, response.Code);
            await Clients.Caller.SendAsync(SyncHubMethods.LobbySnapshot, response.Snapshot);
            return response;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "CreateLobby failed {ConnectionId}", Context.ConnectionId);
            throw new HubException(ex.Message);
        }
    }

    public async Task<LobbySnapshotDto> JoinLobby(JoinLobbyRequest request)
    {
        try
        {
            var snapshot = _registry.JoinLobby(Context.ConnectionId, request);
            _logger.LogInformation(
                "JoinLobby {ConnectionId} code {Code} player {PlayerName} members {MemberCount}",
                Context.ConnectionId,
                snapshot.Code,
                request.PlayerName,
                snapshot.Members.Count);
            await Groups.AddToGroupAsync(Context.ConnectionId, snapshot.Code);
            await Clients.Group(snapshot.Code).SendAsync(SyncHubMethods.LobbySnapshot, snapshot);
            return snapshot;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "JoinLobby failed {ConnectionId} code {Code}", Context.ConnectionId, request.Code);
            throw new HubException(ex.Message);
        }
    }

    public async Task LeaveLobby(LeaveLobbyRequest request)
    {
        _logger.LogInformation("LeaveLobby {ConnectionId} code {Code}", Context.ConnectionId, request.Code);
        var result = _registry.Leave(Context.ConnectionId, request.Code, null);
        await ApplyMembershipChangeAsync(result, false);
    }

    public async Task Heartbeat(HeartbeatRequest request)
    {
        var result = _registry.HandleHeartbeat(Context.ConnectionId, request);
        if (result.HasChanges)
            _logger.LogInformation(
                "Heartbeat membership change {ConnectionId} code {Code} closed {Closed} removed {Removed}",
                Context.ConnectionId,
                result.Code,
                result.LobbyClosed,
                result.CallerRemoved);
        else
            _logger.LogDebug("Heartbeat ok {ConnectionId} code {Code} inGame {InGame}", Context.ConnectionId, request.Code, request.InGame);

        await ApplyMembershipChangeAsync(result, true);
    }

    public async Task UpdateSpellCooldown(SpellCooldownChangeRequest request)
    {
        try
        {
            var state = _registry.UpdateSpellCooldown(Context.ConnectionId, request);
            _logger.LogDebug(
                "UpdateSpellCooldown {ConnectionId} code {Code} roster {RosterKey} slot {Slot}",
                Context.ConnectionId,
                request.Code,
                request.RosterKey,
                request.Slot);
            await Clients.Group(request.Code).SendAsync(SyncHubMethods.SpellCooldownChanged, state);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "UpdateSpellCooldown failed {ConnectionId}", Context.ConnectionId);
            throw new HubException(ex.Message);
        }
    }

    public async Task UpdateCosmicState(CosmicStateChangeRequest request)
    {
        try
        {
            var state = _registry.UpdateCosmicState(Context.ConnectionId, request);
            _logger.LogDebug(
                "UpdateCosmicState {ConnectionId} code {Code} roster {RosterKey} enabled {Enabled}",
                Context.ConnectionId,
                request.Code,
                request.RosterKey,
                request.Enabled);
            await Clients.Group(request.Code).SendAsync(SyncHubMethods.CosmicStateChanged, state);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "UpdateCosmicState failed {ConnectionId}", Context.ConnectionId);
            throw new HubException(ex.Message);
        }
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        if (exception != null)
            _logger.LogWarning(exception, "Client disconnected with error {ConnectionId}", Context.ConnectionId);
        else
            _logger.LogInformation("Client disconnected {ConnectionId}", Context.ConnectionId);

        var result = _registry.HandleDisconnect(Context.ConnectionId);
        if (result.HasChanges)
            _logger.LogInformation(
                "Disconnect lobby effect {ConnectionId} code {Code} lobbyClosed {LobbyClosed}",
                Context.ConnectionId,
                result.Code,
                result.LobbyClosed);

        await ApplyMembershipChangeAsync(result, false);
        await base.OnDisconnectedAsync(exception);
    }

    private async Task ApplyMembershipChangeAsync(MembershipChangeResult result, bool abortCaller)
    {
        if (!result.HasChanges)
            return;

        if (result.CallerRemoved && !string.IsNullOrWhiteSpace(result.Code))
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, result.Code);

        if (result.LobbyClosed)
        {
            _logger.LogInformation("Lobby closed code {Code} reason {Reason}", result.Code, result.Message?.Reason);
            if (result.Message != null && !string.IsNullOrWhiteSpace(result.Code))
                await Clients.Group(result.Code).SendAsync(SyncHubMethods.Disconnected, result.Message);
            return;
        }

        if (result.CallerRemoved && result.Message != null)
        {
            _logger.LogInformation("Member removed from lobby {Code} reason {Reason}", result.Code, result.Message.Reason);
            await Clients.Caller.SendAsync(SyncHubMethods.Disconnected, result.Message);
        }

        if (result.Snapshot != null && !string.IsNullOrWhiteSpace(result.Code))
            await Clients.Group(result.Code).SendAsync(SyncHubMethods.LobbySnapshot, result.Snapshot);

        if (abortCaller && result.CallerRemoved && result.Message != null)
            Context.Abort();
    }
}
