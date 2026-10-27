using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// Developer harness for Aurora21-MathBridge (FIFA21-LocalMathBridge.cs), run by Test-MathBridge.ps1: compiled together
// with the helper source into a temporary exe (csc /main:MathBridgeHarness).  It never opens FIFA:
//  - LocalMathBridge.SwitchMaths runs against a stand-in child process that lays out FIFA's two maths tables (import
//    and shadow) in memory and plays FIFA's startup copy and a later restore;
//  - LocalMathBridge.Report posts to a loopback stand-in server (one refused URL first, so the fallback runs too).
// A stand-in slot counts as bridged when it points into the pinned copy and returns that copy's result.  On a PC whose
// Windows ucrtbase already computes like 26100 (Windows 11 24H2 and later) both copies give the same bits; that is
// reported, not failed.
internal static class MathBridgeHarness
{
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr VirtualAlloc(IntPtr a, UIntPtr s, uint t, uint p);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool VirtualProtect(IntPtr a, UIntPtr s, uint p, out uint o);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern IntPtr LoadLibraryW(string p);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Ansi)] static extern IntPtr GetProcAddress(IntPtr m, string n);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint a, bool i, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr h);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate float F(float x);

    const float Probe = 0.5590308308601379f;

    static int Bits(float v) { return BitConverter.ToInt32(BitConverter.GetBytes(v), 0); }

    static int Target(int copyDelay, int restoreAfter)
    {
        var slots = LocalMathBridge.Slots;
        long shadowFirst = slots.Min(s => s.ShadowRva), importFirst = slots.Min(s => s.ImportRva);
        IntPtr ucrt = LoadLibraryW("ucrtbase.dll");
        IntPtr shadow = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x04);
        IntPtr import = VirtualAlloc(IntPtr.Zero, (UIntPtr)4096, 0x3000, 0x04);
        foreach (var s in slots) Marshal.WriteInt64(import, (int)(s.ImportRva - importFirst), GetProcAddress(ucrt, s.Name).ToInt64());
        uint old;
        VirtualProtect(import, (UIntPtr)4096, 0x02, out old);   // read-only, like a bound import table
        Console.WriteLine("TABLES {0} {1}", shadow.ToInt64(), import.ToInt64());
        Console.Out.Flush();
        Thread.Sleep(copyDelay);
        foreach (var s in slots)   // FIFA's startup copy
            Marshal.WriteInt64(shadow, (int)(s.ShadowRva - shadowFirst), Marshal.ReadInt64(import, (int)(s.ImportRva - importFirst)));
        int acos = Array.FindIndex(slots, s => s.Name == "acosf");
        if (restoreAfter >= 0)
        {
            Thread.Sleep(restoreAfter);
            Marshal.WriteInt64(shadow, (int)(slots[acos].ShadowRva - shadowFirst), GetProcAddress(ucrt, "acosf").ToInt64());
        }
        string line;
        while ((line = Console.ReadLine()) != null && line != "quit")
        {
            IntPtr fn = new IntPtr(Marshal.ReadInt64(shadow, (int)(slots[acos].ShadowRva - shadowFirst)));
            var f = (F)Marshal.GetDelegateForFunctionPointer(fn, typeof(F));
            Console.WriteLine("PROBE {0} {1}", Bits(f(Probe)), fn == GetProcAddress(ucrt, "acosf") ? "windows" : "other");
            Console.Out.Flush();
        }
        return 0;
    }

    static int Drive(string dll, int copyDelay, int restoreAfter, int watch, int expectedSwitched)
    {
        var start = new ProcessStartInfo(Process.GetCurrentProcess().MainModule.FileName,
            string.Format("--target {0} {1}", copyDelay, restoreAfter));
        start.UseShellExecute = false; start.RedirectStandardInput = true; start.RedirectStandardOutput = true;
        using (var target = Process.Start(start))
        {
            string[] tables = target.StandardOutput.ReadLine().Split(' ');
            long shadow = long.Parse(tables[1]), import = long.Parse(tables[2]);
            IntPtr handle = OpenProcess(0xC3A, false, target.Id);
            try
            {
                string system = Path.GetFullPath(Path.Combine(Environment.SystemDirectory, "ucrtbase.dll"));
                long systemBase;
                if (!LocalMathBridge.Modules(handle).TryGetValue(system, out systemBase)) throw new Exception("target has no ucrtbase");
                var clock = Stopwatch.StartNew();
                var console = Console.Out;
                var captured = new StringWriter();
                Console.SetOut(captured);
                int repairs;
                try { repairs = LocalMathBridge.SwitchMaths(handle, target, dll, systemBase, shadow, import, clock, 15, watch); }
                finally { Console.SetOut(console); }
                Console.Write(captured.ToString());
                target.StandardInput.WriteLine("probe");
                string[] probe = target.StandardOutput.ReadLine().Split(' ');
                target.StandardInput.WriteLine("quit");
                target.WaitForExit(5000);
                var mine = (F)Marshal.GetDelegateForFunctionPointer(GetProcAddress(LoadLibraryW(dll), "acosf"), typeof(F));
                var windows = (F)Marshal.GetDelegateForFunctionPointer(GetProcAddress(LoadLibraryW("ucrtbase.dll"), "acosf"), typeof(F));
                // 50 = only the import table was switched and the startup copy carried it over; 100 = both tables.
                string switchedLine = captured.ToString().Split('\n').FirstOrDefault(l => l.StartsWith("[SWITCHED] ")) ?? "";
                bool switchedOk = expectedSwitched == 0 || switchedLine.StartsWith("[SWITCHED] " + expectedSwitched + " ");
                bool ok = probe[2] == "other" && int.Parse(probe[1]) == Bits(mine(Probe)) && switchedOk
                    && (restoreAfter < 0 ? repairs == 0 : repairs >= 1);
                Console.WriteLine("{0} copyDelay={1} restoreAfter={2}: target acosf {3} bits {4}, pinned {5}, this PC's Windows {6}{7}, repairs {8}",
                    ok ? "PASS" : "FAIL", copyDelay, restoreAfter, probe[2] == "other" ? "-> pinned copy" : "-> WINDOWS copy", probe[1],
                    Bits(mine(Probe)), Bits(windows(Probe)), Bits(mine(Probe)) == Bits(windows(Probe)) ? " (same maths on this PC)" : "", repairs);
                return ok ? 0 : 1;
            }
            finally { CloseHandle(handle); }
        }
    }

    // A one-request HTTP stand-in for the server's POST /client/launch/math.
    static int ReportFallback(string dll)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var refusedSocket = new TcpListener(IPAddress.Loopback, 0);
        refusedSocket.Start();
        int refusedPort = ((IPEndPoint)refusedSocket.LocalEndpoint).Port;
        refusedSocket.Stop();   // nothing listens there now: the first URL is refused
        string request = null;
        var server = new Thread(() =>
        {
            using (var client = listener.AcceptTcpClient())
            using (var stream = client.GetStream())
            {
                var buffer = new byte[8192];
                var text = new StringBuilder();
                while (true)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;
                    text.Append(Encoding.UTF8.GetString(buffer, 0, read));
                    string sofar = text.ToString();
                    int end = sofar.IndexOf("\r\n\r\n", StringComparison.Ordinal);
                    if (end < 0) continue;
                    var length = sofar.Split(new[] { "\r\n" }, StringSplitOptions.None)
                        .Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        .Select(l => int.Parse(l.Substring(15).Trim())).FirstOrDefault();
                    if (Encoding.UTF8.GetByteCount(sofar) - Encoding.UTF8.GetByteCount(sofar.Substring(0, end + 4)) >= length) break;
                }
                request = text.ToString();
                byte[] body = Encoding.UTF8.GetBytes("{\"ok\":true,\"group\":\"24h2\"}");
                byte[] head = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                stream.Write(head, 0, head.Length);
                stream.Write(body, 0, body.Length);
            }
        });
        server.Start();
        string code = "0123456789abcdef0123456789abcdef01234567";
        int avx2 = LocalMathBridge.LocalAvx2();
        string math = LocalMathBridge.MathValue(LocalMathBridge.VersionOf(dll), LocalMathBridge.Sha256(dll), 1, avx2);
        var console = Console.Out;
        var captured = new StringWriter();
        Console.SetOut(captured);
        try
        {
            LocalMathBridge.Report(new List<string> {
                string.Format("http://127.0.0.1:{0}/client/launch/math", refusedPort),
                string.Format("http://127.0.0.1:{0}/client/launch/math", port) }, code, math);
        }
        finally { Console.SetOut(console); }
        server.Join(10000);
        listener.Stop();
        string output = captured.ToString();
        bool ok = request != null
            && request.StartsWith("POST /client/launch/math HTTP/1.1\r\n", StringComparison.Ordinal)
            && request.Contains("\r\nX-Aurora21-Math: " + math + "\r\n")
            && request.Contains("\r\nContent-Type: application/json\r\n")
            && request.EndsWith("\r\n\r\n{\"code\":\"" + code + "\"}", StringComparison.Ordinal)
            && output.Contains("[RETRY] Maths report: 127.0.0.1")
            && output.Contains("[REPORTED] " + math + " to 127.0.0.1: {\"ok\":true,\"group\":\"24h2\"}")
            && !output.Contains(code)
            && (avx2 == 0 || avx2 == 1) && math.EndsWith("; fma3=1; avx2=" + avx2, StringComparison.Ordinal)
            && LocalMathBridge.IsReportUrl("https://21.aurorafut.com/client/launch/math")
            && !LocalMathBridge.IsReportUrl("http://21.aurorafut.com/client/launch/math")
            && LocalMathBridge.IsLaunchCode(code) && !LocalMathBridge.IsLaunchCode(code.ToUpperInvariant()) && !LocalMathBridge.IsLaunchCode(null);
        Console.WriteLine("{0} report: first URL refused, second answered 200; header {1}; the code stays out of the log",
            ok ? "PASS" : "FAIL", math);
        if (!ok) Console.WriteLine(output + Environment.NewLine + (request ?? "(no request)"));
        return ok ? 0 : 1;
    }

    static string ReadCode(byte[] prefix, string text)
    {
        using (var input = new MemoryStream(prefix.Concat(Encoding.ASCII.GetBytes(text)).ToArray())) return LocalMathBridge.ReadLaunchCode(input);
    }

    // Standard input as Windows PowerShell may write it: with or without a UTF-8 byte order mark, CRLF or LF.
    static int CodeFromStandardInput()
    {
        string code = "0123456789abcdef0123456789abcdef01234567";
        byte[] bom = { 0xEF, 0xBB, 0xBF }, none = new byte[0];
        bool ok = ReadCode(bom, code + "\r\n") == code && ReadCode(none, code + "\n") == code && ReadCode(none, code) == code
            && ReadCode(bom, "") == null && ReadCode(none, "") == null && ReadCode(none, code.Substring(1) + "\r\n") == null
            && ReadCode(none, code.ToUpperInvariant() + "\r\n") == null && ReadCode(none, "\r\n" + code + "\r\n") == null;
        Console.WriteLine("{0} launch code from standard input: with or without a UTF-8 byte order mark; nothing else counts", ok ? "PASS" : "FAIL");
        return ok ? 0 : 1;
    }

    static int Main(string[] args)
    {
        if (args[0] == "--target") return Target(int.Parse(args[1]), int.Parse(args[2]));
        string dll = Path.GetFullPath(args[0]);
        int failures = 0;
        failures += Drive(dll, 3000, -1, 2, 50);   // switch the import table first; FIFA's startup copy carries it over
        failures += Drive(dll, 0, -1, 2, 0);       // shadow possibly filled already; switch it directly
        failures += Drive(dll, 0, 1500, 3, 0);     // FIFA restores a Windows pointer during the watch
        failures += ReportFallback(dll);
        failures += CodeFromStandardInput();
        Console.WriteLine(failures == 0 ? "ALL PASSED" : "FAILURES: " + failures);
        return failures;
    }
}
