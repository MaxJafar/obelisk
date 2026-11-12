using System.Security.Cryptography;
using System.Text;
using JarvisHud.Core;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;
using Org.BouncyCastle.Security;

namespace JarvisHud.Gateway;

/// <summary>
/// The app's Ed25519 device identity, as the Gateway protocol defines it: the device id is the
/// SHA-256 of the raw public key (hex), and every connect signs the server's challenge.
/// The private key seed is stored DPAPI-encrypted and never leaves this machine.
/// </summary>
public sealed class DeviceIdentity
{
    private const string SecretName = "device-identity";
    private readonly Ed25519PrivateKeyParameters _privateKey;

    public string DeviceId { get; }
    public string PublicKeyBase64Url { get; }

    private DeviceIdentity(Ed25519PrivateKeyParameters privateKey)
    {
        _privateKey = privateKey;
        var publicKey = privateKey.GeneratePublicKey().GetEncoded();
        DeviceId = Convert.ToHexString(SHA256.HashData(publicKey)).ToLowerInvariant();
        PublicKeyBase64Url = Base64Url(publicKey);
    }

    public static DeviceIdentity LoadOrCreate()
    {
        var seed = SecretStore.Read(SecretName);
        if (seed is not null && seed.Length == Ed25519PrivateKeyParameters.KeySize)
            return new DeviceIdentity(new Ed25519PrivateKeyParameters(seed, 0));

        var created = new Ed25519PrivateKeyParameters(new SecureRandom());
        SecretStore.Write(SecretName, created.GetEncoded());
        return new DeviceIdentity(created);
    }

    /// <summary>
    /// Builds and signs the v3 device-auth payload
    /// (packages/gateway-client/src/device-auth.ts, buildDeviceAuthPayloadV3).
    /// </summary>
    public string SignConnect(string clientId, string clientMode, string role, IReadOnlyList<string> scopes,
        long signedAtMs, string? token, string nonce, string platform, string? deviceFamily)
    {
        var payload = string.Join("|",
            "v3",
            DeviceId,
            clientId,
            clientMode,
            role,
            string.Join(",", scopes),
            signedAtMs.ToString(System.Globalization.CultureInfo.InvariantCulture),
            token ?? "",
            nonce,
            NormalizeMetadata(platform),
            NormalizeMetadata(deviceFamily));

        var data = Encoding.UTF8.GetBytes(payload);
        var signer = new Ed25519Signer();
        signer.Init(true, _privateKey);
        signer.BlockUpdate(data, 0, data.Length);
        return Base64Url(signer.GenerateSignature());
    }

    /// <summary>Trim and lowercase ASCII only, matching normalizeDeviceMetadataForAuth.</summary>
    private static string NormalizeMetadata(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var trimmed = value.Trim();
        var chars = trimmed.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (chars[i] is >= 'A' and <= 'Z') chars[i] = (char)(chars[i] + 32);
        return new string(chars);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
