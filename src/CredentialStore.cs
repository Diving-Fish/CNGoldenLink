using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace CNGoldenLink;

// Windows Credential Manager: no token in settings, save data, logs, or release archives.
internal static class CredentialStore
{
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential {
        public uint Flags, Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist, AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias, UserName;
    }
    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Read(string target, uint type, uint flags, out IntPtr credential);
    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Write(ref Credential credential, uint flags);
    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Delete(string target, uint type, uint flags);
    [DllImport("advapi32.dll")] private static extern void CredFree(IntPtr credential);
    private static string Target(Uri origin) => "CNGoldenLink/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(origin.AbsoluteUri)));
    public static string? Load(Uri origin) {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("credential_store_windows_only");
        if (!Read(Target(origin), 1, 0, out var pointer)) {
            if (Marshal.GetLastWin32Error() == 1168) return null;
            throw new IOException("credential_read_failed");
        }
        try {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            if (credential.CredentialBlobSize > 4096) throw new IOException("credential_invalid");
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        } finally { CredFree(pointer); }
    }
    public static void Save(Uri origin, string token) {
        var pointer = Marshal.StringToCoTaskMemUni(token);
        try {
            var credential = new Credential { Type = 1, TargetName = Target(origin), CredentialBlob = pointer,
                CredentialBlobSize = (uint)Encoding.Unicode.GetByteCount(token), Persist = 2, UserName = "CNGoldenLink" };
            if (!Write(ref credential, 0)) throw new IOException("credential_write_failed");
        } finally { Marshal.ZeroFreeCoTaskMemUnicode(pointer); }
    }
    public static void Forget(Uri origin) {
        if (!Delete(Target(origin), 1, 0) && Marshal.GetLastWin32Error() != 1168) throw new IOException("credential_delete_failed");
    }
}
