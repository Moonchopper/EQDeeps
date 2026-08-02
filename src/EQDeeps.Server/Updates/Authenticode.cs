using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace EQDeeps.Server.Updates;

/// <summary>
/// Authenticode verification for a downloaded installer, via WinVerifyTrust —
/// the same check Windows itself performs when you double-click an exe.
///
/// This is the last gate before EQDeeps executes a binary it pulled off the
/// internet, so it is deliberately the strict kind: the signature must be
/// cryptographically valid, chain to a trusted root, and carry the publisher
/// we actually sign with. NetSparkle has already checked the release's Ed25519
/// signature against the key baked into this assembly by then; the two are
/// independent, so compromising the update host alone breaks neither.
///
/// Reading the signer certificate alone (X509Certificate.CreateFromSignedFile)
/// would <em>not</em> do — it happily returns a certificate from a tampered or
/// self-signed file. The chain has to be walked.
/// </summary>
internal static class Authenticode
{
    /// <summary>
    /// Subject substring every EQDeeps release is signed with. Sourced read-only
    /// from the Azure Artifact Signing billing profile, so it changes only if the
    /// certificate subject itself does — see docs/release-signing.md.
    /// </summary>
    private const string ExpectedSubject = "CN=Austin Culbertson";

    /// <summary>
    /// True when <paramref name="filePath"/> carries a valid, trusted Authenticode
    /// signature from the expected publisher. Any failure — unsigned, tampered,
    /// expired chain, wrong publisher — returns false; callers must refuse to run
    /// the file.
    /// </summary>
    public static bool IsTrusted(string filePath, out string reason)
    {
        if (!File.Exists(filePath))
        {
            reason = "installer is missing";
            return false;
        }

        var result = VerifyTrust(filePath);
        if (result != 0)
        {
            // 0x800B0100 TRUST_E_NOSIGNATURE, 0x800B0109 CERT_E_UNTRUSTEDROOT,
            // 0x80096010 TRUST_E_BAD_DIGEST (tampered), and friends.
            reason = $"Authenticode check failed (0x{result:X8})";
            return false;
        }

        try
        {
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(filePath));
            if (!cert.Subject.Contains(ExpectedSubject, StringComparison.OrdinalIgnoreCase))
            {
                reason = $"signed by an unexpected publisher: {cert.Subject}";
                return false;
            }
        }
        catch (Exception ex)
        {
            reason = $"could not read the signing certificate: {ex.Message}";
            return false;
        }

        reason = "ok";
        return true;
    }

    private static int VerifyTrust(string filePath)
    {
        var fileInfo = new WinTrustFileInfo
        {
            cbStruct = Marshal.SizeOf<WinTrustFileInfo>(),
            pcwszFilePath = filePath,
            hFile = IntPtr.Zero,
            pgKnownSubject = IntPtr.Zero,
        };

        var fileInfoPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WinTrustFileInfo>());
        try
        {
            Marshal.StructureToPtr(fileInfo, fileInfoPtr, fDeleteOld: false);
            var data = new WinTrustData
            {
                cbStruct = Marshal.SizeOf<WinTrustData>(),
                pPolicyCallbackData = IntPtr.Zero,
                pSIPClientData = IntPtr.Zero,
                dwUIChoice = UiNone,
                fdwRevocationChecks = RevokeWholeChain,
                dwUnionChoice = ChoiceFile,
                pFile = fileInfoPtr,
                // Fail rather than prompt: this runs with no user attached.
                dwStateAction = StateActionIgnore,
                hWVTStateData = IntPtr.Zero,
                pwszURLReference = null,
                dwProvFlags = RevocationCheckChainExcludeRoot,
                dwUIContext = 0,
                pSignatureSettings = IntPtr.Zero,
            };

            var action = WinTrustActionGenericVerifyV2;
            return WinVerifyTrust(InvalidHandleValue, ref action, ref data);
        }
        finally
        {
            Marshal.DestroyStructure<WinTrustFileInfo>(fileInfoPtr);
            Marshal.FreeHGlobal(fileInfoPtr);
        }
    }

    private static readonly IntPtr InvalidHandleValue = new(-1);
    private static Guid WinTrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private const uint UiNone = 2;
    private const uint RevokeWholeChain = 1;
    private const uint ChoiceFile = 1;
    private const uint StateActionIgnore = 0;
    private const uint RevocationCheckChainExcludeRoot = 0x00000040;

    [DllImport("wintrust.dll", ExactSpelling = true, SetLastError = false, CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WinTrustData pWVTData);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustFileInfo
    {
        public int cbStruct;
        [MarshalAs(UnmanagedType.LPTStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WinTrustData
    {
        public int cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        [MarshalAs(UnmanagedType.LPTStr)] public string? pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }
}
