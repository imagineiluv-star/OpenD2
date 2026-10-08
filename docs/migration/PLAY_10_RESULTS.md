# PLAY-10 — NPC 대기 스프라이트

기준 master: `f89c76f` (PLAY-09). 작업 브랜치: `feat/play-10-npc-artwork`.

## 목적과 구현

Guide NPC는 원형 표시만 가능했고 Scene art의 combat actor 목록에도 없었다.
별도 선택 `NpcArtwork`로 NPC의 Idle 아트를 읽고, 같은 설정 화면에서 검증한 새 scene 복사본을 만든다.

- `LegacyNpcRequest`: Guide ID, 팔레트, Idle DCC/COF·레이어·8방향·FPS, 고정 facing 0..7.
  다섯 전투 동작을 요구하지 않으며 NPC를 전투 actor에 추가하지 않는다.
- 기존 DCC/COF 로더의 파일·방향·프레임·팔레트·예산 검사를 공유한다. NPC 소스의 경로·바이트 수·SHA-256을 보고한다.
  지형/전투 아트/NPC의 입력 128MiB와 decoded pixel 16,777,216 합산 제한을 유지한다.
- scene schema 1의 선택 필드이므로 새 앱은 기존 scene을 읽는다. 미설정 scene의 ContentId는 유지한다.
  설정/리소스 바이트가 바뀌면 ContentId와 체크포인트 슬롯을 구별한다. 구버전 앱은 새 필드를 거부한다.
- NPC Idle은 25Hz 게임 tick으로 반복한다. 같은 tick은 같은 프레임이고 Pause에서 정지하며 복원 tick에서 재개한다.
  기본 X+Y 깊이 정렬에 NPC를 포함한다. 새 텍스처 준비 후 장면을 교체하고 종료/전환 시 해제한다.
- Scene art에 Guide 항목, Idle 전용 탭, 고정 방향 선택을 추가했다. 다른 배우 전환 시 미완성 입력을 유지한다.
  기존 검사기로 미리보기, 새 복사본 저장, 복사본 재열기, Load saved copy를 지원한다.
- `NpcArtworkConfigured`와 `ReadyForAllSpritesGuiCheck`를 보고한다. 기존 `ReadyForSceneGuiCheck` 및 CLI 종료 코드의 의미는 유지한다.
  새 플래그도 GUI 완료 판정은 아니다. `GuiQa=NOT_RUN`, `OriginalRulesValidated=false`를 유지한다.

## 검증

- 실행형 계약 **288/288**: 이전 281개 + 신규 7개.
- 신규 계약: DCC/COF Idle 반복·Pause/reset; 잘못된 방향/동작/경로; Guide 소유권;
  NPC 선택성·전투 actor 수/월드 해시/60 tick 시뮬레이션 상태 불변;
  facing/파일 해시·호출자 변경에서 입력 snapshot 보호; 전체 입력 예산; 저장 왕복/준비 상태 구분.
- Python 패키징/다운로드 계약 **16/16**.
- Godot 합성 통합: NPC 텍스처 생성/크기/불투명 픽셀·장면 해제, Guide Idle 탭/고정 방향,
  배우 전환·복사본 저장/재열기 시 입력 유지, 기존 actor 및 원본 JSON 보존.
- `OPEND2_PLAY10_NPC_READY`를 개발용·배포본 smoke의 필수 marker로 추가했다.
- 최종 Linux 검증 성공: locked restore, 전체 build(경고/오류 0), 경계 검사·기존 상태 벡터,
  Godot import/headless, self-contained export와 SDK 경로를 제거한 배포본 headless smoke.
  원격 Linux/Windows/macOS 및 압축 해제 패키지 결과는 PR CI를 따른다.

초기 입력 예산 테스트는 합성 DCC 뒤에 패딩을 붙여 마지막 방향 bitstream 검사에서 먼저 실패했다.
방향 데이터 앞의 유효한 여백으로 fixture를 수정하고 기존 DCC 검사 기준을 유지했다.
combat 아트만 있는 100MiB 장면은 성공하고 NPC가 추가되어 128MiB를 넘으면 scene 예산 오류로 실패하는 것을 확인했다.

## 남은 인수와 다음 작업

- 실제 LoD MPQ·NPC 경로/프레임/팔레트·native GUI 입력·화면 가림·음향: **NOT_RUN**.
- 합성 텍스처 성공은 실제 디아블로 화면의 방향·색상·타이밍·벽/지붕 효과를 대조한 증거가 아니다.
- NPC는 고정 위치의 Guide 한 명이다. 걷기·대화 모션·바라보기 AI·장비 외형·PL2 특수 효과는 구현하지 않았다.
- 전투·퀘스트·아이템은 기존 preview 규칙이며 전체 캠페인·D2R 수준 3D·오픈월드는 별도다.
- 다음 구현: **PLAY-11 원작 HUD 리소스 연결**. 실제 자료가 확보되면 대표 장면의 플레이어/몬스터/NPC 시각 인수를 병행한다.
- 공개 **v0.2.0-rc.3에는 PLAY-08/09/10이 없다**. 병합 이후 CI 패키지 또는 다음 릴리즈가 필요하다.
