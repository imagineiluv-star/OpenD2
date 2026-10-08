# PLAY-09 — 캐릭터·몬스터 아트 설정 화면

기준 master: `0532a92` (PLAY-08). 작업 브랜치: `feat/play-09-actor-art-setup`.

## 목적과 구현

원본 지형에서 플레이할 수 있어도 원본 배우 아트를 연결하려면 scene JSON의 다섯 동작과 방향/레이어를 직접 작성해야 했다.
Scene art 폼과 기존 DCC-COF 미리보기를 연결해 같은 로더를 거쳐 새 scene 복사본을 만든다.

- Map → Edit generated artwork 또는 Scene art → Open scene JSON for artwork.
- actor ID별 Use artwork, 팔레트, Idle/Walk/Attack/Hit/Death 경로·COF 컴포넌트·8방향 번호·FPS.
- 캐릭터 전환 시 미완성 문자열까지 유지한다. 기존 아트를 가져오며 FPS를 정수/고정 소수 단계로 반올림하지 않는다.
- 동작과 facing을 선택해 DCC-COF 검사기로 전달한다. 파일·방향·프레임 표시와 재생은 기존 검사기를 사용한다.
- 경로 정규화와 레이어/방향 입력 검증은 `LegacyArtworkSetup`에서 공유한다. 파일명·클래스·장비·방향 규칙은 추정하지 않는다.
- 저장은 요청된 모든 아트와 지형·퀘스트 연결을 검증하고 사용자 scenes 폴더의 고유한 새 JSON으로 생성한다.
  일부 actor만 설정한 복사본도 가능하며 미설정 수를 표시한다. 원본 파일/진행 중 게임/기존 세이브는 변경하지 않는다.
- 다른 scene 열기는 편집 교체를 확인한다. 실패/취소는 이전 폼과 저장된 복사본을 보존한다.
- 데이터 폴더 변경 후 저장/미리보기는 재열기를 요구한다. 저장 중 버튼과 편집 필드를 비활성화한다.
- 아트 변경은 기존 ContentId 규칙에 따라 별도 체크포인트 슬롯을 사용한다.

팔레트의 초기값은 배우가 있는 지역의 팔레트이며 실제 배우 색상에 맞는지 확인해야 한다.
새 동작 FPS 12는 입력 예시다. 8방향 매핑은 빈 상태에서 시작하며 확인한 번호를 명시해야 한다.
미리보기 탭에서 수정한 값은 원래 아트 폼에 역반영되지 않는다.

## 검증

- 실행형 계약 **281/281**: 이전 275개 + 신규 6개.
- 신규 계약: DCC/COF 폼 → 다섯 동작 로드; 방향 개수/범위; 안전하지 않은 경로·중복/잘못된 컴포넌트·FPS;
  scene 복사본 왕복/원본 보존/ContentId 변화; 파일에 없는 방향·누락 COF 레이어 거부; 일부 아트의 미완료 상태.
- Python 패키징/다운로드 계약 **16/16**.
- Godot 합성 통합: 빈 폼 상태, 배우 전환 시 미완성 입력 유지, 잘못된 폼 저장 거부, FPS 정밀도,
  저장 중 편집 차단, 두 actor 아트가 있는 복사본 로드, 원본 JSON 바이트 보존, 열기 실패/취소 시 폼 유지.
- `OPEND2_PLAY09_ART_SETUP_READY`를 개발용/배포본 smoke 필수 marker로 추가.
- 최종 Linux 검증 성공: locked restore, 전체 build(경고/오류 0), 경계 검사·기존 상태 벡터, Godot import/headless, self-contained export 및 SDK 경로를 제거한 배포본 headless smoke. 원격 3개 OS 결과는 PR CI를 따른다.

초기 smoke의 deferred Callable이 Task 반환 람다로 해석되어 Godot Variant 변환 오류를 보고했다.
void 람다 블록으로 수정했다. 이 오류를 무시하거나 marker만으로 성공 처리하지 않는다.

## 미완료 인수와 다음 작업

- 실제 LoD MPQ·클래스/몬스터 아트·native GUI 조작·화면/청취: **NOT_RUN**.
- 모든 아트의 디코딩 성공은 실제 방향·색상·프레임 속도·가림 정확성의 증거가 아니다.
  GUI QA는 계속 NOT_RUN, original rules는 false다.
- NPC 스프라이트·장비 교체별 아트·원작 HUD·PL2 특수 효과·캠페인 재현·3D는 미구현/별도 범위다.
- 다음: 실제 자료로 대표 지역/캐릭터/몬스터를 지정해 색상·방향·타이밍 확인 → NPC/원작 HUD 연결 → GUI 플레이 인수.
- 공개 v0.2.0-rc.3에는 PLAY-08/09가 없다. 이번 병합 후 새 CI 패키지 또는 다음 릴리즈가 필요하다.
