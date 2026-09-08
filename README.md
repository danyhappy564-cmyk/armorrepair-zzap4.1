# armorrepair-zzap4.1

레이드 중에 방어구를 수리하는 모드. **SPT 4.1.5** 용으로 포팅했습니다.

> **원작: RaidArmorRepair 제작자 ** — 원본 배포 폴더:
> https://drive.google.com/drive/folders/1_ZyEmQPeDzEZioCpLmk_8d7zNYCJ2dTh?usp=sharing
>
> 이 저장소는 **원작의 SPT 4.1 포팅**입니다. 원작 자체가 아닙니다.
> 원본에 LICENSE 파일이 없고 메타데이터에 `License = "MIT"` 로만 적혀 있어서,
> 재배포 전에 원작자에게 한 번 알려두시는 걸 권합니다.

---

## 뭐 하는 모드냐

레이드 중에 **J 키를 누르고 있으면** 착용 중인 방어구가 수리됩니다.

- 5초에 한 번씩 수리 (설정 가능)
- 누르고 있는 동안 계속, 키를 떼거나 **움직이면 취소**
- 수리하는 동안은 **총을 쏠 수 없음** (방아쇠 차단)
- 가방/조끼/주머니/보안 컨테이너 안의 **수리 키트를 자동으로 찾아** 자원을 소모
- 방탄판(플레이트)도 같이 수리
- 지력(Intellect) 스킬이 높을수록 한 번에 더 많이 회복되고, 최대내구도도 덜 깎임

## 구성

| 프로젝트 | 위치 | 하는 일 |
| --- | --- | --- |
| `RaidArmorRepair` | `BepInEx\plugins\` | 클라이언트 플러그인 — 수리 로직 전부 |
| `MiniArmorRepairKit` | `SPT\user\mods\MiniArmorRepairKit\` | 서버 모드 — 특수 슬롯에 들어가는 **야전 수리 키트** 아이템 추가 |

서버 모드는 없어도 됩니다. 없으면 바닐라 수리 키트만 쓰면 됩니다.

## 빌드

```
dotnet build RaidArmorRepair/RaidArmorRepair.csproj  -c Release
dotnet build MiniArmorRepairKit/MiniArmorRepairKit.csproj -c Release
```

SPT 설치 경로 기본값은 `E:\SPT 4.1` 입니다. 다르면:

```
dotnet build ... -c Release -p:SptRoot="D:\내SPT경로"
```

빌드하면 각각 위 표의 경로로 자동 복사됩니다.

## F12 설정 (`Raid Armor Repair`)

**General**

| 항목 | 기본값 | 설명 |
| --- | --- | --- |
| `RepairKey` | `J` | 수리 단축키 (누르고 있는 동안 수리) |
| `RepairPercentPerUse` | `0.3` | 1틱당 회복량 (최대내구도 대비 비율) |
| `KitResourceCostPerUse` | `20` | 1틱당 소모되는 수리킷 자원 |
| `RepairTickInterval` | `5` | 몇 초마다 한 번 수리할지 |
| `IntellectMaxBonusMultiplier` | `0.5` | 지력 최대일 때 회복량 추가 배율 (+50%) |
| `ShowNotifications` | `true` | 수리 시작/종료 알림 표시 |

**Degradation** (최대내구도 마모)

| 항목 | 기본값 | 설명 |
| --- | --- | --- |
| `EnableMaxDurabilityDegradation` | `true` | 수리할 때 최대내구도도 같이 깎을지 |
| `MaxDurabilityLossAtWorstCase` | `40` | 최악 조건(지력 0 · 저효율 재질)에서의 감소율 % |
| `MaxDurabilityLossAtBestCase` | `5` | 지력 최대일 때 남는 최소 감소율 % |
| `WorstCaseReferenceEfficiency` | `0.26` | 위 최악 감소율의 기준이 되는 수리 효율값 (세라믹) |
| `DefaultArmorMaterialEfficiency` | `1` | 재질을 못 읽었을 때 쓸 대체 효율값 |

> 원본 아카챈 설명에는 "현재는 최대내구도가 안 깎인다"고 되어 있었지만,
> 디컴파일된 코드에는 마모 계산이 들어 있었습니다. 그래서 기능은 살리되
> **`EnableMaxDurabilityDegradation = false` 로 끌 수 있게** 해뒀습니다.

---

## 4.1 포팅에서 바뀐 것

SPT 4.1은 클라이언트 어셈블리의 난독화를 풀었기 때문에, 타입 이름이 실제 이름으로 바뀌었습니다.
실제 4.1.5 `Assembly-CSharp.dll` 로 하나하나 확인한 결과 **바뀐 타입은 딱 3개**였습니다.

| 4.0 (난독화) | 4.1 (실제 이름) |
| --- | --- |
| `SkillClass` | `EFT.Skill` |
| `ArmorPlateItemClass` | `EFT.InventoryLogic.ArmorPlate` |
| `NotificationManagerClass` | `EFT.Communications.NotificationManager` |

`RepairableComponent`, `RepairKitComponent`, `InventoryEquipment`, `SkillManager`,
`ArmorComponent`, `EquipmentSlot`, `Player.FirearmController.SetTriggerPressed` 은
전부 그대로였습니다.

그 외:

- 클라이언트: `net4.8` → `netstandard2.1`, `ItemComponent.Types` / `Sirenix.Serialization` 참조 추가
- 서버: `net9.0` → `net10.0`, 패키지 `SPTarkov.*` → `SPTushonka.*` (4.1.5)
- 서버: `AbstractModMetadata` → `IModMetadata`, `IOnLoad.OnLoad()` → `OnLoadAsync(CancellationToken)`
- `GamePlayerOwner` 를 못 찾아도 죽지 않도록 3단계로 찾고, 실패하면 진행률 패널만 끄고 수리는 계속 동작

## ⚠️ 복원한 부분 (원본과 다를 수 있음)

받은 파일이 **디컴파일 결과물**이라, 일부 데이터가 소실된 상태였습니다.
아래 항목들은 제가 **아카챈 설명과 상식선에서 재구성한 값**이며, 원본과 미묘하게 다를 수 있습니다.

- **야전 수리 키트(Field Repair Kit)의 세부 설정 전부** — 서버 모드의 `OnLoad` 본문이
  디컴파일에서 통째로 날아가고 **아이템 ID 2개만** 남아 있었습니다. 그래서 아래는 전부 제 재구성입니다:
  - 아이템 이름 / 설명 / 한글 로케일
  - 가격 42,000₽, 핸드북 분류 (물물교환 → 도구)
  - 1칸 · 무게 5kg (아카챈 설명 기준)
  - 특수 슬롯 필터 등록 방식
  - **원본 `MiniArmorRepairKit.dll` 이 있으면 정확한 값으로 맞출 수 있습니다.**
- **두 개의 배열 값** — `RuntimeHelpers.InitializeArray` 로 컴파일돼 있어서 디컴파일에서
  내용이 사라졌습니다. 코드 사용처를 보고 재구성했습니다:
  - 수리 대상 슬롯: 방탄복 / 전술조끼 / 헤드기어
  - 수리킷 탐색 슬롯: 가방 / 전술조끼 / 주머니 / 보안 컨테이너
- **방어구 재질별 수리 효율표** — 바닐라 EFT 수치를 기준으로 채웠습니다
  (아라미드 1 · UHMWPE 3 · 강철 3 · 티타늄 0.63 · 복합 0.4 · 세라믹 0.26 · 유리 0.15)

## 실기동에서 잡은 버그

첫 실기동 로그에 이렇게 찍혔습니다:

```
[MiniArmorRepairKit] Field Repair Kit registered (...), allowed in 0 special slot(s)
```

아이템은 만들어졌는데 **특수 슬롯에 하나도 안 들어간** 겁니다. 이 모드의 핵심이 특수 슬롯인데요.

원인: 특수 슬롯은 기본 인벤토리 템플릿(`55d7217a4bdc2d86028b456d`)이 아니라 **주머니(Pockets)**
템플릿에 붙어 있고, 게다가 **템플릿이 2개**입니다 — 일반판과 Unheard 에디션판:

```
627a4e6b255f7527fb05a0f6
65e080be269cbd5c5005e529
```

WTT-ServerCommonLib 의 `SpecialSlotsHelper` 가 정확히 이 두 개를 쓰는 걸 보고 확인했습니다.
지금은 두 템플릿 모두에서 `SpecialSlot*` 슬롯을 찾아 필터를 넓히고, 필터가 아예 없는 슬롯에는
필터를 만들어 넣습니다. 몇 개 슬롯을 실제로 건드렸는지 로그에 찍히니 다음 기동 때 확인하실 수 있습니다.

## 상태

- 빌드: **성공** (두 프로젝트 모두)
- 서버 기동: **확인됨** — 아이템 등록까지 정상
- 인게임 테스트: **아직 안 함** — 베타로 취급하세요
