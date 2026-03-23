using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using RiftSentry.Models;

namespace RiftSentry.Services;

public sealed class LiveClientService : IDisposable
{
    private const int IonianBootsItemId = 3158;
    private const int CosmicInsightRuneId = 8347;
    private static readonly Uri BaseUri = new("https://127.0.0.1:2999/");
    private readonly HttpClient _http;

    public LiveClientService()
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = ValidateLocalhostCert
        };
        _http = new HttpClient(handler) { BaseAddress = BaseUri, Timeout = TimeSpan.FromSeconds(3) };
    }

    private static bool ValidateLocalhostCert(
        HttpRequestMessage message,
        X509Certificate2? _,
        X509Chain? __,
        SslPolicyErrors errors)
    {
        var host = message.RequestUri?.Host;
        var port = message.RequestUri?.Port ?? -1;
        if (host == "127.0.0.1" && port == 2999)
            return true;
        return errors == SslPolicyErrors.None;
    }

    public async Task<GameSnapshot?> TryGetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _http.GetAsync("liveclientdata/allgamedata", cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            return null;

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, default, cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;

        if (!root.TryGetProperty("gameData", out var gameData))
            return new GameSnapshot { InGame = false };

        if (!root.TryGetProperty("allPlayers", out var allPlayers) || allPlayers.ValueKind != JsonValueKind.Array)
            return new GameSnapshot { InGame = false };

        if (!root.TryGetProperty("activePlayer", out var activePlayer))
            return new GameSnapshot { InGame = false };

        var mySummonerName = GetString(activePlayer, "summonerName");
        if (string.IsNullOrEmpty(mySummonerName))
            return new GameSnapshot { InGame = false };

        string? myTeam = null;
        foreach (var p in allPlayers.EnumerateArray())
        {
            if (GetString(p, "summonerName") != mySummonerName) continue;
            myTeam = GetString(p, "team");
            break;
        }

        if (string.IsNullOrEmpty(myTeam))
            return new GameSnapshot { InGame = false };

        var enemies = new List<EnemyPlayerSnapshot>();
        foreach (var p in allPlayers.EnumerateArray())
        {
            var team = GetString(p, "team");
            if (team == myTeam) continue;

            var championName = NormalizeChampionName(GetString(p, "championName"));
            var summonerName = GetString(p, "summonerName");
            var isDead = p.TryGetProperty("isDead", out var idEl) && idEl.ValueKind == JsonValueKind.True;
            var hasIonian = HasIonianBoots(p);
            var (s1, s2, k1, k2) = ReadSummonerSpells(p);
            var (hasDetailedRunes, hasCosmicFromApi) = ParseEnemyRunes(p);

            var rosterKey = $"{summonerName}|{championName}";
            enemies.Add(new EnemyPlayerSnapshot
            {
                RosterKey = rosterKey,
                ChampionName = championName,
                SummonerName = summonerName,
                IsDead = isDead,
                HasIonianBoots = hasIonian,
                SpellOneDisplayName = s1,
                SpellTwoDisplayName = s2,
                SpellOneDataKey = k1,
                SpellTwoDataKey = k2,
                HasDetailedRunes = hasDetailedRunes,
                HasCosmicInsightFromApi = hasCosmicFromApi
            });
        }

        if (enemies.Count == 0)
            return new GameSnapshot { InGame = false };

        return new GameSnapshot
        {
            InGame = true,
            LocalSummonerName = mySummonerName,
            MyTeam = myTeam,
            MatchFingerprint = BuildMatchFingerprint(gameData, allPlayers),
            Enemies = enemies
        };
    }

    private static string NormalizeChampionName(string championName)
    {
        if (string.IsNullOrWhiteSpace(championName))
            return championName;

        if (string.Equals(championName, "MiniGnar", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(championName, "MegaGnar", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(championName, "GnarBig", StringComparison.OrdinalIgnoreCase))
            return "Gnar";

        return championName;
    }

    private static (bool hasDetailedRunes, bool hasCosmicInsightFromApi) ParseEnemyRunes(JsonElement player)
    {
        if (!player.TryGetProperty("runes", out var runes) || runes.ValueKind != JsonValueKind.Object)
            return (false, false);

        var topNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in runes.EnumerateObject())
            topNames.Add(prop.Name);

        var onlyKeystoneAndTrees = topNames.Count == 3
            && topNames.Contains("keystone")
            && topNames.Contains("primaryRuneTree")
            && topNames.Contains("secondaryRuneTree");

        if (onlyKeystoneAndTrees)
            return (false, false);

        var ids = new HashSet<int>();
        var displayNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        CollectRuneIdsAndDisplayNames(runes, ids, displayNames);

        var hasCosmic = ids.Contains(CosmicInsightRuneId)
            || displayNames.Contains("Cosmic Insight");

        var hasDetailed = true;
        return (hasDetailed, hasCosmic);
    }

    private static void CollectRuneIdsAndDisplayNames(JsonElement el, HashSet<int> ids, HashSet<string> displayNames)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var prop in el.EnumerateObject())
                {
                    if (prop.NameEquals("id") && prop.Value.ValueKind == JsonValueKind.Number &&
                        prop.Value.TryGetInt32(out var id))
                        ids.Add(id);
                    if (prop.NameEquals("displayName") && prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var s = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(s))
                            displayNames.Add(s);
                    }

                    CollectRuneIdsAndDisplayNames(prop.Value, ids, displayNames);
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in el.EnumerateArray())
                    CollectRuneIdsAndDisplayNames(item, ids, displayNames);
                break;
        }
    }

    private static bool HasIonianBoots(JsonElement player)
    {
        if (!player.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var it in items.EnumerateArray())
        {
            if (it.ValueKind == JsonValueKind.Number && it.TryGetInt32(out var id) && id == IonianBootsItemId)
                return true;
        }

        return false;
    }

    private static (string displayOne, string displayTwo, string keyOne, string keyTwo) ReadSummonerSpells(JsonElement player)
    {
        var a = "";
        var b = "";
        var k1 = "";
        var k2 = "";
        if (!player.TryGetProperty("summonerSpells", out var spells))
            return (a, b, k1, k2);

        if (spells.TryGetProperty("summonerSpellOne", out var s1))
            (a, k1) = GetSpellNames(s1);
        if (spells.TryGetProperty("summonerSpellTwo", out var s2))
            (b, k2) = GetSpellNames(s2);
        return (a, b, k1, k2);
    }

    private static (string displayName, string dataKey) GetSpellNames(JsonElement spell)
    {
        var d = GetString(spell, "displayName");
        var raw = GetString(spell, "rawDescription");
        var key = ExtractSummonerKeyFromRaw(raw) ?? "";
        if (!string.IsNullOrEmpty(d)) return (d, key);
        return ("", key);
    }

    private static string? ExtractSummonerKeyFromRaw(string raw)
    {
        const string prefix = "GeneratedTip_SummonerSpell_";
        const string suffix = "_Description";
        var i = raw.IndexOf(prefix, StringComparison.Ordinal);
        if (i < 0) return null;
        i += prefix.Length;
        var j = raw.IndexOf(suffix, i, StringComparison.Ordinal);
        if (j < 0 || j <= i) return null;
        return raw.Substring(i, j - i);
    }

    private static string GetString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var p)) return "";
        return p.ValueKind switch
        {
            JsonValueKind.String => p.GetString() ?? "",
            JsonValueKind.Number => p.GetRawText(),
            _ => ""
        };
    }

    private static string BuildMatchFingerprint(JsonElement gameData, JsonElement allPlayers)
    {
        var entries = new List<string>();
        foreach (var player in allPlayers.EnumerateArray())
        {
            var summonerName = GetString(player, "summonerName").Trim();
            if (!string.IsNullOrWhiteSpace(summonerName))
                entries.Add(summonerName.ToUpperInvariant());
        }

        entries.Sort(StringComparer.Ordinal);

        var gameId = GetFirstString(gameData, "gameId", "gameID", "gameUniqueId").Trim();
        var gameStartTime = GetFirstString(gameData, "gameStartTime", "gameStartTimestamp").Trim();
        var queueId = GetString(gameData, "gameQueueConfigId").Trim();
        var mapId = GetString(gameData, "mapNumber").Trim();
        var mode = GetString(gameData, "gameMode").Trim().ToUpperInvariant();
        var raw = string.Join("\n", new[]
        {
            gameId,
            gameStartTime,
            queueId,
            mapId,
            mode,
            string.Join("\n", entries)
        });

        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes);
    }

    private static string GetFirstString(JsonElement el, params string[] names)
    {
        foreach (var name in names)
        {
            var value = GetString(el, name);
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return "";
    }

    public void Dispose() => _http.Dispose();
}
