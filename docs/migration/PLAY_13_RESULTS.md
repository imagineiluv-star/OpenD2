# PLAY-13 — 원본 아이템 참조 정의

기준 master: `1b2aea2` (PLAY-12). 작업 브랜치: `feat/play-13-item-definitions`.

## 목적과 구현

기존 TXT 구문 파서를 원본 아이템의 의미와 연결한다. 사용자 소유 자료에서 코드·분류·가방 크기·장착 부위·
기본 범위·요구치·아이콘 basename을 읽고, 기존 preview 아이템에 명시적으로 연관시킨다.
이번 단계는 참조 데이터 연결이며 **원본 전투·장착·가방 규칙 적용은 아니다**.

- `ItemTables`: 명시적 `lod-1.10f` 프로필, bodylocs/itemtypes/weapons/armor/misc TXT 5종.
  코드별 immutable record/read-only lookup, 원문 이름·namestr key·파일/행·입력 길이/SHA-256 보존.
- 필수 사용 열의 누락/중복, 코드 중복, type/type2/equivalence/bodyloc 참조, equivalence cycle/깊이 64 초과,
  크기 1..10, 수치/범위, 안전한 invfile basename 검증. 사용하지 않는 중복 열은 허용한다.
  weapon의 한손/양손/투척 피해는 분리하며 armor는 자체 reqdex 열을 요구하지 않는다.
- TXT 파일당 8MiB, 합계 32MiB, 아이템 8192·유형 1024·장착 부위 64개 제한.
  기존 TXT 행/열/셀 한도와 scene 합산 입력 128MiB도 적용한다. 정의 자체는 디코딩 픽셀 예산을 사용하지 않는다.
- 선택 `ItemDefinitions`는 `ItemArtwork`와 독립이다. 정확한 preview enum 이름 → 원본 코드 연결을
  I/O 전에 복사·검증한다. TrainingSword는 손 장착 무기, TrainingVest는 몸통 방어구만 연결한다.
  장착 위치는 직접 type 값을 읽으며 equivalence로 장착 규칙을 상속·추측하지 않는다.
- Scene art 검색(최대 100개 표시)·참조 보기·대상 연결·테이블에서 아이콘 경로 채우기.
  팔레트는 사용자가 검증하며 프레임 0은 초기값이다. 실제 파일 존재/미리보기와 저장 검증이 뒤따른다.
  전체 참조 사용 여부·직접 코드 편집·새 복사본 저장/재열기, 실패 시 이전 결과/미리보기/파일 유지.
- 가방/장비 선택 시 원본 정의를 별도 **Reference only** 정보로 표시한다.
  Core RulesVersion 4·GameSave schema 1·기존 두 아이템·8칸 가방·전투 수치는 유지한다.
- 선택 설정과 모든 표의 출처 해시는 scene identity/체크포인트 슬롯에 반영한다.
  설정 없는 기존 scene identity와 gameplay state/save 호환은 유지한다. 새 필드는 구버전 앱에서 거부된다.
- CLI `--items-txt`, scene 보고서 ItemDefinitionSources/ItemDefinitions/ItemDefinitionCount.
  VersionStatus는 unverified, GameplayValidated/ResourceExistenceChecked는 false다.
  준비 개수와 정상 종료는 실제 호환/GUI PASS 판정이 아니다.

## 검증

- 실행형 계약 **317/317** (305 + 신규 12), Python 계약 **16/16** 통과.
- 신규 계약: 필드/출처, I/O 이전 요청 검사, 사용 열, 참조/사이클/선언 순서와 무관한 깊이 제한,
  중복, 경로/수치/크기, 바이트/개수 예산, 종류 연결, snapshot 소유권/identity,
  기존 Core/저장 호환, 새 복사본 저장/실패 보존, scene 합산 예산.
- Godot 합성 smoke: 검색/분류 제한/경로 채우기/미리보기, 읽기 실패 보존·없는 코드 저장 차단,
  저장 중 편집 차단·재열기·참조 비활성화, 상세 참조/기존 피해 값, 잘못된 장면 교체 시 이전 세션 보존.
- 개발용·배포본 필수 marker: `OPEND2_PLAY13_DEFINITIONS_READY`, `OPEND2_PLAY13_DEFINITION_SETUP_READY`.
- Linux locked restore/build 경고·오류 0, 경계·기존 상태 벡터, Godot import/headless,
  self-contained export와 SDK 검색 경로를 제거한 배포본 smoke 통과.
- 압축 패키지와 원격 Windows/macOS/Linux 결과는 PR에 별도로 기록한다.

첫 전체 검사의 Godot 버전 실행에서 로컬 실행 파일 잘림으로 종료됐다. 공식 SHA-512가 일치하는 캐시 압축을
다시 풀어 복구한 뒤 전체 검증을 통과했다. 엔진 오류 검사나 기존 통과 기준을 완화하지 않았다.

## 참고와 코드 출처

필드명·형식·참조 의미를 다음 원본 구현과 대조했으며 외부 구현 코드를 복사하지 않았다.
테스트 표는 자체 합성 자료이며 원본 TXT/MPQ·추출 이미지·원본 DLL은 추가하지 않았다.

- 저장소 `Shared/D2Common_DataTables.hpp`의 D2ItemsTxt.
- [D2MOO ItemsTbls.cpp](https://github.com/ThePhrozenKeep/D2MOO/blob/5596f5cb6c5251a0a07c6637d26458b06099d516/source/D2Common/src/DataTbls/ItemsTbls.cpp):
  code/namestr/type/type2, invfile/크기, 한손/양손/투척/방어, 요구치, BodyLocsLinker 필드 매핑.
- [OpenDiablo2 item loaders](https://github.com/OpenDiablo2/OpenDiablo2/tree/7f92c571bf04057a7fbdfb5d25a486f7d775e3c3/d2core/d2records):
  weapon/armor/misc, item_types, body_locations TXT 로더 구조 참고.

## 남은 작업과 리스크

- 실제 LoD TXT 전체 행/예외·MPQ 패치 우선순위·아이콘/팔레트·native GUI 검증: **NOT_RUN**.
  엄격한 입력 제한에 걸리는 실제 행은 자료와 출처를 확인해 지원해야 하며 조용히 건너뛰지 않는다.
- TBL 지역화, 아이템 BIN, 실행 가능한 옵션/접두·접미/고유·세트/소켓/내구도·스택·드롭 규칙은 미구현이다.
  원본 코드 연관은 아직 Core 아이템 인스턴스나 원본 세이브 본문 변환이 아니다.
- 다음 PLAY-14는 다중 칸 가방과 명시적 규칙/저장 변환이다. 크기를 즉시 기존 InventorySlot 의미에
  덮어쓰면 저장·재생·장착 해제에서 손실이 생길 수 있으므로 별도 버전 경계로 진행한다.
- 실제 원본 장면 시각 인수, 새 테스트 릴리즈·개발 도구 없는 PC 설치, 마나/스킬/벨트,
  바닥 아이템·장비별 캐릭터 아트, D2R 수준 3D·오픈월드는 후속 범위다.
- 공개 `v0.2.0-rc.3`에는 PLAY-08~13이 없다. 이번 코드 작업은 새 공개 릴리즈를 발행하지 않는다.
