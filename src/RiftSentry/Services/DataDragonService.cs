using System.Collections.Concurrent;
using System.Net.Http;
using System.Text.Json;
using RiftSentry.Models;

namespace RiftSentry.Services;

public sealed class DataDragonService
{
    private static readonly Uri DdragonRoot = new("https://ddragon.leagueoflegends.com/");
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<string, SummonerSpellDefinition> _byKey = new(StringComparer.OrdinalIgnoreCase);
    private string? _version;
    private readonly ConcurrentDictionary<string, string> _championImageByApiName = new(StringComparer.OrdinalIgnoreCase);

    public DataDragonService(HttpClient http)
    {
        _http = http;
    }

    public string? CurrentVersion => _version;

    public async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_version != null && _byKey.Count > 0) return;

        var versionsJson = await _http.GetStringAsync(new Uri(DdragonRoot, "api/versions.json"), cancellationToken).ConfigureAwait(false);
        using var versionsDoc = JsonDocument.Parse(versionsJson);
        if (versionsDoc.RootElement.ValueKind != JsonValueKind.Array || versionsDoc.RootElement.GetArrayLength() == 0)
            throw new InvalidOperationException("versions.json");
        _version = versionsDoc.RootElement[0].GetString() ?? throw new InvalidOperationException("version");

        await LoadSummonersAsync(cancellationToken).ConfigureAwait(false);
        await LoadChampionsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task LoadSummonersAsync(CancellationToken cancellationToken)
    {
        var url = $"https://ddragon.leagueoflegends.com/cdn/{_version}/data/en_US/summoner.json";
        var json = await _http.GetStringAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data))
            return;
        foreach (var prop in data.EnumerateObject())
        {
            var el = prop.Value;
            var name = el.TryGetProperty("name", out var n) ? n.GetString() ?? prop.Name : prop.Name;
            var imageFile = ReadImageFull(el);
            if (string.IsNullOrEmpty(imageFile))
                imageFile = $"{prop.Name}.png";
            var cd = 300;
            if (el.TryGetProperty("cooldown", out var cdEl) && cdEl.ValueKind == JsonValueKind.Array && cdEl.GetArrayLength() > 0)
            {
                var first = cdEl[0];
                if (first.ValueKind == JsonValueKind.Number)
                    cd = (int)Math.Round(first.GetDouble());
            }

            var def = new SummonerSpellDefinition
            {
                Key = prop.Name,
                Name = name,
                BaseCooldownSeconds = cd,
                ImageFileName = imageFile
            };
            _byKey[prop.Name] = def;
        }
    }

    private static string ReadImageFull(JsonElement el)
    {
        if (!el.TryGetProperty("image", out var img))
            return "";
        if (img.ValueKind == JsonValueKind.String)
            return img.GetString() ?? "";
        if (img.ValueKind == JsonValueKind.Object && img.TryGetProperty("full", out var full))
            return full.GetString() ?? "";
        return "";
    }

    private async Task LoadChampionsAsync(CancellationToken cancellationToken)
    {
        var url = $"https://ddragon.leagueoflegends.com/cdn/{_version}/data/en_US/champion.json";
        var json = await _http.GetStringAsync(new Uri(url), cancellationToken).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data))
            return;
        foreach (var prop in data.EnumerateObject())
        {
            var el = prop.Value;
            var displayName = el.TryGetProperty("name", out var dn) ? dn.GetString() ?? prop.Name : prop.Name;
            var imageFile = ReadImageFull(el);
            if (string.IsNullOrEmpty(imageFile))
                imageFile = $"{prop.Name}.png";
            _championImageByApiName[prop.Name] = imageFile;
            _championImageByApiName[displayName] = imageFile;
        }
    }

    public SummonerSpellDefinition? ResolveSpell(string displayName, string dataKey)
    {
        if (!string.IsNullOrEmpty(dataKey) && _byKey.TryGetValue(dataKey, out var byK))
            return byK;
        var aliased = ResolveAliasedSpell(displayName, dataKey);
        if (aliased != null)
            return aliased;
        if (string.IsNullOrEmpty(displayName))
            return null;

        SummonerSpellDefinition? best = null;
        foreach (var kv in _byKey)
        {
            if (!string.Equals(kv.Value.Name, displayName, StringComparison.OrdinalIgnoreCase))
                continue;
            if (best == null)
            {
                best = kv.Value;
                continue;
            }

            if (kv.Key.StartsWith("SummonerCherry", StringComparison.OrdinalIgnoreCase))
                continue;
            if (best.Key.StartsWith("SummonerCherry", StringComparison.OrdinalIgnoreCase))
            {
                best = kv.Value;
                continue;
            }
        }

        return best;
    }

    private SummonerSpellDefinition? ResolveAliasedSpell(string displayName, string dataKey)
    {
        var normalizedDisplay = displayName.Trim();
        var normalizedKey = dataKey.Trim();

        if (LooksLikeHexflash(normalizedDisplay, normalizedKey))
        {
            if (!_byKey.TryGetValue("SummonerFlash", out var flash))
                return null;

            return new SummonerSpellDefinition
            {
                Key = normalizedKey.Length > 0 ? normalizedKey : flash.Key,
                Name = normalizedDisplay.Length > 0 ? normalizedDisplay : "Hexflash",
                BaseCooldownSeconds = flash.BaseCooldownSeconds,
                ImageFileName = flash.ImageFileName
            };
        }

        if (LooksLikeTeleport(normalizedDisplay, normalizedKey))
        {
            if (!_byKey.TryGetValue("SummonerTeleport", out var teleport))
                return null;

            if (normalizedDisplay.Contains("Unleashed", StringComparison.OrdinalIgnoreCase))
            {
                return new SummonerSpellDefinition
                {
                    Key = normalizedKey.Length > 0 ? normalizedKey : teleport.Key,
                    Name = normalizedDisplay.Length > 0 ? normalizedDisplay : teleport.Name,
                    BaseCooldownSeconds = 240,
                    ImageFileName = teleport.ImageFileName
                };
            }

            return teleport;
        }

        if (LooksLikeSmite(normalizedDisplay, normalizedKey) &&
            _byKey.TryGetValue("SummonerSmite", out var smite))
            return smite;

        return null;
    }

    private static bool LooksLikeTeleport(string displayName, string dataKey)
    {
        return displayName.Contains("Teleport", StringComparison.OrdinalIgnoreCase)
            || dataKey.Contains("Teleport", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeHexflash(string displayName, string dataKey)
    {
        return displayName.Contains("Hexflash", StringComparison.OrdinalIgnoreCase)
            || displayName.Contains("Flashtraption", StringComparison.OrdinalIgnoreCase)
            || dataKey.Contains("Hexflash", StringComparison.OrdinalIgnoreCase)
            || dataKey.Contains("Flashtraption", StringComparison.OrdinalIgnoreCase)
            || dataKey.Contains("HextechFlash", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeSmite(string displayName, string dataKey)
    {
        return displayName.Contains("Smite", StringComparison.OrdinalIgnoreCase)
            || dataKey.Contains("Smite", StringComparison.OrdinalIgnoreCase);
    }

    public string? GetChampionImageFileName(string championNameFromApi)
    {
        if (_championImageByApiName.TryGetValue(championNameFromApi, out var img))
            return img;
        foreach (var kv in _championImageByApiName)
        {
            if (string.Equals(kv.Key, championNameFromApi, StringComparison.OrdinalIgnoreCase))
                return kv.Value;
        }

        return null;
    }
}
