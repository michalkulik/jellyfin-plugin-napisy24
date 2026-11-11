using System;
using System.Collections.Generic;
using Jellyfin.Plugin.Napisy24.Configuration;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;

namespace Jellyfin.Plugin.Napisy24;

/// <summary>
/// Napisy24.pl subtitle provider plugin.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>
    /// The plugin id. Must never change, otherwise updates are installed side by side.
    /// </summary>
    public static readonly Guid PluginId = Guid.Parse("1e3057d0-c2e0-4bbc-a67b-5fa31cdbebd9");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
    }

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Name => "Napisy24";

    /// <inheritdoc />
    public override string Description => "Polish subtitles from napisy24.pl (hash + metadata search)";

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        var ns = GetType().Namespace;
        return new[]
        {
            new PluginPageInfo
            {
                Name = "napisy24",
                EmbeddedResourcePath = ns + ".Web.config.html",
            },
            new PluginPageInfo
            {
                Name = "napisy24js",
                EmbeddedResourcePath = ns + ".Web.config.js",
            },
        };
    }
}
