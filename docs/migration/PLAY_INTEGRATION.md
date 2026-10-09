# 원본 리소스 플레이 통합 진행

기준 master: `4b1018c`, 2026-10-08. 각 작업은 직전 병합 master에서 별도 브랜치로 진행한다.
코드·합성 검증, 실제 원본 파일 호환, GUI 플레이 인수를 각각 기록한다.

| 순서 | 브랜치 | 작업 | 상태 |
|---|---|---|---|
| 1 | `feat/play-01-data-check` | 선택 원본 지도 사전검사·출처 해시·공통 로더 | 코드·합성 검증 완료; 실데이터 대기 |
| 2 | `feat/play-02-map-integration` | 지도/충돌/게임 상태와 화면 연결 | 코드·합성 검증 완료; 실데이터/GUI 대기 |
| 3 | `feat/play-03-actor-animation` | 게임 상태→캐릭터/몬스터 애니메이션 | 코드·합성 검증 완료; 실데이터/GUI 대기 |
| 4 | `feat/play-04-navigation` | 화면 좌표·마우스 이동·벽 우회 | 코드·합성 검증 완료; GUI 대기 |
| 5 | `feat/play-05-content` | 대표 지역·생성 위치·NPC/포털·콘텐츠 연결 | 연결 인수 도구 구현; 원본 콘텐츠 지정/규칙 대기 |
| 6 | `feat/play-06-continuous-session` | 기록 예산과 플레이 진행 분리 | 코드·합성 검증 완료; 실제 장시간 GUI 대기 |
| 7 | `feat/play-07-acceptance` | 배포·실제 GUI·증거/인수 | 인수 자료 구현; 원본/GUI/2시간 실제 시험 대기 |
| 8 | `feat/play-08-scene-setup` | 데이터 폴더 점검·지도에서 장면 생성·시작 메뉴 연결 | 구현; 실제 자료/GUI 인수 대기 |
| 9 | `feat/play-09-actor-art-setup` | 장면별 캐릭터/몬스터 아트 입력·방향 미리보기·복사본 저장 | 구현; 실제 자료/GUI 인수 대기 |
| 10 | `feat/play-10-npc-artwork` | Guide 대기 아트·고정 방향·깊이 정렬·설정 저장 | 구현; 실제 자료/GUI 인수 대기 |
| 11 | `feat/play-11-legacy-hud` | DC6 HUD 배치·체력 표시·메뉴/인벤토리 동작·설정 미리보기 | 구현; 실제 자료/GUI 인수 대기 |
| 12 | `feat/play-12-item-artwork` | 아이템별 DC6 아이콘·슬롯 표시·설정 미리보기/저장 | 구현; 실제 자료/GUI 인수 대기 |
| 13 | `feat/play-13-item-definitions` | 원본 아이템 TXT 참조 정의·검색·아이콘 경로 연결·상세 표시 | 구현; 실제 자료/GUI·원본 규칙 적용 대기 |
| 14 | `feat/play-14-grid-inventory` | 10×4 점유·이동/교환·드래그·저장 v1→v2 변환 | 구현; 실제 GUI/원본 자료 인수 대기 |
| 15 | `feat/play-15-mana-skill` | 마나/강타·선택 HUD·이전 저장 변환 | 구현; 실제 자료/GUI 인수 대기 |
| 16 | `feat/play-16-potions-belt` | 회복 물약·4칸 벨트·저장 v1–v3 변환 | 구현; 실제 자료/GUI 인수 대기 |

## PLAY-16: 회복 물약과 4칸 벨트

1. 새 몬스터를 처치하고 **F**로 장비/물약을 각각 줍는다. 같은 위치에서는 기존 장비 ID가 먼저 선택된다.
2. 가방에서 **Drink selected potion**으로 쓰거나 벨트로 드래그한다. 키보드는 물약 선택 → 벨트 목적 칸 → **Put selected in belt**다.
3. 벨트 클릭은 선택이고 **Use (1–4)**/게임 화면의 숫자 **1–4**가 사용이다. 키 누름당 한 번 요청한다.
4. 차 있는 벨트와 교환하면 기존 물약이 원래 가방 칸/벨트 칸으로 돌아간다. 가방의 빈 칸으로 드래그 또는 Unequip으로 복귀한다.
5. HealthPotion/ManaPotion에 검증된 DC6 아이콘을 선택 연결할 수 있다. 미설정은 이름으로 표시한다.

체력 +40 / 마나 +30의 즉시 회복 preview다. 상한에서 소비하지 않으며 한 tick에 한 번만 성공한다.
기절은 tick 시작 상태로 검사하고 기존 아이템 처리 단계에서 전투보다 먼저 사용한다. 메뉴/일시정지 중에는 사용 요청을 막는다.
4칸은 고정이며 스택/자동 보충/장착 벨트에 따른 확장과 원본 회복 시간은 후속이다.
새 저장은 **schema 4/rules 7**. v1–v3의 기존 checksum/아이템/명령을 검사하고 메모리만 변환한다.
v3의 마나·선택·대기는 유지하며 기존 사망 몬스터에게 물약을 소급 지급하지 않는다. 명시적 저장 때 이전 정상 파일은 `.bak`으로 남는다.
장비 ID·장면 identity·v2/v3 배치를 유지한다. 바닥의 겹친 전리품 이름/선택 줍기는 PLAY-17에 예정한다.
실제 자료·OS GUI 인수는 **NOT_RUN**. [검증·한계](PLAY_16_RESULTS.md).

## PLAY-15: 마나와 첫 능동 스킬

- 스킬 목록의 **Power strike · 12 MP**를 선택하고 **Q / Cast nearest**로 가장 가까운 적을 공격한다.
  근접 사거리 384, 원래 무기 피해 +12, 마나 12, 재사용 25 tick(1초). 원작 강타 수치의 이식은 아니다.
- 일반 공격과 대기를 공유하고 강타 자체 대기도 검사한다. 기절/죽은 대상/범위/벽/마나 부족은 비용과 RNG를 소비하지 않는다.
  동일 tick의 여러 공격/스킬 명령은 마지막 하나만 처리한다. 선택은 순서대로 적용하고 발동 명령 당시 선택을 사용한다.
- 마나 기본값은 60/60이며 25 tick마다 1 회복한다. 성공한 강타는 회복 누적을 0으로 재시작한다.
  일시정지/메뉴에서는 tick이 멈추고 사망 후에는 회복하지 않는다. 선택 None은 스킬 발동을 해제한다.
- MP 숫자/푸른 바·부족/대기 안내를 표시한다. Scene art의 **Mana** 역할은 지정 DC6 프레임을 아래부터 채운다.
  Health와 독립이며 지정하지 않으면 기본 마나 바가 남는다. **Preview HUD at 50% health/mana**로 배치를 확인한다.
- 현재 저장 **schema 3/rules 6**. v1/rules4는 기존 가방 변환까지 수행하고 v2/rules5는 배치를 그대로 유지한다.
  이전 체크섬 검증 후에만 마나/스킬 기본값을 추가한다. 로드는 메모리만 바꾸며 명시적 저장에서 이전 파일을 `.bak`으로 남긴다.
  기존 scene identity와 슬롯 주소를 유지한다. 미래 형식/콘텐츠 불일치/공간 부족은 파일을 보존하며 거부한다.
- 강타는 기존 Attack 애니메이션·효과음 경로를 쓴다. 원본 스킬 트리/투사체/특수 이펙트는 별도다.
  실제 LoD·native GUI 인수는 **NOT_RUN**. [검증·한계](PLAY_15_RESULTS.md).

## PLAY-14: 격자 인벤토리와 저장 변환

현재 가방은 10×4칸이다. 기본 preview 크기는 검 1×3·방어구 2×3이며 무기/몸통 장비 칸을 유지한다.
아이템에 마우스를 올리면 이름을 확인하고 선택하면 크기·연결 코드·전투 수치가 표시된다.

1. 아이템을 원하는 칸에 끌어 놓는다. 목적 칸은 아이템의 왼쪽 위가 된다. 자기 기존 영역과 겹치는 이동은 가능하다.
2. 다른 아이템과 교환하려면 대상 위치와 돌아갈 위치가 모두 맞아야 한다. 둘 이상과 겹치거나 경계를 넘으면 원래 위치를 유지한다.
3. 키보드는 Tab/Enter로 아이템 → **Move selected** → 목적 칸을 선택한다. 칸 버튼에서 Escape로 이동 모드를 취소한다.
4. 무기/몸통 칸으로 끌어 장착하거나, 장비에서 가방의 빈 영역으로 끌어 지정 위치에 해제한다.
   기존 Equip/Unequip/Drop/Pickup 버튼도 사용할 수 있다. 자동 배치는 위에서 아래, 왼쪽에서 오른쪽으로 찾는다.
5. **Scene art → Original item definitions → Use bound item codes and sizes in the 10×4 bag**를 켜면
   연결한 정의의 코드·크기만 적용한다. 참조 정의 연결이 필요하고 10×4 안에 들어가는 크기만 허용한다.

scene schema 1에서 `"UseItemDimensions": true`를 기존 `ItemDefinitions`와 함께 지정한다. 기본 false다.
활성화 설정은 scene ContentId에 반영되어 별도 체크포인트 슬롯을 사용한다. 옵션이 없는 이전 scene identity는 유지된다.
저장에는 실제 사용한 크기/코드도 포함된다. 불일치하면 다른 규칙으로 조용히 열지 않는다.

이전 저장 **schema 1 / rules 4 → schema 2 / rules 5**: 콘텐츠·이전 hash/소유권/8칸 상태를 먼저 검증하고
기존 슬롯 순서대로 재배치한다. 로드 시 원본 파일을 바꾸지 않는다. 다음 명시적 저장에서 이전 파일을 `.bak`에 보존한다.
공간 부족은 호환 오류이며 이전 백업으로 조용히 돌아가지 않는다. 이전 앱에서 짐을 줄인 뒤 재시도한다.
이미 변환한 파일은 다시 정렬하지 않는다. 이전 규칙의 전체 재생 엔진이나 원본 `.d2s` 본문 변환은 아니다.

확장 벨트·원작 배경/글꼴·회전/스택 분할·원본 아이템 옵션은 후속이다.
GUI 드래그/DPI/실제 파일 인수는 NOT_RUN이며 [PLAY_14_RESULTS](PLAY_14_RESULTS.md)를 따른다.

## PLAY-13: 원본 아이템 정의 조회와 연결

Scene art에서 소유 데이터 폴더의 장면을 열고 **Original item definitions**를 펼친다. 공개 rc.3에는 없다.

1. **Read item TXT tables (LoD 1.10f profile)**로 `data/global/excel`의 bodylocs, itemtypes, weapons, armor, misc TXT를 읽는다.
2. 원본 코드 또는 원문 이름을 검색한다. 최대 100개 결과를 표시하므로 검색을 좁힌다. 이름은 TBL 지역화 전 source label이다.
3. Training sword / Training vest 중 대상을 선택한다. 손 장착 무기 또는 몸통 방어구만 각각 연결할 수 있다.
4. **Use selected definition and inventory image path**를 누르면 코드와 TXT의 invfile에서 만든 DC6 경로를 채운다.
   이 동작은 선택 대상의 기존 아이콘 경로를 바꾼다. 프레임 0은 초기값이므로 소유 자료에서 정확한 팔레트/프레임을 확인하고 **Preview item icons**를 누른다.
5. **Validate and save a new scene copy → Load saved copy** 후 아이템 상세의 원본 크기·장착 부위·요구치·기본 범위를 확인한다.
   **Reference only** 표시처럼 전투 피해·방어·장착 요구치는 아직 적용하지 않는다. 가방 크기 적용은 위 PLAY-14 옵션을 따른다.
6. 참조만 끄려면 **Attach original item reference definitions**를 끄고 새 복사본으로 저장한다. 아이콘 설정은 별도로 유지된다.

코드를 직접 입력할 수도 있지만 존재 여부·분류는 저장 전에 검증한다. 읽기/저장 실패는 이전 결과·미리보기·원본 JSON을 보존한다.
데이터 폴더를 바꿨다면 장면을 다시 연다. 프로필 이름은 실제 파일의 버전/전체 호환을 인증하지 않는다.

scene schema 1의 선택 필드 예시다. 아래 코드 자리는 소유 표에서 검증한 실제 코드로 대체한다.

```json
"ItemDefinitions": {
  "Profile": "lod-1.10f",
  "Bindings": [
    { "Definition": "TrainingSword", "Code": "abc" },
    { "Definition": "TrainingVest", "Code": "def" }
  ]
}
```

`--items-txt <game-data-directory>`는 참조 정의와 5개 소스 해시를 JSON으로 출력한다.
`--check-scene`에는 ItemDefinitionSources/ItemDefinitions와 ItemDefinitionCount가 추가된다.
개수나 종료 코드 0은 GUI/원본 규칙 PASS가 아니다. 설정과 모든 표의 해시는 scene identity와 체크포인트 슬롯에 반영된다.
설정이 없는 이전 장면의 identity는 유지한다. 새 필드를 가진 장면은 구버전 앱에서 읽을 수 없다.
PLAY-13 시점의 원본 TXT/GUI, BIN/TBL, 규칙·격자/저장 잔건은 [PLAY_13_RESULTS](PLAY_13_RESULTS.md)에 기록한다.

## PLAY-12: 원본 아이템 아이콘 연결

Scene art에서 장면을 열고 **Item artwork settings**를 펼친다. 공개 rc.3에는 없다.

1. **Use item artwork**를 켜고 소유 자료의 아이템 팔레트를 입력한다.
2. DC6 탭에서 아이콘 경로와 프레임을 확인한다. Training sword / Training vest 중 연결할 항목을 켜고 경로·프레임을 입력한다.
3. **Preview item icons**로 슬롯 표시를 확인한다. 변경 후 다시 Preview해야 갱신된다.
4. **Validate and save a new scene copy → Load saved copy**로 적용한다. 새 설정은 별도 체크포인트 슬롯을 사용한다.
5. 아이템 획득/장착/해제/버리기 후 아이콘과 이름을 확인한다. 매핑 없는 항목은 이름으로 표시한다.
   아이콘을 끄려면 전체 또는 개별 항목을 끄고 새 복사본으로 저장한다. 전체 사용을 켜면 최소 한 항목이 필요하다.

scene schema 1의 선택 필드 예시다. 경로는 실제 소유 자료에서 확인한 값으로 바꿔야 한다.

```json
"ItemArtwork": {
  "PalettePath": "data/global/palette/units/pal.dat",
  "Icons": [
    { "Definition": "TrainingSword", "Path": "verified-sword.dc6", "Frame": 0 },
    { "Definition": "TrainingVest", "Path": "verified-vest.dc6", "Frame": 0 }
  ]
}
```

현재 catalog의 정확한 이름만 허용한다. 프레임은 방향 순서로 평탄화한 0 기반 번호이며 DC6 offset은 슬롯에 더하지 않는다.
아이콘은 비율을 유지하고 최대 폭 40px/버튼 높이에 맞춘다. 투명도를 보존하며 기존 텍스트·툴팁을 유지한다.
파일당 32MiB·아이템 입력 64MiB·각 프레임 256×256 이하, 전체 scene 입력/픽셀 예산도 적용한다.
설정/소스 해시는 ContentId에 반영한다. `ItemArtworkCount`는 매핑 개수이며 원작 인벤토리 완성을 뜻하지 않는다.
격자·드래그/드롭은 PLAY-14에서 추가했다. 바닥 이미지·장비별 캐릭터 외형·원본 전투 수치 이관은 별도다.
실제 MPQ/GUI 인수는 NOT_RUN이며 [검증/제한](PLAY_12_RESULTS.md)을 따른다.

## PLAY-11: 원본 HUD 이미지 연결

**LoD = Diablo II: Lord of Destruction(디아블로 2: 파괴의 군주)**, 원작 디아블로 2의 확장팩이다.
이 프로젝트는 사용자 소유의 원작 LoD MPQ를 읽는다. `lod-1.10f`는 현재 선택 지도/테이블 프로필의 목표 기준이며
모든 실제 파일의 호환을 검증했다는 뜻이 아니다. 리저렉션의 고해상도 3D 리소스는 이 입력과 구별한다.

PLAY-11 이후 Scene art에서 장면을 열고 **HUD artwork settings**를 펼친다. 공개 v0.2.0-rc.3에는 없다.

1. **Use HUD artwork**를 켠다. 실제 UI에 맞는 팔레트와 캔버스 크기를 입력한다. 기본 800×120은 편집 예시다.
2. DC6 탭에서 소유 자료의 파일·팔레트·프레임을 확인한다. **Add HUD element**로 Role, MPQ DC6 경로,
   Frame, X, Y를 입력한다. Frame은 0부터 시작하는 direction-major 인덱스다.
3. X/Y는 캔버스 안에서 이미지의 **왼쪽 위** 위치다. 파일의 signed offset을 추가하지 않는다.
   타일처럼 나뉜 원본 패널은 여러 Decoration 요소로 명시적으로 이어 붙인다. 프레임 연결을 자동 추측하지 않는다.
4. 목록 순서대로 뒤에서 앞으로 그린다. Decoration은 여러 개, Health/Menu/Inventory는 각각 최대 한 개다.
   Health는 현재 체력 비율만큼 아래쪽 행을 보여준다. 빈 구슬/테두리는 별도 Decoration으로 배치한다.
5. **Preview HUD at 50% health**로 합성 배치를 확인한다. 입력 변경은 다시 Preview해야 보인다.
   미리보기의 버튼 그림은 게임을 조작하지 않는다. 잘못된 프레임이나 캔버스 밖 이미지면 이전 미리보기를 유지한다.
6. **Validate and save a new scene copy → Load saved copy**로 플레이에 연결한다.
   원본 JSON/MPQ를 덮어쓰지 않는다. 배우와 NPC 아트도 함께 유지한다. HUD를 끈 복사본은 기본 체력 바를 사용한다.

실행 중 HUD는 게임 화면 아래에 위치한다. 가용 폭에 맞춰 비율을 유지해 축소하고, 빈 폭은 가운데 정렬하며 원래 크기보다 확대하지 않는다.
Menu 이미지는 기존 일시정지 메뉴를 열고, Inventory 이미지는 기존 인벤토리 패널을 토글한다.
클릭 판정은 프레임의 직사각형이며 겹친 버튼은 나중 요소를 우선한다. 장식은 클릭을 가로막는 마스크가 아니다.
텍스트 HP·상태와 기존 Menu/Inventory 버튼도 유지하므로 일부 이미지만 설정해도 기본 조작이 가능하다.
인벤토리 토글은 게임을 일시정지하지 않으며 새 세션은 패널을 다시 표시한다.

선택 scene 필드의 형식 예시(경로·프레임·실제 크기는 반드시 확인):

```json
"HudArtwork": {
  "PalettePath": "data/global/palette/units/pal.dat",
  "Width": 800, "Height": 120,
  "Elements": [
    {"Role": "Decoration", "Path": "data/global/ui/YOUR_PANEL.dc6", "Frame": 0, "X": 0, "Y": 0},
    {"Role": "Health", "Path": "data/global/ui/YOUR_HEALTH.dc6", "Frame": 0, "X": 16, "Y": 8},
    {"Role": "Menu", "Path": "data/global/ui/YOUR_MENU.dc6", "Frame": 0, "X": 360, "Y": 80},
    {"Role": "Inventory", "Path": "data/global/ui/YOUR_INVENTORY.dc6", "Frame": 0, "X": 420, "Y": 80}
  ]
}
```

- 캔버스 1..4096 × 1..1024, 요소 1..32. HUD 입력 합계 64MiB, 파일당 32MiB,
  고유 decoded frame 픽셀 4,194,304. 같은 파일/프레임의 반복 배치는 디코딩과 텍스처를 공유한다.
  전체 scene 128MiB 입력·16,777,216 픽셀 제한에도 합산한다. 장면 스트리밍/전체 UI 자동 이관은 아니다.
- HUD 설정·소스 해시는 ContentId/장면별 저장 슬롯에 포함한다. HUD 없는 기존 장면의 identity는 유지한다.
  구버전 앱은 새 JSON 필드를 거부한다. 기존 scene schema 1을 읽는 새 빌드를 사용한다.
- CLI는 `HudArtworkSources`와 `Readiness.HudArtworkConfigured`를 보고한다. 구성됐다는 뜻이며
  원작 HUD 전체 완료 플래그가 아니다. 기존 준비 상태와 종료 코드는 그대로다.
- 원작 글꼴·전체 스킬·벨트·메뉴 전체 스킨·격자형 인벤토리 외형·버튼 hover/pressed 프레임은 미구현이다.
  현재 체력·메뉴·인벤토리 기능은 preview 규칙이다. 실제 원본 화면/DPI/마우스 대조는 NOT_RUN이다.

[PLAY_11_RESULTS](PLAY_11_RESULTS.md)에 코드·합성 검증과 실제 인수를 구별해 기록한다.

## PLAY-10: Guide NPC 대기 아트

PLAY-10 이후 **Scene art**의 배우 목록에서 **Guide**를 선택한다. v0.2.0-rc.3에는 없다.
**Use artwork for this actor**를 켜고 팔레트, **Idle**의 DCC 또는 COF/레이어, 8개 방향 번호와 FPS를 지정한다.
NPC에는 Idle 탭만 표시한다. **Guide fixed facing**은 실제 게임에 그릴 방향, **Inspect facing**은 검사기에서 확인할 방향이다.
미완성 입력·고정 방향은 다른 배우로 전환해도 유지하며 저장한 복사본을 다시 열어 수정할 수 있다.
미리보기·검증·새 복사본 저장·로드는 아래 PLAY-09 절차를 따른다.

scene schema 1에 선택 필드 `NpcArtwork`를 추가한다. 다음은 형식 예시이며 경로·방향·FPS를 실제 자료에서 확인해야 한다.

```json
"NpcArtwork": {
  "Entity": 10,
  "PalettePath": "data/global/palette/act1/pal.dat",
  "Idle": {
    "Motion": "Idle", "Path": "data/global/monsters/YOUR_NPC_IDLE.dcc",
    "Layers": null, "Directions": [0, 1, 2, 3, 4, 5, 6, 7], "Fps": 10
  },
  "Facing": 4
}
```

- `Entity`는 해당 scene의 `Npc.Id`여야 한다. 전투 actor에 NPC를 중복 추가하지 않는다.
- `Facing`은 0..7이며 아래 PLAY-03의 Core 방향 벡터 순서다. 파일 내부 방향은 `Directions[Facing]`으로 명시한다.
- Idle은 25Hz 게임 tick으로 반복하므로 Pause/메뉴 정지와 함께 멈춘다. 고정 위치의 NPC 한 명이며 이동·대화 동작 전환은 없다.
- NPC를 캐릭터/몬스터와 같은 X+Y 깊이 순서에 넣고 기본 벽 정렬을 적용한다. 원본 특수 벽·지붕 효과의 정확성은 미검증이다.
- 아트가 없으면 기존 원형 표시다. NPC 설정/파일 해시도 ContentId와 별도 저장 슬롯에 반영한다.
  필드가 없는 기존 scene의 ContentId는 유지한다. 구버전 앱은 알 수 없는 JSON 필드를 거부하므로 새 복사본에는 새 빌드를 사용한다.
- 지형·combat actor·NPC는 입력 128MiB와 decoded pixel 16,777,216 예산을 공유한다. 실패 시 진행 중 장면을 교체하지 않는다.
- `--check-scene`/`--check-play-ready` 출력에 `NpcArtworkSources`, `NpcFacing`,
  `Readiness.NpcArtworkConfigured`, `Readiness.ReadyForAllSpritesGuiCheck`가 추가된다.
  마지막 값은 기존 `ReadyForSceneGuiCheck`와 NPC 아트 설정을 모두 만족해야 true다.
  기존 CLI 종료 코드와 `ReadyForSceneGuiCheck`는 combat actor/정적 퀘스트 검사 의미를 유지하며 NPC 아트를 필수로 만들지 않는다.
  전체 스프라이트 장면 QA에는 새 플래그도 확인한다. true여도 `GuiQa=NOT_RUN`, `OriginalRulesValidated=false`다.

NPC 이름·대화·퀘스트 로직은 기존대로다. 원작 HUD, 장비 교체별 외형, NPC 이동·대화 모션은 후속 작업이다.
합성 검증과 실제 인수의 경계는 [PLAY_10_RESULTS](PLAY_10_RESULTS.md)에 기록한다.

## PLAY-09: 캐릭터·몬스터 아트 연결 화면

PLAY-09 이후 빌드의 **Scene art** 탭을 사용한다. 기존 v0.2.0-rc.3에는 포함되지 않는다.

1. 원본 데이터 폴더를 지정한 상태에서 Map의 **Edit generated artwork**, 또는 Scene art의
   **Open scene JSON for artwork**로 읽을 수 있는 scene을 연다. 지형·기존 아트·오디오를 원본 폴더에서 검증한다.
   다른 scene을 열 때는 편집 중 값 교체를 확인하며, 취소/실패하면 이전 폼과 저장 파일을 유지한다.
2. Player/Monster ID를 선택하고 **Use artwork for this actor**를 켠다. 기존 아트가 있으면 불러온다.
   없는 아트의 팔레트는 해당 지역 팔레트로 채우지만 반드시 실제 캐릭터에 맞는지 확인한다.
3. **Idle / Walk / Attack / Hit / Death** 각각 DCC 또는 COF 경로를 입력한다.
   COF는 `component number=DCC path`를 한 줄씩 입력하며, DCC는 레이어 칸을 비운다.
4. 각 동작에 **8개 방향 번호**를 쉼표로 구분해 입력한다. 순서는 화면의 Core 이동 벡터 안내를 따른다.
   빈 방향을 자동 추정하지 않는다. FPS 기본값 12는 편집 예시이며 원작 속도 판정이 아니다.
5. 확인할 facing을 선택하고 **Inspect this motion in DCC-COF**를 누른다.
   해당 경로·팔레트·레이어·소스 방향·FPS를 기존 애니메이션 탭으로 보내 읽는다.
   Play/Frame/Direction으로 확인하고 Scene art로 돌아와 수정한다. 미리보기에서 한 변경은 폼에 역반영되지 않는다.
6. 배우를 바꿔도 미완성 입력을 유지한다. **Validate and save a new scene copy**는 켠 모든 actor의
   다섯 동작/모든 지정 방향, 지형·배치·퀘스트 연결 및 로더 예산을 검사한다.
   `scenes/artwork-<unique-id>.json`에 새로 저장하며 원본 JSON/MPQ/세이브를 덮어쓰지 않는다.
7. **Load saved copy**로 플레이에 연결한다. 기존 세션 교체 확인과 메뉴 정지/Continue 절차는 PLAY-08과 같다.
   변경한 아트는 ContentId에 반영되므로 별도 체크포인트 슬롯을 사용한다. 로드 후 Save settings로 최근 경로를 기억한다.

아트를 끈 배우는 임시 표시다. 일부 배우만 설정한 중간 복사본도 저장할 수 있으며 설정 수를 보고한다.
모든 combat actor 아트가 로드되어도 실제 색상/방향/타이밍/가림을 확인한 것은 아니다.
NPC Idle 아트는 위 PLAY-10에서 추가했다. 장비 교체별 아트, 원작 HUD, 원본 캠페인 규칙, 3D는 별도 범위다.
리소스 자동 검색/클래스 프리셋 추론 없이 검증한 MPQ 경로를 사용한다.
검증 기록은 [PLAY_09_RESULTS](PLAY_09_RESULTS.md)를 따른다.

## PLAY-08: JSON 수동 작성 없는 첫 지형 장면

이 기능은 PLAY-08 이후 빌드에 포함된다. 기존 **v0.2.0-rc.3에는 포함되지 않는다**.
원본 MPQ는 제공하지 않으며 사용자가 보유한 LoD 설치 폴더를 읽는다.

1. 왼쪽 **Choose directory → Check data directory**로 폴더와 누락 아카이브 목록을 확인한다.
   파일명 존재 검사이며 패치 버전/내용 호환 판정이 아니다. **Save settings**로 경로를 저장한다.
2. **Map** 탭에서 확인한 Level ID / Preset Def / File slot으로 **Resolve table paths**를 실행하거나
   DS1와 DT1 경로를 직접 입력한다. 실제 지역의 Act 팔레트를 확인한 뒤 **Load map**을 누른다.
   BIN 모드는 LoD 1.10f 전용이다. 기본 Act 1 팔레트와 ID 값은 추천 콘텐츠가 아니며 다른 지역에 자동 적용하지 않는다.
3. **Collision**을 켜고 지도를 가리켜 `Cell X,Y`를 읽는다. 장면 이름과 Player / Monster / Guide의 X,Y를 지정한다.
   좌표는 0부터 시작하는 **이동 셀**이며 DS1 타일 하나가 5×5 셀이다. 셀 중앙으로 생성한다.
   기본 위치는 예시이며 원본 생성 위치가 아니다. 서로 다른 이동 가능 셀과 연결된 왕복 경로가 필요하다.
4. **Validate and create scene**은 리소스·충돌·배치·퀘스트 연결을 재검사하고 사용자 데이터의
   `scenes/preview-<unique-id>.json`을 새로 만든다. 기존 장면/세이브/원본 MPQ를 덮어쓰지 않는다.
   경로나 데이터 폴더를 바꿨으면 지도를 다시 읽는다. 실패 시 현재 게임과 기존 파일은 유지한다.
5. **Load generated scene**으로 Simulation으로 이동한다. 기존 세션이 있으면 교체 확인을 받는다.
   시작 메뉴에서 로드하면 일시정지 상태로 결과를 보여주며 **Continue current session**으로 진행한다.
   이 장면의 저장 슬롯으로 **Load checkpoint**도 가능하다. 성공 후 **Save settings**로 최근 경로를 저장한다.
6. 다음 실행에서는 시작 메뉴의 **Load remembered scene**으로 직접 읽는다.
   이미 작성한 아트·오디오 포함 JSON은 **Load original scene JSON**으로 읽는다.

생성 장면은 **원본 지형 + 임시 캐릭터/몬스터/NPC 표시 + 미리보기 규칙**이다.
한 지역·플레이어 1명·몬스터 1마리·Guide 1명·처치 퀘스트이며 포털은 없다.
캐릭터 아트/오디오/원본 오브젝트 배치/캠페인/원작 UI를 자동 추정하지 않는다.
따라서 생성된 장면은 지형 플레이 준비용이며 `--check-play-ready`는 아트 누락으로 미완료를 보고한다.
전체 QA-09 통과를 위해서는 아래 아트/오디오 설정과 실제 원본 자료/GUI 검증이 추가로 필요하다.
검증 범위는 [PLAY_08_RESULTS](PLAY_08_RESULTS.md)에 기록한다.

## PLAY-01: 선택 지도 사전검사

`LegacyMapAsset`는 CLI와 Map 탭에서 같은 코드로 DS1, DT1, Act 팔레트를 읽는다.
선택 파일의 정규화된 MPQ 경로·바이트 수·SHA-256, 지도 크기·누락 타일·중복 키·이동 가능 셀 수를 반환한다.
버전은 계속 `unverified`, 전체 호환과 플레이 검증은 `false`다.
모든 타일을 찾고 이동 가능 셀이 있어야 플레이용 지형 검사를 통과한다. 이는 NPC 생성/경로 도달성 검증이 아니다.
중복 타일은 기존과 같이 첫 DT1을 우선하며 수를 보고한다. 공개 CI는 합성 파일만 사용한다.

사용자 소유의 원본 설치 폴더와 실제로 확인한 경로로 아래 JSON을 작성한다.
아래 `YOUR_*`는 자리표시자이며 실제 콘텐츠 경로/호환을 보증하지 않는다.

```json
{
  "SchemaVersion": 1,
  "Profile": "lod-1.10f",
  "MapPath": "data/global/tiles/YOUR_MAP.ds1",
  "PalettePath": "data/global/palette/act1/pal.dat",
  "Tilesets": ["data/global/tiles/YOUR_TILESET.dt1"]
}
```

```sh
dotnet run --project tools/OpenD2.AssetAudit -- --check-map /path/to/game /path/to/map-request.json
```

JSON은 stdout에 출력한다. 종료 0은 선택 지도 지형 검사·필수 아카이브 파일 존재 검사 통과,
3은 필수 아카이브 누락, 1은 읽기/형식/지형 검사 실패, 2는 인수 오류다.
선택 파일만 읽고 원본 폴더에 쓰거나 리소스를 추출하지 않는다. 64KiB JSON, 파일당 32MiB,
지도 입력 합계 64MiB와 기존 픽셀·타일·충돌 예산을 적용한다.

## 외부 증거가 필요한 잔건

- 실제 LoD 1.10f MPQ가 제공되지 않았다. 파일/해시/이미지 대조와 미지원 형식 해소는 미실행이다.
- 원본 아이템·몬스터·스킬 규칙의 전체 의미 해석은 별도다. 합성 규칙에 원본 이미지를 붙이는 것만으로 게임 호환 완료라고 하지 않는다.
- 배포판을 사람이거나 봇이 조작한 영상/로그, 목표 OS 설치·GPU·2시간 안정성 증거가 필요하다.
- 원본 세이브 가져오기, 고품질 3D, 전체 캠페인·협동·오픈월드는 첫 대표 장면 이후 범위다.

PLAY-01 로컬 검증: 전체 계약 234/234, Linux export 및 배포본 headless smoke 통과.
새 계약 6개는 해시/경로/형식/누락/예산/입력 소유권 경계를 확인한다. 실제 원본 데이터·GUI는 미실행이다.

## PLAY-02: 지도와 플레이 세션 연결

Simulation 탭의 **Load legacy scene JSON**에서 사용자 작성 scene JSON을 선택한다.
왼쪽 게임 데이터 경로를 먼저 지정한다. 1..8개 지도, 플레이어/몬스터 생성 위치, NPC, 포털,
퀘스트 목표를 명시하며 DS1 오브젝트 ID에서 게임 의미를 추측하지 않는다.
`X/Y`와 도착 좌표는 1 navigation cell=256인 Core 좌표다. DS1 한 타일은 5×5 cells다.
모든 생성 위치·목표·포털 참조·충돌을 검사한 후 텍스처를 준비하고 기존 세션을 교체한다.
실패하면 기존 세션/텍스처를 유지한다. 원본 파일은 수정하지 않는다.

최소 JSON 형태(경로와 좌표는 실제 선택 지도에서 확인해야 하는 예시):

```json
{
  "SchemaVersion": 1,
  "Title": "Representative scene test",
  "Regions": [{
    "Id": 1, "Name": "Selected region",
    "Terrain": {
      "SchemaVersion": 1, "Profile": "lod-1.10f",
      "MapPath": "data/global/tiles/YOUR_MAP.ds1",
      "PalettePath": "data/global/palette/act1/pal.dat",
      "Tilesets": ["data/global/tiles/YOUR_TILESET.dt1"]
    }
  }],
  "Actors": [
    {"Id": 1, "Region": 1, "X": 384, "Y": 384, "Player": true, "Health": 100},
    {"Id": 2, "Region": 1, "X": 1664, "Y": 1664, "Player": false, "Health": 36}
  ],
  "Npc": {"Id": 10, "Region": 1, "X": 640, "Y": 384, "Name": "Guide"},
  "Portals": [],
  "QuestTargets": [2]
}
```

포털은 `Id, Region, X, Y, Destination, ArrivalX, ArrivalY` 필드다.
다른 `Regions` 항목으로만 이동하며 서로 반대 방향 포털을 명시해야 왕복할 수 있다.
CLI `--check-scene <game-directory> <scene.json>`도 같은 검증을 실행한다.

게임 파일 저장은 `legacy-<ContentId>.json`과 기존 synthetic 슬롯으로 분리한다.
ContentId는 지도/팔레트/DT1 파일 해시, 월드와 초기 객체 상태를 포함한다.
재실행 후 같은 scene JSON을 먼저 선택한 뒤 **Load checkpoint**를 누른다.
별도 설치 경로로 이동해도 실제 콘텐츠 바이트와 논리 경로가 같으면 같은 슬롯을 쓴다.

- 제한: 원본 지형 위에 기존 preview 전투·퀘스트·아이템 규칙과 도형 캐릭터를 사용한다.
  방향별 캐릭터 애니메이션, 벽과 캐릭터의 정확한 가림, 원본 테이블 의미 해석은 후속이다.
- 명시한 지점이 이동 가능하다는 검사는 수행하지만 모든 NPC/포털 사이 도달 가능성을 보장하지 않는다.
- 전체 scene 입력 128MiB, 전체 decoded tile pixels 16,777,216, 전체 충돌 1,048,576 cells.
  화면 리소스 업로드는 로딩 시 수행하며 스트리밍/프레임 분산 업로드는 미구현이다.
- 로컬 전체 계약 **238/238**, Linux export·배포 headless smoke 성공.
  `OPEND2_PLAY02_TERRAIN_READY`는 합성 DS1/DT1의 실제 텍스처·세션·이동 연결을 검사한다.
  사람/봇의 GUI 조작이나 원본 데이터 호환 증거는 아니다.

PLAY-01은 [PR #11](https://github.com/imagineiluv-star/OpenD2/pull/11),
[3개 OS CI](https://github.com/imagineiluv-star/OpenD2/actions/runs/37806969136) 성공 후
master `ca40b9f`에 병합했다. PLAY-02 브랜치는 이 병합 커밋에서 시작했다.

## PLAY-03: 상태별 원본 캐릭터 표현

scene JSON에 선택 `Artwork` 배열을 추가한다. 각 항목은 `Entity`(Actors에 존재하는 ID),
`PalettePath`, `Motions`다. 각 motion은 아래 형태이며 Idle, Walk, Attack, Hit, Death를 모두 명시한다.
원본의 클래스/장비 파일명, DCC 방향 번호, 애니메이션 속도를 추정하지 않는다.

```json
{
  "Motion": "Walk", "Path": "data/global/chars/YOUR_WALK.dcc",
  "Layers": null, "Directions": [0, 1, 2, 3, 4, 5, 6, 7], "Fps": 10
}
```

`Directions`의 순서는 Core 이동 벡터 `(-1,-1),(0,-1),(1,-1),(1,0),(1,1),(0,1),(-1,1),(-1,0)`다.
값은 파일 내부 방향 인덱스이며 원본 파일에서 확인해야 한다. 위 번호는 예시다.
단일 방향 파일은 명시적으로 같은 값을 반복할 수 있지만 다방향 호환을 검증한 것으로 표시하지 않는다.
COF는 `Path`에 COF를 넣고 `Layers`를 `{"0":"...head.dcc","1":"...torso.dcc"}`처럼 지정한다.
기존 COF/DCC의 프레임 수·레이어·투명 효과 제한을 그대로 적용한다.

- 원본 파일 읽기/디코딩은 로딩 작업에서 완료한다. 실행 중에는 변경된 프레임의 텍스처만 갱신한다.
- 동작 우선순위는 Death → Hit → Attack → Walk → Idle. 25Hz 게임 tick으로 진행하고 일시정지 시 정지한다.
  Death/Attack/Hit는 마지막 프레임을 유지하고 Idle/Walk만 반복한다. 전투 판정이나 RNG에는 영향이 없다.
- 지역 지형과 캐릭터의 기본 깊이 정렬을 추가했다. 특수 벽·지붕 투명 처리·PL2·그림자·장비 교체별 아트는 아직 미구현/미검증이다.
- 프로필이 없는 객체는 기존 도형으로 표시한다. 최대 32개 combat actor artwork 프로필과 PLAY-10의 선택 NPC Idle 프로필 한 개를 지원한다.
- 전체 scene 입력 예산 128MiB에 artwork도 포함하고, 지형+캐릭터 indexed pixels 합계를 16,777,216으로 제한한다.
  같은 motion 안의 반복 방향은 clip을 공유한다. 전체 클래스/장비 아틀라스나 스트리밍 구현은 아니다.
- artwork의 원본 해시·방향/FPS 설정은 ContentId에 포함된다. artwork가 없는 기존 scene의 ID는 유지한다.
- 합성 DCC/COF 계약과 배포본 `OPEND2_PLAY03_ACTOR_READY`로 실제 텍스처 생성·게임 상태 연결을 확인한다.
  실제 LoD 캐릭터의 방향/프레임/색상/가림은 원본 자료와 GUI 대조 전까지 미검증이다.

PLAY-02는 [PR #12](https://github.com/imagineiluv-star/OpenD2/pull/12)의
[3개 OS CI](https://github.com/imagineiluv-star/OpenD2/actions/runs/37808493991) 성공 후 master `cc3b38e`에 병합했다.
PLAY-03 로컬 최종 검증: 계약 **244/244**, Linux export·배포본 smoke 성공.

## PLAY-04: 클릭 이동과 제한된 탐색

지면 클릭은 알려진 이동 가능 셀의 중심으로 이동한다. 몬스터 클릭은 근접 거리/시야가 확보되면
한 번 공격하고, 멀면 클릭 당시 위치까지 경로를 계산해 접근 후 공격한다. 움직이는 목표를 무한 추적하거나
자동 공격을 반복하지 않는다. 방향키, 포커스 상실, 일시정지, 지역 전환, 사망, 저장/로드는 경로를 취소한다.
정지 장애물이 25 tick 동안 진행을 막으면 새 목적지를 클릭하라는 안내를 표시한다.

- A*는 8방향, 대각선 코너 통과 금지, 미확인 셀 차단, 고정 동률 순서, 최대 4,096개 탐색 노드다.
  예산 초과/도달 불가는 부분 경로를 성공으로 반환하지 않는다. 동적 객체 충돌은 기존 Core가 최종 판정한다.
- scene JSON의 `NavigateWalls: true`는 몬스터 벽 우회를 활성화한다. 기본값 false는 기존 규칙/세이브의 동작을 보존한다.
  켜진 모드는 월드 콘텐츠 해시에 포함되므로 다른 모드의 저장 파일을 잘못 불러오지 않는다.
- 몬스터 탐색은 tick당 합계 최대 512개 확장 노드다. 직선 통행이 되면 경로 검색을 생략한다.
  예산이 소진된 객체는 이번 tick에 정지한다. 복잡한 군집의 공정성·공간 인덱스·대형 월드 성능은 별도 인수다.
- 플레이어 경로는 입력 명령으로만 Core에 전달한다. NPC 경로 결정은 상태/tick/지도에서 재계산하며 숨은 저장 상태나 RNG를 추가하지 않는다.
  기존 rules-v4 독립 상태 벡터를 유지하고, 옵션이 켜진 미로의 이동·공격·snapshot 이후 결정성을 별도로 시험한다.
- 합성 headless `OPEND2_PLAY04_NAVIGATION_READY`는 클라이언트 경로 진행을 검사한다.
  실제 마우스/키보드 GUI 검증은 여전히 미실행이다.

PLAY-03은 [PR #13](https://github.com/imagineiluv-star/OpenD2/pull/13),
[3개 OS CI](https://github.com/imagineiluv-star/OpenD2/actions/runs/37809879059) 성공 후 master `a29becf`에 병합했다.
PLAY-04 로컬 검증: **249/249**, 기존 rules-v4 독립 벡터, Linux export·배포 smoke 통과.

## PLAY-05: 대표 장면 연결 인수

`--check-scene`은 정적 퀘스트 왕복과 아트 누락을 추가로 보고한다.
`--check-play-ready <game-directory> <scene.json>`은 필수 아카이브, 모든 actor의 아트,
플레이어→NPC→모든 목표→NPC 귀환의 정적 연결을 모두 확인해야 종료 0이다. 미충족은 종료 3이다.

읽은 지도에서 이동 가능 셀의 연결 영역을 만들고 방향성 포털 그래프를 대조한다.
닫힌 지역·벽으로 고립된 목표·편도 포털로 귀환 불가를 구별한다.
4방향 flood fill은 전체 충돌 셀 예산(1,048,576) 안에서 실행하며 대각선 코너를 건너뛰지 않는다.
동적 몬스터/드롭 위치, 실제 전투 생존 가능성, 상호작용 조작 성공을 정적 검사로 보장하지 않는다.

`ReadyForSceneGuiCheck=true`는 해당 작성 장면의 GUI 시험 준비 상태다.
`OriginalRulesValidated=false`, `GuiQa=NOT_RUN`은 계속 유지한다. 원본 테이블·퀘스트 ID에서
대표 캐릭터·아이템·몬스터 규칙을 정확히 연결하려면 실제 파일과 기준 동작 대조가 필요하다.
현재 preview 전투/아이템 규칙을 원본 규칙 호환 완료로 표시하지 않는다.

**미완료인 콘텐츠 작업**: 실제 LoD 지도/캐릭터 경로와 방향·프레임·좌표를 지정한 scene JSON,
원본 게임 테이블의 의미/전투·아이템 규칙 매핑, NPC 실제 아트 인수·전체 UI·미지원 오디오 형식, 실제 화면/조작/청취 대조.
원본 MPQ 미제공 때문에 이들 항목은 이번 합성 검사 통과와 별도로 열린 상태다.

PLAY-05 로컬 검증: 전체 계약 **253/253**, Linux export·배포본 smoke 성공.
추가 4개 계약은 전체 아트 누락, 편도 포털, 지역 단절, 동일 지도 내 고립과 미검증 상태를 확인한다.

PLAY-04는 [PR #14](https://github.com/imagineiluv-star/OpenD2/pull/14)의
[3개 OS CI](https://github.com/imagineiluv-star/OpenD2/actions/runs/37811296686) 성공 후 master `0adc8fb`에 병합했다.

## PLAY-06: 연속 플레이와 제한된 재생 기록

10분(15,000 tick) 또는 4,096개 입력을 기록하면 현재 상태를 새 재생 기준점으로 보관하고
이전 입력 기록만 비운다. 게임 tick·퀘스트·아이템·지역·체력은 유지하며 일시정지하지 않는다.
진단 화면의 `Replay window`와 `rolled`에서 현재 검증 범위를 확인한다.
**Verify replay는 현재 기록 구간만 검사**한다. 전체 세션을 무제한 메모리에 남기는 기능은 아니다.

- 다음 입력을 받기 전에 기준점을 갱신한다. 이미 대기 중인 명령은 snapshot에만, 이후 명령은 새 기록에만 포함해 중복 적용을 막는다.
- 저장 후 로드는 복원한 상태를 새 기준점으로 사용한다. scene별 저장 슬롯과 콘텐츠 해시 검사는 유지한다.
- 계약은 180,001 tick(게임 시간 2시간 상당)의 연속 진행·12회 구간 교체,
  4,096개 명령 경계에서 대기 입력 중복 방지, 거절된 입력·복원 상태의 replay를 확인한다.
- 배포본 `OPEND2_PLAY06_CONTINUOUS_READY`는 실제 클라이언트 RunTick으로 15,001 tick을 진행하고
  비정지·구간 교체·상태 해시 일치를 검사한다. 빠르게 돌린 합성 시험이며 2시간 실제 GUI soak가 아니다.

PLAY-06 로컬 검증: 전체 계약 **256/256**, Linux export·배포본 smoke 성공.
실제 10분 이상 조작, 저장/재실행, 목표 PC에서 2시간 렌더링·메모리·입력 안정성은 GUI 인수 항목이다.

PLAY-05는 [PR #15](https://github.com/imagineiluv-star/OpenD2/pull/15)의
[3개 OS CI](https://github.com/imagineiluv-star/OpenD2/actions/runs/37812531405) 성공 후 master `3ff1150`에 병합했다.

## PLAY-07: 배포본 인수와 증거 경계

릴리즈 handoff에 이 문서를 `LEGACY_PLAY_SETUP.md`로 첨부하고 SHA256SUMS에 포함한다.
manifest/result schema 2는 기본 프리뷰 GUI, 지정 원본 scene GUI, 실제 2시간 soak를 각각 NOT_RUN으로 시작한다.
기존 개별 package metadata는 schema 1을 유지한다. 자동으로 봇을 호출하거나 GUI 결과를 생성하지 않는다.

| 인수 | 필수 자료/조작 | 현재 증거 |
|---|---|---|
| 코드·배포 자동 검증 | 256개 게임 계약, 16개 패키징/다운로드 계약, OS별 export/압축 해제 실행 | 합성/헤드리스 검증; 단계별 CI 링크 참조 |
| 기본 프리뷰 GUI | QA-01~06/08, 설치·퀘스트·아이템·저장·입력 및 10분 경계 | NOT_RUN |
| 지정 원본 장면 GUI | QA-09, 소유 MPQ·scene JSON·사전검사 리포트와 실제 화면/조작 | 자료 미제공, NOT_RUN |
| 목표 PC 장시간 | QA-10, 2시간 실제 렌더링/입력·메모리·성능 기록 | NOT_RUN |
| 원본 규칙/캠페인 | 실제 전투·아이템·퀘스트 기준과 전체 콘텐츠 대조 | 미구현/미검증 잔건 유지 |

기본 결과의 PASS는 synthetic_preview 범위다. 원본 장면은 ContentId·scene JSON 해시·자료 경로/해시,
실제로 시험한 지역/클래스/동작과 QA-09 증거를 별도로 기록한다. 원본 규칙 검증은 계속 false다.
공개 CI/릴리즈에 원본 MPQ나 추출 리소스를 넣지 않는다. 실제 GUI가 불가능하거나 데이터가 없으면
해당 항목을 BLOCKED/NOT_RUN으로 남기고 자동 검증으로 대체하지 않는다.

다음 실제 시험 순서:
1. 사용자가 소유한 LoD 1.10f 데이터로 대표 지도·팔레트·DT1·DCC/COF 경로/방향을 확인한다.
2. 실제 좌표의 scene JSON과 `--check-play-ready` 리포트를 작성한다. 미지원 형식·경로·아트 누락을 먼저 해소한다.
3. PLAY-07 이후 master로 새 rc를 만들어 OS별 실행 파일과 handoff를 받는다.
4. 일반 창에서 QA-01~06/08 및 준비된 QA-09를 조작하고 영상·입력·로그·저장 복원 증거를 수집한다.
5. 대상 PC별 설치/서명·GPU와 QA-10을 별도 인수한다. 실패 항목은 새 기능 브랜치로 수정 후 같은 절차를 반복한다.

기존 `v0.2.0-rc.1`은 통합 전 버전이며 이번 기능 시험에 사용할 수 없다.
세부 봇 지시는 [RELEASE_QA.md](RELEASE_QA.md)와 [grok-task.md](../../eng/qa/grok-task.md)에 있다.

PLAY-06 첫 CI의 macOS 작업은 Godot 다운로드 HTTP 500으로 빌드 전에 실패했다.
이 장애에 대응해 PLAY-07에서 bootstrap 다운로드의 일시 오류를 최대 3회(2초/4초 대기) 재시도한다.
404와 해시 불일치는 재시도하지 않는다. SHA-512를 검증한 뒤에만 캐시에 확정하고 실패한 임시 파일은 정리한다.
관련 6개 계약은 부분 전송 중단·재시도 상한·잘못된 캐시/해시를 포함하며 실제 네트워크를 사용하지 않는다.
로컬 패키징/다운로드 계약 **16/16**, Linux 실제 압축 해제 후 배포 실행 검사도 통과했다.

PLAY-06은 [PR #16](https://github.com/imagineiluv-star/OpenD2/pull/16),
[3개 OS CI](https://github.com/imagineiluv-star/OpenD2/actions/runs/37814052074) 성공 후 master `5834394`에 병합했다.
첫 시도의 Linux/Windows는 통과했고, HTTP 500으로 중단된 macOS 작업만 2차 시도에서 통과했다.


## M2-06b1: 선택 효과음·지역 음악

v0.2.0-rc.2 이후 빌드에서 scene 최상위에 선택 `Audio`를 추가할 수 있다.
아래는 **설명용 경로**다. 실제 설치의 감사 목록에서 확인한 경로로 교체한다.
자료가 없거나 형식이 지원되지 않으면 `Audio`를 생략한다. 임의 경로를 원본 경로로 간주하지 않는다.

```json
"Audio": {
  "Effects": [
    { "Cue": "Hit", "Path": "data/sfx/verified-hit.wav" },
    { "Cue": "Portal", "Path": "data/sfx/verified-portal.wav" }
  ],
  "Music": [
    { "Region": 1, "Path": "data/global/music/verified-town.wav" },
    { "Region": 2, "Path": "data/global/music/verified-dungeon.wav" }
  ]
}
```

`Effects`와 `Music`은 둘 다 배열이며 빈 배열을 허용한다. cue는 대소문자까지 정확히
`Attack`, `Hit`, `Death`, `Loot`, `Quest`, `Portal`만 허용하고 중복은 거절한다.
Music의 Region은 scene Regions에 있는 ID다. 매핑이 없는 지역은 무음이다.
WAV는 PCM8/16, 모노/스테레오, 8~96 kHz만 지원한다. 압축/float WAV 등은 거절한다.
음악은 전체 구간 반복이며 WAV smpl loop 지점·스트리밍은 아직 지원하지 않는다.

MPQ 파일을 읽을 때 기존 patch 우선순위를 적용한다. 파일 누락/손상/미지원 형식이면
scene 로딩이 실패하고 이전 세션을 유지한다. 오디오를 추가/교체해도 기존 ContentId 저장 슬롯은 유지한다.
`--check-scene`/`--check-play-ready`도 지정된 음원을 읽어 검증하지만 실제 청취 PASS가 아니다.
경로별 실제 청취·출력 장치·무음/복귀·region loop는 QA-06/09 증거로 남긴다.

합성 Camp/Cellar는 직접 생성한 짧은 효과 톤만 사용하고 음악은 없다. 원본 scene에는 합성 톤을 대신 넣지 않는다.
메모리/voice 제한·지원 범위·미인수 항목: [M2-06b1 결과](M2_06B_RESULTS.md).
