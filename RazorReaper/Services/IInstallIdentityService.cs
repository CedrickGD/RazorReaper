namespace RazorReaper.Services;

/// <summary>
/// The three headers that authenticate one app→backend request under the rr.install.v1 contract.
/// </summary>
public sealed record SignedRequestHeaders(string InstallId, string Timestamp, string Signature)
{
    public const string InstallHeaderName = "X-RR-Install";
    public const string TimestampHeaderName = "X-RR-Timestamp";
    public const string SignatureHeaderName = "X-RR-Signature";
}

/// <summary>
/// JWK form of the install's P-256 public key (<c>x</c>/<c>y</c> are base64url, 32 bytes each).
/// </summary>
public sealed record InstallPublicKeyJwk(string Kty, string Crv, string X, string Y);

/// <summary>
/// Owns this install's ECDSA P-256 keypair, registers the public half with the backend once, and
/// signs outgoing requests. The install id itself stays with <see cref="IClientIdentityService"/>.
/// </summary>
public interface IInstallIdentityService
{
    /// <summary>True once the backend has acknowledged the current install id + key.</summary>
    bool IsRegistered { get; }

    /// <summary>Public key of the install (generating the keypair on first use).</summary>
    Task<InstallPublicKeyJwk?> GetPublicKeyAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Registers the install with the backend if it is not known to be registered yet. Never throws
    /// and never blocks on retries — transient failures are retried in the background.
    /// </summary>
    Task EnsureRegisteredAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Produces the signature headers for one request. Returns null until the backend has
    /// acknowledged the key (<see cref="IsRegistered"/>) or when no key is available; the
    /// request is then sent unsigned, which legacy-tolerant routes still accept.
    /// </summary>
    Task<SignedRequestHeaders?> SignAsync(
        HttpMethod method,
        Uri uri,
        byte[] body,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reports that the backend answered HTTP 401 to a request that carried
    /// <paramref name="rejectedHeaders"/>. The install is then treated as unregistered again
    /// (requests go unsigned until the backend acknowledges the key once more) and one
    /// re-registration is scheduled, at most once per ten minutes per process. Reports for
    /// signatures made before the current registration was acknowledged are ignored: the
    /// request was in flight while the install re-registered.
    /// </summary>
    void ReportSignedRequestRejected(Uri uri, SignedRequestHeaders rejectedHeaders);
}
