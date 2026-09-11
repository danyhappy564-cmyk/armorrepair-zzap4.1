using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using EFT;
using EFT.Communications;
using EFT.InventoryLogic;

namespace RaidArmorRepair
{
    /// <summary>
    /// All of the actual repairing. Kept static because there is exactly one repair session
    /// at a time and the Harmony trigger patch needs to read its state.
    /// </summary>
    public static class RepairService
    {
        /// <summary>Intellect is capped at 51 in EFT; the bonus scales linearly to that.</summary>
        private const float MaxIntellectSkillLevel = 51f;

        /// <summary>Where worn armour can be. Plates inside these are handled too.</summary>
        private static readonly EquipmentSlot[] ArmorSlots =
        {
            EquipmentSlot.ArmorVest,
            EquipmentSlot.TacticalVest,
            EquipmentSlot.Headwear,
        };

        /// <summary>Where a repair kit may be carried. Searched recursively.</summary>
        private static readonly EquipmentSlot[] ContainerSlotIds =
        {
            EquipmentSlot.Backpack,
            EquipmentSlot.TacticalVest,
            EquipmentSlot.Pockets,
            EquipmentSlot.SecuredContainer,
        };

        /// <summary>
        /// Repair efficiency per armour material — the same numbers EFT uses out of raid.
        /// Higher is easier to repair, and it feeds the max-durability loss: ceramic and
        /// glass wear out fast, steel and UHMWPE barely at all.
        /// </summary>
        private static readonly Dictionary<string, float> ArmorMaterialEfficiency =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                { "Aramid", 1f },
                { "UHMWPE", 3f },
                { "Combined", 0.4f },
                { "CombinedMaterials", 0.4f },
                { "Titan", 0.63f },
                { "Titanium", 0.63f },
                { "Aluminium", 0.63f },
                { "Aluminum", 0.63f },
                { "ArmoredSteel", 3f },
                { "Armor_steel", 3f },
                { "Steel", 3f },
                { "Ceramic", 0.26f },
                { "Glass", 0.15f },
            };

        public static bool IsRepairing;
        public static float SessionKitResourceUsed;
        public static float SessionDurabilityRepaired;

        private static bool _loggedMaterialFallback;

        public static void NotifyRepairStart(Player player)
        {
            SessionKitResourceUsed = 0f;
            SessionDurabilityRepaired = 0f;
            Notify(player, $"방어구 수리를 시작합니다. 단축키를 누르고 있으면 {Plugin.RepairTickInterval.Value:0.#}초마다 수리됩니다.");
        }

        public static void NotifySessionEnd(Player player)
        {
            if (SessionKitResourceUsed <= 0f && SessionDurabilityRepaired <= 0f)
            {
                return;
            }

            Notify(player, $"방어구 수리 종료: 내구도 +{SessionDurabilityRepaired:0.#} 회복, 수리킷 {SessionKitResourceUsed:0.#} 소모");
            SessionKitResourceUsed = 0f;
            SessionDurabilityRepaired = 0f;
        }

        // ---- skill ---------------------------------------------------------

        private static float GetIntellectMultiplier(Player player) =>
            1f + Normalised(GetIntellectLevel(player)) * Plugin.IntellectMaxBonusMultiplier.Value;

        private static float Normalised(float intellectLevel) =>
            Math.Max(0f, Math.Min(1f, intellectLevel / MaxIntellectSkillLevel));

        private static float GetIntellectLevel(Player player)
        {
            try
            {
                // Skill was SkillClass before 4.1 deobfuscated the client.
                Skill intellect = player?.Profile?.Skills?.Intellect;
                return intellect == null ? 0f : intellect.Level;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[RaidArmorRepair] 지력 스킬 조회 실패, 레벨 0으로 처리: " + ex.Message);
                return 0f;
            }
        }

        /// <summary>
        /// How much max durability one repair costs, as a percent. Worst case is intellect 0
        /// on a low-efficiency material; the loss shrinks with both intellect and material
        /// efficiency, and never drops below the configured best case.
        /// </summary>
        private static float GetMaxDurabilityLossPercent(float intellectLevel, float materialEfficiency)
        {
            if (materialEfficiency <= 0f)
            {
                materialEfficiency = 0.01f;
            }

            float distanceFromMaxIntellect = 1f - Normalised(intellectLevel);
            float best = Plugin.MaxDurabilityLossAtBestCase.Value;
            float worst = Plugin.MaxDurabilityLossAtWorstCase.Value;

            float loss = best + (worst - best)
                * (Plugin.WorstCaseReferenceEfficiency.Value / materialEfficiency)
                * distanceFromMaxIntellect;

            return Math.Max(0f, Math.Min(100f, loss));
        }

        /// <summary>
        /// Reads the armour's material off its template. The property name has moved between
        /// EFT builds, so both known spellings are tried by reflection and an unknown value
        /// falls back to the configured default rather than throwing.
        /// </summary>
        private static float GetArmorMaterialEfficiency(ArmorComponent armorComponent)
        {
            string[] candidates = { "ArmorMaterial", "Material" };

            try
            {
                object template = armorComponent?.Item?.Template;
                if (template != null)
                {
                    Type type = template.GetType();
                    foreach (string name in candidates)
                    {
                        object value = type.GetProperty(name)?.GetValue(template)
                                       ?? type.GetField(name)?.GetValue(template);
                        if (value == null)
                        {
                            continue;
                        }

                        string material = value.ToString();
                        if (ArmorMaterialEfficiency.TryGetValue(material, out float efficiency))
                        {
                            return efficiency;
                        }

                        Plugin.Log.LogWarning($"[RaidArmorRepair] 표에 없는 방어구 재질 '{material}', 기본 효율({Plugin.DefaultArmorMaterialEfficiency.Value})을 사용합니다.");
                        return Plugin.DefaultArmorMaterialEfficiency.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[RaidArmorRepair] 방어구 재질 조회 실패: " + ex.Message);
            }

            // Once per session: repeating this for every tick on every plate would flood the log.
            if (!_loggedMaterialFallback)
            {
                _loggedMaterialFallback = true;
                Plugin.Log.LogWarning($"[RaidArmorRepair] 방어구의 재질 필드를 찾지 못해 기본 효율({Plugin.DefaultArmorMaterialEfficiency.Value})을 사용합니다.");
            }

            return Plugin.DefaultArmorMaterialEfficiency.Value;
        }

        // ---- preview -------------------------------------------------------

        /// <summary>Works out what the next tick would do, without changing anything.</summary>
        public static RepairPreview Peek(Player player)
        {
            var preview = default(RepairPreview);
            InventoryEquipment equipment = player.Inventory.Equipment;

            if (!TryFindDamagedArmorInEquipment(equipment, out Item targetArmor, out ArmorComponent armorComponent, false))
            {
                return preview;
            }

            preview.HasTarget = true;
            preview.TargetArmor = targetArmor;
            preview.TargetComponent = armorComponent;

            RepairableComponent repairable = armorComponent.Repairable;
            preview.MaxDurability = repairable.MaxDurability;
            preview.CurrentDurability = repairable.Durability;
            preview.MissingDurability = preview.MaxDurability - preview.CurrentDurability;

            if (!TryFindRepairKit(equipment, out Item kitItem, out RepairKitComponent kitComponent, false))
            {
                return preview;
            }

            preview.HasKit = true;
            preview.KitItem = kitItem;
            preview.KitComponent = kitComponent;

            float intellectLevel = GetIntellectLevel(player);
            float intellectMultiplier = 1f + Normalised(intellectLevel) * Plugin.IntellectMaxBonusMultiplier.Value;

            float healPerTick = preview.MaxDurability * Plugin.RepairPercentPerUse.Value * intellectMultiplier;
            float kitCost = Plugin.KitResourceCostPerUse.Value;
            float durabilityPerResource = kitCost > 0f ? healPerTick / kitCost : 0f;
            float healTheKitCanAfford = durabilityPerResource > 0f ? kitComponent.Resource * durabilityPerResource : 0f;

            float healIgnoringKit = Math.Max(0f, Math.Min(healPerTick, preview.MissingDurability));
            preview.ExpectedHeal = Math.Max(0f, Math.Min(healPerTick, Math.Min(preview.MissingDurability, healTheKitCanAfford)));
            preview.LimitedByKit = preview.ExpectedHeal < healIgnoringKit - 0.01f;

            if (Plugin.EnableMaxDurabilityDegradation.Value && preview.ExpectedHeal > 0f)
            {
                preview.ExpectedMaxDurabilityLossPercent =
                    GetMaxDurabilityLossPercent(intellectLevel, GetArmorMaterialEfficiency(armorComponent));
            }

            return preview;
        }

        /// <summary>
        /// The objectives-panel label. The trailing "{0:F1}" is a placeholder the game fills
        /// with the remaining countdown, so it has to survive into the returned string.
        /// </summary>
        public static string BuildProgressLabel(RepairPreview preview)
        {
            if (!preview.HasTarget)
            {
                return "수리할 방어구 없음 {0:F1}";
            }

            if (!preview.HasKit)
            {
                return "수리킷 없음 {0:F1}";
            }

            float projected = preview.CurrentDurability + preview.ExpectedHeal;
            float percent = preview.MaxDurability > 0f ? Math.Min(100f, projected / preview.MaxDurability * 100f) : 0f;
            string kitNote = preview.LimitedByKit ? " · 키트 부족" : "";
            string wearNote = preview.ExpectedMaxDurabilityLossPercent > 0.05f
                ? $" · 최대내구도 -{preview.ExpectedMaxDurabilityLossPercent:0.#}%"
                : "";

            return $"방어구 수리 중 (예상 {percent:0}%{kitNote}{wearNote}) " + "{0:F1}s";
        }

        // ---- the tick ------------------------------------------------------

        /// <summary>Applies one repair tick. Returns false when the session should stop —
        /// nothing damaged left, or nothing to repair it with.</summary>
        /// <remarks>
        /// Everything below used to run bare. If any of it threw, the exception unwound straight
        /// out through Plugin.Update() — Unity logs it once and keeps the MonoBehaviour alive,
        /// but that Update() call stops dead at the throw point. Neither the success branch
        /// (ShowProgressPanel) nor the failure branch (ResetRepairState) in the caller ever runs,
        /// so nothing closes the panel and nothing tells the player anything went wrong. The
        /// progress panel is a vanilla EFT.UI.BattleUIPanelExtraction; its own Show(text, duration)
        /// starts a coroutine that calls Close() by itself once `duration` elapses (confirmed by
        /// decompiling both the 4.0 and 4.1 client — not a porting regression, just how it always
        /// worked). With nobody refreshing it, that is exactly what a silent exception here looks
        /// like from the player's side: the "repair starting" notification fires, the panel shows,
        /// and a few seconds later the panel closes on its own timer with no heal ever applied and
        /// no error notification — because the two explicit failure notifies below never had a
        /// chance to fire.
        /// </remarks>
        public static bool TryRepairArmor(Player player)
        {
            try
            {
                return TryRepairArmorInternal(player);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError("[RaidArmorRepair] 수리 틱 처리 중 예외 발생: " + ex);
                Notify(player, "방어구 수리 중 오류가 발생해 이번 시도는 취소되었습니다. (로그 확인 필요)");
                return false;
            }
        }

        private static bool TryRepairArmorInternal(Player player)
        {
            InventoryEquipment equipment = player.Inventory.Equipment;

            if (!TryFindDamagedArmorInEquipment(equipment, out Item armorItem, out ArmorComponent armorComponent, true))
            {
                Notify(player, "수리할 방어구가 없거나 이미 최대 내구도입니다.");
                return false;
            }

            if (!TryFindRepairKit(equipment, out _, out RepairKitComponent kitComponent, true))
            {
                Notify(player, "인벤토리에 사용 가능한 방어구 수리 키트가 없습니다.");
                return false;
            }

            RepairableComponent repairable = armorComponent.Repairable;
            float maxDurability = repairable.MaxDurability;
            float intellectMultiplier = GetIntellectMultiplier(player);

            float missing = maxDurability - repairable.Durability;
            float healPerTick = maxDurability * Plugin.RepairPercentPerUse.Value * intellectMultiplier;
            float kitCost = Plugin.KitResourceCostPerUse.Value;
            float durabilityPerResource = kitCost > 0f ? healPerTick / kitCost : 0f;
            float healTheKitCanAfford = durabilityPerResource > 0f ? kitComponent.Resource * durabilityPerResource : 0f;

            // Whichever runs out first: the tick budget, the damage, or the kit.
            float heal = Math.Min(healPerTick, Math.Min(missing, healTheKitCanAfford));
            if (heal < 0f)
            {
                heal = 0f;
            }

            // Charge for exactly what was healed, not for a full tick.
            float resourceUsed = durabilityPerResource > 0f ? heal / durabilityPerResource : 0f;

            repairable.Durability += heal;
            kitComponent.Resource -= resourceUsed;

            float lossPercent = 0f;
            if (Plugin.EnableMaxDurabilityDegradation.Value && heal > 0f)
            {
                lossPercent = GetMaxDurabilityLossPercent(GetIntellectLevel(player), GetArmorMaterialEfficiency(armorComponent));
                float lossPoints = maxDurability * (lossPercent / 100f);
                if (lossPoints > 0f)
                {
                    // Never below 1, and current durability follows the ceiling down.
                    float newMax = Math.Max(1f, maxDurability - lossPoints);
                    if (repairable.Durability > newMax)
                    {
                        repairable.Durability = newMax;
                    }

                    repairable.MaxDurability = newMax;
                }
            }

            SessionKitResourceUsed += resourceUsed;
            SessionDurabilityRepaired += heal;

            // Durability/resource are already updated above regardless of what happens here — this
            // is only telling the UI to redraw. armorItem can be a plate nested inside the vest's
            // ArmorHolderComponent rather than a top-level equipped item; raising the event on a
            // nested item is the one part of this tick that has ever been iffy across EFT builds,
            // so a stale tooltip is an acceptable failure mode here, a lost repair is not.
            try
            {
                armorItem.RaiseRefreshEvent(false, false);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("[RaidArmorRepair] RaiseRefreshEvent 실패 (내구도는 이미 적용됨, UI만 안 갱신될 수 있음): " + ex.Message);
            }

            Plugin.Log.LogInfo($"[RaidArmorRepair] 틱 적용: +{heal:0.#} 내구도, 수리킷 -{resourceUsed:0.#}, 지력 보너스 x{intellectMultiplier:0.00}, 최대내구도 -{lossPercent:0.#}%");

            return HasAnyDamagedArmor(equipment);
        }

        // ---- lookups -------------------------------------------------------

        private static bool HasAnyDamagedArmor(InventoryEquipment equipment) =>
            TryFindDamagedArmorInEquipment(equipment, out _, out _, false);

        private static bool TryFindDamagedArmorInEquipment(
            InventoryEquipment equipment, out Item targetItem, out ArmorComponent targetComponent, bool verboseLog)
        {
            foreach (EquipmentSlot slotId in ArmorSlots)
            {
                Item item = equipment.GetSlot(slotId)?.ContainedItem;
                if (item == null)
                {
                    continue;
                }

                if (verboseLog)
                {
                    Plugin.Log.LogInfo($"[RaidArmorRepair] 슬롯 {slotId}: {item.TemplateId} 확인 중...");
                }

                if (TryFindDamagedArmor(item, out targetItem, out targetComponent))
                {
                    return true;
                }
            }

            targetItem = null;
            targetComponent = null;
            return false;
        }

        /// <summary>The item itself if it is damaged armour, otherwise the first damaged
        /// plate inside it.</summary>
        private static bool TryFindDamagedArmor(Item equippedItem, out Item targetItem, out ArmorComponent targetComponent)
        {
            ArmorComponent armor = equippedItem.GetItemComponent<ArmorComponent>();
            if (armor != null && IsDamaged(armor))
            {
                targetItem = equippedItem;
                targetComponent = armor;
                return true;
            }

            ArmorHolderComponent holder = equippedItem.GetItemComponent<ArmorHolderComponent>();
            if (holder != null)
            {
                // ArmorPlate was ArmorPlateItemClass before 4.1 deobfuscated the client.
                foreach (ArmorPlate plate in holder.ArmorPlates)
                {
                    ArmorComponent plateArmor = plate.GetItemComponent<ArmorComponent>();
                    if (plateArmor != null && IsDamaged(plateArmor))
                    {
                        targetItem = plate;
                        targetComponent = plateArmor;
                        return true;
                    }
                }
            }

            targetItem = null;
            targetComponent = null;
            return false;
        }

        /// <summary>The 0.01 slack keeps float noise from making a full-durability plate
        /// look repairable forever.</summary>
        private static bool IsDamaged(ArmorComponent armor) =>
            armor.Repairable.Durability < armor.Repairable.MaxDurability - 0.01f;

        private static bool TryFindRepairKit(
            InventoryEquipment equipment, out Item repairKitItem, out RepairKitComponent kitComponent, bool verboseLog)
        {
            var carried = new List<Item>();

            foreach (EquipmentSlot slotId in ContainerSlotIds)
            {
                Item container = equipment.GetSlot(slotId)?.ContainedItem;
                if (container == null)
                {
                    continue;
                }

                if (verboseLog)
                {
                    Plugin.Log.LogInfo($"[RaidArmorRepair] 컨테이너 슬롯 확인: {slotId} -> {container.TemplateId}");
                }

                CollectAllContainedItems(container, carried);
            }

            if (verboseLog)
            {
                Plugin.Log.LogInfo($"[RaidArmorRepair] 컨테이너 내부에서 총 {carried.Count}개 아이템 발견.");
                foreach (Item item in carried)
                {
                    RepairKitComponent kit = item.GetItemComponent<RepairKitComponent>();
                    if (kit != null)
                    {
                        Plugin.Log.LogInfo($"[RaidArmorRepair]   - 수리킷 발견: {item.TemplateId}, 자원 {kit.Resource}");
                    }
                }
            }

            repairKitItem = carried.FirstOrDefault(item =>
            {
                RepairKitComponent kit = item.GetItemComponent<RepairKitComponent>();
                return kit != null && kit.Resource > 0f;
            });

            kitComponent = repairKitItem?.GetItemComponent<RepairKitComponent>();
            return repairKitItem != null;
        }

        /// <summary>Everything inside a container, at any depth — a kit in a case in a
        /// backpack still counts.</summary>
        private static void CollectAllContainedItems(Item item, List<Item> results)
        {
            if (!(item is CompoundItem compound))
            {
                return;
            }

            foreach (IContainer container in compound.Containers)
            {
                foreach (Item contained in container.Items)
                {
                    if (contained == null)
                    {
                        continue;
                    }

                    results.Add(contained);
                    CollectAllContainedItems(contained, results);
                }
            }
        }

        public static void Notify(Player player, string message)
        {
            if (!Plugin.ShowNotifications.Value)
            {
                return;
            }

            Plugin.Log.LogInfo(message);

            try
            {
                // NotificationManagerClass before 4.1 deobfuscated the client.
                NotificationManager.DisplayMessageNotification(message, 0, 0, null);
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning("알림 표시 실패: " + ex.Message);
            }
        }
    }
}
