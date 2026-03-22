namespace RiftSentry.Models;

public sealed class GameSnapshot
{
    public bool InGame { get; init; }
    public IReadOnlyList<EnemyPlayerSnapshot> Enemies { get; init; } = Array.Empty<EnemyPlayerSnapshot>();
}

public sealed class EnemyPlayerSnapshot
{
    public required string RosterKey { get; init; }
    public required string ChampionName { get; init; }
    public required string SummonerName { get; init; }
    public bool IsDead { get; init; }
    public bool HasIonianBoots { get; init; }
    public required string SpellOneDisplayName { get; init; }
    public required string SpellTwoDisplayName { get; init; }
    public string SpellOneDataKey { get; init; } = "";
    public string SpellTwoDataKey { get; init; } = "";
    public bool HasDetailedRunes { get; init; }
    public bool HasCosmicInsightFromApi { get; init; }
}
