using System.Diagnostics;
using System.Runtime.Versioning;
using ZenTune.Core.Advanced;

namespace ZenTune.Core.Checks;

/// <summary>
/// GameInput 4.0.302 (installed by Gaming Services) makes Call of Duty: BO7 / Warzone freeze on start and
/// crash with error code 0x00001337 (known problem, microsoftconnect/GameInput issue 53). GameInput 3.5.283 works.
/// This check shows the installed version and offers to put 3.5.283 back.
/// Gaming Services can install 4.x again after a restart, so the fix may need to be run again.
/// </summary>
[SupportedOSPlatform("windows")]
public static class GameInputCheck
{
    public const string GoodVersion = "3.5.283";

    private static string DllPath => Path.Combine(Environment.SystemDirectory, "GameInputRedist.dll");

    /// <summary>The file version of GameInputRedist.dll, or null when it is not installed or cannot be read.</summary>
    public static string? InstalledVersion()
    {
        try
        {
            return File.Exists(DllPath) ? FileVersionInfo.GetVersionInfo(DllPath).FileVersion : null;
        }
        catch { return null; }
    }

    public static IEnumerable<CheckResult> Run()
    {
        var version = InstalledVersion();
        if (string.IsNullOrEmpty(version)) yield break; // not installed: nothing to warn about

        if (version.StartsWith("4.", StringComparison.Ordinal))
        {
            yield return new CheckResult
            {
                Id = "gameinput",
                Title = $"GameInput {version} can stop BO7 / Warzone from starting",
                Detail = "If Call of Duty: BO7 or Warzone will not start (error code 0x00001337, or it freezes on the loading screen), " +
                         $"this is a known problem with this version of GameInput. Version {GoodVersion} works. " +
                         "The fix installs the older version. Windows may put the new one back after a restart; then press the button again.",
                Status = CheckStatus.Info,
                ActionLabel = $"Install GameInput {GoodVersion}",
                Action = InstallGoodVersion,
                ActionChangesSystem = false,
                ConfirmText = "Close Call of Duty, Steam and Battle.net first.\n\n" +
                              $"ZenTune will remove the current GameInput and install version {GoodVersion} with winget. " +
                              "Windows will ask for admin rights once. " +
                              "This is not added to the undo list. Windows or Gaming Services can install the new version again later.",
                ActionDoneMessage = $"GameInput {GoodVersion} is installed. Start the game again. If Windows puts the new version back after a restart, press the button again.",
            };
        }
        else if (version.StartsWith(GoodVersion, StringComparison.Ordinal))
        {
            yield return new CheckResult
            {
                Id = "gameinput",
                Title = $"GameInput {version}",
                Detail = "The version that works with BO7 / Warzone. Windows may replace it with a newer one after a restart; this page will warn you if it does.",
                Status = CheckStatus.Good,
            };
        }
    }

    /// <summary>Removes the current GameInput and installs the known good version with winget. Runs as admin.</summary>
    private static void InstallGoodVersion()
    {
        string body = $$"""
            $dll = Join-Path $env:SystemRoot 'System32\GameInputRedist.dll'
            $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
            if (-not $winget) {
                Fail 'winget was not found on this PC, so GameInput could not be changed.'
            } else {
                Log 'Removing the current GameInput...'
                $out = & winget.exe uninstall Microsoft.GameInput --silent --accept-source-agreements 2>&1 | Out-String
                Log ('uninstall exit code ' + $LASTEXITCODE)
                Log $out

                Log 'Installing GameInput {{GoodVersion}}...'
                $out = & winget.exe install Microsoft.GameInput --version {{GoodVersion}} --source winget --silent --accept-source-agreements --accept-package-agreements 2>&1 | Out-String
                $code = $LASTEXITCODE
                Log ('install exit code ' + $code)
                Log $out
                if ($code -ne 0) { Fail ('winget install failed with exit code ' + $code) }
            }

            $now = $null
            if (Test-Path -LiteralPath $dll) { $now = (Get-Item -LiteralPath $dll).VersionInfo.FileVersion }
            Log ('GameInputRedist.dll version now: ' + $now)
            if (-not $now -or -not $now.StartsWith('{{GoodVersion}}')) {
                Fail ('GameInput is version ' + $now + ', not {{GoodVersion}}.')
            }
            """;

        var result = ScriptRunner.Run("gameinput-fix", body, elevated: true, TimeSpan.FromMinutes(5));
        if (!result.Ok)
        {
            string last = result.Log
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .LastOrDefault(l => l.StartsWith("ERROR:", StringComparison.Ordinal)) ?? "Unknown error.";
            throw new InvalidOperationException(
                $"{last} GameInput may be missing now, so press the button again. " +
                $"The log is in {Path.GetDirectoryName(result.ScriptPath)}.");
        }
    }
}
