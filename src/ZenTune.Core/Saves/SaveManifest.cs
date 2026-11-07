using System.Net;
using System.Text.Json;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace ZenTune.Core.Saves;

/// <summary>One game in the save database: where its save files live (Windows only), with placeholders like &lt;winDocuments&gt;.</summary>
public sealed class SaveGameEntry
{
    public string Name { get; set; } = "";
    public int SteamId { get; set; }
    public List<string> InstallDirs { get; set; } = [];
    public List<string> Paths { get; set; } = [];
}

/// <summary>The slimmed-down save database ZenTune keeps on disk (only Windows save paths).</summary>
public sealed class SaveDatabase
{
    public List<SaveGameEntry> Games { get; set; } = [];
    public DateTime Updated { get; set; }
}

/// <summary>
/// Knows where the save files of thousands of games are. The data comes from the Ludusavi manifest
/// (MIT license, compiled from PCGamingWiki): https://github.com/mtkennerly/ludusavi-manifest
/// It is downloaded once (a few MB), reduced to Windows save paths and cached in %LOCALAPPDATA%\ZenTune.
/// Nothing about the user is sent: it is a plain download of a public file.
/// </summary>
public static class SaveManifest
{
    public const string Url = "https://raw.githubusercontent.com/mtkennerly/ludusavi-manifest/master/data/manifest.yaml";

    private static string EtagFile => Path.Combine(AppPaths.Root, "saves-manifest.etag");
    private static string SlimFile => Path.Combine(AppPaths.Root, "saves-manifest.json");

    /// <summary>The database from the last download, or null if it was never downloaded.</summary>
    public static SaveDatabase? LoadCached()
    {
        try
        {
            if (!File.Exists(SlimFile)) return null;
            var db = JsonSerializer.Deserialize<SaveDatabase>(File.ReadAllText(SlimFile));
            return db is { Games.Count: > 0 } ? db : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Downloads the newest database. If nothing changed since the last download (checked with the ETag), the cached one is kept.
    /// Throws when there is no internet; the caller can fall back to <see cref="LoadCached"/>.
    /// </summary>
    public static async Task<SaveDatabase> UpdateAsync(CancellationToken ct = default)
    {
        var cached = LoadCached();

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("ZenTune");

        using var request = new HttpRequestMessage(HttpMethod.Get, Url);
        if (cached is not null && File.Exists(EtagFile))
        {
            var etag = File.ReadAllText(EtagFile).Trim();
            if (etag.Length > 0) request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

        if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
        {
            cached.Updated = DateTime.Now;
            File.WriteAllText(SlimFile, JsonSerializer.Serialize(cached));
            return cached;
        }

        response.EnsureSuccessStatusCode();

        var temp = Path.Combine(AppPaths.Root, "saves-manifest.download.yaml");
        try
        {
            await using (var file = File.Create(temp))
                await response.Content.CopyToAsync(file, ct);

            var db = await Task.Run(() => Parse(temp), ct);
            if (db.Games.Count == 0)
                throw new InvalidOperationException("The downloaded game list was empty, so it was not used.");

            File.WriteAllText(SlimFile, JsonSerializer.Serialize(db));

            var tag = response.Headers.ETag;
            if (tag is not null)
                File.WriteAllText(EtagFile, (tag.IsWeak ? "W/" : "") + tag.Tag);

            return db;
        }
        finally
        {
            try { File.Delete(temp); } catch { /* the temp file is removed next time */ }
        }
    }

    // ---------------------------------------------------------------- Parsing

    private sealed class ManifestGame
    {
        public Dictionary<string, ManifestFile?>? Files { get; set; }
        public Dictionary<string, object?>? InstallDir { get; set; }
        public ManifestSteam? Steam { get; set; }
        public string? Alias { get; set; }
    }

    private sealed class ManifestFile
    {
        public List<string>? Tags { get; set; }
        public List<ManifestWhen>? When { get; set; }
    }

    private sealed class ManifestWhen
    {
        public string? Os { get; set; }
        public string? Store { get; set; }
    }

    private sealed class ManifestSteam
    {
        public int? Id { get; set; }
    }

    private static SaveDatabase Parse(string yamlFile)
    {
        var deserializer = new DeserializerBuilder()
            .WithNamingConvention(CamelCaseNamingConvention.Instance)
            .IgnoreUnmatchedProperties()
            .Build();

        Dictionary<string, ManifestGame> all;
        using (var reader = File.OpenText(yamlFile))
            all = deserializer.Deserialize<Dictionary<string, ManifestGame>>(reader) ?? [];

        var db = new SaveDatabase { Updated = DateTime.Now };

        foreach (var (name, game) in all)
        {
            // An alias only points at another entry
            if (game is null || !string.IsNullOrEmpty(game.Alias) || game.Files is null) continue;

            var paths = new List<string>();
            foreach (var (path, entry) in game.Files)
            {
                // Only real save data. Settings files stay with the Games page.
                if (entry?.Tags is null || !entry.Tags.Contains("save", StringComparer.OrdinalIgnoreCase)) continue;

                // No condition means every system. Otherwise one condition must allow Windows.
                if (entry.When is { Count: > 0 } &&
                    !entry.When.Any(w => string.IsNullOrEmpty(w.Os) || w.Os.Equals("windows", StringComparison.OrdinalIgnoreCase)))
                    continue;

                paths.Add(path);
            }
            if (paths.Count == 0) continue;

            db.Games.Add(new SaveGameEntry
            {
                Name = name,
                SteamId = game.Steam?.Id ?? 0,
                InstallDirs = game.InstallDir?.Keys.ToList() ?? [],
                Paths = paths,
            });
        }

        return db;
    }
}
