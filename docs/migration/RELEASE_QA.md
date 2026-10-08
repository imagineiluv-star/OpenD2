# 출시 후보와 Grok Bot 사용 테스트

작업 ID: RELEASE-QA-01. 기준: 2026-10-08, xAI Grok Bot 공식 문서.
범위: **3개 OS 패키지 생성·재실행 검사·prerelease 발행·봇에 전달할 시험 자료**.
워크플로 성공은 GUI 테스트 완료를 뜻하지 않는다. Bot 실행·결과 자동 회수·정식 버전 승격은 미구현이다.

## GitHub Actions 실행

1. Actions → **Release candidate and QA handoff** → **Run workflow**.
2. `master` 선택. 새 `version`에 `v0.2.0-rc.1` 같은 값을 입력한다.
3. `publish=false`는 검증과 자료 생성만 수행한다. 실행 결과의 `OpenD2-<version>-release`
   아티팩트를 받는다. `publish=true`는 동일 파일을 공개 GitHub **prerelease**로 발행한다.
4. 또는 master에 포함된 커밋에 `vX.Y.Z-rc.N` 태그를 push하면 자동으로 prerelease를 만든다.
   정식 `vX.Y.Z` 태그로는 실행되지 않는다. 기존 파일은 덮어쓰지 않으며 재발행은 새 rc 번호를 사용한다.

기존 `m0.yml`을 `workflow_call`로 재사용한다. 세 OS의 locked restore·계약 테스트·Godot 검사·export,
압축을 풀어 실행하는 검사까지 성공해야 발행한다. Linux는 실행 권한을 보존하는 tar.gz,
Windows는 ZIP, macOS는 Godot ZIP 원본 바이트를 사용한다.
기존 `OpenD2-M2-<OS>` CI 아티팩트도 이제 압축 파일과 메타데이터를 포함한다(보관 14일).
최종 handoff 아티팩트는 30일 보관하며 공개 Release 파일은 이 만료와 별개다.

`release-manifest.json`은 버전·소스 SHA·빌드 URL·파일 해시/크기를 기록한다.
PLAY-07부터 manifest/result는 schema 2이며 기본 `synthetic_preview` GUI, 원본 scene GUI,
2시간 soak 상태를 분리한다. 개별 package 메타데이터는 schema 1을 유지한다.
기존 결과 수집기가 있다면 schema 2를 읽도록 수정하고 누락 상태를 통과로 추정하지 않는다.
`LEGACY_PLAY_SETUP.md`에 scene JSON·아트 방향·좌표·사전검사 방법을 함께 첨부한다.
`SHA256SUMS`는 패키지와 지침/메타데이터를 포함한다. GUI QA는 `NOT_RUN`으로 시작한다.
CI에는 SDK가 설치되어 있으므로 검색 경로를 제한한 검사를 'SDK 없는 실제 사용자 PC' 시험으로 간주하지 않는다.
macOS 서명/공증, Linux GUI, 실제 GPU, 설치 경험, 원본 리소스는 별도 인수다.

기본 `GITHUB_TOKEN`만 사용하며 `contents: write`는 발행 작업에만 부여한다.
`XAI_API_KEY`, PAT, 임의 웹훅 URL은 현재 구현에 필요하지 않다.
Release 발행 이벤트가 다음 워크플로를 실행하리라 가정하지 않고 `needs`로 연결한다.
공개 저장소의 임의 PR을 개인 PC self-hosted runner에서 실행하지 않는다.

## Grok Bot 최초 설정

1. Grok Bot 앱에 로그인하고 QA용 Bot을 만든다. 예: **OpenD2 QA**.
   사용 가능한 요금제/계정 연결을 앱에서 확인한다. 일반 Grok 모델 API 키만으로 이 Bot이 생성되지는 않는다.
2. **Agent Computer**를 열어 봇 컴퓨터를 확인한다. 공식 문서상 클라우드 컴퓨터는 Linux다.
   이 저장소의 Linux 빌드는 x64이므로 `uname -m`, 실제 디스플레이/렌더러를 확인한다.
   Windows/macOS 앱을 설치한 사용자 PC와 봇의 Linux 컴퓨터는 별개다.
3. 공개 prerelease는 GitHub 로그인 없이 다운로드할 수 있다. GitHub 연동을 쓸 경우
   Marketplace의 지원 GitHub 플러그인을 필요한 저장소 읽기 권한으로 연결한다.
   첫 시험에는 코드 쓰기·병합·릴리즈 관리·개인 PC 실행 권한을 줄 필요가 없다.
4. Release URL과 아래 지시를 전달한다. `GROK_TASK.md`와 결과 템플릿은 각 릴리즈에 첨부된다.
   dry run을 이용했다면 아티팩트를 먼저 전달해야 한다.

```text
이 OpenD2 출시 후보를 실제 사용 테스트해줘: <Release URL>
release-manifest.json, SHA256SUMS, GROK_TASK.md, qa-result-template.json을 기준으로 해.
Linux x64 배포 파일의 해시를 확인하고 새 테스트 프로필에서 일반 창으로 실행해.
필수 QA-01~06/08을 최대 20분 동안 키보드·마우스로 시험해.
같은 run의 15,000 tick 경계를 지나도 자동 정지하지 않는지 확인하고 저장/재실행해.
모델 다운로드는 하지 마. 실패 재시도는 1회까지만 해.
qa-result.json, 입력 기록, 스크린샷/영상, 게임 로그를 첨부해줘.
실행하지 못한 항목은 BLOCKED/NOT_RUN으로 기록하고 원인도 알려줘.
소스 재빌드·GitHub 수정·다른 OS 통과 판단은 하지 마.
```

5. 실행 OS·커밋·파일 해시·스크린샷·저장/로드 상태를 대조한다.
   게임 창이 안 뜨면 native 앱/그래픽 지원 여부를 확인하고 BLOCKED로 남긴다.
   headless 성공이나 이미지 설명을 GUI 조작 성공으로 바꾸지 않는다.

## 반복 실행 설정

첫 실증이 성공한 후 이 절차를 **OpenD2 Release QA** skill로 저장하도록 요청한다.
공식 문서는 GitHub 알림 등 계정 integration 이벤트에서 routine을 시작할 수 있다고 안내한다.
**GitHub 플러그인 로그인과 이벤트 integration은 별개**이므로 계정의 지원 여부를 확인한다.
연동 화면에서 실제 지원하는 이벤트 중 다음 조건으로 한정한다:

- 저장소 `imagineiluv-star/OpenD2`, 새 prerelease, 태그 `vX.Y.Z-rc.N`.
- Release ID·태그·manifest commit을 확인하고 같은 후보는 한 번만 실행.
- QA-01~06/08, 모델 다운로드 없음, 20분/실패 재시도 1회, 진행 중인 시험과 직렬 처리.
- 실행 불가·시간 초과는 BLOCKED/NOT_RUN, 결과와 증거는 Bot 대화에 첨부.

Release 이벤트/알림 필터가 없으면 링크를 전달하는 수동 경로를 쓴다.
GitHub Watch만으로 봇 호출이 보장되지는 않는다. 공개 Bot 호출 API/CLI와 callback을
이번 검토에서 확정하지 못했으므로 가상 endpoint나 API secret을 만들지 않았다.
자동 이벤트 실행은 계정의 **Test run**으로 확인하기 전까지 미검증이다.

## 인수와 다음 단계

모든 필수 항목 PASS, 증거 존재, 버전/해시 일치, 오류 로그 확인 후 사람이 인수한다.
FAIL/BLOCKED/NOT_RUN/누락은 통과가 아니다. 정식 승격은 현재 워크플로에 없다.
후속: 봇 실제 1회 실행 → 이벤트 연동 확인 → 신뢰된 결과 회수/기계 검증 → 목표 OS/PC별 인수.
실제 NPC 모델 정답률 51%/83%와 원본 데이터 미제공 한계는 그대로 유효하다.

### 원본 장면과 장시간 인수

PLAY-01~06은 원본 지도·충돌·DCC/COF 표현·클릭 경로·연속 세션을 연결했다.
원본 자료가 제공되기 전까지 지원 범위는 합성 fixture로 검증한 코드다. 모든 원본 형식/캠페인 호환을 뜻하지 않는다.

- **QA-08 필수**: 같은 run에서 일반 창으로 10분 이상 진행해 15,000 tick 경계 전후의 이동,
  replay 일치, 저장/재실행을 기록한다. 빠른 합성 tick 반복은 이 GUI 시험을 대체하지 않는다.
- **QA-09 별도 인수**: 사용자 소유 LoD 데이터 + 실제 경로/좌표/아트 방향을 확인한 scene JSON +
  `--check-play-ready` 리포트가 필요하다. 자료가 없으면 BLOCKED이며 기본 프리뷰 결과에 합치지 않는다.
  ContentId·scene JSON 해시·리소스 경로/해시를 기록하고 실제 지형/아트/충돌/퀘스트 왕복을 조작한다.
  원본 리소스 파일은 공개 결과에 첨부하지 않는다. 결과는 해당 scene에만 적용한다.
- **QA-10 별도 인수**: 2시간 실제 창/렌더링/입력·메모리·지역 전환을 관찰한다.
  기본 20분 실행 제한의 예외는 별도 시험 지시로 정한다. 계약 테스트의 '2시간 상당 tick'은 soak PASS가 아니다.
- 현재 원본 전투/아이템 규칙, NPC 아트·HUD·음향, 전체 캠페인과 3D는 별도 잔건이다.
  `original_rules_validated=false`를 유지한다. GUI 한 장면의 성공으로 이 상태를 변경하지 않는다.

## 개발 검증

```sh
python -m unittest discover -s eng -p 'test_*.py' -v
python eng/release.py check-version v0.2.0-rc.1
```

패키징 시험은 작은 가짜 파일을 사용하며 실제 게임 통과 증거가 아니다.
PLAY-07 기준 패키징 10개와 다운로드 경계 6개를 함께 실행한다. Godot의 일시적 다운로드 오류는
최대 3회 재시도하며 SHA-512 불일치는 그대로 실패한다. macOS CI의 HTTP 500 사례를 반영한 변경이다.
실제 검증은 `eng/validate.py --export <OS>` 후 `RELEASE_TAG`, `SOURCE_SHA`를 환경변수로 지정하고
`python eng/release.py pack --preset <OS> --smoke`를 실행한다. 같은 패키지가 있으면 덮어쓰지 않는다.

2026-10-08 로컬 검증: 패키징 계약 10/10, 기존 게임 계약 228/228, Linux release export와
압축 해제 후 실행 성공. actionlint 1.7.12로 두 workflow 문법/식 검사를 통과했다.
Windows에서는 POSIX 권한 검사를 건너뛰며 해당 검사는 Linux/macOS에서 수행한다.
이후 [v0.2.0-rc.1](https://github.com/imagineiluv-star/OpenD2/releases/tag/v0.2.0-rc.1)을
[3개 OS 릴리즈 검증](https://github.com/imagineiluv-star/OpenD2/actions/runs/37800399047) 후 발행했다.
이 버전은 PLAY-01~07 이전이다. 새 기능 시험에는 이후 master로 새 rc를 생성해야 한다.
Grok Bot/실제 원본 데이터 GUI 시험은 아직 수행하지 않았다.

## 공식 근거

- [Grok Bot 시작](https://docs.x.ai/grok-bot/get-started)
- [컴퓨터와 앱](https://docs.x.ai/grok-bot/computer-and-apps)
- [Linux 환경](https://docs.x.ai/grok-bot/identity-and-access)
- [Skill·routine·이벤트 integration](https://docs.x.ai/grok-bot/skills-routines-and-automations)
- [재사용 워크플로](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows)
- [GITHUB_TOKEN 이벤트 동작](https://docs.github.com/en/actions/how-tos/write-workflows/choose-when-workflows-run/trigger-a-workflow)
- [GitHub CLI release create](https://cli.github.com/manual/gh_release_create)
