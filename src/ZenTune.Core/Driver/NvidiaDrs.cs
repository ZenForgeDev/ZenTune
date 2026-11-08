using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using ZenTune.Core.Changes;

namespace ZenTune.Core.Driver;

/// <summary>One value to write to the NVIDIA driver. A null value means "remove ZenTune's value, back to the driver default".</summary>
public sealed record DriverWrite(uint Id, uint? Value);

/// <summary>
/// Reads and writes the global NVIDIA driver profile through NVAPI (NVIDIA's own driver settings API,
/// MIT-licensed headers). It is the same store the NVIDIA Control Panel edits, but it needs neither the
/// Control Panel nor the NVIDIA app, only the driver. Every write records the old value in the
/// <see cref="ChangeJournal"/> first, so it can be undone from here or from the System check page.
/// </summary>
[SupportedOSPlatform("windows")]
public static unsafe class NvidiaDrs
{
    private static readonly object Gate = new();

    // Function ids from NVIDIA's nvapi_interface.h
    private const uint IdInitialize = 0x0150e828;
    private const uint IdGetErrorMessage = 0x6c2d048c;
    private const uint IdCreateSession = 0x0694d52e;
    private const uint IdDestroySession = 0xdad9cff8;
    private const uint IdLoadSettings = 0x375dbd6b;
    private const uint IdSaveSettings = 0xfcbc7e14;
    private const uint IdGetBaseProfile = 0xda8466a0;
    private const uint IdGetSetting = 0x73bf8338;
    private const uint IdSetSetting = 0x577dd202;
    private const uint IdDeleteProfileSetting = 0xe4a26362;
    private const uint IdCreateProfile = 0xcc176068;
    private const uint IdCreateApplication = 0x4347a9de;
    private const uint IdFindProfileByName = 0x7e4a9a0b;
    private const uint IdFindApplicationByName = 0xeee566b2;

    private const int NvApiNoDevice = -6;
    private const int NvApiNoPrivilege = -137;
    private const int NvApiIncompatibleStruct = -9;

    // NVDRS_APPLICATION_V4 (20492 bytes): version, isPredefined, appName[2048 x u16] at 8, userFriendlyName at 4104,
    // launcher at 8200, fileInFolder at 12296, flags at 16392, commandLine at 16396. V3 is the same without commandLine (16396 bytes).
    private const int AppSizeV4 = 20492;
    private const int AppSizeV3 = 16396;
    private const uint AppVersionV4 = (uint)(AppSizeV4 | (4 << 16));
    private const uint AppVersionV3 = (uint)(AppSizeV3 | (3 << 16));

    // NVDRS_PROFILE_V1 (4116 bytes): version, profileName[2048 x u16] at 4, gpuSupport at 4100, isPredefined, numOfApps, numOfSettings
    private const int ProfileSize = 4116;
    private const uint ProfileVersion = (uint)(ProfileSize | (1 << 16));

    // NVDRS_SETTING_V1: version, name[2048 x u16], id, type, location, isCurrentPredefined, isPredefinedValid,
    // then two unions of 4100 bytes each (predefined value, current value). Total 12320 bytes.
    [StructLayout(LayoutKind.Explicit, Size = 12320)]
    private struct NvSetting
    {
        [FieldOffset(0)] public uint Version;
        [FieldOffset(4100)] public uint SettingId;
        [FieldOffset(4104)] public int SettingType;
        [FieldOffset(4108)] public int SettingLocation;
        [FieldOffset(4112)] public uint IsCurrentPredefined;
        [FieldOffset(4116)] public uint IsPredefinedValid;
        [FieldOffset(4120)] public uint PredefinedValue;
        [FieldOffset(8220)] public uint CurrentValue;
    }

    private const int SettingSize = 12320;
    private static readonly uint SettingVersion = (uint)(SettingSize | (1 << 16));

    private static IntPtr _queryInterface;
    private static bool _loadTried;

    // ---------------------------------------------------------------- Public

    /// <summary>True when an NVIDIA driver with NVAPI is installed and a session can be opened.</summary>
    public static bool IsAvailable(out string? reason)
    {
        lock (Gate)
        {
            try
            {
                if (!EnsureLoaded())
                {
                    reason = "No NVIDIA driver was found (nvapi64.dll is missing).";
                    return false;
                }

                int rc = ((delegate* unmanaged[Cdecl]<int>)Fn(IdInitialize))();
                if (rc == NvApiNoDevice)
                {
                    reason = "No NVIDIA graphics card was found.";
                    return false;
                }
                if (rc != 0)
                {
                    reason = $"The NVIDIA driver did not start its settings service: {Describe(rc)}";
                    return false;
                }

                using var _ = new Session();
                reason = null;
                return true;
            }
            catch (Exception ex)
            {
                reason = ex.Message;
                return false;
            }
        }
    }

    /// <summary>
    /// Reads values from the global profile. A missing entry (or a value that is just the driver default)
    /// is returned as null.
    /// </summary>
    public static Dictionary<uint, uint?> Read(IEnumerable<uint> ids)
    {
        lock (Gate)
        {
            var result = new Dictionary<uint, uint?>();
            using var s = new Session();
            foreach (var id in ids.Distinct())
                result[id] = s.Get(id);
            return result;
        }
    }

    /// <summary>True when the driver has a profile (NVIDIA's own or one ZenTune made) that lists this program.</summary>
    public static bool HasProfile(string exe)
    {
        exe = CleanExe(exe);
        lock (Gate)
        {
            using var s = new Session(exe, create: false);
            return s.Exists;
        }
    }

    /// <summary>
    /// Reads values from the profile of one program. When the program has no profile, every value is null and
    /// <paramref name="profileExists"/> is false. Values that NVIDIA itself ships in its profile are returned as null, like in the global profile.
    /// </summary>
    public static Dictionary<uint, uint?> ReadForApp(string exe, IEnumerable<uint> ids, out bool profileExists)
    {
        exe = CleanExe(exe);
        lock (Gate)
        {
            var result = new Dictionary<uint, uint?>();
            using var s = new Session(exe, create: false);
            profileExists = s.Exists;
            foreach (var id in ids.Distinct())
                result[id] = s.Exists ? s.Get(id) : null;
            return result;
        }
    }

    /// <summary>The file name of a program, for example "cod.exe". Throws when it is not a plain .exe name.</summary>
    public static string CleanExe(string text)
    {
        string name = Path.GetFileName((text ?? "").Trim().Trim('"'));
        if (name.Length < 5 || name.Length > 100 || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidOperationException("Type the program's file name, for example game.exe.");
        return name;
    }

    /// <summary>
    /// Writes (or removes) a group of settings in one go, saves them and records one change that can be undone.
    /// </summary>
    public static void Apply(string title, IReadOnlyList<DriverWrite> writes, string? exe = null)
    {
        if (writes.Count == 0) return;
        if (exe is not null) exe = CleanExe(exe);

        lock (Gate)
        {
            // For a game the profile is created when the game has none
            using var s = new Session(exe, create: true);

            // Remember how everything was before touching it
            var data = new Dictionary<string, string>
            {
                ["Ids"] = string.Join(",", writes.Select(w => w.Id)),
            };
            if (exe is not null)
            {
                data["Exe"] = exe;
                data["CreatedProfile"] = s.Created ? "1" : "0";
            }
            foreach (var w in writes)
            {
                var old = s.Get(w.Id);
                data[$"Existed_{w.Id}"] = old is null ? "0" : "1";
                if (old is not null) data[$"Old_{w.Id}"] = old.Value.ToString();
            }

            // Record first, so a crash half way can still be undone
            var record = new ChangeRecord { Title = title, Kind = "nvidia-drs", Data = data };
            ChangeJournal.Add(record);

            try
            {
                foreach (var w in writes)
                {
                    if (w.Value is null) s.Delete(w.Id);
                    else s.Set(w.Id, w.Value.Value);
                }
                s.Save();
            }
            catch
            {
                // Nothing changed for good, so the change must not stay in the list
                try { ChangeJournal.MarkUndone(record.Id); } catch { /* nothing to do */ }
                throw;
            }
        }
    }

    /// <summary>Puts back what <see cref="Apply"/> changed. Called from <c>Tweaks.Undo</c>.</summary>
    public static void Undo(ChangeRecord change)
    {
        var d = change.Data;
        if (!d.TryGetValue("Ids", out var idList) || string.IsNullOrWhiteSpace(idList))
            throw new InvalidOperationException("This change has no saved driver settings.");

        string? exe = d.TryGetValue("Exe", out var e) && !string.IsNullOrWhiteSpace(e) ? e : null;

        lock (Gate)
        {
            using var s = new Session(exe, create: false);
            if (exe is not null && !s.Exists)
                throw new InvalidOperationException($"The driver no longer has a profile for {exe}, so there is nothing to undo.");

            foreach (var part in idList.Split(',', StringSplitOptions.RemoveEmptyEntries))
            {
                uint id = uint.Parse(part);
                if (d.TryGetValue($"Existed_{id}", out var existed) && existed == "1" && d.TryGetValue($"Old_{id}", out var old))
                    s.Set(id, uint.Parse(old));
                else
                    s.Delete(id);
            }
            s.Save();
        }
    }

    // ---------------------------------------------------------------- Session

    private sealed class Session : IDisposable
    {
        private IntPtr _session;
        private IntPtr _profile;
        private uint _appVersion = AppVersionV4;

        /// <summary>False only for a program without a profile when none was asked to be created.</summary>
        public bool Exists { get; private set; } = true;

        /// <summary>True when this session created the profile.</summary>
        public bool Created { get; private set; }

        public Session(string? exe = null, bool create = false)
        {
            if (!EnsureLoaded()) throw new InvalidOperationException("No NVIDIA driver was found.");
            Check(((delegate* unmanaged[Cdecl]<int>)Fn(IdInitialize))(), "start NVAPI");

            IntPtr h = IntPtr.Zero;
            Check(((delegate* unmanaged[Cdecl]<IntPtr*, int>)Fn(IdCreateSession))(&h), "open a driver settings session");
            _session = h;

            try
            {
                Check(((delegate* unmanaged[Cdecl]<IntPtr, int>)Fn(IdLoadSettings))(_session), "load the driver settings");

                if (exe is null)
                {
                    IntPtr p = IntPtr.Zero;
                    Check(((delegate* unmanaged[Cdecl]<IntPtr, IntPtr*, int>)Fn(IdGetBaseProfile))(_session, &p), "find the global profile");
                    _profile = p;
                }
                else if (FindApp(exe, out var found))
                {
                    _profile = found;
                }
                else if (create)
                {
                    _profile = CreateForApp(exe);
                    Created = true;
                }
                else
                {
                    Exists = false;
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        /// <summary>Looks the program up by file name. Any answer except "found" means it has no profile.</summary>
        private bool FindApp(string exe, out IntPtr profile)
        {
            foreach (uint version in new[] { _appVersion, AppVersionV3 }.Distinct())
            {
                byte* app = (byte*)NativeMemory.AllocZeroed(AppSizeV4);
                ushort* name = stackalloc ushort[2048];
                new Span<ushort>(name, 2048).Clear();
                PutString((byte*)name, 0, exe);
                try
                {
                    *(uint*)app = version;
                    IntPtr h = IntPtr.Zero;
                    int rc = ((delegate* unmanaged[Cdecl]<IntPtr, ushort*, IntPtr*, byte*, int>)Fn(IdFindApplicationByName))(_session, name, &h, app);
                    if (rc == 0)
                    {
                        _appVersion = version;
                        profile = h;
                        return true;
                    }
                    if (rc != NvApiIncompatibleStruct) break; // "not found" or similar
                }
                finally { NativeMemory.Free(app); }
            }
            profile = IntPtr.Zero;
            return false;
        }

        /// <summary>Makes a profile named "ZenTune - game.exe" (or reuses one with that name) and adds the program to it.</summary>
        private IntPtr CreateForApp(string exe)
        {
            string profileName = $"ZenTune - {exe}";
            byte* prof = (byte*)NativeMemory.AllocZeroed(ProfileSize);
            IntPtr h = IntPtr.Zero;
            try
            {
                *(uint*)prof = ProfileVersion;
                PutString(prof, 4, profileName);
                *(uint*)(prof + 4100) = 1; // GeForce
                int rc = ((delegate* unmanaged[Cdecl]<IntPtr, byte*, IntPtr*, int>)Fn(IdCreateProfile))(_session, prof, &h);
                if (rc != 0)
                {
                    // A profile with this name may exist already (for example from an earlier run that was undone)
                    ushort* name = stackalloc ushort[2048];
                    new Span<ushort>(name, 2048).Clear();
                    PutString((byte*)name, 0, profileName);
                    h = IntPtr.Zero;
                    int rc2 = ((delegate* unmanaged[Cdecl]<IntPtr, ushort*, IntPtr*, int>)Fn(IdFindProfileByName))(_session, name, &h);
                    if (rc2 != 0) Check(rc, "create a profile for the program");
                }
            }
            finally { NativeMemory.Free(prof); }

            foreach (uint version in new[] { _appVersion, AppVersionV3 }.Distinct())
            {
                byte* app = (byte*)NativeMemory.AllocZeroed(AppSizeV4);
                try
                {
                    *(uint*)app = version;
                    PutString(app, 8, exe);
                    PutString(app, 4104, exe);
                    int rc = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, byte*, int>)Fn(IdCreateApplication))(_session, h, app);
                    if (rc == 0) { _appVersion = version; return h; }
                    if (rc != NvApiIncompatibleStruct) Check(rc, "add the program to its profile");
                }
                finally { NativeMemory.Free(app); }
            }
            throw new InvalidOperationException("The NVIDIA driver would not add the program to its profile: incompatible driver version.");
        }

        /// <summary>The value ZenTune or the user set in the global profile, or null if it is at the driver default.</summary>
        public uint? Get(uint id)
        {
            var setting = new NvSetting { Version = SettingVersion, SettingId = id };
            int rc = ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, NvSetting*, int>)Fn(IdGetSetting))(_session, _profile, id, &setting);

            // Any error here means "this setting is not stored in the profile", which is the same as the default
            if (rc != 0) return null;
            if (setting.IsCurrentPredefined != 0) return null;
            return setting.CurrentValue;
        }

        public void Set(uint id, uint value)
        {
            var setting = new NvSetting
            {
                Version = SettingVersion,
                SettingId = id,
                SettingType = 0, // NVDRS_DWORD_TYPE
                CurrentValue = value,
            };
            Check(((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, NvSetting*, int>)Fn(IdSetSetting))(_session, _profile, &setting),
                $"write setting 0x{id:X8}");
        }

        public void Delete(uint id)
        {
            // Not an error if there was nothing to remove
            ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, uint, int>)Fn(IdDeleteProfileSetting))(_session, _profile, id);
        }

        public void Save() =>
            Check(((delegate* unmanaged[Cdecl]<IntPtr, int>)Fn(IdSaveSettings))(_session), "save the driver settings");

        public void Dispose()
        {
            if (_session == IntPtr.Zero) return;
            try { ((delegate* unmanaged[Cdecl]<IntPtr, int>)Fn(IdDestroySession))(_session); }
            catch { /* closing must never fail */ }
            _session = IntPtr.Zero;
        }
    }

    // ---------------------------------------------------------------- Helpers

    /// <summary>Writes text as UTF-16 into a 2048-character NVAPI string field (zero filled before).</summary>
    private static void PutString(byte* buffer, int offset, string text)
    {
        ushort* p = (ushort*)(buffer + offset);
        int n = Math.Min(text.Length, 2047);
        for (int i = 0; i < n; i++) p[i] = text[i];
        p[n] = 0;
    }

    private static void Check(int rc, string what)
    {
        if (rc == 0) return;
        string hint = rc == NvApiNoPrivilege ? " Try starting ZenTune as administrator." : "";
        throw new InvalidOperationException($"The NVIDIA driver would not {what}: {Describe(rc)}.{hint}");
    }

    private static string Describe(int rc)
    {
        try
        {
            if (_queryInterface != IntPtr.Zero)
            {
                var fn = QueryFunction(IdGetErrorMessage);
                if (fn != IntPtr.Zero)
                {
                    byte* buffer = stackalloc byte[256];
                    ((delegate* unmanaged[Cdecl]<int, byte*, int>)fn)(rc, buffer);
                    var text = Marshal.PtrToStringAnsi((IntPtr)buffer);
                    if (!string.IsNullOrWhiteSpace(text)) return $"{text} (code {rc})";
                }
            }
        }
        catch { /* fall through */ }
        return $"error code {rc}";
    }

    private static bool EnsureLoaded()
    {
        if (_queryInterface != IntPtr.Zero) return true;
        if (_loadTried) return false;
        _loadTried = true;

        try
        {
            if (!NativeLibrary.TryLoad("nvapi64.dll", out var lib)) return false;
            if (!NativeLibrary.TryGetExport(lib, "nvapi_QueryInterface", out var qi)) return false;
            _queryInterface = qi;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static IntPtr QueryFunction(uint id) =>
        ((delegate* unmanaged[Cdecl]<uint, IntPtr>)_queryInterface)(id);

    private static IntPtr Fn(uint id)
    {
        var p = QueryFunction(id);
        if (p == IntPtr.Zero)
            throw new InvalidOperationException($"This NVIDIA driver does not offer the function 0x{id:X8}. Try updating the driver.");
        return p;
    }
}
