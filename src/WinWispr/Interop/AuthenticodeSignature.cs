using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace WinWispr.Interop;

/// <summary>
/// Checks that a file carries an intact Authenticode signature by a pinned certificate. WinWispr signs with its own
/// self-signed certificate, which most machines do not trust, so an untrusted root is accepted while every other
/// failure (no signature, altered file, broken signature) is not; the signer must then be the pinned certificate.
/// </summary>
internal static class AuthenticodeSignature
{
    private const uint CertEUntrustedRoot = 0x800B0109;
    private const uint WtdUiNone = 2;
    private const uint WtdRevokeNone = 0;
    private const uint WtdChoiceFile = 1;
    private const uint WtdStateActionVerify = 1;
    private const uint WtdStateActionClose = 2;
    private const uint WtdRevocationCheckNone = 0x10;
    private const uint WtdCacheOnlyUrlRetrieval = 0x1000;
    private static readonly Guid GenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");

    internal static bool IsSignedBy(string path, string thumbprint)
    {
        nint filePath = Marshal.StringToHGlobalUni(Path.GetFullPath(path));
        nint fileInfo = 0;
        var data = new WinTrustData();
        Guid action = GenericVerifyV2;
        bool verified = false;
        try
        {
            var file = new WinTrustFileInfo { Size = (uint)Marshal.SizeOf<WinTrustFileInfo>(), FilePath = filePath };
            fileInfo = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
            Marshal.StructureToPtr(file, fileInfo, false);
            data = new WinTrustData
            {
                Size = (uint)Marshal.SizeOf<WinTrustData>(),
                UiChoice = WtdUiNone,
                RevocationChecks = WtdRevokeNone,
                UnionChoice = WtdChoiceFile,
                File = fileInfo,
                StateAction = WtdStateActionVerify,
                ProviderFlags = WtdRevocationCheckNone | WtdCacheOnlyUrlRetrieval
            };
            uint result = unchecked((uint)WinVerifyTrust(-1, ref action, ref data));
            verified = true;
            if (result != 0 && result != CertEUntrustedRoot)
                return false;
            return string.Equals(SignerThumbprint(data.StateData), thumbprint, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (verified)
            {
                data.StateAction = WtdStateActionClose;
                WinVerifyTrust(-1, ref action, ref data);
            }
            if (fileInfo != 0)
                Marshal.FreeHGlobal(fileInfo);
            Marshal.FreeHGlobal(filePath);
        }
    }

    private static string? SignerThumbprint(nint stateData)
    {
        if (stateData == 0)
            return null;
        nint provider = WTHelperProvDataFromStateData(stateData);
        nint signer = provider == 0 ? 0 : WTHelperGetProvSignerFromChain(provider, 0, false, 0);
        nint providerCertificate = signer == 0 ? 0 : WTHelperGetProvCertFromChain(signer, 0);
        if (providerCertificate == 0)
            return null;
        // CRYPT_PROVIDER_CERT starts with its DWORD size; the CERT_CONTEXT pointer follows at pointer alignment.
        nint certificateContext = Marshal.ReadIntPtr(providerCertificate, IntPtr.Size);
        if (certificateContext == 0)
            return null;
        using var certificate = new X509Certificate2(certificateContext);
        return certificate.Thumbprint;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustFileInfo
    {
        public uint Size;
        public nint FilePath;
        public nint FileHandle;
        public nint KnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WinTrustData
    {
        public uint Size;
        public nint PolicyCallbackData;
        public nint SipClientData;
        public uint UiChoice;
        public uint RevocationChecks;
        public uint UnionChoice;
        public nint File;
        public uint StateAction;
        public nint StateData;
        public nint UrlReference;
        public uint ProviderFlags;
        public uint UiContext;
        public nint SignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern int WinVerifyTrust(nint window, ref Guid action, ref WinTrustData data);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperProvDataFromStateData(nint stateData);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvSignerFromChain(
        nint providerData, uint signerIndex, [MarshalAs(UnmanagedType.Bool)] bool counterSigner, uint counterSignerIndex);

    [DllImport("wintrust.dll", ExactSpelling = true)]
    private static extern nint WTHelperGetProvCertFromChain(nint signer, uint certificateIndex);
}
