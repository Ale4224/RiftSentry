using System.IO;
using System.Net.Http;

namespace RiftSentry.Services;

public sealed class AssetCacheService
{
    private readonly HttpClient _http;
    private readonly string _root;

    public AssetCacheService(HttpClient http, string? assetsRoot = null)
    {
        _http = http;
        _root = assetsRoot ?? Path.Combine(AppContext.BaseDirectory, "assets");
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public async Task<string> GetOrDownloadAsync(string url, string fileName, CancellationToken cancellationToken = default)
    {
        var safe = SanitizeFileName(fileName);
        var path = Path.Combine(_root, safe);
        if (File.Exists(path))
            return path;

        using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var fs = File.Create(path);
        await response.Content.CopyToAsync(fs, cancellationToken).ConfigureAwait(false);
        return path;
    }

    private static string SanitizeFileName(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        return name;
    }
}
