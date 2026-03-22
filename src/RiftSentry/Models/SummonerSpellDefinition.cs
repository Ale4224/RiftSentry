namespace RiftSentry.Models;

public sealed class SummonerSpellDefinition
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public required int BaseCooldownSeconds { get; init; }
    public required string ImageFileName { get; init; }
}
