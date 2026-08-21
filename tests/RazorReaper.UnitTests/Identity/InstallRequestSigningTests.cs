using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RazorReaper.Services;
using RazorReaper.Services.Http;

namespace RazorReaper.UnitTests.Identity;

public sealed class InstallRequestSigningTests
{
    private const string EmptyBodySha256 = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    [Fact]
    public void SigningStringMatchesContractForKnownBody()
    {
        var body = Encoding.UTF8.GetBytes("""{"a":1}""");
        var uri = new Uri("https://backend.rr-admin-panel.workers.dev/api/ingest?x=1&y=2");

        var signingString = InstallRequestSigning.BuildSigningString(HttpMethod.Post, uri, "1787310000", body);

        Assert.Equal(
            "POST\n/api/ingest\n1787310000\n015abd7f5cc57a2dd94b7590f04ad8084273905ee33ec5cebeae62276a97f862",
            signingString);
    }

    [Fact]
    public void SigningStringUsesEmptyBodyHashAndUppercasesMethod()
    {
        var uri = new Uri("https://rr-admin-panel.pages.dev/api/usage/status?hwid=ABC");

        var signingString = InstallRequestSigning.BuildSigningString(new HttpMethod("get"), uri, "1", []);

        Assert.Equal($"GET\n/api/usage/status\n1\n{EmptyBodySha256}", signingString);
        Assert.DoesNotContain("?", signingString);
        Assert.False(signingString.EndsWith('\n'));
    }

    [Fact]
    public void JwkCoordinatesAreExactly32BytesAndRoundTrip()
    {
        for (var i = 0; i < 8; i++)
        {
            using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

            var jwk = InstallRequestSigning.ToJwk(key);

            Assert.Equal("EC", jwk.Kty);
            Assert.Equal("P-256", jwk.Crv);
            Assert.Equal(32, InstallRequestSigning.Base64UrlDecode(jwk.X).Length);
            Assert.Equal(32, InstallRequestSigning.Base64UrlDecode(jwk.Y).Length);
            Assert.DoesNotContain("=", jwk.X);
            Assert.DoesNotContain("+", jwk.X + jwk.Y);
            Assert.DoesNotContain("/", jwk.X + jwk.Y);

            using var imported = InstallRequestSigning.FromJwk(jwk);
            var expected = key.ExportParameters(false);
            var actual = imported.ExportParameters(false);
            Assert.Equal(expected.Q.X, actual.Q.X);
            Assert.Equal(expected.Q.Y, actual.Q.Y);
        }
    }

    [Fact]
    public void SignatureIsIeeeP1363AndVerifiesWithImportedJwk()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signingString = InstallRequestSigning.BuildSigningString(
            HttpMethod.Post, new Uri("https://backend.rr-admin-panel.workers.dev/api/ingest"), "1787310000", Encoding.UTF8.GetBytes("{}"));

        var signature = InstallRequestSigning.Sign(key, signingString);

        Assert.Equal(64, signature.Length);
        using var verifier = InstallRequestSigning.FromJwk(InstallRequestSigning.ToJwk(key));
        Assert.True(verifier.VerifyData(
            Encoding.UTF8.GetBytes(signingString),
            signature,
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        Assert.False(InstallRequestSigning.Verify(verifier, signingString + "x", signature));
    }

    [Fact]
    public void Base64UrlHasNoPaddingAndDecodes()
    {
        var bytes = new byte[] { 0xfb, 0xff, 0xfe, 0x00, 0x01 };

        var encoded = InstallRequestSigning.Base64UrlEncode(bytes);

        Assert.Equal("-__-AAE", encoded);
        Assert.Equal(bytes, InstallRequestSigning.Base64UrlDecode(encoded));
    }

    public static TheoryData<string> ServerFixtureVectorNames()
    {
        var data = new TheoryData<string>();
        foreach (var vector in ServerFixture.CachedVectors)
        {
            data.Add(vector.Name);
        }

        return data;
    }

    /// <summary>
    /// The server test-suite's vectors (RR-Admin-Panel/tests/install-auth/fixtures/vectors.json,
    /// copied to Fixtures/install-signing-vectors.json): same signing string, and the server's
    /// signatures verify against the server's JWK through this implementation.
    /// </summary>
    [Theory]
    [MemberData(nameof(ServerFixtureVectorNames))]
    public void ServerFixtureVectorsMatchThisImplementation(string name)
    {
        var fixture = ServerFixture.Load();
        var vector = fixture.Vectors.Single(v => v.Name == name);
        var bodyBytes = Encoding.UTF8.GetBytes(vector.Body);

        Assert.Equal(vector.BodySha256, Convert.ToHexStringLower(SHA256.HashData(bodyBytes)));
        var signingString = InstallRequestSigning.BuildSigningString(
            new HttpMethod(vector.Method), new Uri(vector.Url), vector.Timestamp, bodyBytes);
        Assert.Equal(vector.SigningString, signingString);

        using var verifier = InstallRequestSigning.FromJwk(fixture.PublicKeyJwk);
        var signature = InstallRequestSigning.Base64UrlDecode(vector.Signature);
        Assert.Equal(64, signature.Length);
        Assert.True(InstallRequestSigning.Verify(verifier, signingString, signature), $"fixture signature for '{name}' must verify");

        // And a signature produced here from the fixture's private key verifies too.
        using var signer = ECDsa.Create();
        signer.ImportPkcs8PrivateKey(Convert.FromBase64String(fixture.PrivateKeyPkcs8Base64), out _);
        Assert.True(InstallRequestSigning.Verify(verifier, signingString, InstallRequestSigning.Sign(signer, signingString)));
    }

    [Fact]
    public void ServerFixturePrivateKeyDerivesTheFixtureJwk()
    {
        var fixture = ServerFixture.Load();

        using var key = ECDsa.Create();
        key.ImportPkcs8PrivateKey(Convert.FromBase64String(fixture.PrivateKeyPkcs8Base64), out _);

        Assert.Equal(256, key.KeySize);
        Assert.Equal(fixture.PublicKeyJwk, InstallRequestSigning.ToJwk(key));
        Assert.True(Guid.TryParseExact(fixture.InstallId, "D", out _));
    }

    /// <summary>
    /// Produces the cross-implementation vectors the server test-suite checks. With
    /// RR_EMIT_SIGNING_VECTORS=&lt;dir&gt; the vectors are written to &lt;dir&gt;/csharp-vectors.json;
    /// without it they are only verified locally.
    /// </summary>
    [Fact]
    public void SigningVectorsVerifyAndCanBeEmitted()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var jwk = InstallRequestSigning.ToJwk(key);
        var cases = new (HttpMethod Method, string Url, string Timestamp, string Body)[]
        {
            (HttpMethod.Post, "https://backend.rr-admin-panel.workers.dev/api/ingest", "1787310000", """{"event":"session_start","n":1}"""),
            (HttpMethod.Get, "https://rr-admin-panel.pages.dev/api/usage/status?hwid=ABC123", "1787310042", ""),
            (HttpMethod.Post, "https://rr-admin-panel.pages.dev/api/feedback", "1787310777", """{"message":"hällo wörld ✓","contact":null}"""),
        };

        var vectors = new List<Dictionary<string, object?>>();
        using var verifier = InstallRequestSigning.FromJwk(jwk);
        foreach (var (method, url, timestamp, body) in cases)
        {
            var bodyBytes = Encoding.UTF8.GetBytes(body);
            var uri = new Uri(url);
            var signingString = InstallRequestSigning.BuildSigningString(method, uri, timestamp, bodyBytes);
            var signature = InstallRequestSigning.Sign(key, signingString);

            Assert.True(InstallRequestSigning.Verify(verifier, signingString, signature));

            vectors.Add(new Dictionary<string, object?>
            {
                ["publicKeyJwk"] = new Dictionary<string, string>
                {
                    ["kty"] = jwk.Kty,
                    ["crv"] = jwk.Crv,
                    ["x"] = jwk.X,
                    ["y"] = jwk.Y
                },
                ["method"] = method.Method,
                ["path"] = uri.AbsolutePath,
                ["timestamp"] = timestamp,
                ["bodyUtf8"] = body,
                ["signingString"] = signingString,
                ["signature"] = InstallRequestSigning.Base64UrlEncode(signature)
            });
        }

        Assert.Equal(3, vectors.Count);
        Assert.Contains(vectors, v => (string)v["method"]! == "GET" && (string)v["bodyUtf8"]! == string.Empty);

        var outputDirectory = Environment.GetEnvironmentVariable("RR_EMIT_SIGNING_VECTORS");
        if (string.IsNullOrWhiteSpace(outputDirectory))
        {
            return;
        }

        Directory.CreateDirectory(outputDirectory);
        var path = Path.Combine(outputDirectory, "csharp-vectors.json");
        File.WriteAllText(path, JsonSerializer.Serialize(vectors, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        }));
        Assert.True(File.Exists(path));
    }

    private sealed record ServerFixtureVector(
        string Name,
        string Method,
        string Url,
        string Timestamp,
        string Body,
        string BodySha256,
        string SigningString,
        string Signature);

    private sealed record ServerFixture(
        string InstallId,
        string PrivateKeyPkcs8Base64,
        InstallPublicKeyJwk PublicKeyJwk,
        IReadOnlyList<ServerFixtureVector> Vectors)
    {
        private static readonly Lazy<ServerFixture> Cached = new(Load);

        public static IReadOnlyList<ServerFixtureVector> CachedVectors => Cached.Value.Vectors;

        public static ServerFixture Load()
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "install-signing-vectors.json");
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            Assert.Equal("rr.install.v1", root.GetProperty("version").GetString());

            var jwk = root.GetProperty("public_key_jwk");
            var vectors = root.GetProperty("vectors").EnumerateArray()
                .Select(v => new ServerFixtureVector(
                    v.GetProperty("name").GetString()!,
                    v.GetProperty("method").GetString()!,
                    v.GetProperty("url").GetString()!,
                    v.GetProperty("timestamp").GetString()!,
                    v.GetProperty("body").GetString()!,
                    v.GetProperty("body_sha256").GetString()!,
                    v.GetProperty("signing_string").GetString()!,
                    v.GetProperty("signature").GetString()!))
                .ToList();
            Assert.NotEmpty(vectors);

            return new ServerFixture(
                root.GetProperty("install_id").GetString()!,
                root.GetProperty("private_key_pkcs8_base64").GetString()!,
                new InstallPublicKeyJwk(
                    jwk.GetProperty("kty").GetString()!,
                    jwk.GetProperty("crv").GetString()!,
                    jwk.GetProperty("x").GetString()!,
                    jwk.GetProperty("y").GetString()!),
                vectors);
        }
    }
}
