using System.Security.Cryptography;
using RiftSentry.SyncContracts;

namespace RiftSentry.SyncServer.Services;

public sealed class LobbyRegistry
{
    private readonly object _gate = new();
    private readonly Dictionary<string, LobbyState> _lobbies = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _connectionToCode = new(StringComparer.Ordinal);

    public CreateLobbyResponse CreateLobby(string connectionId, CreateLobbyRequest request)
    {
        lock (_gate)
        {
            RemoveMembershipInternal(connectionId);

            var code = GenerateLobbyCode();
            var lobby = new LobbyState(code, request.MatchFingerprint);
            lobby.Members[connectionId] = new LobbyMemberState(request.PlayerName, true);

            foreach (var state in request.SpellStates)
                StoreSpellState(lobby, state);

            foreach (var state in request.CosmicStates)
                StoreCosmicState(lobby, state);

            _lobbies[code] = lobby;
            _connectionToCode[connectionId] = code;
            PruneExpiredSpellStates(lobby);

            return new CreateLobbyResponse(code, lobby.ToSnapshot());
        }
    }

    public LobbySnapshotDto JoinLobby(string connectionId, JoinLobbyRequest request)
    {
        lock (_gate)
        {
            if (_lobbies.TryGetValue(request.Code, out var existingLobby) &&
                existingLobby.Members.ContainsKey(connectionId) &&
                CanJoinLobby(existingLobby, request.MatchFingerprint))
            {
                PruneExpiredSpellStates(existingLobby);
                return existingLobby.ToSnapshot();
            }

            RemoveMembershipInternal(connectionId);

            if (!_lobbies.TryGetValue(request.Code, out var lobby))
                throw new InvalidOperationException("Lobby not found.");

            if (!CanJoinLobby(lobby, request.MatchFingerprint))
                throw new InvalidOperationException("You are not in the same game as the lobby owner.");

            lobby.Members[connectionId] = new LobbyMemberState(request.PlayerName, false);
            _connectionToCode[connectionId] = lobby.Code;
            PruneExpiredSpellStates(lobby);
            return lobby.ToSnapshot();
        }
    }

    public SpellStateDto UpdateSpellCooldown(string connectionId, SpellCooldownChangeRequest request)
    {
        lock (_gate)
        {
            var lobby = GetValidatedLobby(connectionId, request.Code, request.MatchFingerprint);
            var state = new SpellStateDto(request.RosterKey, request.Slot, request.StartedAtUtc, request.EndsAtUtc);
            StoreSpellState(lobby, state);
            PruneExpiredSpellStates(lobby);

            if (lobby.SpellStates.TryGetValue((request.RosterKey, request.Slot), out var storedState))
                return storedState;

            return new SpellStateDto(request.RosterKey, request.Slot, null, null);
        }
    }

    public CosmicStateDto UpdateCosmicState(string connectionId, CosmicStateChangeRequest request)
    {
        lock (_gate)
        {
            var lobby = GetValidatedLobby(connectionId, request.Code, request.MatchFingerprint);
            var state = new CosmicStateDto(request.RosterKey, request.Enabled);
            StoreCosmicState(lobby, state);
            return state;
        }
    }

    public MembershipChangeResult Leave(string connectionId, string? code, string? reason)
    {
        lock (_gate)
        {
            if (!TryGetLobby(connectionId, code, out var lobby))
                return MembershipChangeResult.None;

            return RemoveMember(lobby, connectionId, reason);
        }
    }

    public MembershipChangeResult HandleHeartbeat(string connectionId, HeartbeatRequest request)
    {
        lock (_gate)
        {
            if (!TryGetLobby(connectionId, request.Code, out var lobby))
                return MembershipChangeResult.None;

            if (!lobby.Members.TryGetValue(connectionId, out var member))
                return MembershipChangeResult.None;

            if (member.IsOwner)
            {
                if (request.InGame && !string.IsNullOrWhiteSpace(request.MatchFingerprint))
                {
                    lobby.MatchFingerprint = request.MatchFingerprint;
                    return MembershipChangeResult.None;
                }

                if (string.IsNullOrWhiteSpace(lobby.MatchFingerprint))
                    return MembershipChangeResult.None;

                return RemoveMember(lobby, connectionId, "Lobby closed because the owner is no longer in game.");
            }

            if (string.IsNullOrWhiteSpace(lobby.MatchFingerprint))
                return MembershipChangeResult.None;

            if (request.InGame && string.Equals(lobby.MatchFingerprint, request.MatchFingerprint, StringComparison.Ordinal))
                return MembershipChangeResult.None;

            return RemoveMember(lobby, connectionId, "You are no longer in the same game as the lobby owner.");
        }
    }

    public MembershipChangeResult HandleDisconnect(string connectionId)
    {
        lock (_gate)
        {
            if (!TryGetLobby(connectionId, null, out var lobby))
                return MembershipChangeResult.None;

            return RemoveMember(lobby, connectionId, "Lobby closed.");
        }
    }

    private MembershipChangeResult RemoveMember(LobbyState lobby, string connectionId, string? reason)
    {
        if (!lobby.Members.TryGetValue(connectionId, out var member))
            return MembershipChangeResult.None;

        lobby.Members.Remove(connectionId);
        _connectionToCode.Remove(connectionId);

        if (member.IsOwner)
        {
            foreach (var memberConnectionId in lobby.Members.Keys)
                _connectionToCode.Remove(memberConnectionId);

            _lobbies.Remove(lobby.Code);
            return new MembershipChangeResult(
                lobby.Code,
                true,
                true,
                null,
                new DisconnectMessage(reason ?? "Lobby closed."));
        }

        PruneExpiredSpellStates(lobby);
        return new MembershipChangeResult(
            lobby.Code,
            true,
            false,
            lobby.ToSnapshot(),
            string.IsNullOrWhiteSpace(reason) ? null : new DisconnectMessage(reason));
    }

    private LobbyState GetValidatedLobby(string connectionId, string code, string matchFingerprint)
    {
        if (!TryGetLobby(connectionId, code, out var lobby))
            throw new InvalidOperationException("Lobby not found.");

        if (!string.IsNullOrWhiteSpace(lobby.MatchFingerprint) &&
            !string.Equals(lobby.MatchFingerprint, matchFingerprint, StringComparison.Ordinal))
            throw new InvalidOperationException("You are not in the same game as the lobby owner.");

        return lobby;
    }

    private static bool CanJoinLobby(LobbyState lobby, string matchFingerprint)
    {
        if (string.IsNullOrWhiteSpace(lobby.MatchFingerprint))
            return true;

        return string.Equals(lobby.MatchFingerprint, matchFingerprint, StringComparison.Ordinal);
    }

    private bool TryGetLobby(string connectionId, string? code, out LobbyState lobby)
    {
        if (!string.IsNullOrWhiteSpace(code) && _lobbies.TryGetValue(code, out lobby!))
            return lobby.Members.ContainsKey(connectionId);

        if (_connectionToCode.TryGetValue(connectionId, out var mappedCode) && _lobbies.TryGetValue(mappedCode, out lobby!))
            return true;

        lobby = null!;
        return false;
    }

    private void RemoveMembershipInternal(string connectionId)
    {
        if (!_connectionToCode.TryGetValue(connectionId, out var code))
            return;

        if (_lobbies.TryGetValue(code, out var lobby))
            lobby.Members.Remove(connectionId);

        _connectionToCode.Remove(connectionId);
    }

    private string GenerateLobbyCode()
    {
        string code;
        do
        {
            code = RandomNumberGenerator.GetInt32(0, 1_000_000).ToString("D6");
        } while (_lobbies.ContainsKey(code));

        return code;
    }

    private static void StoreSpellState(LobbyState lobby, SpellStateDto state)
    {
        if (state.StartedAtUtc.HasValue && state.EndsAtUtc.HasValue && state.EndsAtUtc.Value > DateTime.UtcNow)
            lobby.SpellStates[(state.RosterKey, state.Slot)] = state;
        else
            lobby.SpellStates.Remove((state.RosterKey, state.Slot));
    }

    private static void StoreCosmicState(LobbyState lobby, CosmicStateDto state)
    {
        if (state.Enabled)
            lobby.CosmicStates[state.RosterKey] = true;
        else
            lobby.CosmicStates.Remove(state.RosterKey);
    }

    private static void PruneExpiredSpellStates(LobbyState lobby)
    {
        var expiredKeys = new List<(string RosterKey, SpellSlotNumber Slot)>();
        foreach (var pair in lobby.SpellStates)
        {
            if (!pair.Value.EndsAtUtc.HasValue || pair.Value.EndsAtUtc.Value <= DateTime.UtcNow)
                expiredKeys.Add(pair.Key);
        }

        foreach (var key in expiredKeys)
            lobby.SpellStates.Remove(key);
    }

    private sealed class LobbyState
    {
        public LobbyState(string code, string matchFingerprint)
        {
            Code = code;
            MatchFingerprint = matchFingerprint;
        }

        public string Code { get; }

        public string MatchFingerprint { get; set; }

        public Dictionary<string, LobbyMemberState> Members { get; } = new(StringComparer.Ordinal);

        public Dictionary<(string RosterKey, SpellSlotNumber Slot), SpellStateDto> SpellStates { get; } = new();

        public Dictionary<string, bool> CosmicStates { get; } = new(StringComparer.Ordinal);

        public LobbySnapshotDto ToSnapshot()
        {
            var members = Members.Values
                .OrderByDescending(member => member.IsOwner)
                .ThenBy(member => member.PlayerName, StringComparer.OrdinalIgnoreCase)
                .Select(member => new LobbyMemberDto(member.PlayerName, member.IsOwner))
                .ToArray();

            var spellStates = SpellStates.Values
                .OrderBy(state => state.RosterKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(state => state.Slot)
                .ToArray();

            var cosmicStates = CosmicStates.Keys
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .Select(key => new CosmicStateDto(key, true))
                .ToArray();

            return new LobbySnapshotDto(Code, MatchFingerprint, members, spellStates, cosmicStates);
        }
    }

    private sealed class LobbyMemberState
    {
        public LobbyMemberState(string playerName, bool isOwner)
        {
            PlayerName = playerName;
            IsOwner = isOwner;
        }

        public string PlayerName { get; }

        public bool IsOwner { get; }
    }
}

public sealed record MembershipChangeResult(
    string Code,
    bool CallerRemoved,
    bool LobbyClosed,
    LobbySnapshotDto? Snapshot,
    DisconnectMessage? Message)
{
    public static MembershipChangeResult None { get; } = new("", false, false, null, null);

    public bool HasChanges => CallerRemoved || LobbyClosed || Snapshot != null || Message != null;
}
