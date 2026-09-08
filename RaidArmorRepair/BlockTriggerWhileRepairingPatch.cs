using EFT;
using HarmonyLib;

namespace RaidArmorRepair
{
    /// <summary>You cannot shoot while repairing. Swallowing only the "pressed" edge means
    /// releasing the trigger still goes through, so the weapon cannot be left stuck firing
    /// if a repair starts mid-burst.</summary>
    [HarmonyPatch(typeof(Player.FirearmController), nameof(Player.FirearmController.SetTriggerPressed))]
    internal static class BlockTriggerWhileRepairingPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(bool pressed) => !RepairService.IsRepairing || !pressed;
    }
}
