using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace Aurora21.UltimateTeam.Server.Tools;

/// <summary>A byte sequence to replace in the running game.</summary>
public sealed record MemoryPatch(string Label, byte[] Original, byte[] Replacement);

/// <summary>
/// Process-local trust bridge for FIFA 21: finds EA's pinned GOS 2015 CA modulus in the running
/// game's memory and swaps in the local CA modulus (and, in LSX replacement mode, bypasses the
/// Origin client bootstrap check). Nothing on disk is modified; the patch disappears when the game exits.
/// Output lines prefixed [PATCHED]/[ALREADY]/[SUCCESS] are parsed by Launch-UltimateTeam.ps1.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TrustBridge
{
    public const string OriginBootstrapLabel = "Origin client bootstrap bypass";

    private const int ModulusBytes = 256;

    // The GS 2019 anchor in this build is a 65-byte EC point; writing 256-byte files there corrupts TLS state.
    private static readonly (string Original, string Local, string Label)[] ModulusPairs =
    {
        ("gos2015-original-modulus.bin", "local-gos2015-modulus.bin", "GOS 2015 Certificate Authority"),
    };

    private static readonly MemoryPatch OriginBootstrap = new(OriginBootstrapLabel,
        Convert.FromHexString("85F67567418B8768040000"), Convert.FromHexString("EB699090418B8768040000"));

    private static readonly string[] GameExecutables = { "FIFA21.exe", "FIFA21_Trial.exe" };

    /// <param name="trustAnchors">The folder holding the replacement CA modulus files (a hosted server's exported trust
    /// anchors); null uses <paramref name="certificateRoot"/>, the locally generated CAs. EA's original moduli always come
    /// from <paramref name="certificateRoot"/>.</param>
    public static int Patch(string certificateRoot, int? pid, string? only, bool skipOrigin, string? trustAnchors = null)
    {
        var patches = LoadModulusPairs(certificateRoot, trustAnchors, only, restore: false);
        if (patches.Count == 0)
        {
            return Fail("No patchable CA pairs are available.");
        }

        Console.WriteLine("FIFA 21 localhost trust bridge");
        Console.WriteLine("==============================");
        Console.WriteLine("Scope: running FIFA 21 process only");
        Console.WriteLine("No file on disk is modified; the patch vanishes when the game exits.");
        foreach (var patch in patches)
        {
            Console.WriteLine($"  target: {patch.Label}");
        }
        Console.WriteLine();

        if (FindGameProcess(pid) is not { } gamePid)
        {
            return Fail(pid is null
                ? "FIFA21.exe or FIFA21_Trial.exe is not running. Launch FIFA 21 and stay at the main menu first."
                : $"The requested FIFA 21 process PID {pid} is no longer running.");
        }
        Console.WriteLine($"[INFO] FIFA 21 process PID: {gamePid}");

        using var process = ProcessMemory.Open(gamePid);
        if (process is null)
        {
            return Fail("OpenProcess failed. Run this as Administrator, and make sure FIFA 21 is still running.");
        }

        var total = patches.Sum(patch => Apply(process, patch));
        if (skipOrigin)
        {
            Console.WriteLine($"[SKIP] {OriginBootstrapLabel}: EA App session mode");
        }
        else
        {
            total += Apply(process, OriginBootstrap);
        }

        if (total == 0)
        {
            return Fail("The patch targets were found but no copy could be patched.");
        }
        return Succeed($"Patched {total} copy/copies in the running FIFA 21 process.");
    }

    /// <param name="trustAnchors">The same replacement-modulus folder the patch used; null for the local CAs.</param>
    public static int Restore(string certificateRoot, int? pid, string? trustAnchors = null)
    {
        var patches = LoadModulusPairs(certificateRoot, trustAnchors, null, restore: true);
        if (patches.Count == 0)
        {
            return Fail("No restore pairs available.");
        }
        Console.WriteLine("FIFA 21 localhost trust bridge - RESTORE");
        Console.WriteLine("========================================");
        if (FindGameProcess(pid) is not { } gamePid)
        {
            return Fail("FIFA21.exe is not running.");
        }
        Console.WriteLine($"[INFO] FIFA21.exe PID: {gamePid}");
        using var process = ProcessMemory.Open(gamePid);
        if (process is null)
        {
            return Fail("OpenProcess failed. Run this as Administrator, and make sure FIFA21.exe is still running.");
        }

        var restored = 0;
        foreach (var patch in patches)
        {
            Console.WriteLine($"[INFO] Scanning for {patch.Label}...");
            var matches = process.Scan(patch.Original);
            Console.WriteLine($"[INFO] Exact local copies found: {matches.Count}");
            restored += WriteAll(process, patch, matches, "RESTORED", verbose: false);
        }
        if (restored == 0)
        {
            return Fail("No local copies were found. Nothing was changed.");
        }
        return Succeed($"Restored {restored} copy/copies in the running FIFA21.exe.");
    }

    /// <summary>Patches every original copy; copies already holding the replacement count as done.</summary>
    private static int Apply(ProcessMemory process, MemoryPatch patch)
    {
        Console.WriteLine($"[INFO] Scanning for {patch.Label}...");
        var matches = process.Scan(patch.Original);
        var already = process.Scan(patch.Replacement);
        PrintAddresses("Exact original copies found", matches);
        PrintAddresses("Already-patched copies found", already);
        var patched = WriteAll(process, patch, matches, "PATCHED", verbose: true);
        if (already.Count > 0)
        {
            Console.WriteLine($"[ALREADY] {already.Count} copy/copies ({patch.Label})");
        }
        return patched + already.Count;
    }

    private static int WriteAll(ProcessMemory process, MemoryPatch patch, IEnumerable<long> addresses, string doneTag, bool verbose)
    {
        var written = 0;
        foreach (var address in addresses)
        {
            if (process.WriteExact(address, patch.Original, patch.Replacement, verbose))
            {
                written++;
                Console.WriteLine($"[{doneTag}] 0x{address:X16} ({patch.Label})");
            }
            else
            {
                Console.WriteLine($"[FAILED]  0x{address:X16}");
            }
        }
        return written;
    }

    private static void PrintAddresses(string heading, IReadOnlyCollection<long> addresses)
    {
        Console.WriteLine($"[INFO] {heading}: {addresses.Count}");
        foreach (var address in addresses)
        {
            Console.WriteLine($"       0x{address:X16}");
        }
    }

    private static List<MemoryPatch> LoadModulusPairs(string root, string? trustAnchors, string? only, bool restore)
    {
        var patches = new List<MemoryPatch>();
        foreach (var (originalName, localName, label) in ModulusPairs.Where(pair => only is null || pair.Label.Contains(only, StringComparison.OrdinalIgnoreCase)))
        {
            var originalPath = Path.Combine(root, originalName);
            var localPath = Path.Combine(trustAnchors ?? root, localName);
            if (!File.Exists(originalPath) || !File.Exists(localPath))
            {
                Console.WriteLine($"[SKIP] {label}: missing {originalName} or {localName}. Run generate-certs first.");
                continue;
            }
            var original = File.ReadAllBytes(originalPath);
            var local = File.ReadAllBytes(localPath);
            if (original.Length != ModulusBytes || local.Length != ModulusBytes)
            {
                Console.WriteLine($"[SKIP] {label}: modulus files must be exactly {ModulusBytes} bytes.");
                continue;
            }
            if (original.AsSpan().SequenceEqual(local))
            {
                Console.WriteLine($"[SKIP] {label}: local modulus equals the original (cert not regenerated?).");
                continue;
            }
            patches.Add(restore ? new MemoryPatch(label, local, original) : new MemoryPatch(label, original, local));
        }
        return patches;
    }

    private static int? FindGameProcess(int? requestedPid)
    {
        foreach (var process in System.Diagnostics.Process.GetProcesses())
        {
            using (process)
            {
                var matchesName = GameExecutables.Any(exe => string.Equals(process.ProcessName + ".exe", exe, StringComparison.OrdinalIgnoreCase));
                if (matchesName && (requestedPid is null || process.Id == requestedPid))
                {
                    return process.Id;
                }
            }
        }
        return null;
    }

    private static int Succeed(string message)
    {
        Console.WriteLine();
        Console.WriteLine($"[SUCCESS] {message}");
        Console.WriteLine("No file on disk was modified.");
        return 0;
    }

    private static int Fail(string message)
    {
        Console.WriteLine($"[ERROR] {message}");
        return 1;
    }
}

/// <summary>Read/scan/write access to another process's memory through kernel32.</summary>
[SupportedOSPlatform("windows")]
internal sealed partial class ProcessMemory : IDisposable
{
    private const uint ProcessQueryInformation = 0x0400;
    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint MemCommit = 0x1000;
    private const uint PageNoAccess = 0x01;
    private const uint PageGuard = 0x100;
    private const uint PageExecuteReadWrite = 0x40;
    private const long MaxAddress = 0x00007FFFFFFFFFFF;
    private const int ChunkSize = 16 * 1024 * 1024;
    private const int WriteAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(50);

    private readonly IntPtr _handle;

    private ProcessMemory(IntPtr handle) => _handle = handle;

    public static ProcessMemory? Open(int pid)
    {
        var handle = OpenProcess(ProcessQueryInformation | ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, (uint)pid);
        return handle == IntPtr.Zero ? null : new ProcessMemory(handle);
    }

    public byte[]? Read(long address, int size)
    {
        if (size <= 0)
        {
            return null;
        }
        var buffer = new byte[size];
        if (!ReadProcessMemory(_handle, (IntPtr)address, buffer, (nuint)size, out var read) || read == 0)
        {
            return null;
        }
        return read == (nuint)size ? buffer : buffer.AsSpan(0, (int)read).ToArray();
    }

    /// <summary>Every exact match in committed, readable memory, in address order (16 MiB chunks; a match across a chunk boundary is found through the carried tail).</summary>
    public List<long> Scan(byte[] needle)
    {
        var matches = new List<long>();
        long address = 0;
        var overlap = needle.Length - 1;
        while (address < MaxAddress)
        {
            if (VirtualQueryEx(_handle, (IntPtr)address, out var info, (nuint)Marshal.SizeOf<MemoryBasicInformation>()) == 0)
            {
                break;
            }
            var baseAddress = (long)info.BaseAddress;
            var size = (long)info.RegionSize;
            var readable = info.State == MemCommit && (info.Protect & PageGuard) == 0 && info.Protect != PageNoAccess;
            if (readable && size > 0)
            {
                long offset = 0;
                var carry = Array.Empty<byte>();
                while (offset < size)
                {
                    var want = (int)Math.Min(ChunkSize, size - offset);
                    var block = Read(baseAddress + offset, want);
                    if (block is null)
                    {
                        break;
                    }
                    var data = carry.Length == 0 ? block : [.. carry, .. block];
                    var dataStart = baseAddress + offset - carry.Length;
                    for (var index = data.AsSpan().IndexOf(needle); index >= 0; index = NextMatch(data, needle, index))
                    {
                        // The carry is one byte shorter than the needle, so no match is found twice.
                        matches.Add(dataStart + index);
                    }
                    carry = data.Length >= overlap ? data.AsSpan(data.Length - overlap).ToArray() : data;
                    offset += block.Length;
                    if (block.Length < want)
                    {
                        break;
                    }
                }
            }
            var next = baseAddress + size;
            if (next <= address)
            {
                break;
            }
            address = next;
        }
        return matches;
    }

    private static int NextMatch(byte[] data, byte[] needle, int previous)
    {
        var next = data.AsSpan(previous + 1).IndexOf(needle);
        return next < 0 ? -1 : previous + 1 + next;
    }

    /// <summary>Writes only when the bytes still hold the expected original; verifies and retries up to <see cref="WriteAttempts"/> times.</summary>
    public bool WriteExact(long address, byte[] original, byte[] replacement, bool verbose)
    {
        var current = Read(address, original.Length);
        if (current is not null && current.AsSpan().SequenceEqual(replacement))
        {
            return true;
        }
        if (current is null || !current.AsSpan().SequenceEqual(original))
        {
            if (verbose)
            {
                Console.WriteLine($"[SKIP] 0x{address:X16}: bytes changed before write current_sha256={Hash(current ?? Array.Empty<byte>())} expected_sha256={Hash(original)}");
            }
            return false;
        }

        for (var attempt = 1; attempt <= WriteAttempts; attempt++)
        {
            if (!VirtualProtectEx(_handle, (IntPtr)address, (nuint)replacement.Length, PageExecuteReadWrite, out var oldProtect))
            {
                if (verbose)
                {
                    Console.WriteLine($"[WRITE ERROR] 0x{address:X16}: VirtualProtectEx failed attempt={attempt}/{WriteAttempts} winerr={Marshal.GetLastPInvokeError()}");
                }
                Thread.Sleep(RetryDelay);
                continue;
            }
            bool ok;
            nuint written;
            int error;
            try
            {
                ok = WriteProcessMemory(_handle, (IntPtr)address, replacement, (nuint)replacement.Length, out written);
                error = Marshal.GetLastPInvokeError();
            }
            finally
            {
                VirtualProtectEx(_handle, (IntPtr)address, (nuint)replacement.Length, oldProtect, out _);
            }
            var verify = Read(address, replacement.Length);
            var verified = verify is not null && verify.AsSpan().SequenceEqual(replacement);
            if (ok && written == (nuint)replacement.Length && verified)
            {
                return true;
            }
            if (verbose)
            {
                Console.WriteLine($"[WRITE ERROR] 0x{address:X16}: WriteProcessMemory failed attempt={attempt}/{WriteAttempts} winerr={error} written={written}/{replacement.Length} verified={verified}");
            }
            Thread.Sleep(RetryDelay);
        }
        return false;
    }

    public void Dispose() => CloseHandle(_handle);

    private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data))[..16].ToLowerInvariant();

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryBasicInformation
    {
        public IntPtr BaseAddress;
        public IntPtr AllocationBase;
        public uint AllocationProtect;
        public ushort PartitionId;
        public nuint RegionSize;
        public uint State;
        public uint Protect;
        public uint Type;
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nuint VirtualQueryEx(IntPtr process, IntPtr address, out MemoryBasicInformation info, nuint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(IntPtr process, IntPtr address, [Out] byte[] buffer, nuint size, out nuint read);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint written);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualProtectEx(IntPtr process, IntPtr address, nuint size, uint newProtect, out uint oldProtect);
}
