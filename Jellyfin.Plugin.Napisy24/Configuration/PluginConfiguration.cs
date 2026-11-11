using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Napisy24.Configuration;

/// <summary>
/// Plugin configuration.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>
    /// Gets or sets the signed activation code. The plugin does nothing until
    /// a valid code for this server is configured.
    /// </summary>
    public string ActivationCode { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the napisy24 base URL.
    /// </summary>
    public string BaseUrl { get; set; } = "https://napisy24.pl";

    /// <summary>
    /// Gets or sets a value indicating whether hash lookup is enabled.
    /// </summary>
    public bool EnableHashLookup { get; set; } = true;

    /// <summary>
    /// Gets or sets the agent user name used by CheckSubAgent.php.
    /// </summary>
    public string AgentUsername { get; set; } = "subliminal";

    /// <summary>
    /// Gets or sets the agent password used by CheckSubAgent.php.
    /// </summary>
    public string AgentPassword { get; set; } = "lanimilbus";

    /// <summary>
    /// Gets or sets a value indicating whether metadata search (webapi.php) is enabled.
    /// </summary>
    public bool EnableMetadataSearch { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether automated Jellyfin tasks may use
    /// metadata search. Hash lookup is always allowed for automated tasks.
    /// </summary>
    public bool EnableAutomaticMetadataSearch { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the napiprojekt MD5 hash is sent
    /// as a fallback when the OSH hash found nothing.
    /// </summary>
    public bool UseMd5Fallback { get; set; }

    /// <summary>
    /// Gets or sets the maximum number of search results returned to Jellyfin.
    /// </summary>
    public int MaxResults { get; set; } = 10;

    /// <summary>
    /// Gets or sets the minimum delay between requests to napisy24, in milliseconds.
    /// </summary>
    public int RequestDelayMs { get; set; } = 750;

    /// <summary>
    /// Gets or sets a value indicating whether multi-CD subtitles are merged into one SRT.
    /// </summary>
    public bool MergeMultiCd { get; set; } = true;

    /// <summary>
    /// Gets or sets the score threshold used for automated metadata downloads.
    /// </summary>
    public int AutoScoreThreshold { get; set; } = 60;
}
