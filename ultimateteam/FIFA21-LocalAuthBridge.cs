using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

// Ultimate Team compatibility only. EA App still performs launch/licence operations.
// No file in the game installation is changed and no debugger is used.
internal static class LocalAuthBridge
{
    private const long AuthRva = 0x10290A0;
    private const long DestroyRva = 0x1029350;
    // DoTick's post-call path for the direct FOS-SERVER request. The 15-byte span reloads the returned pointer and
    // length then jumps to the common success check. Keep the original auth call and its ownership side effects.
    private const long FutReturnRva = 0xB790E0;
    private const long FutContinuationRva = 0xB79145;
    // The swap chain's present path: when Present returns DXGI_STATUS_OCCLUDED (window minimised or hidden, windowed or
    // borderless) it waits up to 100 ms on every frame, so an alt-tabbed game runs at about 10 fps and its lockstep
    // online opponent freezes with it. The span is the occluded check, the exclusive-fullscreen skip and the wait's
    // timeout; only the timeout's low byte changes, 100 -> 1 ms. Same instructions, still an alertable wait.
    private const long PresentWaitRva = 0x2665F8A;
    private const int PresentTimeoutOffset = 14;
    private const int PageSize = 4096;
    private const int AuthOffset = 0x100;
    private const int DestroyOffset = 0x500;
    private const int AuthOriginalOffset = 0x700;
    private const int DestroyOriginalOffset = 0x780;
    private const int FutOffset = 0x900;
    private const int CodeStringOffset = 0x100;
    private const int FutCodeOffset = 0x380;
    private const int FutCodeLength = 40;
    private const int CounterOffset = 0x80;
    // Diagnostic record of the client IDs passed through to FIFA's own auth (not ours): each such call claims the next
    // 64-byte slot (atomic index) and copies the ID's first 63 bytes. IDs only, never a code. Read with --status.
    private const int PassedIndexOffset = CounterOffset + 32;
    private const int PassedSlotOffset = 0x400;
    private const int PassedSlotSize = 64;
    private const int PassedSlotCount = (PageSize - PassedSlotOffset) / PassedSlotSize;
    private const uint ReadWrite = 0x04;
    private const uint ExecuteRead = 0x20;
    private const uint ExecuteReadWrite = 0x40;
    // FIFA has to be running and unpacked before it can be patched. The module-list wait and the live auth-function wait
    // share this one budget, so the bridge reports its own result inside the launchers' 60-second helper timeout.
    private const int StartupWaitMs = 45000;
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("ULTIMATETEAM21-AUTH-BRIDGE-V2\0");
    // Full instruction boundaries, verified against the EA build's unpacked code.
    private static readonly byte[] AuthPrologue = Hex("48895C240848896C24104889742418");
    private static readonly byte[] DestroyPrologue = Hex("40534883EC30488B0563CDF807488BD9");
    private static readonly byte[] FutReturnInstructions = Hex("488B5C2478488BB42480000000EB56");
    private static readonly byte[] PresentWaitInstructions = Hex("3D01007A08751E837F5401741FBA64000000448D429D");
    private static readonly byte[] PresentWaitPatch = Hex("3D01007A08751E837F5401741FBA01000000448D429D");
    // PROCESS_POWER_THROTTLING_STATE control bits: EcoQoS execution speed, and ignoring the process's timer resolution.
    private const int ProcessPowerThrottling = 4;
    private const uint ThrottleExecutionSpeed = 0x1;
    private const uint ThrottleIgnoreTimerResolution = 0x4;
    private const string SupportedExeSha256 = "44d0acc62a214c6f4dab2b8a4f4bdd068869c636f15cc262035b4edc862a1494";
    // Additional same-version copy reported by the maintainer; live instruction checks remain mandatory.
    private const string AlternateExeSha256 = "05cc56f897201a6ba74d608f971bcf157b1a46c10e9cda335e868c17004fbc71";

    private static bool SupportedExecutableHash(string hash)
    {
        return hash == SupportedExeSha256 || hash == AlternateExeSha256;
    }
    private const string SupportedCardsDllSha256 = "37133be975c0552c919e2901b7da203eb458792b5bf43764821ad687de548a49";
    private static readonly string[] ClientIds = {
        "ultimateteam21-blaze-server", "ultimateteam21-blaze-sdk", "ultimateteam21-ssf"
    };
    private const string LocalCode = "LOCAL-FUT21-AUTH-CODE";

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();
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
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, UIntPtr size);
    [DllImport("ntdll.dll")]
    private static extern int NtSuspendProcess(IntPtr process);
    [DllImport("ntdll.dll")]
    private static extern int NtResumeProcess(IntPtr process);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint access, bool inherit, uint threadId);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetThreadContext(IntPtr thread, IntPtr context);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, [Out] long[] information, int length, out int returned);
    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(IntPtr thread, int infoClass, out uint information, int length, IntPtr returned);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState information, int length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessInformation(IntPtr process, int infoClass, ref PowerThrottlingState information, int length);

    [StructLayout(LayoutKind.Sequential)]
    private struct PowerThrottlingState
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int AuthCall(ulong user, IntPtr clientId, IntPtr code, IntPtr length);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int DestroyCall(IntPtr handle);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate int FutTestCall(IntPtr job, IntPtr result);

    private sealed class Assembler
    {
        private readonly List<byte> bytes = new List<byte>();
        private readonly Dictionary<string, int> labels = new Dictionary<string, int>();
        private readonly List<KeyValuePair<int, string>> fixups = new List<KeyValuePair<int, string>>();
        internal void Emit(params byte[] value) { bytes.AddRange(value); }
        internal void U64(long value) { Emit(BitConverter.GetBytes(value)); }
        internal void Label(string name) { labels.Add(name, bytes.Count); }
        internal void Branch(string name, params byte[] opcode)
        {
            Emit(opcode);
            fixups.Add(new KeyValuePair<int, string>(bytes.Count, name));
            Emit(0, 0, 0, 0);
        }
        internal void AbsoluteJump(long address) { Emit(0xFF, 0x25, 0, 0, 0, 0); U64(address); }
        internal byte[] Finish()
        {
            byte[] result = bytes.ToArray();
            foreach (var fixup in fixups)
                Buffer.BlockCopy(BitConverter.GetBytes(checked(labels[fixup.Value] - fixup.Key - 4)), 0, result, fixup.Key, 4);
            return result;
        }
    }

    private static byte[] Hex(string text)
    {
        return Enumerable.Range(0, text.Length / 2).Select(i => Convert.ToByte(text.Substring(i * 2, 2), 16)).ToArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static byte[] Read(IntPtr process, long address, int size)
    {
        byte[] bytes = new byte[size];
        UIntPtr count;
        if (!ReadProcessMemory(process, new IntPtr(address), bytes, (UIntPtr)size, out count) || count.ToUInt64() != (ulong)size)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot read the selected FIFA process.");
        return bytes;
    }

    private static void Write(IntPtr process, long address, byte[] bytes)
    {
        UIntPtr count;
        if (!WriteProcessMemory(process, new IntPtr(address), bytes, (UIntPtr)bytes.Length, out count) || count.ToUInt64() != (ulong)bytes.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot write the selected process allocation.");
    }

    private static long Allocate(IntPtr process)
    {
        IntPtr address = VirtualAllocEx(process, IntPtr.Zero, (UIntPtr)PageSize, 0x3000, ReadWrite);
        if (address == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Local bridge allocation failed.");
        return address.ToInt64();
    }

    private static void ProtectCode(IntPtr process, long address)
    {
        uint previous;
        if (!VirtualProtectEx(process, new IntPtr(address), (UIntPtr)PageSize, ExecuteRead, out previous) ||
            !FlushInstructionCache(process, new IntPtr(address), (UIntPtr)PageSize))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot finalize local bridge code.");
    }

    private static void ReplaceCode(IntPtr process, long address, byte[] expected, byte[] replacement, out uint originalProtection)
    {
        originalProtection = 0;
        Require(expected.Length == replacement.Length, "Patch length mismatch.");
        Require(Read(process, address, expected.Length).SequenceEqual(expected), "FIFA code changed during installation; refusing the patch.");
        uint previous;
        if (!VirtualProtectEx(process, new IntPtr(address), (UIntPtr)expected.Length, ExecuteReadWrite, out previous))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot protect the verified FIFA code range.");
        originalProtection = previous;
        try
        {
            Write(process, address, replacement);
            Require(Read(process, address, replacement.Length).SequenceEqual(replacement), "FIFA patch read-back failed.");
            if (!FlushInstructionCache(process, new IntPtr(address), (UIntPtr)replacement.Length))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "FIFA instruction-cache refresh failed.");
        }
        finally
        {
            uint ignored;
            if (!VirtualProtectEx(process, new IntPtr(address), (UIntPtr)expected.Length, previous, out ignored))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot restore FIFA code protection.");
        }
    }

    private static byte[] JumpPatch(long target, int length)
    {
        var code = new Assembler();
        code.AbsoluteJump(target);
        byte[] jump = code.Finish();
        Require(length >= jump.Length, "Insufficient verified patch space.");
        return jump.Concat(Enumerable.Repeat((byte)0x90, length - jump.Length)).ToArray();
    }

    private static void CheckSuspendedInstructionPointers(Process game, long imageBase)
    {
        // AMD64 CONTEXT must be 16-byte aligned. Read control registers only.
        IntPtr allocation = Marshal.AllocHGlobal(1232 + 15);
        IntPtr context = new IntPtr((allocation.ToInt64() + 15) & ~15L);
        try
        {
            game.Refresh(); // Discard the pre-unpack ProcessInfo/thread snapshot.
            foreach (ProcessThread item in game.Threads)
            {
                IntPtr thread = OpenThread(0x48, false, (uint)item.Id);
                if (thread == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot inspect a suspended FIFA thread.");
                try
                {
                    Marshal.WriteInt32(context, 48, 0x100001);
                    if (!GetThreadContext(thread, context))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot verify a suspended FIFA instruction pointer.");
                    long ip = Marshal.ReadInt64(context, 248);
                    Require(!(ip >= imageBase + AuthRva && ip < imageBase + AuthRva + AuthPrologue.Length) &&
                        !(ip >= imageBase + DestroyRva && ip < imageBase + DestroyRva + DestroyPrologue.Length) &&
                        !(ip >= imageBase + FutReturnRva && ip < imageBase + FutReturnRva + FutReturnInstructions.Length),
                        "A FIFA thread is inside an auth entry point. No patch applied; retry while at the title screen.");
                }
                finally { CloseHandle(thread); }
            }
        }
        finally { Marshal.FreeHGlobal(allocation); }
    }

    private static bool RestoreAttemptedPatch(IntPtr process, long address, byte[] original, byte[] patch, uint originalProtection)
    {
        try
        {
            byte[] current = Read(process, address, original.Length);
            // A short write may contain a mixture, not the entire intended jump.
            for (int i = 0; i < current.Length; i++)
                Require(current[i] == original[i] || current[i] == patch[i], "Unexpected external change during rollback.");
            uint ignored;
            try { ReplaceCode(process, address, current, original, out ignored); }
            finally
            {
                // Do not mistake the RWX state left by a failed protection restore
                // for the original protection of this executable image page.
                if (originalProtection != 0 && !VirtualProtectEx(process, new IntPtr(address),
                    (UIntPtr)original.Length, originalProtection, out ignored))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Cannot restore original FIFA page protection during rollback.");
            }
            return true;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine("[ERROR] Auth bridge rollback failed: " + error.Message);
            return false;
        }
    }

    private static long JumpTarget(byte[] bytes)
    {
        return bytes.Length >= 14 && bytes.Take(6).SequenceEqual(new byte[] { 0xFF, 0x25, 0, 0, 0, 0 })
            ? BitConverter.ToInt64(bytes, 6) : 0;
    }

    private static byte[] BuildAuth(long data, long fallback)
    {
        var code = new Assembler();
        code.Emit(0x48, 0x85, 0xD2); // test rdx,rdx (clientId)
        code.Branch("original", 0x0F, 0x84);
        for (int i = 0; i < ClientIds.Length; i++)
        {
            code.Label("id" + i);
            code.Emit(0x49, 0xBA); code.U64(data + 0x200 + i * 0x80); // r10 = expected ID
            code.Emit(0x49, 0x89, 0xD3); // r11 = input; compare one byte at a time, including NUL
            code.Label("compare" + i);
            code.Emit(0x41, 0x8A, 0x02, 0x41, 0x3A, 0x03);
            code.Branch(i + 1 == ClientIds.Length ? "record" : "id" + (i + 1), 0x0F, 0x85);
            code.Emit(0x84, 0xC0);
            code.Branch("match" + i, 0x0F, 0x84);
            code.Emit(0x49, 0xFF, 0xC2, 0x49, 0xFF, 0xC3);
            code.Branch("compare" + i, 0xE9);
        }
        for (int i = 0; i < ClientIds.Length; i++)
        {
            code.Label("match" + i);
            code.Emit(0x49, 0xBB); code.U64(data + CounterOffset + i * 8);
            code.Branch(i == 0 ? "ultimateTeam" : "local", 0xE9);
        }
        code.Label("ultimateTeam");
        code.Emit(0x4D, 0x85, 0xC0); // output char**
        code.Branch("invalid", 0x0F, 0x84);
        code.Emit(0x4D, 0x85, 0xC9); // output length*
        code.Branch("invalid", 0x0F, 0x84);
        code.Emit(0xF0, 0x49, 0xFF, 0x03); // lock inc qword [r11]
        code.Emit(0x48, 0xB8); code.U64(data + FutCodeOffset);
        code.Emit(0x49, 0x89, 0x00);
        code.Emit(0x48, 0xB8); code.U64(FutCodeLength);
        code.Emit(0x49, 0x89, 0x01, 0x31, 0xC0, 0xC3);
        code.Label("local");
        code.Emit(0x4D, 0x85, 0xC0); // output char**
        code.Branch("invalid", 0x0F, 0x84);
        code.Emit(0x4D, 0x85, 0xC9); // output length*
        code.Branch("invalid", 0x0F, 0x84);
        code.Emit(0xF0, 0x49, 0xFF, 0x03); // lock inc qword [r11]
        code.Emit(0x48, 0xB8); code.U64(data + CodeStringOffset);
        code.Emit(0x49, 0x89, 0x00); // *code = bridge-owned, process-lifetime ASCII
        code.Emit(0x48, 0xB8); code.U64(Encoding.ASCII.GetByteCount(LocalCode));
        code.Emit(0x49, 0x89, 0x01, 0x31, 0xC0, 0xC3);
        code.Label("invalid");
        code.Emit(0xB8, 1, 0, 0, 0, 0xC3); // non-success, without forwarding local IDs to EA
        // A non-null ID that is not ours: record it, then take FIFA's own path. Only rax, r10 and r11 are used (volatile,
        // not argument registers, unread by the original prologue), so rcx/rdx/r8/r9 and the stack reach it unchanged.
        code.Label("record");
        code.Emit(0xB8, 1, 0, 0, 0); // mov eax,1
        code.Emit(0x49, 0xBA); code.U64(data + PassedIndexOffset); // r10 = slot index
        code.Emit(0xF0, 0x49, 0x0F, 0xC1, 0x02); // lock xadd [r10],rax: rax = this call's slot
        code.Emit(0x48, 0x83, 0xF8, (byte)PassedSlotCount); // cmp rax,slots
        code.Branch("original", 0x0F, 0x83); // jae: every slot taken, record nothing
        code.Emit(0x48, 0xC1, 0xE0, 0x06); // shl rax,6 (64-byte slots)
        code.Emit(0x49, 0xBA); code.U64(data + PassedSlotOffset); // r10 = first slot
        code.Emit(0x49, 0x01, 0xC2); // add r10,rax
        code.Emit(0x49, 0x89, 0xD3); // r11 = client ID
        code.Label("copy");
        code.Emit(0x41, 0x8A, 0x03, 0x41, 0x88, 0x02); // mov al,[r11]; mov [r10],al
        code.Emit(0x84, 0xC0);
        code.Branch("original", 0x0F, 0x84); // copied the NUL
        code.Emit(0x49, 0xFF, 0xC3, 0x49, 0xFF, 0xC2); // inc r11; inc r10
        code.Emit(0x44, 0x89, 0xD0, 0x83, 0xE0, 0x3F, 0x83, 0xF8, 0x3F); // eax = r10 & 63; cmp eax,63
        code.Branch("copy", 0x0F, 0x85);
        code.Emit(0x41, 0xC6, 0x02, 0x00); // 63 bytes copied: terminate the slot
        code.Label("original");
        code.AbsoluteJump(fallback);
        return code.Finish();
    }

    private static byte[] BuildDestroy(long data, long fallback)
    {
        var code = new Assembler();
        code.Emit(0x48, 0xB8); code.U64(data + CodeStringOffset);
        code.Emit(0x48, 0x39, 0xC1); // only our exact synthetic handle is intercepted
        code.Branch("original", 0x0F, 0x85);
        code.Emit(0x49, 0xBA); code.U64(data + CounterOffset + ClientIds.Length * 8);
        code.Emit(0xF0, 0x49, 0xFF, 0x02, 0x31, 0xC0, 0xC3);
        code.Label("original");
        code.AbsoluteJump(fallback);
        return code.Finish();
    }

    private static byte[] BuildFut(long data, long continuation)
    {
        var code = new Assembler();
        code.Emit(FutReturnInstructions.Take(13).ToArray()); // original pointer and length loads, without the short jump
        code.Emit(0x85, 0xC0); // test eax,eax: only replace a successful original result
        code.Branch("original", 0x0F, 0x85);
        code.Emit(0x49, 0xBA); code.U64(BitConverter.ToInt64(Encoding.ASCII.GetBytes("FOS-SERV"), 0));
        code.Emit(0x4D, 0x39, 0x56, 0x18); // cmp qword [r14+18h],r10
        code.Branch("original", 0x0F, 0x85);
        code.Emit(0x66, 0x41, 0x81, 0x7E, 0x20, 0x45, 0x52); // cmp word [r14+20h],"ER"
        code.Branch("original", 0x0F, 0x85);
        code.Emit(0x41, 0x80, 0x7E, 0x22, 0); // exact NUL terminator
        code.Branch("original", 0x0F, 0x85);
        code.Emit(0x48, 0xBB); code.U64(data + FutCodeOffset); // bridge-owned, process-lifetime ASCII
        code.Emit(0xBE); code.Emit(BitConverter.GetBytes(FutCodeLength));
        code.Label("original");
        code.AbsoluteJump(continuation);
        return code.Finish();
    }

    private static byte[] DestroyTrampoline(long imageBase)
    {
        var code = new Assembler();
        code.Emit(DestroyPrologue.Take(6).ToArray()); // push rbx; sub rsp,30h
        long sourceGlobal = checked(imageBase + DestroyRva + 13 + BitConverter.ToInt32(DestroyPrologue, 9));
        code.Emit(0x48, 0xA1); code.U64(sourceGlobal); // relocated mov rax,[absolute global]
        code.Emit(DestroyPrologue.Skip(13).ToArray()); // mov rbx,rcx
        code.AbsoluteJump(imageBase + DestroyRva + DestroyPrologue.Length);
        return code.Finish();
    }

    private static void Put(byte[] page, int offset, byte[] bytes)
    {
        Require(offset >= 0 && offset + bytes.Length <= page.Length, "Bridge allocation overflow.");
        Buffer.BlockCopy(bytes, 0, page, offset, bytes.Length);
    }

    private static byte[] DataPage(string futCode)
    {
        byte[] page = new byte[PageSize];
        Put(page, CodeStringOffset, Encoding.ASCII.GetBytes(LocalCode + "\0"));
        for (int i = 0; i < ClientIds.Length; i++)
            Put(page, 0x200 + i * 0x80, Encoding.ASCII.GetBytes(ClientIds[i] + "\0"));
        Put(page, FutCodeOffset, Encoding.ASCII.GetBytes(futCode + "\0"));
        return page;
    }

    private static byte[] CodePage(int pid, long imageBase, long codeAddress, long dataAddress)
    {
        byte[] page = new byte[PageSize];
        Put(page, 0, Magic);
        Put(page, 32, BitConverter.GetBytes(pid));
        Put(page, 40, BitConverter.GetBytes(imageBase));
        Put(page, 48, BitConverter.GetBytes(dataAddress));
        byte[] auth = BuildAuth(dataAddress, codeAddress + AuthOriginalOffset);
        Require(AuthOffset + auth.Length <= DestroyOffset, "Auth code overlaps the handle adapter.");
        Put(page, AuthOffset, auth);
        Put(page, DestroyOffset, BuildDestroy(dataAddress, codeAddress + DestroyOriginalOffset));
        var original = new Assembler();
        original.Emit(AuthPrologue);
        original.AbsoluteJump(imageBase + AuthRva + AuthPrologue.Length);
        Put(page, AuthOriginalOffset, original.Finish());
        Put(page, DestroyOriginalOffset, DestroyTrampoline(imageBase));
        Put(page, FutOffset, BuildFut(dataAddress, imageBase + FutContinuationRva));
        return page;
    }

    private static void CheckServer(bool hosted)
    {
        // Hosted launches already received a player-bound code from /client/launch.
        // The local bootstrap profile describes a different runtime.
        if (hosted) { Console.WriteLine("[MODE] Hosted authentication; no local bootstrap service required."); return; }
        var request = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:8090/ultimateteam/auth/bootstrap");
        request.Proxy = null;
        request.AllowAutoRedirect = false;
        request.Timeout = 3000;
        request.ReadWriteTimeout = 3000;
        using (var response = (HttpWebResponse)request.GetResponse())
        using (var reader = new StreamReader(response.GetResponseStream()))
        {
            Require(response.StatusCode == HttpStatusCode.OK, "Updated local auth service is not ready.");
            char[] buffer = new char[8193];
            int length = reader.ReadBlock(buffer, 0, buffer.Length);
            Require(length <= 8192, "Local auth profile exceeds its size limit.");
            var profile = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(new string(buffer, 0, length));
            Require(profile != null && profile.ContainsKey("version") && Convert.ToInt32(profile["version"]) == 1,
                "Server lacks local auth profile v1. Restart the updated Aurora 21 server.");
            Require(profile.ContainsKey("authCode") && (string)profile["authCode"] == LocalCode, "Local auth profile/code mismatch.");
            var ids = profile.ContainsKey("clientIds") ? profile["clientIds"] as System.Collections.IEnumerable : null;
            Require(ids != null && ids.Cast<object>().Select(Convert.ToString).SequenceEqual(ClientIds), "Local auth profile/client-ID mismatch.");
            var config = profile.ContainsKey("config") ? profile["config"] as Dictionary<string, object> : null;
            Require(config != null, "Missing native auth configuration.");
            CheckValue(config, "blazeServerClientId", ClientIds[0]);
            CheckValue(config, "blazeSdkClientId", ClientIds[1]);
            CheckValue(config, "blazeSdkClientSecret", "");
            CheckValue(config, "nucleusConnect", "http://127.0.0.1:8090/");
            CheckValue(config, "identityRedirectUri", "http://127.0.0.1:8090/ultimateteam/callback");
        }
    }

    private static void CheckValue(Dictionary<string, object> config, string key, string expected)
    {
        Require(config.ContainsKey(key) && config[key] is string && (string)config[key] == expected,
            "Local auth configuration mismatch: " + key);
    }

    private static bool Existing(IntPtr process, int pid, long imageBase, string futCode)
    {
        long entry = JumpTarget(Read(process, imageBase + AuthRva, AuthPrologue.Length));
        if (entry == 0) return false;
        long codeAddress = entry - AuthOffset;
        byte[] header = Read(process, codeAddress, 56);
        Require(header.Take(Magic.Length).SequenceEqual(Magic) && BitConverter.ToInt32(header, 32) == pid &&
            BitConverter.ToInt64(header, 40) == imageBase, "Unknown auth hook already installed; restart FIFA instead of layering patches.");
        long dataAddress = BitConverter.ToInt64(header, 48);
        Require(JumpTarget(Read(process, imageBase + DestroyRva, DestroyPrologue.Length)) == codeAddress + DestroyOffset,
            "Incomplete auth bridge installation; restart FIFA.");
        Require(JumpTarget(Read(process, imageBase + FutReturnRva, FutReturnInstructions.Length)) == codeAddress + FutOffset,
            "Incomplete FUT code substitution; restart FIFA.");
        Require(Read(process, imageBase + PresentWaitRva, PresentWaitPatch.Length).SequenceEqual(PresentWaitPatch),
            "Incomplete alt-tab present patch; restart FIFA.");
        Require(Read(process, codeAddress, PageSize).SequenceEqual(CodePage(pid, imageBase, codeAddress, dataAddress)),
            "Existing auth bridge code differs from this build; restart FIFA.");
        byte[] actualData = Read(process, dataAddress, PageSize);
        string installedCode = Encoding.ASCII.GetString(actualData, FutCodeOffset, FutCodeLength);
        Require(ValidFutCode(installedCode) && actualData[FutCodeOffset + FutCodeLength] == 0,
            "Existing FUT code storage is invalid; restart FIFA.");
        if (futCode != null)
            Require(installedCode == futCode, "A different FUT code is already installed; restart FIFA.");
        // Only the call counters, the pass-through index and its slots are mutable.
        Array.Clear(actualData, CounterOffset, PassedIndexOffset + 8 - CounterOffset);
        Array.Clear(actualData, PassedSlotOffset, PassedSlotCount * PassedSlotSize);
        Require(actualData.SequenceEqual(DataPage(installedCode)),
            "Existing auth bridge data differs from this build; restart FIFA.");
        Console.WriteLine("[ALREADY] Local authentication bridge is installed for PID " + pid + ".");
        Console.WriteLine("[FUT-CODE] len=40 sha256=" + FutCodeHashPrefix(installedCode));
        Console.WriteLine("[FOCUS] unfocused present wait 1 ms; Windows throttling: " + DescribeSpeed(process));
        PrintCounters(process, dataAddress);
        return true;
    }

    /// <summary>
    /// Opts FIFA out of Windows' background throttling: EcoQoS, and (Windows 11) ignoring its timer resolution while
    /// its window is minimised or covered, which slows its frame pacing like the present wait does. Windows 10 knows
    /// only the execution-speed bit, so that is retried alone. Returns null on success, else why it failed; a failure
    /// only leaves Windows' default. The setting lasts for the process's lifetime.
    /// </summary>
    private static string KeepFullSpeed(IntPtr process)
    {
        int error = 0;
        foreach (uint mask in new[] { ThrottleExecutionSpeed | ThrottleIgnoreTimerResolution, ThrottleExecutionSpeed })
        {
            var state = new PowerThrottlingState { Version = 1, ControlMask = mask, StateMask = 0 };
            try
            {
                if (SetProcessInformation(process, ProcessPowerThrottling, ref state, Marshal.SizeOf(typeof(PowerThrottlingState)))) return null;
            }
            catch (EntryPointNotFoundException) { return "this Windows version has no process power throttling"; }
            error = Marshal.GetLastWin32Error();
        }
        return "Windows error " + error;
    }

    /// <summary>"full speed" when both throttles are switched off, else what Windows reports.</summary>
    private static string DescribeSpeed(IntPtr process)
    {
        var state = new PowerThrottlingState { Version = 1 };
        try
        {
            if (!GetProcessInformation(process, ProcessPowerThrottling, ref state, Marshal.SizeOf(typeof(PowerThrottlingState))))
                return "unknown (Windows error " + Marshal.GetLastWin32Error() + ")";
        }
        catch (EntryPointNotFoundException) { return "Windows default (no process power throttling)"; }
        uint off = state.ControlMask & ~state.StateMask;
        if ((off & (ThrottleExecutionSpeed | ThrottleIgnoreTimerResolution)) == (ThrottleExecutionSpeed | ThrottleIgnoreTimerResolution)) return "full speed";
        if ((off & ThrottleExecutionSpeed) != 0) return "full speed (execution only)";
        return string.Format(CultureInfo.InvariantCulture, "Windows default (control=0x{0:X} state=0x{1:X})", state.ControlMask, state.StateMask);
    }

    private static void PrintCounters(IntPtr process, long dataAddress)
    {
        byte[] counters = Read(process, dataAddress + CounterOffset, 32);
        Console.WriteLine("[CALLS] server={0} sdk={1} ssf={2} local-handle-release={3}",
            BitConverter.ToInt64(counters, 0), BitConverter.ToInt64(counters, 8),
            BitConverter.ToInt64(counters, 16), BitConverter.ToInt64(counters, 24));
        Console.WriteLine("[PASSED] " + PassedThrough(Read(process, dataAddress, PageSize)));
    }

    /// <summary>The client IDs FIFA asked its own auth for (not ours), each with its call count, in first-seen order.</summary>
    private static string PassedThrough(byte[] page)
    {
        long calls = BitConverter.ToInt64(page, PassedIndexOffset);
        var seen = new List<KeyValuePair<string, int>>();
        for (int slot = 0; slot < Math.Min(calls, PassedSlotCount); slot++)
        {
            int start = PassedSlotOffset + slot * PassedSlotSize, length = 0;
            while (length < PassedSlotSize && page[start + length] != 0) length++;
            // Printable ASCII only; anything else is shown as '?'.
            string id = new string(page.Skip(start).Take(length).Select(b => b >= 0x20 && b < 0x7F ? (char)b : '?').ToArray());
            int index = seen.FindIndex(entry => entry.Key == id);
            if (index < 0) seen.Add(new KeyValuePair<string, int>(id, 1));
            else seen[index] = new KeyValuePair<string, int>(id, seen[index].Value + 1);
        }
        string listed = seen.Count == 0 ? "none" : string.Join(", ", seen.Select(entry => "\"" + entry.Key + "\" x" + entry.Value));
        return string.Format("client IDs passed to FIFA's own auth: {0} ({1} calls{2})", listed, calls,
            calls > PassedSlotCount ? ", only the first " + PassedSlotCount + " recorded" : "");
    }

    private static bool ValidFutCode(string code)
    {
        return code != null && code.Length == FutCodeLength &&
            code.All(c => c >= '0' && c <= '9' || c >= 'a' && c <= 'f');
    }

    private static string FutCodeHashPrefix(string code)
    {
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(Encoding.ASCII.GetBytes(code))).Replace("-", "").ToLowerInvariant().Substring(0, 12);
    }

    private static string FileSha256(string path)
    {
        using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var sha = SHA256.Create())
            return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
    }

    private static void ReadMainModule(Process game, Stopwatch startup, out string path, out long imageBase)
    {
        // FIFA's module list only exists once its main thread has run the Windows loader. A launch that is still
        // suspended (by EA app, Steam, a security product or a launcher that did not resume it) makes MainModule throw
        // Windows error 299 after .NET's own 50 quick retries, or, when only FIFA's own entry fails, .NET Framework skips
        // that entry and returns an empty module whose FileName throws NullReferenceException. Retry both inside the
        // shared start-up budget and log FIFA's state, so a launch that never starts running shows why in the log.
        bool described = false;
        for (int retries = 0; ; retries++)
        {
            string unreadable;
            try
            {
                ProcessModule mainModule = game.MainModule;
                path = mainModule.FileName;
                imageBase = mainModule.BaseAddress.ToInt64();
                if (retries > 0)
                    Console.WriteLine("[WAIT] FIFA module list readable after " + startup.ElapsedMilliseconds + " ms (" + retries + " retries).");
                return;
            }
            catch (Win32Exception moduleRead)
            {
                if (moduleRead.NativeErrorCode != 299)
                {
                    Console.WriteLine("[DIAG] " + DescribeGame(game));
                    throw new InvalidOperationException("Cannot read the FIFA module list (Windows error " + moduleRead.NativeErrorCode +
                        ": " + moduleRead.Message + "); no patch applied.");
                }
                unreadable = "Windows error 299";
            }
            catch (NullReferenceException)
            {
                unreadable = "FIFA's own module entry was unreadable";
            }
            bool exited = game.HasExited, expired = startup.ElapsedMilliseconds >= StartupWaitMs;
            if (!described)
                Console.WriteLine("[WAIT] FIFA has not started running yet (module list unreadable: " + unreadable + ").");
            if (!described || exited || expired)
                Console.WriteLine("[DIAG] " + DescribeGame(game));
            described = true;
            Require(!exited, "FIFA exited before it started running; no patch applied.");
            Require(!expired, "FIFA did not start running within " + StartupWaitMs / 1000 + " seconds (module list unreadable: " +
                unreadable + "); no patch applied. See the [DIAG] lines above.");
            Thread.Sleep(250);
        }
    }

    /// <summary>
    /// FIFA as seen from outside, for the log when it is not ready: its age and parent, whether its main thread has run the
    /// Windows loader yet, each thread's state and suspend count, and any other FIFA process. Read-only; never throws.
    /// </summary>
    private static string DescribeGame(Process game)
    {
        var parts = new List<string>();
        string name = "FIFA21";
        DateTime started = DateTime.MinValue;
        try
        {
            name = game.ProcessName;
            game.Refresh();
            if (game.HasExited) return "FIFA PID " + game.Id + " has exited.";
            started = game.StartTime;
            parts.Add("FIFA PID " + game.Id + " started " + (DateTime.Now - started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s ago");
        }
        catch (Exception error) { parts.Add("FIFA PID " + game.Id + " (age unavailable: " + error.Message + ")"); }
        IntPtr process = OpenProcess(0x410, false, game.Id); // query information and read memory
        if (process == IntPtr.Zero) parts.Add("not openable (Windows error " + Marshal.GetLastWin32Error() + ")");
        else
        {
            try
            {
                // PROCESS_BASIC_INFORMATION: [1] PEB address, [5] parent PID. PEB+0x18 is the loader's data, set by the main
                // thread's first instructions; it is still null while the launch has never run.
                var basic = new long[6];
                int returned;
                if (NtQueryInformationProcess(process, 0, basic, basic.Length * 8, out returned) < 0) parts.Add("process information unavailable");
                else
                {
                    parts.Add("parent " + DescribeProcess((int)basic[5], started));
                    byte[] loader = new byte[8];
                    UIntPtr read;
                    if (basic[1] == 0 || !ReadProcessMemory(process, new IntPtr(basic[1] + 0x18), loader, (UIntPtr)8, out read) || read.ToUInt64() != 8)
                        parts.Add("loader state unreadable (Windows error " + Marshal.GetLastWin32Error() + ")");
                    else parts.Add(BitConverter.ToInt64(loader, 0) == 0 ? "loader not started (main thread has never run)" : "loader started");
                }
            }
            finally { CloseHandle(process); }
        }
        try
        {
            var threads = game.Threads.Cast<ProcessThread>().ToList();
            int suspended = threads.Count(thread => thread.ThreadState == System.Diagnostics.ThreadState.Wait &&
                thread.WaitReason == ThreadWaitReason.Suspended);
            var listed = threads.Take(6).Select(DescribeThread).ToList();
            if (threads.Count > 6) listed.Add("+" + (threads.Count - 6) + " more");
            parts.Add(threads.Count + " thread(s), " + suspended + " suspended: " + string.Join(", ", listed));
        }
        catch (Exception error) { parts.Add("threads unavailable (" + error.Message + ")"); }
        try
        {
            var others = Process.GetProcessesByName(name).Where(other => other.Id != game.Id).ToList();
            try
            {
                parts.Add(others.Count == 0 ? "no other " + name + " process" : "other " + name + " process(es): " +
                    string.Join(", ", others.Take(4).Select(other => DescribeProcess(other.Id, DateTime.MinValue))));
            }
            finally { foreach (var other in others) other.Dispose(); }
        }
        catch (Exception error) { parts.Add("other FIFA processes unavailable (" + error.Message + ")"); }
        return string.Join("; ", parts) + ".";
    }

    private static string DescribeThread(ProcessThread thread)
    {
        string state = "?", suspendCount = "?";
        try
        {
            state = thread.ThreadState == System.Diagnostics.ThreadState.Wait
                ? "Wait/" + thread.WaitReason : thread.ThreadState.ToString();
            IntPtr handle = OpenThread(0x40, false, (uint)thread.Id); // query information
            if (handle != IntPtr.Zero)
            {
                try
                {
                    uint count;
                    if (NtQueryInformationThread(handle, 35, out count, 4, IntPtr.Zero) >= 0) suspendCount = count.ToString(); // ThreadSuspendCount
                }
                finally { CloseHandle(handle); }
            }
        }
        catch (Exception) { }
        return "TID " + thread.Id + " " + state + " suspend=" + suspendCount;
    }

    /// <summary>"PID (name, age)"; a process started after <paramref name="notAfter"/> reuses an exited parent's PID.</summary>
    private static string DescribeProcess(int pid, DateTime notAfter)
    {
        try
        {
            using (var process = Process.GetProcessById(pid))
            {
                DateTime started = process.StartTime;
                if (notAfter != DateTime.MinValue && started > notAfter) return pid + " (exited)";
                return pid + " (" + process.ProcessName + ", started " + (DateTime.Now - started).TotalSeconds.ToString("0.0", CultureInfo.InvariantCulture) + " s ago)";
            }
        }
        catch (Exception) { return pid + " (exited or not inspectable)"; }
    }

    private static long BridgeAgeMs()
    {
        try { using (var self = Process.GetCurrentProcess()) return (long)(DateTime.Now - self.StartTime).TotalMilliseconds; }
        catch (Exception) { return -1; }
    }

    private static void Install(int pid, string gamePath, bool statusOnly, string futCode, bool hosted)
    {
        Require(Environment.Is64BitProcess, "Run the 64-bit local authentication bridge.");
        Require(statusOnly || ValidFutCode(futCode), "A 40-character lowercase hex FUT code is required on standard input.");
        string expectedPath = Path.GetFullPath(gamePath);
        var file = new FileInfo(expectedPath);
        Require(file.Exists && file.Length == 571958064 &&
            FileVersionInfo.GetVersionInfo(expectedPath).ProductVersion == "21.0.4750997.0",
            "Unsupported FIFA executable. This bridge supports build 21.0.4750997.0 only.");
        Require(SupportedExecutableHash(FileSha256(expectedPath)),
            "FIFA executable hash is not allowed for this build; no patch applied.");
        string cardsPath = Path.Combine(Path.GetDirectoryName(expectedPath), "CardsDLL_Win64_retail.dll");
        Require(File.Exists(cardsPath) && FileSha256(cardsPath) == SupportedCardsDllSha256,
            "FIFA CardsDLL hash differs from the verified build; no patch applied.");
        using (var game = Process.GetProcessById(pid))
        {
            Require(game.ProcessName.Equals("FIFA21", StringComparison.OrdinalIgnoreCase), "Selected PID is not FIFA21.exe.");
            Console.WriteLine("[CHECK] FIFA files verified " + BridgeAgeMs() + " ms after the bridge started; waiting up to " +
                StartupWaitMs / 1000 + " seconds for FIFA PID " + pid + " to run and unpack.");
            var startup = Stopwatch.StartNew();
            string mainModulePath;
            long imageBase;
            ReadMainModule(game, startup, out mainModulePath, out imageBase);
            Require(string.Equals(Path.GetFullPath(mainModulePath), expectedPath, StringComparison.OrdinalIgnoreCase),
                "Selected FIFA process does not match the requested executable path.");
            // Install adds PROCESS_SET_INFORMATION (0x200) for the power-throttling opt-out.
            IntPtr process = OpenProcess(statusOnly ? 0x410u : 0xE38u, false, pid);
            if (process == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Administrator access is required for the selected FIFA process.");
            try
            {
                if (Existing(process, pid, imageBase, futCode)) return;
                Require(!statusOnly, "No local authentication bridge is installed in this process.");
                CheckServer(hosted);
                Console.WriteLine("[WAIT] Waiting for the supported game's live auth functions (up to " +
                    Math.Max(0, (StartupWaitMs - startup.ElapsedMilliseconds) / 1000) + " seconds).");
                while (!Read(process, imageBase + AuthRva, AuthPrologue.Length).SequenceEqual(AuthPrologue) ||
                    !Read(process, imageBase + DestroyRva, DestroyPrologue.Length).SequenceEqual(DestroyPrologue) ||
                    !Read(process, imageBase + FutReturnRva, FutReturnInstructions.Length).SequenceEqual(FutReturnInstructions) ||
                    !Read(process, imageBase + PresentWaitRva, PresentWaitInstructions.Length).SequenceEqual(PresentWaitInstructions))
                {
                    if (startup.ElapsedMilliseconds >= StartupWaitMs)
                    {
                        Console.WriteLine("[DIAG] " + DescribeGame(game));
                        throw new InvalidOperationException("Live auth or present instructions do not match the supported build; no patch applied.");
                    }
                    Thread.Sleep(250);
                }
                long code = 0, data = 0;
                bool suspended = false, installed = false, touchedAuth = false, touchedDestroy = false, touchedFut = false, touchedPresent = false, safeToResume = true;
                byte[] authPatch = null, destroyPatch = null, futPatch = null;
                uint authProtection = 0, destroyProtection = 0, futProtection = 0, presentProtection = 0;
                try
                {
                    data = Allocate(process);
                    code = Allocate(process);
                    Write(process, data, DataPage(futCode));
                    Write(process, code, CodePage(pid, imageBase, code, data));
                    ProtectCode(process, code);
                    authPatch = JumpPatch(code + AuthOffset, AuthPrologue.Length);
                    destroyPatch = JumpPatch(code + DestroyOffset, DestroyPrologue.Length);
                    futPatch = JumpPatch(code + FutOffset, FutReturnInstructions.Length);
                    Require(NtSuspendProcess(process) >= 0, "Cannot briefly suspend FIFA for an atomic bridge installation.");
                    suspended = true;
                    CheckSuspendedInstructionPointers(game, imageBase);
                    Require(Read(process, imageBase + AuthRva, AuthPrologue.Length).SequenceEqual(AuthPrologue) &&
                        Read(process, imageBase + DestroyRva, DestroyPrologue.Length).SequenceEqual(DestroyPrologue) &&
                        Read(process, imageBase + FutReturnRva, FutReturnInstructions.Length).SequenceEqual(FutReturnInstructions) &&
                        Read(process, imageBase + PresentWaitRva, PresentWaitInstructions.Length).SequenceEqual(PresentWaitInstructions),
                        "FIFA auth code changed before installation; no patch applied.");
                    // Install handle ownership first; genuine EA handles still take their original path.
                    safeToResume = false;
                    touchedDestroy = true;
                    ReplaceCode(process, imageBase + DestroyRva, DestroyPrologue, destroyPatch, out destroyProtection);
                    touchedFut = true;
                    ReplaceCode(process, imageBase + FutReturnRva, FutReturnInstructions, futPatch, out futProtection);
                    touchedAuth = true;
                    ReplaceCode(process, imageBase + AuthRva, AuthPrologue, authPatch, out authProtection);
                    // Alt-tab keeps the match running: no login without it. Only an immediate operand changes, so a
                    // thread stopped at any instruction in the span runs either the old or the new timeout, both valid.
                    touchedPresent = true;
                    ReplaceCode(process, imageBase + PresentWaitRva, PresentWaitInstructions, PresentWaitPatch, out presentProtection);
                    installed = true;
                    safeToResume = true;
                }
                finally
                {
                    try
                    {
                        if (!installed && suspended)
                        {
                            bool presentRestored = !touchedPresent || RestoreAttemptedPatch(process, imageBase + PresentWaitRva, PresentWaitInstructions, PresentWaitPatch, presentProtection);
                            bool authRestored = !touchedAuth || RestoreAttemptedPatch(process, imageBase + AuthRva, AuthPrologue, authPatch, authProtection);
                            bool futRestored = !touchedFut || RestoreAttemptedPatch(process, imageBase + FutReturnRva, FutReturnInstructions, futPatch, futProtection);
                            bool destroyRestored = !touchedDestroy || RestoreAttemptedPatch(process, imageBase + DestroyRva, DestroyPrologue, destroyPatch, destroyProtection);
                            safeToResume = presentRestored && authRestored && futRestored && destroyRestored;
                        }
                    }
                    finally
                    {
                        if (suspended && !safeToResume)
                            throw new InvalidOperationException("FIFA remains suspended because rollback could not be verified. Close FIFA and relaunch; do not resume this process.");
                        if (suspended && NtResumeProcess(process) < 0)
                            throw new InvalidOperationException("FIFA could not be resumed after bridge installation.");
                        // Retain allocations after any attempted patch if rollback could not be proved.
                        if (!installed && !touchedAuth && !touchedFut && !touchedDestroy && !touchedPresent)
                        {
                            if (code != 0) VirtualFreeEx(process, new IntPtr(code), UIntPtr.Zero, 0x8000);
                            if (data != 0) VirtualFreeEx(process, new IntPtr(data), UIntPtr.Zero, 0x8000);
                        }
                    }
                }
                string speed = KeepFullSpeed(process);
                if (speed != null) Console.WriteLine("[WARN] Windows background throttling left on: " + speed + ".");
                Require(Existing(process, pid, imageBase, futCode), "Bridge installation verification failed.");
                Console.WriteLine("[READY] Local authentication bridge installed. EA launch/licence calls remain unchanged.");
                Console.WriteLine("[SCOPE] Current FIFA process only; no game files or account settings changed.");
            }
            finally { CloseHandle(process); }
        }
    }

    private static void SelfTest()
    {
        Require(Environment.Is64BitProcess, "Self-tests require an x64 build.");
        CheckServer(true); // Hosted mode must not contact a local bootstrap service.
        Require(SupportedExecutableHash(SupportedExeSha256) && SupportedExecutableHash(AlternateExeSha256)
            && !SupportedExecutableHash(new string('0', 64)) && !SupportedExecutableHash(null),
            "Executable hash allowlist must accept both known copies and reject unknown hashes.");
        Require(AuthPrologue.Length == 15 && DestroyPrologue.Length == 16 && FutReturnInstructions.Length == 15,
            "Invalid instruction boundaries.");
        Require(BitConverter.ToInt64(DestroyTrampoline(0x140000000), 8) == 0x148FB60C0,
            "DestroyHandle RIP-relative relocation is incorrect.");
        IntPtr process = GetCurrentProcess();
        long code = Allocate(process), data = Allocate(process);
        IntPtr output = Marshal.AllocHGlobal(24);
        IntPtr job = Marshal.AllocHGlobal(64);
        IntPtr originalFut = Marshal.StringToHGlobalAnsi("original-fut-code");
        const string testFutCode = "0123456789abcdef0123456789abcdef01234567";
        try
        {
            byte[] page = new byte[PageSize];
            Put(page, AuthOffset, BuildAuth(data, code + AuthOriginalOffset));
            Put(page, DestroyOffset, BuildDestroy(data, code + DestroyOriginalOffset));
            Put(page, FutOffset, BuildFut(data, code + 0xB80));
            // A test-only caller with DoTick's stack layout and r14 job pointer. The FUT stub jumps to its exit.
            var futCaller = new Assembler();
            futCaller.Emit(0x53, 0x56, 0x41, 0x56, 0x48, 0x81, 0xEC, 0x90, 0, 0, 0);
            futCaller.Emit(0x49, 0x89, 0xCE, 0x49, 0x89, 0xD3);
            futCaller.Emit(0x48, 0x8B, 0x02, 0x48, 0x89, 0x44, 0x24, 0x78);
            futCaller.Emit(0x48, 0x8B, 0x42, 0x08, 0x48, 0x89, 0x84, 0x24, 0x80, 0, 0, 0);
            futCaller.Emit(0x8B, 0x42, 0x10);
            futCaller.AbsoluteJump(code + FutOffset);
            Put(page, 0xA00, futCaller.Finish());
            Put(page, 0xB80, Hex("49891B498973084881C490000000415E5E5BC3"));
            Put(page, AuthOriginalOffset, new byte[] { 0xB8, 0x34, 0x12, 0, 0, 0xC3 });
            Put(page, DestroyOriginalOffset, new byte[] { 0xB8, 0x78, 0x56, 0, 0, 0xC3 });
            Write(process, data, DataPage(testFutCode)); Write(process, code, page); ProtectCode(process, code);
            var auth = (AuthCall)Marshal.GetDelegateForFunctionPointer(new IntPtr(code + AuthOffset), typeof(AuthCall));
            var destroy = (DestroyCall)Marshal.GetDelegateForFunctionPointer(new IntPtr(code + DestroyOffset), typeof(DestroyCall));
            var fut = (FutTestCall)Marshal.GetDelegateForFunctionPointer(new IntPtr(code + 0xA00), typeof(FutTestCall));
            Action<string, int, long, long> checkFut = (clientId, status, pointer, length) =>
            {
                byte[] jobBytes = new byte[64];
                Put(jobBytes, 0x18, Encoding.ASCII.GetBytes(clientId + "\0"));
                Marshal.Copy(jobBytes, 0, job, jobBytes.Length);
                Marshal.WriteInt64(output, originalFut.ToInt64());
                Marshal.WriteInt64(output, 8, "original-fut-code".Length);
                Marshal.WriteInt32(output, 16, status);
                Require(fut(job, output) == status && Marshal.ReadInt64(output) == pointer &&
                    Marshal.ReadInt64(output, 8) == length, "FOS-SERVER return substitution ABI failed.");
            };
            checkFut("FOS-SERVER", 0, data + FutCodeOffset, FutCodeLength);
            checkFut("FOS-OTHER", 0, originalFut.ToInt64(), "original-fut-code".Length);
            checkFut("FOS-SERVQ", 0, originalFut.ToInt64(), "original-fut-code".Length);
            checkFut("FOS-SERVERX", 0, originalFut.ToInt64(), "original-fut-code".Length);
            checkFut("FOS-SERVER", 1, originalFut.ToInt64(), "original-fut-code".Length);
            string[] foreign = { "FC26_PC_CLIENT", "ultimateteam21-other", "", "ultimateteam21-blaze-sdk-extra", new string('x', 70) };
            foreach (string id in ClientIds.Concat(foreign))
            {
                IntPtr name = Marshal.StringToHGlobalAnsi(id);
                try
                {
                    Marshal.WriteInt64(output, 0); Marshal.WriteInt64(output, 8, 0);
                    int result = auth(42, name, output, IntPtr.Add(output, 8));
                    if (ClientIds.Contains(id))
                    {
                        long expectedPointer = id == ClientIds[0] ? data + FutCodeOffset : data + CodeStringOffset;
                        int expectedLength = id == ClientIds[0] ? FutCodeLength : LocalCode.Length;
                        Require(result == 0 && Marshal.ReadInt64(output) == expectedPointer &&
                            Marshal.ReadInt64(output, 8) == expectedLength, "Synthetic auth ABI failed.");
                        Require(auth(42, name, IntPtr.Zero, IntPtr.Add(output, 8)) != 0, "Null output was accepted.");
                        Require(auth(42, name, output, IntPtr.Zero) != 0, "Null length was accepted.");
                    }
                    else Require(result == 0x1234 && Marshal.ReadInt64(output) == 0, "Nonlocal auth was intercepted.");
                }
                finally { Marshal.FreeHGlobal(name); }
            }
            Require(auth(0, IntPtr.Zero, output, IntPtr.Add(output, 8)) == 0x1234, "Null ID did not preserve original behavior.");
            // Every foreign, non-null ID was recorded once, in call order, cut to 63 bytes; ours and the null ID were not.
            byte[] recorded = Read(process, data, PageSize);
            Require(BitConverter.ToInt64(recorded, PassedIndexOffset) == foreign.Length, "Pass-through index is incorrect.");
            for (int i = 0; i < foreign.Length; i++)
            {
                string expected = foreign[i].Length > PassedSlotSize - 1 ? foreign[i].Substring(0, PassedSlotSize - 1) : foreign[i];
                byte[] slot = recorded.Skip(PassedSlotOffset + i * PassedSlotSize).Take(PassedSlotSize).ToArray();
                Require(Encoding.ASCII.GetString(slot, 0, expected.Length) == expected && slot[expected.Length] == 0,
                    "Pass-through slot " + i + " is incorrect.");
            }
            Require(PassedThrough(recorded).Contains("\"FC26_PC_CLIENT\" x1") && PassedThrough(recorded).Contains("(5 calls)"),
                "Pass-through report is incorrect.");
            // A full record keeps passing calls through, recording nothing more.
            Write(process, data + PassedIndexOffset, BitConverter.GetBytes((long)PassedSlotCount));
            IntPtr late = Marshal.StringToHGlobalAnsi("LATE_CLIENT");
            try
            {
                Require(auth(42, late, output, IntPtr.Add(output, 8)) == 0x1234, "A full record blocked the original path.");
            }
            finally { Marshal.FreeHGlobal(late); }
            Require(!Encoding.ASCII.GetString(Read(process, data, PageSize)).Contains("LATE_CLIENT"), "A full record was overwritten.");
            Require(destroy(new IntPtr(data + CodeStringOffset)) == 0, "Synthetic handle release failed.");
            Require(destroy(IntPtr.Zero) == 0x5678 && destroy(new IntPtr(data + CodeStringOffset + 1)) == 0x5678,
                "Nonlocal handles were intercepted.");
            byte[] counters = Read(process, data + CounterOffset, 32);
            for (int i = 0; i < 4; i++) Require(BitConverter.ToInt64(counters, i * 8) == 1, "Adapter call counters are incorrect.");
            Require(JumpTarget(JumpPatch(code, 15)) == code, "Absolute jump encoding failed.");
            byte[] entry = Enumerable.Repeat((byte)0x90, 16).ToArray();
            byte[] patch = JumpPatch(code + AuthOffset, entry.Length);
            long testEntry = data + 0x800;
            Write(process, testEntry, entry);
            uint protection;
            ReplaceCode(process, testEntry, entry, patch, out protection);
            Require(protection == ReadWrite, "Patch lost the original protection.");
            // Simulate a short write in this helper's own allocation, never FIFA.
            Write(process, testEntry, patch.Take(7).Concat(entry.Skip(7)).ToArray());
            Require(RestoreAttemptedPatch(process, testEntry, entry, patch, protection) &&
                Read(process, testEntry, entry.Length).SequenceEqual(entry), "Partial-write rollback failed.");
            // The present patch changes the wait's timeout operand only: 100 ms (0x64) -> 1 ms, nothing else.
            Require(PresentWaitInstructions.Length == 22 && PresentWaitPatch.Length == PresentWaitInstructions.Length &&
                Enumerable.Range(0, PresentWaitPatch.Length).All(i => i == PresentTimeoutOffset ||
                    PresentWaitPatch[i] == PresentWaitInstructions[i]) &&
                PresentWaitInstructions[PresentTimeoutOffset - 1] == 0xBA && PresentWaitInstructions[PresentTimeoutOffset] == 100 &&
                PresentWaitPatch[PresentTimeoutOffset] == 1, "The present patch must change only the 100 ms timeout to 1 ms.");
            long presentEntry = data + 0x900;
            Write(process, presentEntry, PresentWaitInstructions);
            ReplaceCode(process, presentEntry, PresentWaitInstructions, PresentWaitPatch, out protection);
            Require(Read(process, presentEntry, PresentWaitPatch.Length).SequenceEqual(PresentWaitPatch), "Present patch write failed.");
            bool refused = false;
            try { ReplaceCode(process, presentEntry, PresentWaitInstructions, PresentWaitPatch, out protection); }
            catch (InvalidOperationException) { refused = true; }
            Require(refused, "The present patch was layered over code that is not the verified original.");
            Require(RestoreAttemptedPatch(process, presentEntry, PresentWaitInstructions, PresentWaitPatch, protection) &&
                Read(process, presentEntry, PresentWaitInstructions.Length).SequenceEqual(PresentWaitInstructions), "Present patch rollback failed.");
            // This helper's own process only: the throttling opt-out round-trips through Windows.
            string speedError = KeepFullSpeed(process);
            Require(speedError == null, "Power-throttling opt-out failed: " + speedError);
            Require(DescribeSpeed(process).StartsWith("full speed", StringComparison.Ordinal), "Power-throttling opt-out did not read back: " + DescribeSpeed(process));
            Console.WriteLine("PASS native adapter: local IDs, FOS-SERVER direct return ABI and exact match, output ABI, null outputs, handle ownership, fallback, counters, pass-through record (order, truncation, full record), relocation, jump encoding, partial-write rollback, present timeout patch (operand only, refuses layering, rollback), throttling opt-out (" + DescribeSpeed(process) + ").");
            Console.WriteLine("No FIFA process was opened or modified by these self-tests.");
        }
        finally
        {
            Marshal.FreeHGlobal(output);
            Marshal.FreeHGlobal(job);
            Marshal.FreeHGlobal(originalFut);
            VirtualFreeEx(process, new IntPtr(code), UIntPtr.Zero, 0x8000);
            VirtualFreeEx(process, new IntPtr(data), UIntPtr.Zero, 0x8000);
        }
    }

    private static int Main(string[] args)
    {
        StreamWriter log = null;
        TextWriter originalOutput = Console.Out, originalError = Console.Error;
        try
        {
            if (args.Length == 1 && args[0] == "--self-test") { SelfTest(); return 0; }
            int pid = 0; string path = null, logPath = null; bool statusOnly = false, codeFromStdin = false, hosted = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--hosted") hosted = true;
                else if (args[i] == "--status") statusOnly = true;
                else if (args[i] == "--fut-code-stdin") codeFromStdin = true;
                else if (args[i] == "--pid" && i + 1 < args.Length) pid = int.Parse(args[++i]);
                else if (args[i] == "--game" && i + 1 < args.Length) path = args[++i];
                else if (args[i] == "--log" && i + 1 < args.Length) logPath = args[++i];
                else throw new ArgumentException("Usage: --pid N --game <FIFA21.exe> (--fut-code-stdin | --status) [--hosted] [--log <absolute path>], or --self-test");
            }
            Require(pid > 0 && !string.IsNullOrWhiteSpace(path), "Specify the exact FIFA PID and executable path.");
            Require(statusOnly != codeFromStdin, "Choose exactly one of --fut-code-stdin or --status.");
            string futCode = codeFromStdin ? Console.In.ReadLine() : null;
            Require(statusOnly || ValidFutCode(futCode), "Standard input must contain one 40-character lowercase hex FUT code.");
            if (logPath != null)
            {
                Require(Path.IsPathRooted(logPath), "The bridge log path must be absolute.");
                log = new StreamWriter(new FileStream(logPath, FileMode.Create, FileAccess.Write, FileShare.Read));
                log.AutoFlush = true;
                Console.SetOut(log);
                Console.SetError(log);
            }
            Install(pid, path, statusOnly, futCode, hosted);
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
