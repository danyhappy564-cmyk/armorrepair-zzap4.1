using SPTarkov.Common.Models.Logging;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Services.Modding.Custom;

namespace MiniArmorRepairKit;

/// <summary>
/// Adds the Field Repair Kit: a small, heavy repair kit that fits a special slot, so armour
/// can be repaired mid-raid without giving up a whole container slot for a full-size kit.
///
/// <para>Registered late (after trader registration) so the cloned item exists before
/// anything that reads the item database for assorts.</para>
/// </summary>
[Injectable(TypePriority = OnLoadOrder.TraderRegistration + 1)]
public class MiniArmorRepairKitMod(
    CustomItemService customItemService,
    TemplateTable templates,
    ISptLogger<MiniArmorRepairKitMod> logger) : IOnLoad
{
    /// <summary>The stock repair kit this is cloned from.</summary>
    private static readonly MongoId SourceItemId = new("591094e086f7747caa7bb2ef");

    /// <summary>The Field Repair Kit's own template id.</summary>
    private static readonly MongoId NewItemId = new("6a7c23c47e28a1c59d51f94c");

    /// <summary>The default inventory template that carries the three special slots.</summary>
    private static readonly MongoId DefaultInventoryId = new("55d7217a4bdc2d86028b456d");

    /// <summary>Handbook: Barter items → Tools.</summary>
    private const string HandbookToolsCategory = "5b5f704686f77447ec5d76d7";

    public Task OnLoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            if (templates.Items.ContainsKey(NewItemId))
            {
                // Another copy of the mod, or a rerun. Adding it twice would fail noisily.
                logger.Info("[MiniArmorRepairKit] Field Repair Kit already registered, skipping");
                return Task.CompletedTask;
            }

            var result = customItemService.CreateItemFromClone(new NewItemFromCloneDetails
            {
                ItemTplToClone = SourceItemId,
                NewId = NewItemId,
                ParentId = templates.Items.TryGetValue(SourceItemId, out var source)
                    ? source.Parent
                    : new MongoId("57864e4c24597754843f8723"), // Barter item, if the source is missing
                NewItemName = "field_repair_kit",
                HandbookParentId = HandbookToolsCategory,
                HandbookPriceRoubles = 42000,
                FleaPriceRoubles = 42000,
                OverrideProperties = new TemplateItemProperties
                {
                    // One inventory cell, and heavy enough that carrying it costs something.
                    Width = 1,
                    Height = 1,
                    Weight = 5,
                },
                Locales = new Dictionary<string, LocaleDetails>
                {
                    ["en"] = new()
                    {
                        Name = "Field Repair Kit",
                        ShortName = "FRK",
                        Description = "A stripped-down repair kit that fits a special slot. Repairs armour in raid, at the cost of its maximum durability.",
                    },
                    ["ko"] = new()
                    {
                        Name = "야전 수리 키트",
                        ShortName = "야전킷",
                        Description = "특수 슬롯에 들어가는 소형 수리 키트. 레이드 중에 방어구를 수리할 수 있지만 최대 내구도가 함께 깎인다.",
                    },
                },
            });

            if (!result.Success)
            {
                logger.Error($"[MiniArmorRepairKit] could not create the Field Repair Kit: {string.Join("; ", result.Errors)}");
                return Task.CompletedTask;
            }

            int slots = AllowInSpecialSlots();
            logger.Success($"[MiniArmorRepairKit] Field Repair Kit registered ({NewItemId}), allowed in {slots} special slot(s)");
        }
        catch (Exception ex)
        {
            // A failure here must not stop the server booting; the client plugin still works
            // with vanilla repair kits.
            logger.Error($"[MiniArmorRepairKit] failed to load: {ex}");
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Special slots only accept what their filter lists, so the new tpl has to be added to
    /// each one. Walking the slots by name rather than by index keeps this working if BSG
    /// adds or reorders them.
    /// </summary>
    private int AllowInSpecialSlots()
    {
        if (!templates.Items.TryGetValue(DefaultInventoryId, out var inventory) || inventory.Properties?.Slots is null)
        {
            logger.Warning("[MiniArmorRepairKit] default inventory template has no slots; the kit will not fit a special slot");
            return 0;
        }

        var updated = 0;

        foreach (var slot in inventory.Properties.Slots)
        {
            if (slot.Name is null || !slot.Name.StartsWith("SpecialSlot", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var filter in slot.Properties?.Filters ?? [])
            {
                filter.Filter ??= [];
                if (filter.Filter.Add(NewItemId))
                {
                    updated++;
                }
            }
        }

        return updated;
    }
}
