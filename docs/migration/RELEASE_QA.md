# 출시 후보와 Grok Bot 사용 테스트

작업 ID: RELEASE-QA-01. 기준: 2026-10-08, xAI Grok Bot 공식 문서.
범위: **3개 OS 패키지 생성·재실행 검사·prerelease 발행·봇에 전달할 시험 자료**.
워크플로 성공은 GUI 테스트 완료를 뜻하지 않는다. Bot 실행·결과 자동 회수·정식 버전 승격은 미구현이다.

## Original hash list (원본 해시 목록, 태그 릴리즈 필수)

태그(`vX.Y.Z`, `vX.Y.Z-rc.N`) 릴리즈는 **의도적으로** 원본 파일 SHA-256 목록이 있어야만 패키징된다. 기본 목록은 저장소에 이미 커밋된
`src/OpenD2.Assets/Manifests/d2-mpq-manifest.json`(#108, 지원 MPQ의 해시와 크기만 있음, 게임 데이터 아님)이라 시크릿 없이도 충족된다.
아래 시크릿/환경 변수는 해시를 **추가**하는 선택 사항이다. 목록이 없거나
항목이 5개 미만이면 `release.py pack`/`release-local.py`가 `MissingOriginalHashes`로 실패하고 아카이브를 남기지 않는다.
목록은 정품 설치본의 sha256sum 형식 파일(`<hash>  <basename>`, 설치 경로 없이)이며 게임 파일 자체는 아니다. 저장소에는 절대 커밋하지 않는다.

- 로컬: `OPEND2_ORIGINAL_SHA256SUMS=<설치본>/SHA256SUMS` 또는 `D2_DATA_PATH=<설치본>`.
- GitHub Actions: 저장소 Settings → Secrets → Actions에 `OPEND2_ORIGINAL_SHA256SUMS` 이름으로 목록 내용을 넣는다.
  `release.yml`은 이 시크릿 하나만 명시적으로 `m0.yml`에 넘긴다. `m0.yml`은 태그 릴리즈 호출(`release_tag`가 비어 있지 않고
  `ci`가 아님)에서만 값을 주입하고, push/pull_request/`ci` 실행에는 넘기지 않는다. 이것은 사고 방지일 뿐 보안 경계가 아니다
  (`release.yml`은 아무 브랜치에서나 dispatch할 수 있고 그 브랜치의 `m0.yml`/`release.py`를 쓴다). 경계가 필요하면 시크릿을 태그·master만
  허용하는 GitHub Environment(`release`)에 둔다. 내용은 MPQ 해시라 커밋된 매니페스트와 겹치며 민감도는 낮다. pack 단계는 `umask 077`에서 `mktemp`로 만든
  임시 파일에 쓰고 `trap`으로 지운다. 값은 로그에 출력하지 않는다.
- **추출 캐시 해시(자동):** `release.py pack`, `release-local.py`, `data_guard.py`는 실행할 때 `~/.cache/opend2/asset-cache`
  (또는 `OPEND2_ASSET_CACHE`, `none`이면 끔) 아래의 `cache-manifest.json`(converter `p1-mpq*`)이 있는 폴더에서 64바이트 이상인 모든 파일의
  SHA-256을 계산해 차단 목록에 더한다. 그래서 #108이 추출한 DC6/TXT/WAV와 캐시의 PNG 변환본을 그대로 복사해도 잡힌다.
  이 해시는 커밋하지 않고 실행할 때마다 다시 만든다(약 5만 개, 수 초). 최소 5개 조건은 원본 목록에만 적용된다.
- 태그 릴리즈에서 추출 캐시 해시가 0개면(CI 러너처럼 캐시가 없을 때) 경고를 출력하고 매니페스트 `warnings`에 남긴다.
  형식을 알 수 없는 바이너리 엔트리는 차단하지 않지만 `unknown_formats` 목록으로 기록해 릴리즈마다 차이를 리뷰할 수 있게 한다.
- **남는 위험:** 변환 파생물(캐시 밖에서 다시 인코딩·크기 조정·재압축한 PNG/OGG, 다른 포맷으로 옮긴 테이블)은 이름, 매직, 해시
  어느 것으로도 탐지할 수 없다. 이런 파일이 들어가지 않게 하는 것은 리뷰와 저장소 규칙의 몫이다. 저장소/히스토리/LFS 검사도 아직 없다.
- 태그 없는 `ci` 빌드와 `--rehearsal`(`OPEND2_RELEASE_REHEARSAL=1`)은 경고만 하고 결과를 `PASS (no original hash list)`로 표시한다.

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
PLAY-07부터 manifest/result는 기본 `synthetic_preview` GUI, 원본 scene GUI, 2시간 soak 상태를 분리한다.
**rc.5부터 schema 3**이며 온라인 서버 패키지(`server_packages`)와 별도의 `online_qa_status`/`online_case_ids`(QA-15~18)를 추가한다.
개별 package 메타데이터는 schema 1을 유지한다. 기존 결과 수집기가 있다면 schema 3을 읽도록 수정하고 누락 상태를 통과로 추정하지 않는다.
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
필수 QA-01~06/08/11~14을 최대 20분 동안 키보드·마우스로 시험해.
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
- QA-01~06/08/11~14, 모델 다운로드 없음, 20분/실패 재시도 1회, 진행 중인 시험과 직렬 처리.
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
- 현재 원본 전투/아이템 규칙, NPC 실제 아트 인수·HUD·음향, 전체 캠페인과 3D는 별도 잔건이다.
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


### rc.5 준비 — 서버 패키지와 온라인 QA 계획

릴리스에 **self-contained 온라인 서버**가 `OpenD2-<버전>-server-<runtime>.zip`(linux-x64, win-x64, osx-arm64)으로 포함된다. 서버 압축은 CI의 `validate`가
압축을 푼 서버를 실제로 실행해 TLS, 두 패키지 클라이언트의 게임플레이·재접속, 협동 월드, 네트워크 장애, 32개 스트림(Arena·월드) 검사를 통과한 뒤에만
아티팩트가 되며, `release.py prepare`가 **같은 커밋의 출처, 체크섬, 필수 PASS 항목**을 다시 확인한다(어느 하나라도 틀리면 handoff 실패).
확인한 압축은 같은 바이트의 버전 이름으로 바뀌고 `server-package-<OS>.json`과 `release-manifest.json`의 `server_packages`에 검증 결과가 남는다.
`physical_multi_pc`, `wan`, `packet_loss`, `maximum_server_capacity`, `gui`가 `NOT_RUN`이 아닌 서버 메타데이터는 과장으로 보고 거부한다.
`qa_plan=RC5`이고 `FEATURE_QA.md`는 [RC5_QA](RC5_QA.md)다. 필수 목록(QA-01~06/08/11~14)은 그대로이고 온라인 QA-15~18은 선택 항목이며 `online_qa_status`로 따로 기록한다.
이 변경은 릴리스를 **발행하지 않는다**. `publish=false`(기본)인 수동 실행은 Actions 아티팩트만 만든다.

서버 패키지에는 이후 추가된 직업·레벨·스킬, 몬스터 종류, 방 경험치 표시, Open 파일 형식 4가 들어 있고 `/health`에 `characterProgress`·`monsterKinds`·`tables`가 추가됐다. 서버 폴더의 문서에 [CHARACTER_PROGRESSION](CHARACTER_PROGRESSION.md)과 [MONSTER_KINDS](MONSTER_KINDS.md)가 함께 들어가고, 운영자는 `--tables <폴더>`로 본인이 꺼낸 표를 읽게 할 수 있다. 이 숫자들은 모두 미리보기이며 원본 값이 아니다. 시험 지침은 [RC5_QA](RC5_QA.md)의 "rc.4 이후 이 후보에 들어간 것"과 QA-16~18에 있다.

### rc.4 준비 — PLAY-17 QA 계획

새 후보는 필수 QA-11~14(격자 가방·마나/강타·물약/벨트·선택 줍기)를 포함한다.
manifest/result schema 2에 `qa_plan=PLAY-17`을 추가하고 manifest의 `required_case_ids`를 명시한다.
기존 수집기는 고정 QA-01~10 목록 대신 이 필수 목록을 확인해야 한다. 누락 사례는 NOT_RUN이며 전체 PASS가 아니다.
함께 배포하는 `FEATURE_QA.md`의 구체적인 조작과 판정을 따른다. 세 OS는 서로 다른 결과 파일로 기록한다.
각 20분 회차에서 끝내지 못한 항목은 그대로 남긴다. 다른 커밋/해시의 결과는 합치지 않는다.
