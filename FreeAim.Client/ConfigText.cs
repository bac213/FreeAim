using BepInEx.Configuration;

namespace FreeAim.Client;

/// <summary>
/// Display overrides read by BepInEx ConfigurationManager (F12) through reflection: the class name and the
/// public field names must match. Only the display changes; config sections and keys stay English, so saved
/// values in the .cfg keep working.
/// </summary>
internal sealed class ConfigurationManagerAttributes
{
    public string Category;
    public string DispName;
    public int? Order;
    public bool? IsAdvanced;
}

/// <summary>Binds config entries with a Chinese display name, category and description (F12 menu).</summary>
internal sealed class ConfigText
{
    private readonly ConfigFile _config;
    private int _order = 10000;

    /// <summary>Entries bound while this is set only show in F12 with "Advanced settings" ticked.</summary>
    internal bool Advanced { get; set; }

    internal ConfigText(ConfigFile config)
    {
        _config = config;
    }

    /// <param name="section">English section (stored in the .cfg).</param>
    /// <param name="key">English key (stored in the .cfg).</param>
    /// <param name="category">Chinese category shown in F12.</param>
    /// <param name="name">Chinese name shown in F12.</param>
    /// <param name="description">Chinese description (F12 tooltip and .cfg comment).</param>
    internal ConfigEntry<T> Bind<T>(string section, string key, T defaultValue, string category, string name,
                                    string description, AcceptableValueBase range = null)
    {
        var tags = new ConfigurationManagerAttributes
        {
            Category = category,
            DispName = name,
            Order = _order--, // keep the code order inside each category (F12 sorts by Order descending)
            IsAdvanced = Advanced ? true : null,
        };
        return _config.Bind(section, key, defaultValue, new ConfigDescription(description, range, tags));
    }

    internal static AcceptableValueRange<float> Range(float min, float max) => new(min, max);
}
