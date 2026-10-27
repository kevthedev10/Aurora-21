using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

// Identical maths on every PC for online matches.
//
// FIFA21.exe takes its maths (acosf, atan2f, pow, ...) from the operating
// system's C runtime (api-ms-win-crt-math-l1-1-0 -> System32\ucrtbase.dll).
// Windows 11 24H2 changed several of those functions in the last bit, so two PCs
// on different Windows builds simulate a lockstep match differently and desync.
// A ucrtbase.dll next to FIFA21.exe does not help: Windows 10 and 11 always load
// the system copy.  So this helper loads one pinned, unmodified Microsoft
// ucrtbase.dll (the Windows SDK redistributable, shipped in the client pack's
// ucrt folder) into the running FIFA process by full path and points FIFA's 50
// maths imports at it.  The client pack's Start-Aurora21.ps1 runs it on every
// launch, so every player runs the same maths.
//
// Scope: the current FIFA process only.  No game or Windows file is changed; the
// system ucrtbase.dll stays in use for everything that is not maths.
//
// Report: with --code-stdin and --report, once the imports are switched and have
// stayed switched for the watch period, the helper tells the server which maths
// this FIFA now runs (POST /client/launch/math: the launch code in the JSON body,
// the [MATH] value in the X-Aurora21-Math header), so matchmaking pairs the player
// by that library.  The code arrives on standard input, only for the FIFA process
// the launch patched, and is never logged.
internal static class LocalMathBridge
{
    internal const string SupportedProductVersion = "21.0.4750997.0";
    internal const long SupportedFileLength = 571958064;

    // SHA-256 of the Microsoft ucrtbase.dll the client pack ships (Windows SDK
    // Redist\ucrt\DLLs\x64).  Everyone must load the same file, so any other
    // copy is refused.
    internal static readonly string[] PinnedUcrtSha256 = {
        "2E5FB14B7BF8540278F3614A12F0226E56A7CC9E64B81CBD976C6FCF2F71CBFB",
    };

    internal struct Slot
    {
        internal readonly string Name;
        internal readonly long ShadowRva, ImportRva;
        internal Slot(string name, long shadowRva, long importRva)
        {
            Name = name; ShadowRva = shadowRva; ImportRva = importRva;
        }
    }

    // FIFA21.exe 21.0.4750997.0: its api-ms-win-crt-math-l1-1-0 import table
    // (ImportRva, filled by the Windows loader) is copied at startup into a
    // shadow table (ShadowRva) that every maths call reads at call time, via a
    // thunk "jmp [slot]" or a direct "call [slot]".  Nothing else references the
    // import table.  Both tables are 50 contiguous 8-byte slots, and in a
    // running game each slot holds the plain ucrtbase export address.
    internal static readonly Slot[] Slots = {
        new Slot("sqrt", 0x63F31B0, 0x2332D398),
        new Slot("sqrtf", 0x63F31B8, 0x2332D3A0),
        new Slot("logf", 0x63F31C0, 0x2332D340),
        new Slot("log10f", 0x63F31C8, 0x2332D328),
        new Slot("log10", 0x63F31D0, 0x2332D320),
        new Slot("log", 0x63F31D8, 0x2332D318),
        new Slot("fmodf", 0x63F31E0, 0x2332D300),
        new Slot("fmod", 0x63F31E8, 0x2332D2F8),
        new Slot("floorf", 0x63F31F0, 0x2332D2E0),
        new Slot("floor", 0x63F31F8, 0x2332D2D8),
        new Slot("tanh", 0x63F3200, 0x2332D3B8),
        new Slot("sinh", 0x63F3208, 0x2332D390),
        new Slot("expf", 0x63F3210, 0x2332D2C8),
        new Slot("exp", 0x63F3218, 0x2332D2C0),
        new Slot("cosh", 0x63F3220, 0x2332D2B8),
        new Slot("cosf", 0x63F3228, 0x2332D2B0),
        new Slot("cos", 0x63F3230, 0x2332D2A8),
        new Slot("ceilf", 0x63F3238, 0x2332D2A0),
        new Slot("pow", 0x63F3240, 0x2332D358),
        new Slot("atanf", 0x63F3248, 0x2332D290),
        new Slot("atan2f", 0x63F3250, 0x2332D288),
        new Slot("powf", 0x63F3258, 0x2332D360),
        new Slot("atan", 0x63F3260, 0x2332D278),
        new Slot("asinf", 0x63F3268, 0x2332D270),
        new Slot("ldexp", 0x63F3270, 0x2332D310),
        new Slot("asin", 0x63F3278, 0x2332D268),
        new Slot("frexp", 0x63F3280, 0x2332D308),
        new Slot("acosf", 0x63F3288, 0x2332D260),
        new Slot("acos", 0x63F3290, 0x2332D258),
        new Slot("_fdclass", 0x63F3298, 0x2332D248),
        new Slot("_dclass", 0x63F32A0, 0x2332D240),
        new Slot("log2f", 0x63F32A8, 0x2332D338),
        new Slot("fminf", 0x63F32B0, 0x2332D2F0),
        new Slot("expm1f", 0x63F32B8, 0x2332D2D0),
        new Slot("nextafterf", 0x63F32C0, 0x2332D350),
        new Slot("rintf", 0x63F32C8, 0x2332D368),
        new Slot("fmaxf", 0x63F32D0, 0x2332D2E8),
        new Slot("truncf", 0x63F32D8, 0x2332D3C0),
        new Slot("_finite", 0x63F32E0, 0x2332D250),
        new Slot("__setusermatherr", 0x63F32E8, 0x2332D238),
        new Slot("sin", 0x63F32F0, 0x2332D380),
        new Slot("ceil", 0x63F32F8, 0x2332D298),
        new Slot("sinf", 0x63F3300, 0x2332D388),
        new Slot("log2", 0x63F3308, 0x2332D330),
        new Slot("round", 0x63F3310, 0x2332D370),
        new Slot("roundf", 0x63F3318, 0x2332D378),
        new Slot("modff", 0x63F3320, 0x2332D348),
        new Slot("tan", 0x63F3328, 0x2332D3A8),
        new Slot("tanf", 0x63F3330, 0x2332D3B0),
        new Slot("atan2", 0x63F3338, 0x2332D280),
    };

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr read);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] bytes, UIntPtr size, out UIntPtr written);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, UIntPtr size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFreeEx(IntPtr process, IntPtr address, UIntPtr size, uint type);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualProtectEx(IntPtr process, IntPtr address, UIntPtr size, uint protection, out uint oldProtection);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateRemoteThread(IntPtr process, IntPtr attributes, UIntPtr stack, IntPtr start, IntPtr parameter, uint flags, out uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern IntPtr GetProcAddress(IntPtr module, string name);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadLibraryW(string path);
    [DllImport("psapi.dll", SetLastError = true)]
    private static extern bool EnumProcessModulesEx(IntPtr process, [Out] IntPtr[] modules, uint size, out uint needed, uint filter);
    [DllImport("psapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern uint GetModuleFileNameExW(IntPtr process, IntPtr module, StringBuilder name, uint size);
    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr process);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int IntGetter();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CpuidFunction(uint leaf, uint subleaf, [Out] uint[] registers);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate ulong XgetbvFunction();
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr VirtualAlloc(IntPtr address, UIntPtr size, uint type, uint protection);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool VirtualFree(IntPtr address, UIntPtr size, uint type);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float FloatFunction(float x);

    internal static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static bool TryRead(IntPtr process, long address, byte[] bytes)
    {
        UIntPtr count;
        return ReadProcessMemory(process, new IntPtr(address), bytes, (UIntPtr)bytes.Length, out count) &&
            count.ToUInt64() == (ulong)bytes.Length;
    }

    private static void Write(IntPtr process, long address, byte[] bytes)
    {
        UIntPtr count;
        if (!WriteProcessMemory(process, new IntPtr(address), bytes, (UIntPtr)bytes.Length, out count) || count.ToUInt64() != (ulong)bytes.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot write the selected FIFA process.");
    }

    internal static string Sha256(string path)
    {
        using (var sha = SHA256.Create())
        using (var stream = File.OpenRead(path))
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
    }

    // Export RVAs of an x64 DLL on disk.  Forwarded exports are left out: every
    // maths import must land on code inside that DLL.
    internal static Dictionary<string, uint> ExportRvas(string path)
    {
        byte[] image = File.ReadAllBytes(path);
        int pe = BitConverter.ToInt32(image, 0x3C);
        Require(BitConverter.ToUInt32(image, pe) == 0x4550 && BitConverter.ToUInt16(image, pe + 4) == 0x8664 &&
            BitConverter.ToUInt16(image, pe + 24) == 0x20B, "Not an x64 DLL: " + path);
        int sections = BitConverter.ToUInt16(image, pe + 6);
        int sectionTable = pe + 24 + BitConverter.ToUInt16(image, pe + 20);
        Func<uint, int> offset = rva =>
        {
            for (int i = 0; i < sections; i++)
            {
                int s = sectionTable + 40 * i;
                uint va = BitConverter.ToUInt32(image, s + 12);
                uint size = Math.Max(BitConverter.ToUInt32(image, s + 8), BitConverter.ToUInt32(image, s + 16));
                if (rva >= va && rva < va + size) return checked((int)(rva - va + BitConverter.ToUInt32(image, s + 20)));
            }
            throw new InvalidOperationException("Export table points outside " + path);
        };
        uint exportRva = BitConverter.ToUInt32(image, pe + 24 + 112), exportSize = BitConverter.ToUInt32(image, pe + 24 + 116);
        int exports = offset(exportRva);
        int count = checked((int)BitConverter.ToUInt32(image, exports + 24));
        int functions = offset(BitConverter.ToUInt32(image, exports + 28));
        int names = offset(BitConverter.ToUInt32(image, exports + 32));
        int ordinals = offset(BitConverter.ToUInt32(image, exports + 36));
        var result = new Dictionary<string, uint>(StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            int nameOffset = offset(BitConverter.ToUInt32(image, names + 4 * i));
            string name = Encoding.ASCII.GetString(image, nameOffset, Array.IndexOf(image, (byte)0, nameOffset) - nameOffset);
            uint rva = BitConverter.ToUInt32(image, functions + 4 * BitConverter.ToUInt16(image, ordinals + 2 * i));
            if (rva >= exportRva && rva < exportRva + exportSize) continue;
            result[name] = rva;
        }
        return result;
    }

    internal static Dictionary<string, long> Modules(IntPtr process)
    {
        var result = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var handles = new IntPtr[2048];
        uint needed;
        // Fails while the Windows loader is still starting the process.
        if (!EnumProcessModulesEx(process, handles, (uint)(IntPtr.Size * handles.Length), out needed, 0x02)) return result;
        int n = Math.Min(handles.Length, (int)(needed / IntPtr.Size));
        for (int i = 0; i < n; i++)
        {
            var name = new StringBuilder(1024);
            if (GetModuleFileNameExW(process, handles[i], name, (uint)name.Capacity) == 0) continue;
            result[Path.GetFullPath(name.ToString())] = handles[i].ToInt64();
        }
        return result;
    }

    private static long LoadIntoGame(IntPtr process, string dllPath)
    {
        byte[] path = Encoding.Unicode.GetBytes(dllPath + "\0");
        IntPtr remote = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)path.Length, 0x3000, 0x04);
        if (remote == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot allocate the library path in FIFA.");
        bool finished = false;
        try
        {
            Write(process, remote.ToInt64(), path);
            IntPtr loadLibrary = GetProcAddress(GetModuleHandleW("kernel32.dll"), "LoadLibraryW");
            uint threadId;
            IntPtr thread = CreateRemoteThread(process, IntPtr.Zero, UIntPtr.Zero, loadLibrary, remote, 0, out threadId);
            if (thread == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot start the loader thread in FIFA.");
            try { finished = WaitForSingleObject(thread, 30000) == 0; }
            finally { CloseHandle(thread); }
            Require(finished, "Loading the maths library in FIFA timed out.");
        }
        finally
        {
            // A loader thread that is still running owns the path buffer.
            if (finished) VirtualFreeEx(process, remote, UIntPtr.Zero, 0x8000);
        }
        long module;
        Require(Modules(process).TryGetValue(dllPath, out module), "FIFA did not load the bundled maths library.");
        return module;
    }

    private static long[] ReadTable(IntPtr process, long address)
    {
        byte[] bytes = new byte[8 * Slots.Length];
        if (!TryRead(process, address, bytes)) return null;
        var values = new long[Slots.Length];
        for (int i = 0; i < values.Length; i++) values[i] = BitConverter.ToInt64(bytes, 8 * i);
        return values;
    }

    private static void WriteTable(IntPtr process, long address, long[] values)
    {
        byte[] bytes = new byte[8 * values.Length];
        for (int i = 0; i < values.Length; i++) BitConverter.GetBytes(values[i]).CopyTo(bytes, 8 * i);
        uint old, ignored;
        Require(VirtualProtectEx(process, new IntPtr(address), (UIntPtr)bytes.Length, 0x04, out old),
            "Cannot make FIFA's maths table writable.");
        try { Write(process, address, bytes); }
        finally { VirtualProtectEx(process, new IntPtr(address), (UIntPtr)bytes.Length, old, out ignored); }
    }

    // A table in memory is ordered by its own RVAs; Slots order is the shadow order.
    internal static long[] InSlotOrder(long[] table, long firstRva, Func<Slot, long> rvaOf)
    {
        if (table == null) return null;
        return Slots.Select(s => table[(rvaOf(s) - firstRva) / 8]).ToArray();
    }

    internal static long[] InTableOrder(long[] values, long firstRva, Func<Slot, long> rvaOf)
    {
        var table = new long[Slots.Length];
        for (int i = 0; i < Slots.Length; i++) table[(rvaOf(Slots[i]) - firstRva) / 8] = values[i];
        return table;
    }

    internal static string VersionOf(string dllPath)
    {
        var info = FileVersionInfo.GetVersionInfo(dllPath);
        return string.Format("{0}.{1}.{2}.{3}", info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
    }

    internal static int LocalFma3(string dllPath)
    {
        IntPtr module = LoadLibraryW(dllPath);
        Require(module != IntPtr.Zero, "Cannot load the bundled maths library.");
        IntPtr get = GetProcAddress(module, "_get_FMA3_enable");
        Require(get != IntPtr.Zero, "The bundled maths library has no _get_FMA3_enable.");
        return ((IntGetter)Marshal.GetDelegateForFunctionPointer(get, typeof(IntGetter)))();
    }

    // x64 stubs: cpuid(leaf, subleaf) writes eax, ebx, ecx, edx to the array; xgetbv(0) returns XCR0.
    private static readonly byte[] CpuidCode =
    {
        0x53,                   // push rbx
        0x89, 0xC8,             // mov eax, ecx (leaf)
        0x89, 0xD1,             // mov ecx, edx (subleaf)
        0x0F, 0xA2,             // cpuid
        0x41, 0x89, 0x00,       // mov [r8], eax
        0x41, 0x89, 0x58, 0x04, // mov [r8+4], ebx
        0x41, 0x89, 0x48, 0x08, // mov [r8+8], ecx
        0x41, 0x89, 0x50, 0x0C, // mov [r8+12], edx
        0x5B,                   // pop rbx
        0xC3,                   // ret
    };
    private static readonly byte[] XgetbvCode =
    {
        0x31, 0xC9,             // xor ecx, ecx
        0x0F, 0x01, 0xD0,       // xgetbv
        0x48, 0xC1, 0xE2, 0x20, // shl rdx, 32
        0x48, 0x09, 0xD0,       // or rax, rdx
        0xC3,                   // ret
    };

    // Whether this CPU runs AVX2 code: the CPU has it (CPUID 7.EBX bit 5) and Windows saves the YMM registers (CPUID 1.ECX
    // OSXSAVE and AVX, XCR0 bits 1 and 2). FIFA's own maths may take AVX2 paths outside ucrtbase, which the bridge cannot
    // pin, so the server keeps a CPU without AVX2 apart (an AMD FX-8300 has FMA3 but no AVX2 and desynced with everyone).
    // Read straight from CPUID: IsProcessorFeaturePresent's AVX2 flag is unknown to older Windows builds, which answer no.
    internal static int LocalAvx2()
    {
        IntPtr page = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x40);
        Require(page != IntPtr.Zero, "Cannot read this CPU's features.");
        try
        {
            Marshal.Copy(CpuidCode, 0, page, CpuidCode.Length);
            Marshal.Copy(XgetbvCode, 0, page + 64, XgetbvCode.Length);
            var cpuid = (CpuidFunction)Marshal.GetDelegateForFunctionPointer(page, typeof(CpuidFunction));
            var r = new uint[4];
            cpuid(0, 0, r);
            if (r[0] < 7) return 0;
            cpuid(1, 0, r);
            const uint OsXsave = 1u << 27, Avx = 1u << 28;
            if ((r[2] & (OsXsave | Avx)) != (OsXsave | Avx)) return 0;
            ulong xcr0 = ((XgetbvFunction)Marshal.GetDelegateForFunctionPointer(page + 64, typeof(XgetbvFunction)))();
            if ((xcr0 & 6) != 6) return 0;
            cpuid(7, 0, r);
            return (r[1] & (1u << 5)) != 0 ? 1 : 0;
        }
        finally { VirtualFree(page, UIntPtr.Zero, 0x8000); }
    }

    internal static string CheckLibrary(string dllPath, string devSha256)
    {
        Require(File.Exists(dllPath), "Bundled maths library not found: " + dllPath);
        Require(string.Equals(Path.GetFileName(dllPath), "ucrtbase.dll", StringComparison.OrdinalIgnoreCase),
            "The bundled maths library must keep its original file name (ucrtbase.dll).");
        string systemDll = Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "ucrtbase.dll"));
        Require(!string.Equals(dllPath, systemDll, StringComparison.OrdinalIgnoreCase), "Refusing to bridge to the Windows copy itself.");
        string hash = Sha256(dllPath);
        if (devSha256 != null)
        {
            Require(string.Equals(hash, devSha256, StringComparison.OrdinalIgnoreCase), "--dev-sha256 does not match " + dllPath);
            Console.WriteLine("[DEV] Unpinned maths library for a developer test: " + hash);
        }
        else
        {
            Require(PinnedUcrtSha256.Any(p => string.Equals(p, hash, StringComparison.OrdinalIgnoreCase)),
                "The bundled maths library is not the pinned Microsoft build; reinstall the launcher.");
        }
        var exports = ExportRvas(dllPath);
        foreach (Slot slot in Slots) Require(exports.ContainsKey(slot.Name), "Bundled maths library lacks " + slot.Name);
        return hash;
    }

    // Point both maths tables of a running process at the bundled ucrtbase.dll.
    // Returns how many pointers had to be switched again during the watch.
    internal static int SwitchMaths(IntPtr process, Process game, string dllPath, long systemBase,
        long shadowAddress, long importAddress, Stopwatch clock, int timeoutSeconds, int watchSeconds)
    {
        var bridgeExports = ExportRvas(dllPath);
        var systemExports = ExportRvas(Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "ucrtbase.dll")));
        long shadowFirst = Slots.Min(s => s.ShadowRva), importFirst = Slots.Min(s => s.ImportRva);
        string version = VersionOf(dllPath);
        long[] system = Slots.Select(s => systemBase + systemExports[s.Name]).ToArray();
        long bridgeBase;
        if (!Modules(process).TryGetValue(dllPath, out bridgeBase)) bridgeBase = LoadIntoGame(process, dllPath);
        long[] bridge = Slots.Select(s => bridgeBase + bridgeExports[s.Name]).ToArray();

        // Switch every slot that holds the Windows function.  The loader
        // fills the import table and FIFA's startup copies it into the
        // shadow table; slots not filled yet are switched on a later pass,
        // so maths FIFA runs during startup already uses the bundled copy.
        var tables = new[] {
            new { Address = shadowAddress, First = shadowFirst, Rva = (Func<Slot, long>)(s => s.ShadowRva) },
            new { Address = importAddress, First = importFirst, Rva = (Func<Slot, long>)(s => s.ImportRva) }
        };
        int switched = 0, repairs = 0;
        Stopwatch settled = null;
        while (true)
        {
            Require(!game.HasExited, "FIFA exited while the maths bridge was switching its imports.");
            bool pending = false, changed = false;
            foreach (var table in tables)
            {
                long[] values = InSlotOrder(ReadTable(process, table.Address), table.First, table.Rva);
                if (values == null) { pending = true; continue; }
                bool needs = false;
                for (int i = 0; i < values.Length; i++)
                {
                    if (values[i] == system[i]) needs = true;
                    else if (values[i] != bridge[i]) pending = true;
                }
                if (!needs) continue;
                Require(NtSuspendProcess(process) >= 0, "Cannot briefly pause FIFA to switch its maths.");
                try
                {
                    values = InSlotOrder(ReadTable(process, table.Address), table.First, table.Rva);
                    Require(values != null, "FIFA's maths table became unreadable.");
                    int count = 0;
                    for (int i = 0; i < values.Length; i++)
                        if (values[i] == system[i]) { values[i] = bridge[i]; count++; }
                    WriteTable(process, table.Address, InTableOrder(values, table.First, table.Rva));
                    long[] check = InSlotOrder(ReadTable(process, table.Address), table.First, table.Rva);
                    Require(check != null && check.SequenceEqual(values), "Maths table verification failed.");
                    if (settled != null) repairs += count; else switched += count;
                    changed = true;
                }
                finally { NtResumeProcess(process); }
            }
            if (pending || changed)
            {
                if (settled != null && changed) Console.WriteLine("[REPAIRED] FIFA restored Windows maths pointers; switched them again.");
                Require(clock.Elapsed.TotalSeconds < timeoutSeconds + watchSeconds,
                    "FIFA's maths tables did not reach the expected state in time (unsupported build or protection change).");
                if (pending) settled = null;
                Thread.Sleep(pending ? 1 : 50);
                continue;
            }
            if (settled == null)
            {
                settled = Stopwatch.StartNew();
                Console.WriteLine("[SWITCHED] {0} maths pointers use the bundled ucrtbase {1}; watching for {2} seconds.", switched, version, watchSeconds);
            }
            if (settled.Elapsed.TotalSeconds >= watchSeconds) break;
            Thread.Sleep(50);
        }
        return repairs;
    }

    // The X-Aurora21-Math value for a FIFA that runs this library on this CPU (the server's bridge contract).
    // avx2 follows fma3, so a server that only knows fma3 still reads the report.
    internal static string MathValue(string version, string hash, int fma3, int avx2)
    {
        return string.Format("v=1; ucrt={0}; sha256={1}; fma3={2}; avx2={3}", version, hash.Substring(0, 16), fma3, avx2);
    }

    // Returns the [MATH] value of the library FIFA now runs.
    private static string Install(int pid, string gamePath, string dllPath, string devSha256, bool statusOnly, int timeoutSeconds, int watchSeconds)
    {
        Require(Environment.Is64BitProcess, "Run the 64-bit maths bridge.");
        string expectedGame = Path.GetFullPath(gamePath);
        var file = new FileInfo(expectedGame);
        Require(file.Exists && file.Length == SupportedFileLength &&
            FileVersionInfo.GetVersionInfo(expectedGame).ProductVersion == SupportedProductVersion,
            "Unsupported FIFA executable. The maths bridge supports build " + SupportedProductVersion + " only.");
        string hash = CheckLibrary(dllPath, devSha256);
        string version = VersionOf(dllPath);
        // _get_FMA3_enable of the pinned library on this CPU: the code path FIFA's maths will take.
        int fma3 = LocalFma3(dllPath);
        int avx2 = LocalAvx2();
        string math = MathValue(version, hash, fma3, avx2);
        string systemDll = Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "ucrtbase.dll"));
        var bridgeExports = ExportRvas(dllPath);
        var systemExports = ExportRvas(systemDll);
        foreach (Slot slot in Slots) Require(systemExports.ContainsKey(slot.Name), "Windows ucrtbase.dll lacks " + slot.Name);
        long shadowFirst = Slots.Min(s => s.ShadowRva), importFirst = Slots.Min(s => s.ImportRva);

        using (var game = Process.GetProcessById(pid))
        {
            Require(game.ProcessName.Equals("FIFA21", StringComparison.OrdinalIgnoreCase), "Selected PID is not FIFA21.exe.");
            IntPtr process = OpenProcess(statusOnly ? 0x410u : 0xC3Au, false, pid);
            if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Administrator access is required for the selected FIFA process.");
            try
            {
                var clock = Stopwatch.StartNew();
                Console.WriteLine("[WAIT] Waiting for FIFA to load its maths imports (up to {0} seconds).", timeoutSeconds);
                Dictionary<string, long> modules;
                long systemBase;
                while (true)
                {
                    modules = Modules(process);
                    if (modules.TryGetValue(systemDll, out systemBase)) break;
                    Require(!game.HasExited, "FIFA exited before its maths imports were loaded.");
                    Require(clock.Elapsed.TotalSeconds < timeoutSeconds, "FIFA did not load ucrtbase.dll in time; nothing was changed.");
                    Thread.Sleep(5);
                }
                string mainPath = Path.GetFullPath(game.MainModule.FileName);
                Require(string.Equals(mainPath, expectedGame, StringComparison.OrdinalIgnoreCase),
                    "Selected FIFA process does not match the requested executable path.");
                long imageBase = game.MainModule.BaseAddress.ToInt64();

                long bridgeBase;
                bool loaded = modules.TryGetValue(dllPath, out bridgeBase);
                if (statusOnly)
                {
                    long[] shadowNow = InSlotOrder(ReadTable(process, imageBase + shadowFirst), shadowFirst, s => s.ShadowRva);
                    Require(loaded && shadowNow != null && shadowNow.SequenceEqual(Slots.Select(s => bridgeBase + bridgeExports[s.Name])),
                        "The maths bridge is not installed in this FIFA process.");
                    Console.WriteLine("[ALREADY] Maths bridge is installed for PID {0}.", pid);
                    Console.WriteLine("[MATH] " + math);
                    return math;
                }
                int repairs = SwitchMaths(process, game, dllPath, systemBase, imageBase + shadowFirst, imageBase + importFirst,
                    clock, timeoutSeconds, watchSeconds);
                Console.WriteLine("[READY] Maths bridge installed for PID {0}: {1} maths imports use ucrtbase {2} (FMA3 {3}, AVX2 {4}){5}.",
                    pid, Slots.Length, version, fma3 == 1 ? "on" : "off", avx2 == 1 ? "on" : "off", repairs > 0 ? "; repaired " + repairs : "");
                Console.WriteLine("[MATH] " + math);
                Console.WriteLine("[SCOPE] Current FIFA process only; no game or Windows files changed.");
                return math;
            }
            finally { CloseHandle(process); }
        }
    }

    internal static bool IsLaunchCode(string code)
    {
        return code != null && code.Length == 40 && code.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'));
    }

    // The launch code from standard input: one line of 40 lowercase hex, or null.  Read as bytes, because the writer's
    // encoding is not ours to choose: Windows PowerShell's Process.StandardInput opens with the console input encoding
    // and, on a PC whose code page is UTF-8, sends a byte order mark first.
    internal static string ReadLaunchCode(Stream input)
    {
        var bytes = new List<byte>();
        int next;
        while (bytes.Count < 256 && (next = input.ReadByte()) >= 0 && next != '\n') bytes.Add((byte)next);
        int start = bytes.Count >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
        string line = Encoding.ASCII.GetString(bytes.ToArray(), start, bytes.Count - start).Trim();
        return IsLaunchCode(line) ? line : null;
    }

    // The code is a bearer secret: it only travels over HTTPS, or plain HTTP to this PC (a local test server).
    internal static bool IsReportUrl(string url)
    {
        Uri uri;
        return Uri.TryCreate(url, UriKind.Absolute, out uri) && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback));
    }

    private static string OneLine(string text, int limit)
    {
        text = (text ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();
        return text.Length <= limit ? text : text.Substring(0, limit);
    }

    // Tell the server which maths this FIFA runs, so matchmaking can pair it: POST /client/launch/math with
    // { "code": <launch code> } and the X-Aurora21-Math header.  The code names the player without being used up
    // (FIFA logs in with the same code).  The first URL that answers 200 wins; every URL is the same server.
    internal static void Report(IList<string> urls, string code, string math)
    {
        Require(IsLaunchCode(code), "No valid launch code.");
        // A .NET Framework 4 program without a newer target offers TLS 1.0 only; the server's HTTPS front needs TLS 1.2.
        ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
        ServicePointManager.Expect100Continue = false;
        byte[] body = Encoding.UTF8.GetBytes("{\"code\":\"" + code + "\"}");
        string failure = "no report URL";
        foreach (string url in urls)
        {
            string host = new Uri(url).Host;
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(url);
                request.Method = "POST";
                request.Timeout = 10000;
                request.ReadWriteTimeout = 10000;
                request.UserAgent = "Aurora21-MathBridge/1";
                request.ContentType = "application/json";
                request.Headers["X-Aurora21-Math"] = math;
                request.ContentLength = body.Length;
                using (var stream = request.GetRequestStream()) stream.Write(body, 0, body.Length);
                using (var response = (HttpWebResponse)request.GetResponse())
                using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                {
                    string reply = OneLine(reader.ReadToEnd(), 200);
                    if (response.StatusCode == HttpStatusCode.OK)
                    {
                        Console.WriteLine("[REPORTED] {0} to {1}: {2}", math, host, reply);
                        return;
                    }
                    failure = string.Format("{0} answered {1} {2}", host, (int)response.StatusCode, reply);
                }
            }
            catch (WebException error)
            {
                var response = error.Response as HttpWebResponse;
                if (response != null)
                {
                    using (response)
                    using (var reader = new StreamReader(response.GetResponseStream(), Encoding.UTF8))
                        failure = string.Format("{0} answered {1} {2}", host, (int)response.StatusCode, OneLine(reader.ReadToEnd(), 200));
                }
                else failure = host + ": " + error.Message;
            }
            Console.WriteLine("[RETRY] Maths report: " + failure);
        }
        throw new InvalidOperationException(failure);
    }

    private static void SelfTest(string dllPath, string devSha256)
    {
        Require(Environment.Is64BitProcess, "Self-tests require an x64 build.");
        Require(Slots.Length == 50 && Slots.Select(s => s.Name).Distinct().Count() == 50, "The slot map must list 50 distinct imports.");
        Require(Slots.Max(s => s.ShadowRva) - Slots.Min(s => s.ShadowRva) == 8 * 49 &&
            Slots.Select(s => s.ShadowRva).Distinct().Count() == 50, "The shadow table is not 50 contiguous slots.");
        Require(Slots.Max(s => s.ImportRva) - Slots.Min(s => s.ImportRva) == 8 * 49 &&
            Slots.Select(s => s.ImportRva).Distinct().Count() == 50, "The import table is not 50 contiguous slots.");
        long[] probe = Enumerable.Range(0, 50).Select(i => (long)i * 7 + 3).ToArray();
        long first = Slots.Min(s => s.ImportRva);
        Require(InSlotOrder(InTableOrder(probe, first, s => s.ImportRva), first, s => s.ImportRva).SequenceEqual(probe),
            "Slot ordering round trip failed.");
        Console.WriteLine("PASS slot map: 50 imports, contiguous shadow and import tables.");
        if (dllPath == null) return;

        string hash = CheckLibrary(dllPath, devSha256);
        Console.WriteLine("PASS library: SHA-256 {0}, all 50 exports present, version {1}.",
            hash, VersionOf(dllPath));
        IntPtr system = GetModuleHandleW("ucrtbase.dll");
        IntPtr bundled = LoadLibraryW(dllPath);
        Require(bundled != IntPtr.Zero && system != IntPtr.Zero && bundled != system, "The bundled copy did not load as a separate module.");
        var mine = (FloatFunction)Marshal.GetDelegateForFunctionPointer(GetProcAddress(bundled, "acosf"), typeof(FloatFunction));
        var windows = (FloatFunction)Marshal.GetDelegateForFunctionPointer(GetProcAddress(system, "acosf"), typeof(FloatFunction));
        int differ = 0;
        var random = new Random(21);
        byte[] bits = new byte[4];
        for (int i = 0; i < 200000; i++)
        {
            random.NextBytes(bits);
            // Finite inputs in (-1, 1): clear the exponent's top bit.
            float x = BitConverter.ToSingle(BitConverter.GetBytes(BitConverter.ToUInt32(bits, 0) & 0xBF7FFFFFu), 0);
            if (BitConverter.ToInt32(BitConverter.GetBytes(mine(x)), 0) != BitConverter.ToInt32(BitConverter.GetBytes(windows(x)), 0)) differ++;
        }
        Console.WriteLine("PASS separate module: bundled acosf differs from this PC's Windows copy on {0} of 200000 inputs; FMA3 {1}; AVX2 {2}.",
            differ, LocalFma3(dllPath) == 1 ? "on" : "off", LocalAvx2() == 1 ? "on" : "off");
        Console.WriteLine("No FIFA process was opened or modified by these self-tests.");
    }

    internal static int Main(string[] args)
    {
        StreamWriter log = null;
        TextWriter originalOutput = Console.Out, originalError = Console.Error;
        try
        {
            int pid = 0, timeoutSeconds = 180, watchSeconds = 20;
            string game = null, dll = null, logPath = null, devSha256 = null;
            var reportUrls = new List<string>();
            bool statusOnly = false, selfTest = false, codeFromStdin = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--status") statusOnly = true;
                else if (args[i] == "--self-test") selfTest = true;
                else if (args[i] == "--code-stdin") codeFromStdin = true;
                else if (args[i] == "--pid" && i + 1 < args.Length) pid = int.Parse(args[++i]);
                else if (args[i] == "--game" && i + 1 < args.Length) game = args[++i];
                else if (args[i] == "--dll" && i + 1 < args.Length) dll = Path.GetFullPath(args[++i]);
                else if (args[i] == "--log" && i + 1 < args.Length) logPath = args[++i];
                else if (args[i] == "--report" && i + 1 < args.Length) reportUrls.Add(args[++i]);
                else if (args[i] == "--timeout" && i + 1 < args.Length) timeoutSeconds = int.Parse(args[++i]);
                else if (args[i] == "--watch" && i + 1 < args.Length) watchSeconds = int.Parse(args[++i]);
                else if (args[i] == "--dev-sha256" && i + 1 < args.Length) devSha256 = args[++i];
                else throw new ArgumentException("Usage: --pid N --game <FIFA21.exe> --dll <ucrt\\ucrtbase.dll> [--code-stdin --report <url> ...] [--status] [--log <absolute path>] [--timeout s] [--watch s], or --self-test [--dll <ucrt\\ucrtbase.dll>]");
            }
            if (logPath != null)
            {
                Require(Path.IsPathRooted(logPath), "The maths bridge log path must be absolute.");
                log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read));
                log.AutoFlush = true;
                Console.SetOut(log);
                Console.SetError(log);
            }
            if (selfTest) { SelfTest(dll, devSha256); return 0; }
            Require(pid > 0 && !string.IsNullOrWhiteSpace(game) && dll != null, "Specify the exact FIFA PID, executable path and bundled --dll.");
            Require(timeoutSeconds > 0 && timeoutSeconds <= 600 && watchSeconds >= 0 && watchSeconds <= 600, "Timeouts must be 0-600 seconds.");
            Require(reportUrls.Count == 0 || codeFromStdin, "--report needs --code-stdin: the report names the player by its launch code.");
            Require(reportUrls.All(IsReportUrl), "A --report URL must be HTTPS (or HTTP to this PC).");
            string math = Install(pid, game, dll, devSha256, statusOnly, timeoutSeconds, watchSeconds);
            if (reportUrls.Count > 0 && !statusOnly)
            {
                // Read only now: the launch hands the code over (or closes standard input without one) once it knows
                // this is the FIFA process it patched.
                string code;
                using (var input = Console.OpenStandardInput()) code = ReadLaunchCode(input);
                if (code == null)
                {
                    Console.WriteLine("[INFO] No launch code was handed over for this FIFA process; the maths report was not sent.");
                    return 0;
                }
                try { Report(reportUrls, code, math); }
                catch (Exception error)
                {
                    // The bridge is installed; matchmaking falls back to the Windows build.
                    Console.WriteLine("[WARN] Maths report not sent: " + error.Message);
                }
                finally { code = null; }
            }
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("[ERROR] " + error.Message);
            return 1;
        }
        finally
        {
            Console.SetOut(originalOutput);
            Console.SetError(originalError);
            if (log != null) log.Dispose();
        }
    }
}
