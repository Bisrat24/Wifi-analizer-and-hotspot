using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace WifiAnalyzerHotspot.Services
{
    /// <summary>
    /// Encrypts settings secrets with Windows DPAPI (current-user scope) so passwords
    /// are never stored as plaintext in settings.json. Values without the marker are
    /// treated as legacy plaintext and passed through unchanged.
    /// </summary>
    internal static class DpapiSecret
    {
        private const string Prefix = "dpapi:";
        private const int CryptProtectUiForbidden = 0x1;

        [StructLayout(LayoutKind.Sequential)]
        private struct DataBlob
        {
            public int CbData;
            public IntPtr PbData;
        }

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptProtectData(
            ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

        [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool CryptUnprotectData(
            ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
            IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr hMem);

        public static string Protect(string? plain)
        {
            if (string.IsNullOrEmpty(plain)) return plain ?? string.Empty;
            try
            {
                byte[] blob = Transform(Encoding.UTF8.GetBytes(plain), protect: true);
                return Prefix + Convert.ToBase64String(blob);
            }
            catch
            {
                // Never lose a user-entered password: keep it usable in memory this session.
                return plain;
            }
        }

        public static string Unprotect(string? stored)
        {
            if (string.IsNullOrEmpty(stored)) return stored ?? string.Empty;
            if (!stored.StartsWith(Prefix, StringComparison.Ordinal)) return stored; // legacy plaintext
            try
            {
                byte[] blob = Convert.FromBase64String(stored.Substring(Prefix.Length));
                return Encoding.UTF8.GetString(Transform(blob, protect: false));
            }
            catch
            {
                return string.Empty;
            }
        }

        private static byte[] Transform(byte[] input, bool protect)
        {
            var inBlob = new DataBlob
            {
                CbData = input.Length,
                PbData = Marshal.AllocHGlobal(input.Length)
            };
            IntPtr outPtr = IntPtr.Zero;
            try
            {
                Marshal.Copy(input, 0, inBlob.PbData, input.Length);

                DataBlob outBlob;
                bool ok = protect
                    ? CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob)
                    : CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out outBlob);

                if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
                outPtr = outBlob.PbData;

                var result = new byte[outBlob.CbData];
                Marshal.Copy(outBlob.PbData, result, 0, outBlob.CbData);
                return result;
            }
            finally
            {
                if (inBlob.PbData != IntPtr.Zero) Marshal.FreeHGlobal(inBlob.PbData);
                if (outPtr != IntPtr.Zero) LocalFree(outPtr);
            }
        }
    }
}
