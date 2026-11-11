using System;
using Jellyfin.Plugin.Napisy24.Configuration;
using MediaBrowser.Controller;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Napisy24.Activation;

/// <summary>
/// Current activation state of the plugin.
/// </summary>
public sealed record ActivationStatus(
    bool IsActivated,
    string ServerId,
    string? Name,
    DateTimeOffset? Expires,
    string? Error);

/// <summary>
/// Validates the configured activation code against this Jellyfin server.
/// </summary>
public sealed class ActivationService
{
    // Public half of the activation key. The private key is kept outside the
    // repository and is only used by tools/make-activation-code.py.
    public const string PublicKeyBase64 =
        "MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEv8pZAGARpMpgDPDpvonS2fV8SfSLvAnyyQ/K5qOFOSUSBN7iqT3aYj12RiRzydZ5n/FjTpBvTfg47VcZ7q+OVQ==";

    private readonly IServerApplicationHost _applicationHost;
    private readonly ILogger<ActivationService> _logger;
    private readonly object _lock = new();
    private string? _cachedCode;
    private ActivationStatus? _cachedStatus;

    /// <summary>
    /// Initializes a new instance of the <see cref="ActivationService"/> class.
    /// </summary>
    public ActivationService(IServerApplicationHost applicationHost, ILogger<ActivationService> logger)
    {
        _applicationHost = applicationHost;
        _logger = logger;
    }

    /// <summary>
    /// Gets the Jellyfin server id the activation codes are bound to.
    /// </summary>
    public string ServerId => _applicationHost.SystemId;

    /// <summary>
    /// Gets the current activation status.
    /// </summary>
    public ActivationStatus GetStatus()
    {
        var code = Plugin.Instance?.Configuration?.ActivationCode;

        lock (_lock)
        {
            if (string.Equals(code, _cachedCode, StringComparison.Ordinal) && _cachedStatus is not null)
            {
                return _cachedStatus;
            }

            var status = Evaluate(code);
            _cachedCode = code;
            _cachedStatus = status;
            return status;
        }
    }

    /// <summary>
    /// Validates and stores an activation code.
    /// </summary>
    public ActivationStatus Activate(string? code)
    {
        var status = Evaluate(code);
        if (!status.IsActivated)
        {
            return status;
        }

        var plugin = Plugin.Instance
            ?? throw new InvalidOperationException("The Napisy24 plugin is not initialized.");

        var configuration = plugin.Configuration;
        configuration.ActivationCode = code!.Trim();
        plugin.UpdateConfiguration(configuration);

        lock (_lock)
        {
            _cachedCode = configuration.ActivationCode;
            _cachedStatus = status;
        }

        _logger.LogInformation("Napisy24 activated for server {ServerId}", ServerId);
        return status;
    }

    /// <summary>
    /// Removes the stored activation code.
    /// </summary>
    public ActivationStatus Deactivate()
    {
        var plugin = Plugin.Instance
            ?? throw new InvalidOperationException("The Napisy24 plugin is not initialized.");

        var configuration = plugin.Configuration;
        configuration.ActivationCode = string.Empty;
        plugin.UpdateConfiguration(configuration);

        var status = new ActivationStatus(false, ServerId, null, null, "Not activated.");
        lock (_lock)
        {
            _cachedCode = string.Empty;
            _cachedStatus = status;
        }

        _logger.LogInformation("Napisy24 deactivated for server {ServerId}", ServerId);
        return status;
    }

    private ActivationStatus Evaluate(string? code)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            return new ActivationStatus(false, ServerId, null, null, "Enter an activation code.");
        }

        if (ActivationCode.TryVerify(code, ServerId, PublicKeyBase64, out var info, out var error))
        {
            return new ActivationStatus(true, ServerId, info!.Name, info.Expires, null);
        }

        return new ActivationStatus(false, ServerId, null, null, error);
    }
}
