using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;
using ZenTune.Core.Changes;

namespace ZenTune.Core.Advanced;

public enum RegHive { CurrentUser, LocalMachine }

/// <summary>One registry value a tweak sets. Value null means the value is removed.</summary>
public sealed record RegSet(RegHive Hive, string Path, string Name, RegistryValueKind Kind, string? Value);

/// <summary>One service a tweak changes. StartType: Automatic, AutomaticDelayedStart, Manual or Disabled.</summary>
public sealed record SvcSet(string Name, string StartType);

public enum TweakState { NotApplied, Applied, AppliedByZenTune }

/// <summary>
/// A Windows tweak from Chris Titus Tech's WinUtil (MIT license), adapted for ZenTune.
/// Every tweak records the old values before it changes anything, so Undo puts back exactly what was there.
/// </summary>
public sealed class AdvancedTweak
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required string Description { get; init; }
    public required string Group { get; init; }

    /// <summary>What the user gives up, shown in orange. Null when there is no real downside.</summary>
    public string? Caution { get; init; }

    /// <summary>When set, the app asks the user to confirm with this text before applying.</summary>
    public string? Confirm { get; init; }

    public RegSet[] Registry { get; init; } = [];
    public SvcSet[] Services { get; init; } = [];

    /// <summary>Extra PowerShell run after the registry and service changes.</summary>
    public string? ApplyScript { get; init; }

    /// <summary>Extra PowerShell run after the old registry values are restored.</summary>
    public string? UndoScript { get; init; }

    public bool RestartNeeded { get; init; }
    public bool SignOutNeeded { get; init; }

    public bool NeedsAdmin =>
        Registry.Any(r => r.Hive == RegHive.LocalMachine) || Services.Length > 0 || ApplyScript is not null;
}

[SupportedOSPlatform("windows")]
public static class AdvancedTweaks
{
    private const string JournalKind = "tweak";
    private const string GroupGaming = "Gaming and performance";
    private const string GroupPrivacy = "Privacy and background activity";
    private const string GroupLook = "Look";
    private const string GroupSystem = "System and storage";

    private static RegSet Cu(string path, string name, int value) => new(RegHive.CurrentUser, path, name, RegistryValueKind.DWord, value.ToString());
    private static RegSet CuS(string path, string name, string value) => new(RegHive.CurrentUser, path, name, RegistryValueKind.String, value);
    private static RegSet Lm(string path, string name, int value) => new(RegHive.LocalMachine, path, name, RegistryValueKind.DWord, value.ToString());
    private static RegSet LmS(string path, string name, string value) => new(RegHive.LocalMachine, path, name, RegistryValueKind.String, value);

    private static long TotalMemoryKb()
    {
        var status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status) ? (long)(status.ullTotalPhys / 1024) : 0;
    }

    public static IReadOnlyList<AdvancedTweak> All { get; } = Build();

    private static List<AdvancedTweak> Build()
    {
        long memKb = TotalMemoryKb();

        return
        [
            // ------------------------------------------------------------ Gaming and performance
            new AdvancedTweak
            {
                Id = "bgapps",
                Group = GroupGaming,
                Title = "Stop background apps",
                Description = "Microsoft Store apps (Weather, Mail, Xbox and others) stop running in the background, so they don't use CPU, memory or network while you play.",
                Caution = "Store apps only update their live tiles and notifications while they are open.",
                Registry = [Cu(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1)],
            },
            new AdvancedTweak
            {
                Id = "visualfx",
                Group = GroupGaming,
                Title = "Visual effects: best performance",
                Description = "Turns off window animations, fading menus and shadows in Windows. The desktop feels snappier and the GPU has a little less to do outside games.",
                Caution = "Windows looks plainer. Sign out and back in to see every change.",
                SignOutNeeded = true,
                Registry =
                [
                    CuS(@"Control Panel\Desktop", "DragFullWindows", "0"),
                    CuS(@"Control Panel\Desktop", "MenuShowDelay", "200"),
                    CuS(@"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0"),
                    CuS(@"Control Panel\Keyboard", "KeyboardDelay", "0"),
                    Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ListviewAlphaSelect", 0),
                    Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ListviewShadow", 0),
                    Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarAnimations", 0),
                    Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 3),
                    Cu(@"Software\Microsoft\Windows\DWM", "EnableAeroPeek", 0),
                    new(RegHive.CurrentUser, @"Control Panel\Desktop", "UserPreferencesMask", RegistryValueKind.Binary, "144,18,3,128,16,0,0,0"),
                ],
            },
            // "Turn off Multiplane Overlay" (WinUtil's OverlayTestMode tweak) was removed in 0.4: on Windows 11 24H2
            // it made the Desktop Window Manager crash over and over (black, flickering screen) when a game started.
            // Old changes can still be undone from the System check page, because the journal keeps the old values.
            new AdvancedTweak
            {
                Id = "stickykeys",
                Group = GroupGaming,
                Title = "No Sticky Keys pop-up",
                Description = "Stops the \"Do you want to turn on Sticky Keys?\" box when you press Shift five times, which can kick you out of a game.",
                Registry = [CuS(@"Control Panel\Accessibility\StickyKeys", "Flags", "506")],
            },
            new AdvancedTweak
            {
                Id = "hibernate",
                Group = GroupGaming,
                Title = "Turn off hibernation",
                Description = "Deletes hiberfil.sys, which is usually as big as 40% of your memory, and gives that disk space back. Hibernation is meant for laptops.",
                Caution = "Fast Startup uses hibernation, so cold boots can take a few seconds longer. Sleep still works.",
                Registry =
                [
                    Lm(@"System\CurrentControlSet\Control\Session Manager\Power", "HibernateEnabled", 0),
                    Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\FlyoutMenuSettings", "ShowHibernateOption", 0),
                ],
                ApplyScript = "Try-Run 'powercfg /hibernate off' { powercfg.exe /hibernate off }",
                UndoScript = """
                    $h = (Get-ItemProperty -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\System\CurrentControlSet\Control\Session Manager\Power' -Name HibernateEnabled -ErrorAction SilentlyContinue).HibernateEnabled
                    if ($h -eq 1) { Try-Run 'powercfg /hibernate on' { powercfg.exe /hibernate on } }
                    """,
            },
            new AdvancedTweak
            {
                Id = "endtask",
                Group = GroupGaming,
                Title = "\"End task\" on the taskbar",
                Description = "Adds End task when you right-click an app on the taskbar. Handy when a game freezes and won't close.",
                Registry = [Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced\TaskbarDeveloperSettings", "TaskbarEndTask", 1)],
            },
            new AdvancedTweak
            {
                Id = "memintegrity",
                Group = GroupGaming,
                Title = "Turn off Memory integrity",
                Description = "Turns off Memory integrity (HVCI, part of Core isolation). Benchmarks measured about 4–6% lower FPS on average with it on, and more in CPU-limited games. Windows can turn it back on after big updates, so check Windows Security → Device security now and then.",
                Caution = "Removes a Windows protection against malicious or vulnerable drivers. Restart needed. If the setting is locked by your UEFI/BIOS, Windows keeps it on.",
                Confirm = "Memory integrity stops malicious or vulnerable drivers from running in the Windows kernel. Turning it off can give a few percent more FPS but makes the PC less safe against that kind of attack. Undo turns it back on. Continue?",
                RestartNeeded = true,
                Registry = [Lm(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0)],
                ApplyScript = """
                    $locked = (Get-ItemProperty -LiteralPath 'Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity' -Name Locked -ErrorAction SilentlyContinue).Locked
                    if ($locked -eq 1) { Fail 'Memory integrity is locked by UEFI, so Windows keeps it on. Turn it off in Windows Security -> Device security -> Core isolation details; Secure Boot may need to be off.' }
                    """,
            },
            new AdvancedTweak
            {
                Id = "bitlocker",
                Group = GroupGaming,
                Title = "Turn off drive encryption (BitLocker)",
                Description = "Decrypts your Windows drive (the one Windows is installed on) if BitLocker or Device encryption protects it, and tells Windows not to turn it on by itself again. Same command as WinUtil uses. Encryption adds CPU work to every disk read and write; one report measured SSD speed up to 45% lower with software encryption. Decrypting runs in the background and can take a while.",
                Caution = "Your data is no longer protected if the PC is stolen. Undo does NOT re-encrypt the drive; it only lets Windows encrypt automatically again. Other drives are left alone.",
                Confirm = "This starts decrypting your Windows drive and removes its BitLocker keys. Anyone with the PC can then read your files. ZenTune cannot undo the decryption: to encrypt again you have to set up BitLocker or Device encryption in Windows. Continue?",
                Registry = [Lm(@"SYSTEM\CurrentControlSet\Control\BitLocker", "PreventDeviceEncryption", 1)],
                ApplyScript = """
                    $vols = @(Get-BitLockerVolume -MountPoint $env:SystemDrive -ErrorAction SilentlyContinue)
                    if ($vols.Count -eq 0) { Log 'no BitLocker drive found' }
                    foreach ($v in $vols) {
                        $mp = $v.MountPoint
                        if ($v.VolumeStatus -ne 'FullyDecrypted' -or $v.ProtectionStatus -eq 'On') {
                            Try-Run ('start decrypting ' + $mp) { Disable-BitLocker -MountPoint $mp -ErrorAction Stop | Out-Null }
                        } else { Log ('drive ' + $mp + ' is not encrypted') }
                    }
                    """,
            },

            // ------------------------------------------------------------ Privacy and background activity
            new AdvancedTweak
            {
                Id = "telemetry",
                Group = GroupPrivacy,
                Title = "Turn off telemetry",
                Description = "Stops Windows from sending usage data, typing data and ad tracking to Microsoft, and turns off the telemetry service that runs in the background.",
                Registry =
                [
                    Cu(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0),
                    Cu(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0),
                    Cu(@"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", 0),
                    Cu(@"Software\Microsoft\Input\TIPC", "Enabled", 0),
                    Cu(@"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", 1),
                    Cu(@"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", 1),
                    Cu(@"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", 0),
                    Cu(@"Software\Microsoft\Personalization\Settings", "AcceptedPrivacyPolicy", 0),
                    Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", 0),
                    Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackProgs", 0),
                    Cu(@"Software\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", 0),
                ],
                Services = [new SvcSet("DiagTrack", "Disabled")],
                Caution = "Online speech recognition turns off.",
            },
            new AdvancedTweak
            {
                Id = "activity",
                Group = GroupPrivacy,
                Title = "Turn off activity history",
                Description = "Windows stops saving and uploading a timeline of the apps and files you use. Clipboard history still works.",
                Registry =
                [
                    Lm(@"SOFTWARE\Policies\Microsoft\Windows\System", "EnableActivityFeed", 1),
                    Lm(@"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
                    Lm(@"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0),
                ],
            },
            new AdvancedTweak
            {
                Id = "consumer",
                Group = GroupPrivacy,
                Title = "No promoted apps",
                Description = "Stops Windows from installing suggested apps and games (like Candy Crush) on its own and shows fewer app suggestions.",
                Registry = [Lm(@"SOFTWARE\Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", 1)],
            },
            new AdvancedTweak
            {
                Id = "deliveryopt",
                Group = GroupPrivacy,
                Title = "Don't share updates with other PCs",
                Description = "Stops Windows from using your internet connection to upload updates to other PCs. Good for ping while you play.",
                Registry = [Lm(@"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", 0)],
            },
            new AdvancedTweak
            {
                Id = "location",
                Group = GroupPrivacy,
                Title = "Turn off location",
                Description = "Turns off location tracking and the location service for all apps.",
                Caution = "Weather, maps and Find my device can't see where you are.",
                Registry =
                [
                    LmS(@"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\location", "Value", "Deny"),
                    Lm(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Sensor\Overrides\{BFA794E4-F964-4FDB-90F6-51056BFE4B44}", "SensorPermissionState", 0),
                    Lm(@"SYSTEM\Maps", "AutoUpdateEnabled", 0),
                ],
                Services = [new SvcSet("lfsvc", "Disabled")],
            },
            new AdvancedTweak
            {
                Id = "wpbt",
                Group = GroupPrivacy,
                Title = "Block PC maker boot software (WPBT)",
                Description = "Stops your PC maker's firmware from installing its own software into Windows at every boot, without asking.",
                Caution = "Anti-theft software built into some laptops stops working.",
                Registry = [Lm(@"SYSTEM\CurrentControlSet\Control\Session Manager", "DisableWpbtExecution", 1)],
            },
            new AdvancedTweak
            {
                Id = "devicemeta",
                Group = GroupPrivacy,
                Title = "No extra apps for new devices",
                Description = "Stops Windows from downloading companion apps and ads when you plug in a mouse, headset or monitor.",
                Caution = "Some devices then need their app installed by hand.",
                Registry = [Lm(@"SOFTWARE\Policies\Microsoft\Windows\Device Metadata", "PreventDeviceMetadataFromNetwork", 1)],
            },
            new AdvancedTweak
            {
                Id = "bing",
                Group = GroupPrivacy,
                Title = "No Bing results in Start search",
                Description = "Searching in the Start menu only shows your own apps, files and settings, not web results.",
                Registry = [Cu(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0)],
            },
            new AdvancedTweak
            {
                Id = "widgets",
                Group = GroupPrivacy,
                Title = "Turn off Widgets",
                Description = "Turns off the Windows 11 Widgets board: the taskbar button, the Win+W shortcut and the Settings switch. Policy-based, so Windows updates are less likely to turn it back on.",
                Caution = "The weather and news board is gone. Restart Windows to see the change.",
                RestartNeeded = true,
                Registry = [Lm(@"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", 0)],
            },
            new AdvancedTweak
            {
                Id = "windowsai",
                Group = GroupPrivacy,
                Title = "Turn off Windows AI features",
                Description = "Uses Microsoft's own policies to turn off Recall (screen snapshots), the AI features in Notepad and the AI tools in Paint (Cocreator, Generative fill, Image Creator). Nothing is uninstalled, so Undo puts everything back.",
                Caution = "Some Windows screens may say a setting is managed by your organization. A few of these policies only work on certain Windows 11 builds. The Copilot app itself is not touched.",
                Registry =
                [
                    Lm(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", 1),
                    Lm(@"SOFTWARE\Policies\Microsoft\Windows\WindowsAI", "AllowRecallEnablement", 0),
                    Lm(@"SOFTWARE\Policies\WindowsNotepad", "DisableAIFeatures", 1),
                    Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableCocreator", 1),
                    Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableGenerativeFill", 1),
                    Lm(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\Paint", "DisableImageCreator", 1),
                ],
            },
            new AdvancedTweak
            {
                Id = "edgedebloat",
                Group = GroupPrivacy,
                Title = "Edge: fewer extras",
                Description = "Turns off Edge's shopping helper, Collections, rewards, recommendations, web widget, feedback prompts, diagnostic data and the first-run screen, and asks websites not to track you. Edge itself stays installed.",
                Caution = "Edge will say it is managed by your organization, because these are policies. Shopping helper, Collections and rewards stop working.",
                Registry =
                [
                    Lm(@"SOFTWARE\Policies\Microsoft\EdgeUpdate", "CreateDesktopShortcutDefault", 0),
                    Lm(@"SOFTWARE\Policies\Microsoft\Edge", "PersonalizationReportingEnabled", 0),
                    Lm(@"SOFTWARE\Policies\Microsoft\Edge", "ShowRecommendationsEnabled", 0),
                    Lm(@"SOFTWARE\Policies\Microsoft\Edge", "HideFirstRunExperience", 1),
                    Lm(@"SOFTWARE\Policies\Microsoft\Edge", "UserFeedbackAllowed", 0),
                    Lm(@"SOFTWARE\Policies\Microsoft\Edge", "ConfigureDoNotTrack", 1),
                    Lm(@"SOFTWARE\Policies\Microsoft\Edge", "AlternateErrorPagesEnabled", 0),
                    Lm(@"SOFTWARE\Policies\Microsoft\Edge", "EdgeCollectionsEnabled", 0),
                    Lm(@"SOFTWARE\Policies\Microsoft\Edge", "EdgeShoppingAssistantEnabled", 0),
                    Lm(@"SOFTWARE\Policies\Microsoft\Edge", "MicrosoftEdgeInsiderPromotionEnabled", 0),
                    Lm(@"SOFTWARE\Policies\Microsoft\Edge", "ShowMicrosoftRewards", 0),
                    Lm(@"SOFTWARE\Policies\Microsoft\Edge", "WebWidgetAllowed", 0),
                    Lm(@"SOFTWARE\Policies\Microsoft\Edge", "DiagnosticData", 0),
                    Lm(@"SOFTWARE\Policies\Microsoft\Edge", "EdgeAssetDeliveryServiceEnabled", 0),
                    Lm(@"SOFTWARE\Policies\Microsoft\Edge", "WalletDonationEnabled", 0),
                    Lm(@"SOFTWARE\Policies\Microsoft\Edge", "DefaultBrowserSettingsCampaignEnabled", 0),
                ],
            },
            new AdvancedTweak
            {
                Id = "razerblock",
                Group = GroupPrivacy,
                Title = "Block Razer software auto-install",
                Description = "Stops Windows from installing Razer software on its own when you plug in a Razer device, by making the folder it uses read-only for everyone. Lighter than WinUtil's version: ZenTune does not change driver search settings for your other devices.",
                Caution = "Razer's own installer may fail until you undo this. Needs admin.",
                ApplyScript = """
                    $dir = Join-Path $env:SystemRoot 'Installer\Razer'
                    Try-Run 'block the Razer installer folder' {
                        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force -ErrorAction Stop | Out-Null }
                        $out = & icacls.exe $dir /deny '*S-1-1-0:(W)' 2>&1
                        if ($LASTEXITCODE -ne 0) { throw ('icacls failed: ' + $out) }
                    }
                    """,
                UndoScript = """
                    $dir = Join-Path $env:SystemRoot 'Installer\Razer'
                    if (Test-Path -LiteralPath $dir) {
                        Try-Run 'unblock the Razer installer folder' {
                            $out = & icacls.exe $dir /remove:d '*S-1-1-0' 2>&1
                            if ($LASTEXITCODE -ne 0) { throw ('icacls failed: ' + $out) }
                        }
                    }
                    """,
            },
            new AdvancedTweak
            {
                Id = "logiblock",
                Group = GroupPrivacy,
                Title = "Block Logitech Download Assistant",
                Description = "Stops Logitech's Download Assistant from coming back by making its folder read-only for everyone.",
                Caution = "Logitech's own installers may fail until you undo this. Needs admin.",
                ApplyScript = """
                    $pf = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
                    $dir = Join-Path $pf 'LogiDownloadAssistant'
                    Try-Run 'block the Logitech Download Assistant folder' {
                        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force -ErrorAction Stop | Out-Null }
                        $out = & icacls.exe $dir /deny '*S-1-1-0:(W)' 2>&1
                        if ($LASTEXITCODE -ne 0) { throw ('icacls failed: ' + $out) }
                    }
                    """,
                UndoScript = """
                    $pf = if ($env:ProgramW6432) { $env:ProgramW6432 } else { $env:ProgramFiles }
                    $dir = Join-Path $pf 'LogiDownloadAssistant'
                    if (Test-Path -LiteralPath $dir) {
                        Try-Run 'unblock the Logitech Download Assistant folder' {
                            $out = & icacls.exe $dir /remove:d '*S-1-1-0' 2>&1
                            if ($LASTEXITCODE -ne 0) { throw ('icacls failed: ' + $out) }
                        }
                    }
                    """,
            },
            new AdvancedTweak
            {
                Id = "services",
                Group = GroupPrivacy,
                Title = "Fewer background services",
                Description = "Sets a few services that most gaming PCs never use to start only when needed, and groups Windows' svchost processes to match your memory, so far fewer of them run.",
                Caution = "Offline Files, Mobile hotspot and Internet Connection Sharing stop working until you undo this.",
                Registry = memKb > 0
                    ? new[] { Lm(@"SYSTEM\CurrentControlSet\Control", "SvcHostSplitThresholdInKB", (int)Math.Min(memKb, int.MaxValue)) }
                    : Array.Empty<RegSet>(),
                Services =
                [
                    new SvcSet("CscService", "Disabled"),
                    new SvcSet("DiagTrack", "Disabled"),
                    new SvcSet("MapsBroker", "Manual"),
                    new SvcSet("StorSvc", "Manual"),
                    new SvcSet("SharedAccess", "Disabled"),
                ],
                RestartNeeded = true,
            },

            // ------------------------------------------------------------ Look
            new AdvancedTweak
            {
                Id = "darkmode",
                Group = GroupLook,
                Title = "Dark mode",
                Description = "Dark theme for Windows and for apps.",
                Registry =
                [
                    Cu(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 0),
                    Cu(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0),
                ],
            },
            new AdvancedTweak
            {
                Id = "transparency",
                Group = GroupLook,
                Title = "Turn off transparency effects",
                Description = "Start, the taskbar and system windows become solid instead of see-through, so Windows has a little less to draw.",
                Caution = "Glass and blur looks stop working, including any see-through shell or theme. The sign-in screen background also loses its blur. Sign out and back in if it doesn't change right away.",
                SignOutNeeded = true,
                Registry = [Cu(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0)],
            },
            new AdvancedTweak
            {
                Id = "classicmenu",
                Group = GroupLook,
                Title = "Classic right-click menu",
                Description = "Brings back the full Windows 10 style right-click menu in File Explorer and on the desktop, so you don't need \"Show more options\" every time.",
                Confirm = "File Explorer restarts so the menu changes right away: open folder windows close and the taskbar blinks once. Save anything you have open in Explorer first.",
                ApplyScript = """
                    Try-Run 'switch to the classic right-click menu' {
                        $k = 'Registry::HKEY_CURRENT_USER\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32'
                        New-Item -Path $k -Force -Value '' -ErrorAction Stop | Out-Null
                        if (-not (Test-Path -LiteralPath $k)) { throw 'The registry key was not created.' }
                    }
                    Try-Run 'restart File Explorer' {
                        Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
                        Start-Sleep -Seconds 3
                        if (Get-Process -Name explorer -ErrorAction SilentlyContinue) { Log 'explorer is running again' } else { Log 'explorer did not come back by itself - sign out and back in' }
                    }
                    """,
                UndoScript = """
                    Try-Run 'switch back to the new right-click menu' {
                        $k = 'Registry::HKEY_CURRENT_USER\Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}'
                        if (Test-Path -LiteralPath $k) { Remove-Item -LiteralPath $k -Recurse -Force -ErrorAction Stop }
                    }
                    Try-Run 'restart File Explorer' {
                        Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
                        Start-Sleep -Seconds 3
                        if (Get-Process -Name explorer -ErrorAction SilentlyContinue) { Log 'explorer is running again' } else { Log 'explorer did not come back by itself - sign out and back in' }
                    }
                    """,
            },
            new AdvancedTweak
            {
                Id = "fileext",
                Group = GroupLook,
                Title = "Show file extensions",
                Description = "Always shows .exe, .txt, .png and so on after file names, so you can tell what a file really is.",
                SignOutNeeded = true,
                Registry = [Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", 0)],
            },
            new AdvancedTweak
            {
                Id = "hiddenfiles",
                Group = GroupLook,
                Title = "Show hidden files",
                Description = "Shows hidden files and folders in File Explorer, faded. Handy for game config folders and AppData.",
                Caution = "Easier to delete something important by mistake. Protected Windows files stay hidden.",
                SignOutNeeded = true,
                Registry = [Cu(@"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", 1)],
            },
            new AdvancedTweak
            {
                Id = "lockscreen",
                Group = GroupLook,
                Title = "Skip the lock screen",
                Description = "Goes straight to the sign-in screen instead of showing the lock screen picture first.",
                Caution = "May be ignored on some Windows editions.",
                Registry = [Lm(@"SOFTWARE\Policies\Microsoft\Windows\Personalization", "NoLockScreen", 1)],
            },
            new AdvancedTweak
            {
                Id = "folderdiscovery",
                Group = GroupLook,
                Title = "Stop Explorer guessing folder types",
                Description = "Explorer normally scans a folder to guess whether it holds pictures, music or videos, which can make big folders slow to open and changes how they are grouped. This makes every folder a plain folder.",
                Caution = "Folders you already opened may keep their saved view. WinUtil also wipes those saved views; ZenTune doesn't, so nothing of yours is lost.",
                SignOutNeeded = true,
                Registry = [CuS(@"Software\Classes\Local Settings\Software\Microsoft\Windows\Shell\Bags\AllFolders\Shell", "FolderType", "NotSpecified")],
            },

            // ------------------------------------------------------------ System and storage
            new AdvancedTweak
            {
                Id = "storagesense",
                Group = GroupSystem,
                Title = "Turn off Storage Sense",
                Description = "Windows stops deleting temporary files and emptying the Recycle Bin by itself.",
                Caution = "Temporary files pile up until you clean them. The PC cleaner above can do that.",
                Registry = [Cu(@"SOFTWARE\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy", "01", 0)],
            },
            new AdvancedTweak
            {
                Id = "reservedstorage",
                Group = GroupSystem,
                Title = "Turn off Reserved Storage",
                Description = "Gives back the 7–10 GB Windows keeps free for updates.",
                Caution = "Big Windows updates need free space, so turn this back on (Undo) before a feature update. Undo turns it on even if it was off before.",
                ApplyScript = """
                    Try-Run 'turn off reserved storage' {
                        $out = & dism.exe /Online /Set-ReservedStorageState /State:Disabled 2>&1
                        if ($LASTEXITCODE -ne 0) { throw ('DISM failed (' + $LASTEXITCODE + '): ' + $out) }
                    }
                    """,
                UndoScript = """
                    Try-Run 'turn on reserved storage' {
                        $out = & dism.exe /Online /Set-ReservedStorageState /State:Enabled 2>&1
                        if ($LASTEXITCODE -ne 0) { throw ('DISM failed (' + $LASTEXITCODE + '): ' + $out) }
                    }
                    """,
            },
            new AdvancedTweak
            {
                Id = "longpaths",
                Group = GroupSystem,
                Title = "Allow long file paths",
                Description = "Lets apps that support it use file paths longer than 260 characters, which some game mods and developer tools need.",
                Registry = [Lm(@"SYSTEM\CurrentControlSet\Control\FileSystem", "LongPathsEnabled", 1)],
            },
        ];
    }

    // ---------------------------------------------------------------- State

    public static TweakState State(AdvancedTweak tweak)
    {
        // The classic menu is a registry KEY, not a value, so it is checked on its own: this also notices
        // when the menu was already classic before ZenTune, or when the key was removed by hand.
        if (tweak.Id == "classicmenu")
        {
            if (!ClassicMenuKeyExists()) return TweakState.NotApplied;
            return ActiveChange(tweak.Id) is not null ? TweakState.AppliedByZenTune : TweakState.Applied;
        }

        if (ActiveChange(tweak.Id) is not null) return TweakState.AppliedByZenTune;

        bool regOk = tweak.Registry.All(r => Read(r.Hive, r.Path, r.Name).Value == r.Value);
        bool svcOk = tweak.Services.All(s => ReadServiceStart(s.Name) is not { } cur || cur == s.StartType);
        bool hasChecks = tweak.Registry.Length > 0 || tweak.Services.Length > 0;
        return hasChecks && regOk && svcOk ? TweakState.Applied : TweakState.NotApplied;
    }

    private static bool ClassicMenuKeyExists()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Classes\CLSID\{86ca1aa0-34aa-4e8b-a509-50c905bae2a2}\InprocServer32");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    public static ChangeRecord? ActiveChange(string id) =>
        ChangeJournal.Active().FirstOrDefault(c => c.Kind == JournalKind && c.Data.GetValueOrDefault("Id") == id);

    // ---------------------------------------------------------------- Apply

    internal sealed class OldReg
    {
        public RegHive Hive { get; set; }
        public string Path { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Existed { get; set; }
        public RegistryValueKind Kind { get; set; }
        public string? Value { get; set; }
    }

    internal sealed class OldSvc
    {
        public string Name { get; set; } = "";
        public string StartType { get; set; } = "";
    }

    internal sealed class OldState
    {
        public List<OldReg> Registry { get; set; } = [];
        public List<OldSvc> Services { get; set; } = [];
    }

    /// <summary>Applies a tweak. Shows one UAC prompt if it needs admin. Returns a log of anything that failed.</summary>
    public static string Apply(AdvancedTweak tweak)
    {
        var old = new OldState();
        foreach (var r in tweak.Registry)
        {
            var cur = Read(r.Hive, r.Path, r.Name);
            old.Registry.Add(new OldReg { Hive = r.Hive, Path = r.Path, Name = r.Name, Existed = cur.Exists, Kind = cur.Kind, Value = cur.Value });
        }
        foreach (var s in tweak.Services)
        {
            var cur = ReadServiceStart(s.Name);
            if (cur is not null && old.Services.All(o => o.Name != s.Name))
                old.Services.Add(new OldSvc { Name = s.Name, StartType = cur });
        }

        string problems = "";
        if (!tweak.NeedsAdmin)
        {
            foreach (var r in tweak.Registry) WriteDirect(r.Hive, r.Path, r.Name, r.Kind, r.Value);
        }
        else
        {
            var sb = new StringBuilder();
            foreach (var r in tweak.Registry) sb.AppendLine(r.Value is null ? DelLine(r.Hive, r.Path, r.Name) : SetLine(r.Hive, r.Path, r.Name, r.Kind, r.Value));
            foreach (var s in tweak.Services.Where(s => old.Services.Any(o => o.Name == s.Name)))
                sb.AppendLine($"SetSvc {ScriptRunner.Quote(s.Name)} {ScriptRunner.Quote(s.StartType)}");
            if (tweak.ApplyScript is not null) sb.AppendLine(tweak.ApplyScript);

            var result = ScriptRunner.Run("apply-" + tweak.Id, sb.ToString(), elevated: true);
            if (!result.Ok) problems = Errors(result);
        }

        ChangeJournal.Add(new ChangeRecord
        {
            Title = tweak.Title,
            Kind = JournalKind,
            Data = new() { ["Id"] = tweak.Id, ["Old"] = JsonSerializer.Serialize(old) },
        });

        Notify(tweak);
        return problems;
    }

    /// <summary>Puts back the values that were there before the tweak. Called from <see cref="Tweaks.Undo"/>.</summary>
    internal static void Undo(ChangeRecord change)
    {
        var tweak = All.FirstOrDefault(t => t.Id == change.Data.GetValueOrDefault("Id"));
        var old = JsonSerializer.Deserialize<OldState>(change.Data["Old"]) ?? new OldState();

        bool needsAdmin = old.Registry.Any(r => r.Hive == RegHive.LocalMachine) || old.Services.Count > 0 || tweak?.UndoScript is not null;

        if (!needsAdmin)
        {
            foreach (var r in old.Registry)
                WriteDirect(r.Hive, r.Path, r.Name, r.Kind, r.Existed ? r.Value : null);
        }
        else
        {
            var sb = new StringBuilder();
            foreach (var r in old.Registry)
                sb.AppendLine(r.Existed && r.Value is not null ? SetLine(r.Hive, r.Path, r.Name, r.Kind, r.Value) : DelLine(r.Hive, r.Path, r.Name));
            foreach (var s in old.Services)
                sb.AppendLine($"SetSvc {ScriptRunner.Quote(s.Name)} {ScriptRunner.Quote(s.StartType)}");
            if (tweak?.UndoScript is not null) sb.AppendLine(tweak.UndoScript);

            var result = ScriptRunner.Run("undo-" + (tweak?.Id ?? "tweak"), sb.ToString(), elevated: true);
            if (!result.Ok) throw new InvalidOperationException("Some values could not be put back:\n" + Errors(result));
        }

        if (tweak is not null) Notify(tweak);
    }

    private static string Errors(ScriptRunner.Result r)
    {
        var lines = r.Log.Split('\n').Where(l => l.StartsWith("ERROR")).Take(5).ToList();
        return lines.Count > 0 ? string.Join("\n", lines) : $"PowerShell exited with code {r.ExitCode}. See {r.ScriptPath}";
    }

    // ---------------------------------------------------------------- Restore point

    /// <summary>Creates a Windows restore point (asks for admin). Windows allows one every few minutes.</summary>
    public static void CreateRestorePoint()
    {
        const string body = """
            Try-Run 'enable system protection' { Enable-ComputerRestore -Drive "$env:SystemDrive\" -ErrorAction Stop }
            SetReg 'Registry::HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore' 'SystemRestorePointCreationFrequency' 'DWord' 0
            Try-Run 'create restore point' { Checkpoint-Computer -Description 'ZenTune' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop }
            """;
        var result = ScriptRunner.Run("restore-point", body, elevated: true);
        if (!result.Ok)
            throw new InvalidOperationException("Windows could not create the restore point:\n" + Errors(result));
    }

    // ---------------------------------------------------------------- Ultimate Performance

    private const string UltimateKind = "ultimate";
    private const string UltimateTemplate = "e9a42b02-d5df-448d-aa00-03f14749eb61";

    public static bool UltimateActive() =>
        ChangeJournal.Active().Any(c => c.Kind == UltimateKind && Tweaks.ActivePowerPlan()?.ToString() == c.Data.GetValueOrDefault("New"));

    /// <summary>Adds Microsoft's hidden Ultimate Performance power plan and switches to it.</summary>
    public static void EnableUltimate()
    {
        var old = Tweaks.ActivePowerPlan() ?? Tweaks.Balanced;

        // powercfg works without admin on most PCs; fall back to an elevated run if it doesn't
        var (code, output) = RunPowercfg($"/duplicatescheme {UltimateTemplate}");
        var match = System.Text.RegularExpressions.Regex.Match(output, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
        if (code != 0 || !match.Success)
        {
            var file = Path.Combine(AppPaths.Root, "Scripts", "ultimate-guid.txt");
            var r = ScriptRunner.Run("ultimate", $"Try-Run 'duplicate scheme' {{ powercfg.exe /duplicatescheme {UltimateTemplate} | Out-File -LiteralPath {ScriptRunner.Quote(file)} -Encoding UTF8 }}", elevated: true);
            output = File.Exists(file) ? File.ReadAllText(file) : "";
            match = System.Text.RegularExpressions.Regex.Match(output, "[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}");
            if (!r.Ok || !match.Success) throw new InvalidOperationException("Windows did not add the Ultimate Performance plan.");
        }

        var plan = Guid.Parse(match.Value);
        var (setCode, _) = RunPowercfg($"/setactive {plan}");
        if (setCode != 0) throw new InvalidOperationException("The Ultimate Performance plan was added but could not be switched on.");

        ChangeJournal.Add(new ChangeRecord
        {
            Title = "Ultimate Performance power plan",
            Kind = UltimateKind,
            Data = new() { ["Old"] = old.ToString(), ["New"] = plan.ToString() },
        });
    }

    internal static void UndoUltimate(ChangeRecord change)
    {
        RunPowercfg($"/setactive {change.Data["Old"]}");
        RunPowercfg($"/delete {change.Data["New"]}"); // best effort
    }

    private static (int Code, string Output) RunPowercfg(string args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("powercfg.exe", args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = System.Diagnostics.Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(15000);
        return (p.ExitCode, output);
    }

    // ---------------------------------------------------------------- Registry helpers

    private static string PsPath(RegHive hive, string path) =>
        "Registry::" + (hive == RegHive.CurrentUser ? "HKEY_CURRENT_USER\\" : "HKEY_LOCAL_MACHINE\\") + path;

    private static string SetLine(RegHive hive, string path, string name, RegistryValueKind kind, string value)
    {
        string literal = kind switch
        {
            RegistryValueKind.DWord => $"([int]{int.Parse(value)})",
            RegistryValueKind.QWord => $"([long]{long.Parse(value)})",
            RegistryValueKind.Binary => value.Length == 0 ? "([byte[]]@())" : $"([byte[]]({value}))",
            _ => ScriptRunner.Quote(value),
        };
        string type = kind switch
        {
            RegistryValueKind.DWord => "DWord",
            RegistryValueKind.QWord => "QWord",
            RegistryValueKind.Binary => "Binary",
            RegistryValueKind.ExpandString => "ExpandString",
            RegistryValueKind.MultiString => "MultiString",
            _ => "String",
        };
        return $"SetReg {ScriptRunner.Quote(PsPath(hive, path))} {ScriptRunner.Quote(name)} '{type}' {literal}";
    }

    private static string DelLine(RegHive hive, string path, string name) =>
        $"DelReg {ScriptRunner.Quote(PsPath(hive, path))} {ScriptRunner.Quote(name)}";

    private static RegistryKey Root(RegHive hive) => hive == RegHive.CurrentUser ? Microsoft.Win32.Registry.CurrentUser : Microsoft.Win32.Registry.LocalMachine;

    private static (bool Exists, RegistryValueKind Kind, string? Value) Read(RegHive hive, string path, string name)
    {
        try
        {
            using var key = Root(hive).OpenSubKey(path);
            var raw = key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            if (key is null || raw is null) return (false, RegistryValueKind.Unknown, null);

            var kind = key.GetValueKind(name);
            string value = raw switch
            {
                byte[] b => string.Join(",", b),
                string[] m => string.Join("\n", m),
                int i => i.ToString(),
                long l => l.ToString(),
                _ => Convert.ToString(raw) ?? "",
            };
            return (true, kind, value);
        }
        catch
        {
            return (false, RegistryValueKind.Unknown, null);
        }
    }

    private static void WriteDirect(RegHive hive, string path, string name, RegistryValueKind kind, string? value)
    {
        if (value is null)
        {
            using var existing = Root(hive).OpenSubKey(path, writable: true);
            existing?.DeleteValue(name, throwOnMissingValue: false);
            return;
        }

        using var key = Root(hive).CreateSubKey(path, writable: true);
        object data = kind switch
        {
            RegistryValueKind.DWord => int.Parse(value),
            RegistryValueKind.QWord => long.Parse(value),
            RegistryValueKind.Binary => value.Length == 0 ? Array.Empty<byte>() : value.Split(',').Select(byte.Parse).ToArray(),
            RegistryValueKind.MultiString => value.Split('\n'),
            _ => value,
        };
        key.SetValue(name, data, kind == RegistryValueKind.Unknown ? RegistryValueKind.String : kind);
    }

    /// <summary>"Automatic", "AutomaticDelayedStart", "Manual", "Disabled", or null if the service does not exist.</summary>
    private static string? ReadServiceStart(string name)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            if (key?.GetValue("Start") is not int start) return null;
            bool delayed = key.GetValue("DelayedAutostart") is int d && d == 1;
            return start switch
            {
                2 => delayed ? "AutomaticDelayedStart" : "Automatic",
                3 => "Manual",
                4 => "Disabled",
                _ => "Automatic", // boot and system drivers: treat as automatic
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Tells running apps that settings changed, so dark mode and effects apply right away.</summary>
    private static void Notify(AdvancedTweak tweak)
    {
        if (tweak.Id is not ("darkmode" or "visualfx" or "transparency")) return;
        try
        {
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "ImmersiveColorSet", 0x0002, 1000, out _);
            SendMessageTimeout(new IntPtr(0xFFFF), 0x001A, IntPtr.Zero, "WindowMetrics", 0x0002, 1000, out _);
        }
        catch { /* cosmetic only */ }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SendMessageTimeout(IntPtr hWnd, uint msg, IntPtr wParam, string lParam, uint flags, uint timeout, out IntPtr result);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);
}
