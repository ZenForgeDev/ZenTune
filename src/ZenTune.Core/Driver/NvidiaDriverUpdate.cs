using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using ZenTune.Core.Hardware;

namespace ZenTune.Core.Driver;

/// <summary>A driver package NVIDIA offers for one graphics card.</summary>
public sealed record NvidiaDriverRelease(
    string Version, string Name, string Url, string Size, string Released, bool IsWhql, bool IsBeta, string Kind);

/// <summary>
/// The result of "check for a new driver". When <see cref="Ok"/> is false, ZenTune is not sure it has found the
/// right driver for this card and offers nothing, <see cref="Message"/> says why.
/// </summary>
public sealed record NvidiaDriverCheck(
    bool Ok,
    string Message,
    string GpuName,
    string? MatchedName,
    string? Installed,
    NvidiaDriverRelease? Latest,
    bool UpdateAvailable);

/// <summary>
/// Finds the newest NVIDIA driver for the graphics card in this PC. It uses the same two web services as NVIDIA's
/// own "manual driver search" page. ZenTune never guesses: the card name must match NVIDIA's list, desktop and laptop
/// drivers are kept apart, and the driver's own list of supported cards must contain this card.
/// </summary>
public static class NvidiaDriverUpdate
{
    private const string LookupUrl = "https://www.nvidia.com/Download/API/lookupValueSearch.aspx?TypeID=3";
    private const string DriverServiceUrl = "https://gfwsl.geforce.com/services_toolkit/services/com/nvidia/services/AjaxDriverService.php";

    // NVIDIA's operating system ids (lookupValueSearch TypeID=4)
    private const int OsWindows10x64 = 57;
    private const int OsWindows11 = 135;

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        c.DefaultRequestHeaders.UserAgent.ParseAdd("ZenTune");
        return c;
    }

    // ---------------------------------------------------------------- Check

    /// <param name="studio">False for the Game Ready driver (made for games), true for the Studio driver.</param>
    public static async Task<NvidiaDriverCheck> CheckAsync(GpuInfo gpu, bool isLaptop, bool studio, CancellationToken ct = default)
    {
        string? installed = string.IsNullOrWhiteSpace(gpu.DriverVersion) ? null : gpu.DriverVersion.Trim();

        NvidiaDriverCheck Fail(string message, string? matched = null) =>
            new(false, message, gpu.Name, matched, installed, null, false);

        if (gpu.Vendor != GpuVendor.Nvidia)
            return Fail("This graphics card is not made by NVIDIA.");

        try
        {
            // 1. Find the card in NVIDIA's own list
            var xml = await Http.GetStringAsync(LookupUrl, ct);
            var doc = XDocument.Parse(xml);

            string wanted = Norm(gpu.Name);
            var candidates = doc.Descendants("LookupValue")
                .Select(e => (Name: ((string?)e.Element("Name") ?? "").Trim(), Id: ((string?)e.Element("Value") ?? "").Trim()))
                .Where(e => e.Id.Length > 0 && Norm(e.Name) == wanted)
                .DistinctBy(e => e.Id)
                .ToList();

            if (candidates.Count == 0)
                return Fail($"\"{gpu.Name}\" is not in NVIDIA's list of graphics cards, so ZenTune can't be sure which driver is right. Nothing is offered.");

            int os = Environment.OSVersion.Version.Build >= 22000 ? OsWindows11 : OsWindows10x64;
            bool wantNotebook = isLaptop || gpu.Name.Contains("Laptop", StringComparison.OrdinalIgnoreCase)
                                         || gpu.Name.Contains("Max-Q", StringComparison.OrdinalIgnoreCase);
            string wantKind = wantNotebook ? "Notebook" : "Desktop";

            // 2. Ask for the driver of every matching entry (a few cards are listed as both desktop and laptop)
            var found = new List<(string Matched, NvidiaDriverRelease Release, HashSet<string> Products)>();
            foreach (var c in candidates)
            {
                var one = await LookupDriverAsync(c.Id, os, studio, ct);
                if (one is { } o) found.Add((c.Name, o.Release, o.Products));
            }

            if (found.Count == 0)
                return Fail($"NVIDIA has no {(studio ? "Studio" : "Game Ready")} driver for \"{gpu.Name}\" on this version of Windows.", candidates[0].Name);

            // 3. Keep desktop and laptop apart
            var right = found.Where(f => f.Release.Kind == wantKind).ToList();
            if (right.Count == 0 && found.Count == 1 && found[0].Release.Kind.Length == 0)
                right = found; // NVIDIA's file name does not say which kind it is, and there is only one choice

            if (right.Count == 0)
                return Fail($"This PC needs the {wantKind.ToLowerInvariant()} driver, but NVIDIA only lists a {found[0].Release.Kind.ToLowerInvariant()} driver for this card name. Nothing is offered.", found[0].Matched);

            if (right.Select(r => r.Release.Url).Distinct().Count() > 1)
                return Fail("NVIDIA lists more than one driver for this card name and ZenTune can't tell which one is right. Nothing is offered.", right[0].Matched);

            var pick = right[0];

            // 4. The driver's own list of supported cards must contain this card
            if (!pick.Products.Contains(wanted))
                return Fail($"Driver {pick.Release.Version} is the newest, but its list of supported cards does not include \"{gpu.Name}\". Nothing is offered.", pick.Matched);

            bool update = installed is null || CompareVersions(pick.Release.Version, installed) > 0;
            string message = update
                ? $"A newer driver is available: {pick.Release.Version}."
                : "You have the newest driver.";
            return new NvidiaDriverCheck(true, message, gpu.Name, pick.Matched, installed, pick.Release, update);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return Fail($"Could not reach NVIDIA's driver service: {ex.Message}");
        }
    }

    private static async Task<(NvidiaDriverRelease Release, HashSet<string> Products)?> LookupDriverAsync(
        string productId, int os, bool studio, CancellationToken ct)
    {
        string url = $"{DriverServiceUrl}?func=DriverManualLookup&pfid={productId}&osID={os}&dch=1&beta=0&upCRD={(studio ? 1 : 0)}&numberOfResults=5";
        var json = await Http.GetStringAsync(url, ct);

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("IDS", out var ids) || ids.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var item in ids.EnumerateArray())
        {
            if (!item.TryGetProperty("downloadInfo", out var info) || info.ValueKind != JsonValueKind.Object) continue;

            string version = Str(info, "Version");
            string link = Str(info, "DownloadURL");
            if (version.Length == 0 || link.Length == 0) continue;
            if (!link.StartsWith("http", StringComparison.OrdinalIgnoreCase)) link = "https://" + link;

            string kind = link.Contains("-notebook-", StringComparison.OrdinalIgnoreCase) ? "Notebook"
                        : link.Contains("-desktop-", StringComparison.OrdinalIgnoreCase) ? "Desktop"
                        : "";

            var products = new HashSet<string>();
            if (info.TryGetProperty("series", out var series) && series.ValueKind == JsonValueKind.Array)
            {
                foreach (var s in series.EnumerateArray())
                {
                    if (!s.TryGetProperty("products", out var list) || list.ValueKind != JsonValueKind.Array) continue;
                    foreach (var p in list.EnumerateArray())
                    {
                        string name = Str(p, "productName");
                        if (name.Length > 0) products.Add(Norm(Uri.UnescapeDataString(name)));
                    }
                }
            }

            var release = new NvidiaDriverRelease(
                version, Str(info, "Name"), link, Str(info, "DownloadURLFileSize"), Str(info, "ReleaseDateTime"),
                Str(info, "IsWHQL") == "1", Str(info, "IsBeta") == "1", kind);
            return (release, products);
        }
        return null;
    }

    // ---------------------------------------------------------------- Download and verify

    /// <summary>Downloads the installer to %LOCALAPPDATA%\ZenTune\Drivers and returns its path.</summary>
    public static async Task<string> DownloadAsync(NvidiaDriverRelease release, IProgress<double>? progress, CancellationToken ct = default)
    {
        string dir = Path.Combine(AppPaths.Root, "Drivers");
        Directory.CreateDirectory(dir);

        string name = Path.GetFileName(new Uri(release.Url).LocalPath);
        if (string.IsNullOrWhiteSpace(name) || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = $"nvidia-driver-{release.Version}.exe";

        string path = Path.Combine(dir, name);
        string part = path + ".part";

        using var response = await Http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? 0;

        await using (var src = await response.Content.ReadAsStreamAsync(ct))
        await using (var dst = File.Create(part))
        {
            var buffer = new byte[1024 * 256];
            long done = 0;
            int read;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0) progress?.Report(done / (double)total);
            }
        }

        File.Move(part, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Asks Windows to check the installer's digital signature (that the file is whole and made by NVIDIA).
    /// </summary>
    public static async Task<(bool Ok, string Detail)> VerifySignatureAsync(string path, CancellationToken ct = default)
    {
        try
        {
            var psi = new ProcessStartInfo("powershell.exe")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-NonInteractive");
            psi.ArgumentList.Add("-Command");
            psi.ArgumentList.Add($"$s = Get-AuthenticodeSignature -LiteralPath '{path.Replace("'", "''")}'; $s.Status; $s.SignerCertificate.Subject");

            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start PowerShell.");
            string output = await p.StandardOutput.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);

            var lines = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
            string status = lines.Length > 0 ? lines[0].Trim() : "Unknown";
            string subject = lines.Length > 1 ? lines[1].Trim() : "";

            bool ok = status == "Valid" && subject.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase);
            return (ok, ok ? "Signed by NVIDIA Corporation." : $"Signature check failed (status: {status}).");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return (false, $"Could not check the signature: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- Helpers

    /// <summary>Makes "NVIDIA GeForce RTX 4070 SUPER" and "GeForce RTX 4070 Super" the same text.</summary>
    internal static string Norm(string name)
    {
        string s = Format.CleanName(name ?? "");
        s = Regex.Replace(s, @"^NVIDIA\s+", "", RegexOptions.IgnoreCase);
        s = s.Replace("with Max-Q Design", "", StringComparison.OrdinalIgnoreCase);
        return Regex.Replace(s, @"\s+", " ").Trim().ToLowerInvariant();
    }

    /// <summary>Compares "621.14" with "566.36". Positive when <paramref name="a"/> is newer.</summary>
    internal static int CompareVersions(string a, string b)
    {
        var pa = a.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        var pb = b.Split('.').Select(x => int.TryParse(x, out var n) ? n : 0).ToArray();
        for (int i = 0; i < Math.Max(pa.Length, pb.Length); i++)
        {
            int x = i < pa.Length ? pa[i] : 0;
            int y = i < pb.Length ? pb[i] : 0;
            if (x != y) return x.CompareTo(y);
        }
        return 0;
    }

    private static string Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return "";
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "1",
            JsonValueKind.False => "0",
            _ => "",
        };
    }
}
