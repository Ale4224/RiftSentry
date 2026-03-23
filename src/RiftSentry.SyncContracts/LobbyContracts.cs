namespace RiftSentry.SyncContracts;

public enum SpellSlotNumber
{
    One = 1,
    Two = 2
}

public sealed record CreateLobbyRequest(
    string MatchFingerprint,
    string PlayerName,
    IReadOnlyList<SpellStateDto> SpellStates,
    IReadOnlyList<CosmicStateDto> CosmicStates);

public sealed record CreateLobbyResponse(
    string Code,
    LobbySnapshotDto Snapshot);

public sealed record JoinLobbyRequest(
    string Code,
    string MatchFingerprint,
    string PlayerName);

public sealed record HeartbeatRequest(
    string Code,
    string MatchFingerprint,
    bool InGame);

public sealed record LeaveLobbyRequest(
    string Code);

public sealed record SpellCooldownChangeRequest(
    string Code,
    string MatchFingerprint,
    string RosterKey,
    SpellSlotNumber Slot,
    DateTime? StartedAtUtc,
    DateTime? EndsAtUtc);

public sealed record CosmicStateChangeRequest(
    string Code,
    string MatchFingerprint,
    string RosterKey,
    bool Enabled);

public sealed record LobbySnapshotDto(
    string Code,
    string MatchFingerprint,
    IReadOnlyList<LobbyMemberDto> Members,
    IReadOnlyList<SpellStateDto> SpellStates,
    IReadOnlyList<CosmicStateDto> CosmicStates);

public sealed record LobbyMemberDto(
    string PlayerName,
    bool IsOwner);

public sealed record SpellStateDto(
    string RosterKey,
    SpellSlotNumber Slot,
    DateTime? StartedAtUtc,
    DateTime? EndsAtUtc);

public sealed record CosmicStateDto(
    string RosterKey,
    bool Enabled);

public sealed record DisconnectMessage(
    string Reason);

public static class SyncHubMethods
{
    public const string LobbySnapshot = "LobbySnapshot";
    public const string SpellCooldownChanged = "SpellCooldownChanged";
    public const string CosmicStateChanged = "CosmicStateChanged";
    public const string Disconnected = "Disconnected";
}
