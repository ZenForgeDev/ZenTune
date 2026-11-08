using System.ComponentModel;
using System.Diagnostics;

namespace ZenTune.Core.Driver;

/// <summary>What to install. NVIDIA app, GeForce Experience, ShadowPlay and telemetry are always left out.</summary>
public sealed record InstallOptions(bool Audio, bool PhysX, bool Clean);

/// <summary>Which folders of the unpacked driver are kept and which are removed before setup runs.</summary>
public sealed record InstallPlan(string Folder, IReadOnlyList<string> Keep, IReadOnlyList<string> Remove);

/// <summary>
/// Unpacks NVIDIA's installer with 7-Zip, removes the parts you don't want and runs NVIDIA's own setup.exe silently.
/// NVIDIA's installer does not take component choices itself, so the parts are removed from the unpacked folder first.
/// </summary>
public static class NvidiaInstaller
{
    // Parts that are always left out. Matched on the start of the folder name, so names that change a little between versions still match.
    private static readonly string[] AlwaysRemove = ["GFExperience", "NvTelemetry", "Telemetry", "ShadowPlay", "3DVision", "Update.Core", "NvCamera"];

    // NVIDIA's setup still reads the licence files in the NvApp folder, so those stay
    private static readonly string[] NvAppKeep = ["EULA", "PrivacyPolicy", "Unified_EULA"];

    public static string? FindSevenZip()
    {
        foreach (var root in new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) })
        {
            if (string.IsNullOrEmpty(root)) continue;
            var path = Path.Combine(root, "7-Zip", "7z.exe");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>Installs 7-Zip with winget (which checks the download itself). Returns true when 7-Zip can be found afterwards.</summary>
    public static async Task<bool> InstallSevenZipAsync(CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo("winget.exe") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "install", "--id", "7zip.7zip", "-e", "--silent", "--accept-package-agreements", "--accept-source-agreements" })
            psi.ArgumentList.Add(a);

        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start winget.");
            await p.WaitForExitAsync(ct);
        }
        catch (Win32Exception)
        {
            throw new InvalidOperationException("winget was not found on this PC. Install 7-Zip from 7-zip.org and try again.");
        }
        return FindSevenZip() is not null;
    }

    /// <summary>Unpacks the installer into %LOCALAPPDATA%\ZenTune\Drivers\unpacked and returns that folder.</summary>
    public static async Task<string> ExtractAsync(string installerExe, string sevenZip, CancellationToken ct = default)
    {
        string target = Path.Combine(AppPaths.Root, "Drivers", "unpacked");
        if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
        Directory.CreateDirectory(target);

        var psi = new ProcessStartInfo(sevenZip)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        psi.ArgumentList.Add("x");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-o" + target);
        psi.ArgumentList.Add(installerExe);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start 7-Zip.");
        var output = p.StandardOutput.ReadToEndAsync(ct);
        var error = p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);

        if (p.ExitCode != 0)
        {
            string tail = (await error).Trim();
            throw new InvalidOperationException($"7-Zip could not unpack the installer (code {p.ExitCode}). {tail}");
        }
        _ = await output;

        if (!File.Exists(Path.Combine(target, "setup.exe")))
            throw new InvalidOperationException("The unpacked driver has no setup.exe. NVIDIA may have changed how the installer is built.");

        return target;
    }

    /// <summary>Works out what to keep and what to remove. Nothing is deleted yet.</summary>
    public static InstallPlan Plan(string folder, InstallOptions options)
    {
        var keep = new List<string>();
        var remove = new List<string>();

        foreach (var dir in Directory.GetDirectories(folder).Select(d => Path.GetFileName(d)!).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            bool drop =
                dir.Equals("NvApp", StringComparison.OrdinalIgnoreCase) ||
                AlwaysRemove.Any(p => dir.StartsWith(p, StringComparison.OrdinalIgnoreCase)) ||
                (!options.Audio && dir.Equals("HDAudio", StringComparison.OrdinalIgnoreCase)) ||
                (!options.PhysX && dir.Equals("PhysX", StringComparison.OrdinalIgnoreCase));

            (drop ? remove : keep).Add(dir);
        }

        // The graphics driver and NVIDIA's installer core are the two parts setup cannot run without
        foreach (var required in new[] { "Display.Driver", "NVI2" })
        {
            if (!keep.Contains(required, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException($"The unpacked driver has no {required} folder, so ZenTune can't be sure how to install it.");
        }

        return new InstallPlan(folder, keep, remove);
    }

    /// <summary>Removes the folders in <see cref="InstallPlan.Remove"/>.</summary>
    public static void Prune(InstallPlan plan)
    {
        foreach (var name in plan.Remove)
        {
            string path = Path.Combine(plan.Folder, name);
            if (!Directory.Exists(path)) continue;

            if (name.Equals("NvApp", StringComparison.OrdinalIgnoreCase))
            {
                foreach (var sub in Directory.GetDirectories(path))
                    if (!NvAppKeep.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                        Directory.Delete(sub, recursive: true);

                foreach (var file in Directory.GetFiles(path))
                    if (!file.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        File.Delete(file);
            }
            else
            {
                Directory.Delete(path, recursive: true);
            }
        }
    }

    /// <summary>Runs NVIDIA's setup.exe silently (one admin prompt) and returns its exit code. 0 = done, 1 = done, restart needed.</summary>
    public static async Task<int> RunSetupAsync(string folder, InstallOptions options, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(Path.Combine(folder, "setup.exe"))
        {
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = folder,
            Arguments = "-s -noreboot -noeula -nofinish" + (options.Clean ? " -clean" : ""),
        };

        try
        {
            using var p = Process.Start(psi) ?? throw new InvalidOperationException("Could not start NVIDIA's setup.");
            await p.WaitForExitAsync(ct);
            return p.ExitCode;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("The admin prompt was cancelled, so nothing was installed.");
        }
    }
}
