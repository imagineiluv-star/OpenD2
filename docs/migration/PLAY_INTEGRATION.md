# 원본 리소스 플레이 통합 진행

기준 master: `4b1018c`, 2026-10-08. 각 작업은 직전 병합 master에서 별도 브랜치로 진행한다.
코드·합성 검증, 실제 원본 파일 호환, GUI 플레이 인수를 각각 기록한다.

| 순서 | 브랜치 | 작업 | 상태 |
|---|---|---|---|
| 1 | `feat/play-01-data-check` | 선택 원본 지도 사전검사·출처 해시·공통 로더 | 코드·합성 검증 완료; 실데이터 대기 |
| 2 | `feat/play-02-map-integration` | 지도/충돌/게임 상태와 화면 연결 | 코드·합성 검증 완료; 실데이터/GUI 대기 |
| 3 | `feat/play-03-actor-animation` | 게임 상태→캐릭터/몬스터 애니메이션 | 대기 |
| 4 | `feat/play-04-navigation` | 화면 좌표·마우스 이동·벽 우회 | 대기 |
| 5 | `feat/play-05-content` | 대표 지역·생성 위치·NPC/포털·콘텐츠 연결 | 대기 |
| 6 | `feat/play-06-continuous-session` | 기록 예산과 플레이 진행 분리 | 대기 |
| 7 | `feat/play-07-acceptance` | 배포·실제 GUI·증거/인수 | 대기 |

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
