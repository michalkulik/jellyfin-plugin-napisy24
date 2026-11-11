using System;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Jellyfin.Plugin.Napisy24.Napisy24;

namespace Jellyfin.Plugin.Napisy24.Activation;

/// <summary>
/// Details carried by a valid activation code.
/// </summary>
internal sealed record ActivationInfo(string ServerId, string Name, DateTimeOffset? Expires);

/// <summary>
/// Verifies activation codes signed with the project's private ECDSA key.
/// Only the public key is shipped with the plugin, so a public repository
/// cannot be used to mint new codes.
/// </summary>
internal static class ActivationCode
{
    public const string Prefix = "N24v1";

    public static bool TryVerify(
        string? code,
        string serverId,
        string publicKeyBase64,
        out ActivationInfo? info,
        out string error)
    {
        info = null;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(code))
        {
            error = "Enter an activation code.";
            return false;
        }

        var parts = code.Trim().Split('.');
        if (parts.Length != 3 || !string.Equals(parts[0], Prefix, StringComparison.Ordinal))
        {
            error = "Invalid activation code format.";
            return false;
        }

        byte[] payloadBytes;
        byte[] signature;
        try
        {
            payloadBytes = Base64Url.DecodeBytes(parts[1]);
            signature = Base64Url.DecodeBytes(parts[2]);
        }
        catch (FormatException)
        {
            error = "Invalid activation code encoding.";
            return false;
        }

        bool valid;
        try
        {
            using var ecdsa = ECDsa.Create();
            ecdsa.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKeyBase64), out _);
            valid = ecdsa.VerifyData(
                payloadBytes,
                signature,
                HashAlgorithmName.SHA256,
                DSASignatureFormat.Rfc3279DerSequence);
        }
        catch (Exception)
        {
            error = "Invalid activation public key.";
            return false;
        }

        if (!valid)
        {
            error = "Invalid activation code signature.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadBytes);
            var root = document.RootElement;

            var codeServerId = root.TryGetProperty("sid", out var sidElement)
                ? sidElement.GetString() ?? string.Empty
                : string.Empty;

            var name = root.TryGetProperty("name", out var nameElement)
                ? nameElement.GetString() ?? string.Empty
                : string.Empty;

            var expires = 0L;
            if (root.TryGetProperty("exp", out var expElement) && !expElement.TryGetInt64(out expires))
            {
                expires = 0;
            }

            if (!string.Equals(codeServerId, "*", StringComparison.Ordinal)
                && !string.Equals(codeServerId, serverId, StringComparison.OrdinalIgnoreCase))
            {
                error = "This activation code is for a different server.";
                return false;
            }

            DateTimeOffset? expiry = null;
            if (expires > 0)
            {
                expiry = DateTimeOffset.FromUnixTimeSeconds(expires);
                if (expiry.Value <= DateTimeOffset.UtcNow)
                {
                    error = "This activation code has expired.";
                    return false;
                }
            }

            info = new ActivationInfo(codeServerId, name, expiry);
            return true;
        }
        catch (Exception)
        {
            error = "Invalid activation code payload.";
            return false;
        }
    }
}
