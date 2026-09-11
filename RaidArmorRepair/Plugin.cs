using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using EFT;
using HarmonyLib;
using UnityEngine;

namespace RaidArmorRepair
{
    [BepInPlugin("com.pineapplelover.raidarmorrepair", "Raid Armor Repair", "1.2.0")]
    public class Plugin : BaseUnityPlugin
    {
        /// <summary>The objectives panel needs a GamePlayerOwner. It is not always up the
        /// moment the raid starts, so it is retried a few times before giving up — repairs
        /// still work without it, only the panel is lost.</summary>
        private const float OwnerRetryInterval = 1f;
        private const int OwnerRetryMax = 15;

        public static ManualLogSource Log;

        public static ConfigEntry<KeyboardShortcut> RepairKey;
        public static ConfigEntry<float> RepairPercentPerUse;
        public static ConfigEntry<float> KitResourceCostPerUse;
        public static ConfigEntry<float> RepairTickInterval;
        public static ConfigEntry<float> IntellectMaxBonusMultiplier;
        public static ConfigEntry<bool> ShowNotifications;
        public static ConfigEntry<bool> EnableMaxDurabilityDegradation;
        public static ConfigEntry<float> MaxDurabilityLossAtWorstCase;
        public static ConfigEntry<float> MaxDurabilityLossAtBestCase;
        public static ConfigEntry<float> WorstCaseReferenceEfficiency;
        public static ConfigEntry<float> DefaultArmorMaterialEfficiency;

        private float _repairTimer;
        private Player _lastKnownPlayer;
        private GamePlayerOwner _owner;
        private bool _panelShown;
        private float _ownerRetryTimer;
        private int _ownerRetryCount;

        private void Awake()
        {
            Log = Logger;

            RepairKey = Config.Bind("General", "RepairKey",
                new KeyboardShortcut(KeyCode.J),
                "레이드 중 방어구를 수리할 단축키 (기본: J). 누르고 있는 동안 계속 수리됩니다.");

            RepairPercentPerUse = Config.Bind("General", "RepairPercentPerUse", 0.3f,
                new ConfigDescription("1틱당 회복되는 최대내구도 대비 비율 (지력 보너스 적용 전 기본값)",
                    new AcceptableValueRange<float>(0.05f, 1f)));

            KitResourceCostPerUse = Config.Bind("General", "KitResourceCostPerUse", 20f,
                "1틱당 소모되는 수리킷 자원(Resource) 수치");

            RepairTickInterval = Config.Bind("General", "RepairTickInterval", 5f,
                new ConfigDescription("단축키를 누르고 있을 때 몇 초마다 한 번씩 수리할지",
                    new AcceptableValueRange<float>(1f, 30f)));

            IntellectMaxBonusMultiplier = Config.Bind("General", "IntellectMaxBonusMultiplier", 0.5f,
                new ConfigDescription("지력(Intellect) 스킬이 최대일 때 1틱 회복량에 추가되는 배율 (0.5 = 최대 +50%)",
                    new AcceptableValueRange<float>(0f, 2f)));

            ShowNotifications = Config.Bind("General", "ShowNotifications", true,
                "수리 시작/종료시 화면 알림 표시 여부");

            EnableMaxDurabilityDegradation = Config.Bind("Degradation", "EnableMaxDurabilityDegradation", true,
                "수리할 때마다 최대내구도가 함께 감소하는 마모 효과를 사용할지 여부");

            MaxDurabilityLossAtWorstCase = Config.Bind("Degradation", "MaxDurabilityLossAtWorstCase", 40f,
                new ConfigDescription("지력 0 · 수리 효율이 WorstCaseReferenceEfficiency일 때의 최대내구도 감소율(%)",
                    new AcceptableValueRange<float>(0f, 100f)));

            MaxDurabilityLossAtBestCase = Config.Bind("Degradation", "MaxDurabilityLossAtBestCase", 5f,
                new ConfigDescription("지력이 최대(51)일 때 남는 최소 감소율(%). 재질/효율과 무관하게 이 값 밑으로는 내려가지 않습니다",
                    new AcceptableValueRange<float>(0f, 100f)));

            WorstCaseReferenceEfficiency = Config.Bind("Degradation", "WorstCaseReferenceEfficiency", 0.26f,
                new ConfigDescription("MaxDurabilityLossAtWorstCase 감소율의 기준이 되는 수리 효율 값",
                    new AcceptableValueRange<float>(0.01f, 10f)));

            DefaultArmorMaterialEfficiency = Config.Bind("Degradation", "DefaultArmorMaterialEfficiency", 1f,
                new ConfigDescription("방어구에서 재질을 읽지 못하거나 표에 없는 재질일 때 사용할 대체 수리 효율값",
                    new AcceptableValueRange<float>(0.01f, 10f)));

            Log.LogInfo("Raid Armor Repair loaded.");

            new Harmony("com.pineapplelover.raidarmorrepair").PatchAll();
        }

        private void Update()
        {
            if (!Singleton<GameWorld>.Instantiated)
            {
                ResetRepairState();
                return;
            }

            Player mainPlayer = Singleton<GameWorld>.Instance.MainPlayer;
            if (mainPlayer == null || mainPlayer.HealthController == null || !mainPlayer.HealthController.IsAlive)
            {
                ResetRepairState();
                return;
            }

            TrackOwner(mainPlayer);

            KeyboardShortcut key = RepairKey.Value;
            if (key.IsDown())
            {
                StartRepairSession(mainPlayer);
            }
            else if (RepairService.IsRepairing && !key.IsPressed())
            {
                // Released, or any other reason the key stopped being held.
                ResetRepairState();
            }

            if (!RepairService.IsRepairing)
            {
                return;
            }

            _repairTimer += Time.deltaTime;
            if (_repairTimer < RepairTickInterval.Value)
            {
                return;
            }

            // Subtract rather than zero, so a long frame does not silently eat progress.
            _repairTimer -= RepairTickInterval.Value;

            if (RepairService.TryRepairArmor(mainPlayer))
            {
                ShowProgressPanel(mainPlayer);
            }
            else
            {
                ResetRepairState();
            }
        }

        private void TrackOwner(Player mainPlayer)
        {
            if (_lastKnownPlayer != mainPlayer)
            {
                _lastKnownPlayer = mainPlayer;
                _owner = ResolveOwner(mainPlayer);
                _ownerRetryTimer = 0f;
                _ownerRetryCount = 0;
                return;
            }

            if (_owner != null || _ownerRetryCount >= OwnerRetryMax)
            {
                return;
            }

            _ownerRetryTimer += Time.deltaTime;
            if (_ownerRetryTimer < OwnerRetryInterval)
            {
                return;
            }

            _ownerRetryTimer = 0f;
            _ownerRetryCount++;
            _owner = ResolveOwner(mainPlayer);

            if (_owner == null && _ownerRetryCount >= OwnerRetryMax)
            {
                Log.LogWarning("[RaidArmorRepair] GamePlayerOwner를 여러 번 재시도해도 못 찾음. 이번 레이드에서는 진행률 패널을 비활성화합니다 (수리 자체는 정상 동작).");
            }
        }

        private void StartRepairSession(Player player)
        {
            RepairService.IsRepairing = true;
            _repairTimer = 0f;
            RepairService.NotifyRepairStart(player);
            ShowProgressPanel(player);
        }

        /// <summary>
        /// The vanilla panel (EFT.UI.BattleUIPanelExtraction) closes itself once the duration
        /// passed to Show() runs out - confirmed by decompiling the client, present in both 4.0
        /// and 4.1, not something this mod controls. We re-show it every RepairTickInterval, so
        /// passing exactly that value as the duration means our refresh and the panel's own
        /// auto-close are scheduled to land on the same frame. Our refresh calls StopCoroutine on
        /// the old countdown before it gets a chance to run again that frame, so this should not
        /// race in the normal case - but if a tick's TryRepairArmor call ever throws (see the
        /// remarks on TryRepairArmor) or any other frame hiccups, there is nothing left to stop
        /// the vanilla timer, and the panel closes on schedule with no visible explanation. The
        /// margin below buys slack against exactly that: the panel now outlives one full tick
        /// even if a single refresh is missed, instead of closing the instant it is due.
        /// </summary>
        private const float PanelDurationMargin = 2f;

        private void ShowProgressPanel(Player player)
        {
            if (_owner == null)
            {
                return;
            }

            try
            {
                _owner.ShowObjectivesPanel(
                    RepairService.BuildProgressLabel(RepairService.Peek(player)),
                    RepairTickInterval.Value + PanelDurationMargin);
                _panelShown = true;
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] 진행률 패널 표시 실패: " + ex.Message);
            }
        }

        private void ClosePanel()
        {
            if (_panelShown && _owner != null)
            {
                try
                {
                    _owner.CloseObjectivesPanel();
                }
                catch (Exception ex)
                {
                    Log.LogWarning("[RaidArmorRepair] 진행률 패널 닫기 실패: " + ex.Message);
                }
            }

            _panelShown = false;
        }

        /// <summary>Three escalating lookups: the player object, its children, then the whole
        /// scene. Any of them can throw during a scene change, so each is guarded separately.</summary>
        private GamePlayerOwner ResolveOwner(Player player)
        {
            try
            {
                GamePlayerOwner onPlayer = player.gameObject.GetComponent<GamePlayerOwner>();
                if (onPlayer != null)
                {
                    Log.LogInfo("[RaidArmorRepair] GamePlayerOwner: player.gameObject에서 발견");
                    return onPlayer;
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] GamePlayerOwner GetComponent 실패: " + ex.Message);
            }

            try
            {
                GamePlayerOwner inChildren = player.gameObject.GetComponentInChildren<GamePlayerOwner>(true);
                if (inChildren != null)
                {
                    Log.LogInfo("[RaidArmorRepair] GamePlayerOwner: player 자식 오브젝트에서 발견");
                    return inChildren;
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] GamePlayerOwner GetComponentInChildren 실패: " + ex.Message);
            }

            try
            {
                GamePlayerOwner anywhere = FindObjectOfType<GamePlayerOwner>();
                if (anywhere != null)
                {
                    Log.LogInfo("[RaidArmorRepair] GamePlayerOwner: FindObjectOfType으로 발견");
                    return anywhere;
                }
            }
            catch (Exception ex)
            {
                Log.LogWarning("[RaidArmorRepair] GamePlayerOwner FindObjectOfType 실패, 진행률 패널 비활성화: " + ex.Message);
            }

            return null;
        }

        private void ResetRepairState()
        {
            bool wasRepairing = RepairService.IsRepairing;

            RepairService.IsRepairing = false;
            _repairTimer = 0f;
            ClosePanel();

            if (wasRepairing && _lastKnownPlayer != null)
            {
                RepairService.NotifySessionEnd(_lastKnownPlayer);
            }
        }
    }
}
