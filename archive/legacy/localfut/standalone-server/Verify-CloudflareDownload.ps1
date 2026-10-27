if (-not ('Project21Authenticode' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class Project21Authenticode
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public uint cbStruct;
        public IntPtr pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
    }

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = true)]
    private static extern int WinVerifyTrust(
        IntPtr hwnd,
        [MarshalAs(UnmanagedType.LPStruct)] Guid action,
        ref WinTrustData data);

    public static bool Verify(string path)
    {
        IntPtr pathPointer = IntPtr.Zero;
        IntPtr filePointer = IntPtr.Zero;
        try
        {
            pathPointer = Marshal.StringToCoTaskMemUni(path);
            var file = new WinTrustFileInfo {
                cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustFileInfo)),
                pcwszFilePath = pathPointer,
                hFile = IntPtr.Zero,
                pgKnownSubject = IntPtr.Zero
            };
            filePointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WinTrustFileInfo)));
            Marshal.StructureToPtr(file, filePointer, false);
            var data = new WinTrustData {
                cbStruct = (uint)Marshal.SizeOf(typeof(WinTrustData)),
                dwUIChoice = 2,
                fdwRevocationChecks = 0,
                dwUnionChoice = 1,
                pFile = filePointer,
                dwStateAction = 0,
                dwProvFlags = 0,
                dwUIContext = 0
            };
            var action = new Guid("00AAC56B-CD44-11D0-8CC2-00C04FC295EE");
            return WinVerifyTrust(new IntPtr(-1), action, ref data) == 0;
        }
        finally
        {
            if (filePointer != IntPtr.Zero) Marshal.FreeCoTaskMem(filePointer);
            if (pathPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pathPointer);
        }
    }
}
'@
}

function Assert-CloudflareAuthenticodeSignature {
    param([Parameter(Mandatory = $true)][string]$Path)
    if (-not [Project21Authenticode]::Verify([IO.Path]::GetFullPath($Path))) {
        throw "Windows did not trust the Authenticode signature on '$Path'."
    }
    try {
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($Path)
        $subject = $certificate.Subject
    } catch {
        throw 'The trusted download did not expose an Authenticode signer certificate.'
    }
    if ($subject -notmatch 'Cloudflare') {
        throw 'The trusted download was not signed by Cloudflare.'
    }
}
