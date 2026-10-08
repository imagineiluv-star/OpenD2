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
- 프로필이 없는 객체는 기존 도형으로 표시한다. 현재 최대 32개 combat actor artwork 프로필이며 NPC 스프라이트는 별도다.
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
원본 게임 테이블의 의미/전투·아이템 규칙 매핑, NPC 아트·전체 UI·미지원 오디오 형식, 실제 화면/조작/청취 대조.
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
