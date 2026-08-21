using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;

namespace RazorReaper.Services.Http;

/// <summary>
/// Pure helpers for the rr.install.v1 signing contract: canonical signing string, base64url,
/// and the JWK projection of a P-256 public key. No state, no I/O — fully unit-testable.
/// </summary>
public static class InstallRequestSigning
{
    public const int CoordinateLength = 32;

    /// <summary>
    /// <c>METHOD\npath\ntimestamp\nsha256hex(body)</c> — LF separated, no trailing newline, path
    /// without query. An empty body hashes to SHA-256("").
    /// </summary>
    public static string BuildSigningString(HttpMethod method, Uri uri, string timestamp, ReadOnlySpan<byte> body)
    {
        ArgumentNullException.ThrowIfNull(method);
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentException.ThrowIfNullOrWhiteSpace(timestamp);

        var path = uri.IsAbsoluteUri ? uri.AbsolutePath : uri.OriginalString.Split('?', 2)[0];
        if (string.IsNullOrEmpty(path))
        {
            path = "/";
        }

        var bodyHash = Convert.ToHexStringLower(SHA256.HashData(body));
        return string.Concat(method.Method.ToUpperInvariant(), "\n", path, "\n", timestamp, "\n", bodyHash);
    }

    public static byte[] Sign(ECDsa key, string signingString)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.SignData(
            Encoding.UTF8.GetBytes(signingString),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static bool Verify(ECDsa key, string signingString, ReadOnlySpan<byte> signature)
    {
        ArgumentNullException.ThrowIfNull(key);
        return key.VerifyData(
            Encoding.UTF8.GetBytes(signingString),
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
    }

    public static InstallPublicKeyJwk ToJwk(ECDsa key)
    {
        ArgumentNullException.ThrowIfNull(key);
        var parameters = key.ExportParameters(includePrivateParameters: false);
        return new InstallPublicKeyJwk(
            "EC",
            "P-256",
            Base64UrlEncode(LeftPad(parameters.Q.X, CoordinateLength)),
            Base64UrlEncode(LeftPad(parameters.Q.Y, CoordinateLength)));
    }

    public static ECDsa FromJwk(InstallPublicKeyJwk jwk)
    {
        ArgumentNullException.ThrowIfNull(jwk);
        var parameters = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint
            {
                X = Base64UrlDecode(jwk.X),
                Y = Base64UrlDecode(jwk.Y)
            }
        };
        return ECDsa.Create(parameters);
    }

    public static string Base64UrlEncode(ReadOnlySpan<byte> bytes) => Base64Url.EncodeToString(bytes);

    public static byte[] Base64UrlDecode(string text) => Base64Url.DecodeFromChars(text);

    private static byte[] LeftPad(byte[]? value, int length)
    {
        value ??= [];
        if (value.Length == length)
        {
            return value;
        }

        if (value.Length > length)
        {
            // Strip leading zero bytes some providers prepend; the coordinate itself fits.
            var excess = value.Length - length;
            for (var i = 0; i < excess; i++)
            {
                if (value[i] != 0)
                {
                    throw new CryptographicException("P-256 coordinate longer than 32 bytes.");
                }
            }

            return value[excess..];
        }

        var padded = new byte[length];
        Buffer.BlockCopy(value, 0, padded, length - value.Length, value.Length);
        return padded;
    }
}
