using SPTarkov.Server.Core.Models.Spt.Mod;

namespace MiniArmorRepairKit;

/// <summary>
/// 4.1 replaced the abstract <c>AbstractModMetadata</c> base with the <c>IModMetadata</c>
/// interface and added <c>HasPrepatcher</c>.
/// </summary>
public record ModMetadata : IModMetadata
{
    public string ModGuid { get; init; } = "com.pineapplelover.miniarmorrepairkit";
    public string Name { get; init; } = "MiniArmorRepairKit";
    public string Author { get; init; } = "PineappleLover";
    public SemanticVersioning.Version Version { get; init; } = new("1.2.0");
    public SemanticVersioning.Range SptVersion { get; init; } = new("~4.1.5");
    public string License { get; init; } = "MIT";
    public bool HasPrepatcher { get; init; }

    public List<string>? Contributors { get; init; }
    public List<string>? Incompatibilities { get; init; }
    public Dictionary<string, SemanticVersioning.Range>? ModDependencies { get; init; }
    public string? Url { get; init; }
}
