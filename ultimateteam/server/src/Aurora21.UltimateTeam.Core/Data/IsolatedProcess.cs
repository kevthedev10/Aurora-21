using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Aurora21.UltimateTeam.Core.Data;

/// <summary>
/// Starts a Windows process that inherits only a NUL stdin and an output log file.
///
/// <c>Process.Start</c> always lets the child inherit every inheritable handle this process
/// holds, including ones inherited from whoever started the server (its stdout/stderr pipes or
/// log files). <c>pg_ctl start</c> passes those on to the postmaster, which outlives the server,
/// so a caller waiting for the server's output to end would wait for the database instead.
/// An explicit PROC_THREAD_ATTRIBUTE_HANDLE_LIST limits inheritance to the two handles given.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class IsolatedProcess
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateNoWindow = 0x08000000;
    private const int StartfUseStdHandles = 0x00000100;
    private const int ProcThreadAttributeHandleList = 0x00020002;
    private const uint HandleFlagInherit = 0x1;
    private const uint WaitTimeout = 0x102;

    /// <summary>Runs <paramref name="executable"/> to completion; its stdout/stderr are appended to <paramref name="outputPath"/>.</summary>
    public static (int ExitCode, string Output) Run(string executable, IReadOnlyList<string> arguments, string outputPath, TimeSpan timeout)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var output = new FileStream(outputPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        var outputStart = output.Length;
        using var input = new FileStream("NUL", FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var inputHandle = input.SafeFileHandle;
        var outputHandle = output.SafeFileHandle;
        MakeInheritable(inputHandle);
        MakeInheritable(outputHandle);

        var handles = Marshal.AllocHGlobal(2 * IntPtr.Size);
        var attributeListSize = IntPtr.Zero;
        var attributeList = IntPtr.Zero;
        try
        {
            Marshal.WriteIntPtr(handles, 0, inputHandle.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, outputHandle.DangerousGetHandle());

            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeListSize);
            attributeList = Marshal.AllocHGlobal(attributeListSize);
            if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            if (!UpdateProcThreadAttribute(attributeList, 0, ProcThreadAttributeHandleList, handles, 2 * IntPtr.Size, IntPtr.Zero, IntPtr.Zero))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfoEx>(),
                    dwFlags = StartfUseStdHandles,
                    hStdInput = inputHandle.DangerousGetHandle(),
                    hStdOutput = outputHandle.DangerousGetHandle(),
                    hStdError = outputHandle.DangerousGetHandle(),
                },
                lpAttributeList = attributeList,
            };
            var commandLine = new StringBuilder(Quote(executable));
            foreach (var argument in arguments)
            {
                commandLine.Append(' ').Append(Quote(argument));
            }
            if (!CreateProcessW(executable, commandLine, IntPtr.Zero, IntPtr.Zero, true, ExtendedStartupInfoPresent | CreateNoWindow,
                    IntPtr.Zero, Path.GetDirectoryName(executable), ref startup, out var process))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }
            try
            {
                if (WaitForSingleObject(process.hProcess, (uint)timeout.TotalMilliseconds) == WaitTimeout)
                {
                    TerminateProcess(process.hProcess, 1);
                    throw new TimeoutException($"{Path.GetFileName(executable)} did not finish within {timeout.TotalSeconds:0} seconds");
                }
                GetExitCodeProcess(process.hProcess, out var exitCode);
                output.Flush();
                return ((int)exitCode, ReadFrom(outputPath, outputStart));
            }
            finally
            {
                CloseHandle(process.hThread);
                CloseHandle(process.hProcess);
            }
        }
        finally
        {
            if (attributeList != IntPtr.Zero)
            {
                DeleteProcThreadAttributeList(attributeList);
                Marshal.FreeHGlobal(attributeList);
            }
            Marshal.FreeHGlobal(handles);
        }
    }

    private static void MakeInheritable(SafeFileHandle handle)
    {
        if (!SetHandleInformation(handle.DangerousGetHandle(), HandleFlagInherit, HandleFlagInherit))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    private static string ReadFrom(string path, long offset)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(Math.Min(offset, stream.Length), SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Quotes one argument for the MSVC command-line parser.</summary>
    private static string Quote(string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny(new[] { ' ', '\t', '"' }) < 0)
        {
            return argument;
        }
        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var character in argument)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            quoted.Append('\\', character == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            quoted.Append(character);
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? lpReserved;
        public string? lpDesktop;
        public string? lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(string applicationName, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles, uint creationFlags, IntPtr environment, string? currentDirectory,
        ref StartupInfoEx startupInfo, out ProcessInformation processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(IntPtr attributeList, int attributeCount, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(IntPtr attributeList, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previousValue, IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
