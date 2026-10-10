using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace BatCave.WindowsAgent;

internal sealed record ApprovedPeer(string DeviceId, string CertificateSha256);

// This transport is independent of cursor routing. Discovery and display names
// do not grant trust; the caller supplies identities approved by the user.
internal static class SecurePeerTransport
{
    public static string Fingerprint(X509Certificate certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData()));

    public static bool MatchesApprovedPeer(X509Certificate? certificate, ApprovedPeer peer)
    {
        if (certificate is null || string.IsNullOrWhiteSpace(peer.DeviceId) || peer.CertificateSha256?.Length != 64) { return false; }
        using var leaf = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
        var now = DateTime.UtcNow;
        if (now < leaf.NotBefore.ToUniversalTime() || now > leaf.NotAfter.ToUniversalTime()) { return false; }
        try
        {
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(leaf.RawData),
                Convert.FromHexString(peer.CertificateSha256));
        }
        catch (FormatException) { return false; }
    }

    public static async Task<SslStream> AuthenticateServerAsync(TcpClient socket,
        X509Certificate2 identity, ApprovedPeer expectedPeer, CancellationToken cancellationToken = default)
        => (await AuthenticateServerAsync(socket, identity, new[] { expectedPeer }, cancellationToken).ConfigureAwait(false)).Stream;

    public static async Task<(SslStream Stream, ApprovedPeer Peer)> AuthenticateServerAsync(TcpClient socket,
        X509Certificate2 identity, IReadOnlyList<ApprovedPeer> approvedPeers, CancellationToken cancellationToken = default)
    {
        ApprovedPeer? authenticatedPeer = null;
        var stream = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false,
            (_, cert, _, _) =>
            {
                authenticatedPeer = approvedPeers.FirstOrDefault(peer => MatchesApprovedPeer(cert, peer));
                return authenticatedPeer is not null;
            });
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await stream.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
            {
                ServerCertificate = identity, ClientCertificateRequired = true,
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateChainPolicy = LocalPinPolicy(), AllowRenegotiation = false
            }, timeout.Token).ConfigureAwait(false);
            RequireMutualTls(stream);
            return (stream, authenticatedPeer ?? throw new AuthenticationException("No approved peer identity."));
        }
        catch { stream.Dispose(); throw; }
    }

    public static async Task<SslStream> AuthenticateClientAsync(TcpClient socket,
        X509Certificate2 identity, ApprovedPeer expectedPeer, CancellationToken cancellationToken = default)
    {
        var stream = new SslStream(socket.GetStream(), leaveInnerStreamOpen: false,
            (_, cert, _, _) => MatchesApprovedPeer(cert, expectedPeer));
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await stream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "batcave.local", ClientCertificates = new X509CertificateCollection { identity },
                // Send the pinned identity even when the server's CA hint list is empty
                // or does not include our explicitly approved self-signed identity.
                LocalCertificateSelectionCallback = (_, _, _, _, _) => identity,
                EnabledSslProtocols = SslProtocols.Tls13,
                CertificateChainPolicy = LocalPinPolicy(), AllowRenegotiation = false
            }, timeout.Token).ConfigureAwait(false);
            RequireMutualTls(stream);
            return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    private static X509ChainPolicy LocalPinPolicy() => new()
    {
        RevocationMode = X509RevocationMode.NoCheck, DisableCertificateDownloads = true
    };

    private static void RequireMutualTls(SslStream stream)
    {
        if (!stream.IsAuthenticated || !stream.IsMutuallyAuthenticated || !stream.IsEncrypted
            || stream.SslProtocol != SslProtocols.Tls13)
            throw new AuthenticationException("Mutually authenticated TLS 1.3 is required.");
    }
}
