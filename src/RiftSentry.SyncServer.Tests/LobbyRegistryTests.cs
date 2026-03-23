using RiftSentry.SyncContracts;
using RiftSentry.SyncServer.Services;

namespace RiftSentry.SyncServer.Tests;

public sealed class LobbyRegistryTests
{
    [Fact]
    public void CreateLobby_ReturnsInitialSnapshotWithStates()
    {
        var registry = new LobbyRegistry();
        var startedAtUtc = DateTime.UtcNow;
        var endsAtUtc = startedAtUtc.AddSeconds(90);

        var response = registry.CreateLobby(
            "owner-1",
            new CreateLobbyRequest(
                "match-1",
                "Owner",
                new[]
                {
                    new SpellStateDto("Enemy1|Flash", SpellSlotNumber.One, startedAtUtc, endsAtUtc)
                },
                new[]
                {
                    new CosmicStateDto("Enemy1", true)
                }));

        Assert.Matches("^[0-9]{6}$", response.Code);
        Assert.Equal(response.Code, response.Snapshot.Code);
        Assert.Equal("match-1", response.Snapshot.MatchFingerprint);
        Assert.Single(response.Snapshot.Members);
        Assert.True(response.Snapshot.Members[0].IsOwner);
        Assert.Single(response.Snapshot.SpellStates);
        Assert.Single(response.Snapshot.CosmicStates);
    }

    [Fact]
    public void JoinLobby_WithMatchingFingerprint_ReturnsSharedSnapshot()
    {
        var registry = new LobbyRegistry();
        var response = registry.CreateLobby("owner-1", new CreateLobbyRequest("match-1", "Owner", Array.Empty<SpellStateDto>(), Array.Empty<CosmicStateDto>()));

        var snapshot = registry.JoinLobby("guest-1", new JoinLobbyRequest(response.Code, "match-1", "Guest"));

        Assert.Equal(response.Code, snapshot.Code);
        Assert.Equal(2, snapshot.Members.Count);
        Assert.Contains(snapshot.Members, member => member.PlayerName == "Owner" && member.IsOwner);
        Assert.Contains(snapshot.Members, member => member.PlayerName == "Guest" && !member.IsOwner);
    }

    [Fact]
    public void UpdateState_IsVisibleToLateJoiners()
    {
        var registry = new LobbyRegistry();
        var response = registry.CreateLobby("owner-1", new CreateLobbyRequest("match-1", "Owner", Array.Empty<SpellStateDto>(), Array.Empty<CosmicStateDto>()));
        var startedAtUtc = DateTime.UtcNow;
        var endsAtUtc = startedAtUtc.AddSeconds(75);

        registry.UpdateSpellCooldown(
            "owner-1",
            new SpellCooldownChangeRequest(response.Code, "match-1", "EnemyA", SpellSlotNumber.Two, startedAtUtc, endsAtUtc));
        registry.UpdateCosmicState(
            "owner-1",
            new CosmicStateChangeRequest(response.Code, "match-1", "EnemyA", true));

        var snapshot = registry.JoinLobby("guest-1", new JoinLobbyRequest(response.Code, "match-1", "Guest"));

        Assert.Contains(snapshot.SpellStates, state => state.RosterKey == "EnemyA" && state.Slot == SpellSlotNumber.Two && state.EndsAtUtc == endsAtUtc);
        Assert.Contains(snapshot.CosmicStates, state => state.RosterKey == "EnemyA" && state.Enabled);
    }

    [Fact]
    public void HeartbeatMismatch_RemovesGuestButKeepsLobbyOpen()
    {
        var registry = new LobbyRegistry();
        var response = registry.CreateLobby("owner-1", new CreateLobbyRequest("match-1", "Owner", Array.Empty<SpellStateDto>(), Array.Empty<CosmicStateDto>()));
        registry.JoinLobby("guest-1", new JoinLobbyRequest(response.Code, "match-1", "Guest"));

        var result = registry.HandleHeartbeat("guest-1", new HeartbeatRequest(response.Code, "other-match", true));

        Assert.True(result.CallerRemoved);
        Assert.False(result.LobbyClosed);
        Assert.NotNull(result.Snapshot);
        Assert.Single(result.Snapshot!.Members);
        Assert.Equal("Owner", result.Snapshot.Members[0].PlayerName);

        var lateJoinSnapshot = registry.JoinLobby("guest-2", new JoinLobbyRequest(response.Code, "match-1", "Guest2"));
        Assert.Equal(2, lateJoinSnapshot.Members.Count);
    }

    [Fact]
    public void OwnerLeavingMatch_ClosesLobby()
    {
        var registry = new LobbyRegistry();
        var response = registry.CreateLobby("owner-1", new CreateLobbyRequest("match-1", "Owner", Array.Empty<SpellStateDto>(), Array.Empty<CosmicStateDto>()));
        registry.JoinLobby("guest-1", new JoinLobbyRequest(response.Code, "match-1", "Guest"));

        var result = registry.HandleHeartbeat("owner-1", new HeartbeatRequest(response.Code, "", false));

        Assert.True(result.CallerRemoved);
        Assert.True(result.LobbyClosed);
        Assert.NotNull(result.Message);
        Assert.Throws<InvalidOperationException>(() => registry.JoinLobby("guest-2", new JoinLobbyRequest(response.Code, "match-1", "Guest2")));
    }

    [Fact]
    public void CreateLobby_WithoutMatchFingerprint_AllowsPregameJoin()
    {
        var registry = new LobbyRegistry();
        var response = registry.CreateLobby("owner-1", new CreateLobbyRequest("", "Owner", Array.Empty<SpellStateDto>(), Array.Empty<CosmicStateDto>()));

        var snapshot = registry.JoinLobby("guest-1", new JoinLobbyRequest(response.Code, "", "Guest"));

        Assert.Equal("", snapshot.MatchFingerprint);
        Assert.Equal(2, snapshot.Members.Count);
    }

    [Fact]
    public void JoinLobby_WhenAlreadyMember_ReturnsSnapshotWithoutDemotingOwner()
    {
        var registry = new LobbyRegistry();
        var response = registry.CreateLobby("owner-1", new CreateLobbyRequest("match-1", "Owner", Array.Empty<SpellStateDto>(), Array.Empty<CosmicStateDto>()));

        var snapshot = registry.JoinLobby("owner-1", new JoinLobbyRequest(response.Code, "match-1", "Owner"));

        Assert.Equal(response.Code, snapshot.Code);
        Assert.Single(snapshot.Members);
        Assert.True(snapshot.Members[0].IsOwner);
    }

    [Fact]
    public void OwnerHeartbeat_SetsFingerprint_AndMismatchedGuestIsRemoved()
    {
        var registry = new LobbyRegistry();
        var response = registry.CreateLobby("owner-1", new CreateLobbyRequest("", "Owner", Array.Empty<SpellStateDto>(), Array.Empty<CosmicStateDto>()));
        registry.JoinLobby("guest-1", new JoinLobbyRequest(response.Code, "", "Guest"));

        var ownerResult = registry.HandleHeartbeat("owner-1", new HeartbeatRequest(response.Code, "match-2", true));
        var guestResult = registry.HandleHeartbeat("guest-1", new HeartbeatRequest(response.Code, "", false));

        Assert.False(ownerResult.HasChanges);
        Assert.True(guestResult.CallerRemoved);
        Assert.False(guestResult.LobbyClosed);
        Assert.NotNull(guestResult.Snapshot);
        Assert.Equal("match-2", guestResult.Snapshot!.MatchFingerprint);
    }
}
