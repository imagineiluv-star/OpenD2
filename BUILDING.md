# C# / Godot 마이그레이션 개발

기준: `master`, M2 작업: `feat/m2-playable-slice`. MPQ 읽기·인벤토리, Palette·text TBL·DC6·DCC·COF·DT1·DS1 파서, TXT/1.10f 맵 BIN 로더·참조 검사·타일 캐시와 이미지/애니메이션/지도 뷰어를 구현했다. M2-01~03 고정 tick·충돌·근접 전투·AI·재생에 합성 마을·던전 이동, 지역 상태 보존과 NPC 퀘스트 한 흐름을 연결했다. 실제 원본 캠페인과 고품질 아트는 후속 범위다. 기존 C++ 빌드와 별도로 운영한다.

## 개발자 설치

- .NET SDK **10.0.401** (`global.json`, 정확한 버전 필요)
- Python **3.12+**, Git
- CMake **3.25+**, C++17 컴파일러 (Windows: Visual Studio C++ Build Tools, macOS: Xcode Command Line Tools, Linux: GCC/Clang)
- Godot **4.7.2 .NET** 및 같은 버전의 .NET export templates

저장소 루트에서 실행:

```sh
python eng/bootstrap.py
python eng/build-native.py
python eng/build-npc.py
python eng/validate.py --export Linux
# Windows x64: --export Windows
# macOS arm64: --export macOS
```

bootstrap은 공식 Godot 배포본을 내려받아 `eng/toolchain.json`의 SHA-512로 검사한다. SDK는 별도 설치한다. bootstrap 출력의 실행 파일로 `src/OpenD2.Client/project.godot`를 열면 된다. 기존 Godot 설치를 사용하려면 `python eng/validate.py --godot <실행파일경로> --export Linux`를 사용한다.

`eng/validate.py`는 참조 경계 검사 → locked restore → 전체 Debug 빌드 → 전체 계약 테스트 → Godot import → 헤드리스 시작 검사 → 선택한 OS export·SDK 검색 경로를 제거한 배포본 실행 순으로 실행한다. Godot이 오류를 출력하고도 종료 코드 0을 반환하는 경우도 실패로 취급한다. 작은 프로젝트의 재현성을 위해 MSBuild 병렬도를 제한했다.

Godot 4.7.2는 Android 프리셋이 없는 desktop 프로젝트에서 불필요한 장치 감시 스레드를 시작하지 않는다.
4.6.3에서 반복된 EditorSettings 종료 경합과 버전 선정 근거는 [CI 수명 검증 기록](docs/migration/CI_EDITOR_LIFECYCLE.md)을 참조한다.
`python eng/check-editor-lifecycle.py --iterations 12`로 별도 임시 프로젝트의 cold import와 export 종료를 반복 검사한다.
macOS CI에서는 이 검사를 항상 수행하고, 실제 게임의 .NET 내보내기·압축 해제 후 실행도 별도로 검사한다.
오류 발생 시 즉시 실패하며 재시도나 ERROR 예외 목록은 없다. 단계별 원문 로그는 `artifacts/validation/`과
`OpenD2-validation-<OS>` 아티팩트에 남긴다. 헤드리스 통과는 실제 GUI 플레이·오디오·DPI 검증을 뜻하지 않는다.

Godot 없이 코어를 검증할 수 있다:

```sh
dotnet build tests/OpenD2.Tests -c Release -m:1
dotnet run --project tests/OpenD2.Tests -c Release --no-build
dotnet run --project tools/OpenD2.AssetAudit -- --help
```

Tests는 외부 테스트 프레임워크가 없는 실행형 계약 테스트다. **`dotnet test` 대신 위 `dotnet run` 명령을 사용한다.** AssetAudit의 사용법과 인벤토리 한계는 [M1 검증 기록](docs/migration/M1_RESULTS.md)을 참조한다. `build-native.py`는 고정 StormLib 소스를 빌드하고 OS별 라이브러리를 클라이언트·CLI·테스트에 포함한다. 플레이어에게 C++ 빌드 도구를 요구하지 않는다.

M1-04 디코더·뷰어·`--decode` 사용법과 한계는 [M1-04 기록](docs/migration/M1_04_RESULTS.md)을 참조한다.

M1-05 DCC/COF 합성·애니메이션 사용법과 인수 잔건은 [M1-05 기록](docs/migration/M1_05_RESULTS.md)을 참조한다.

M1-06 DT1/DS1 지도·레이어·충돌 뷰어의 사용법과 한계는 [M1-06 기록](docs/migration/M1_06_RESULTS.md)을 참조한다.

M1-07 TXT/BIN·참조 검사·타일 캐시와 Map 탭의 **Resolve table paths** 사용법은 [M1-07 기록](docs/migration/M1_07_RESULTS.md)을 참조한다.

```sh
dotnet run --project tools/OpenD2.AssetAudit -- --tables-txt /path/to/game
# Original item TXT reference definitions (not gameplay rule import)
dotnet run --project tools/OpenD2.AssetAudit -- --items-txt /path/to/game
# BIN은 사용자가 1.10f 스키마를 명시적으로 선택한다. 자동 버전 검출이 아니다.
dotnet run --project tools/OpenD2.AssetAudit -- --tables-bin-110f /path/to/game
```

시작 메뉴에서 **New game**을 선택한 뒤 **Simulation** 탭에서 그리드를 클릭하고 방향키로 이동한다. 초록색 **Camp Guide** 근처에서 **E / Interact**로 퀘스트를 수락한다. 금색 **Cellar** 포털 근처에서 E로 던전에 들어가 **Space / Attack nearest**로 세 몬스터를 처치한다. 던전 입구의 **Camp** 포털로 돌아와 Guide에게 E로 완료를 보고하면 체력을 한 번 회복한다. 지역을 오가도 체력·몬스터 위치·사망·퀘스트는 현재 실행 중에 유지된다. 공격 중에는 상호작용이 실패할 수 있으므로 Space를 놓고 E를 누른다.

회색 벽과 보라색 미확인 셀은 이동을 막는다. **Show diagnostics**를 켜면 Seed·Step one tick·Signal·Verify replay와 tick/hash가 나타난다. 사망 후 또는 Seed 변경 후에는 **New run**으로 처음부터 시작한다. Save checkpoint / Load checkpoint로 사용자 saves 폴더의 슬롯을 저장/복원한다. 로드 후 Resume으로 진행한다. 원본 scene은 ContentId별 저장 슬롯을 사용한다. 조작·판정은 [M2-05 기록](docs/migration/M2_05_RESULTS.md), 최신 통합/연속 플레이는 [PLAY 기록](docs/migration/PLAY_INTEGRATION.md)을 참조한다.

시작하면 **Simulation** 탭의 시작 메뉴가 열리고 게임 시간은 멈춘다. **New game**은 현재 장면/Seed로 시작하고, **Continue current session**은 메모리의 진행을 이어간다. 이미 Pause 상태였다면 **Return to paused session**으로 돌아가며 Resume을 눌러야 진행한다. **Menu (Esc)** 또는 Esc로 메뉴를 다시 열 수 있다. 처음 실행에서는 Continue를 비활성화한다.
시작 메뉴의 **Load checkpoint**는 현재 장면의 파일 또는 `.bak`이 있을 때 활성화한다. 불러온 뒤 메뉴에서 내용을 확인하고 Continue를 누른다. 실패하면 기존 세션과 파일을 유지한다. 진행 중 새 게임/불러오기는 확인창을 표시한다. 메뉴에서는 NPC의 대기 대화를 취소하며 이동·전투 입력을 받지 않는다. 설정은 왼쪽에서 조정할 수 있다.
시작 메뉴/체크포인트 검증 범위는 [M2-06d 기록](docs/migration/M2_06D_RESULTS.md)을 참조한다. 상단 HUD는 체력 바/HP 숫자·지역·진행/일시정지/사망 상태를 표시한다.
게임 화면과 자주 쓰는 버튼을 위에 두고, 리소스·진단·NPC AI 설정은 아래로 스크롤해 접근한다.
왼쪽 FPS 제한(30~240)·Fullscreen·Show diagnostics는 즉시 적용되며 **Save settings**를 눌러야 재실행 후 유지된다.
**F11**은 전체화면을 전환한다. 최소 창 크기는 1000×680이며 더 큰 창에서는 컨테이너가 가용 공간에 맞춰 확장된다.
전체/효과/음악 음량(0~100)과 Mute audio도 즉시 적용되며 Save settings로 유지한다. 합성 장면은 짧은 효과 톤만 재생한다. 원본 scene PCM WAV 효과·지역 음악은 [오디오 연결 절차](docs/migration/PLAY_INTEGRATION.md)를 따른다.
실제 DPI·다중 모니터·전체화면 전환·키보드 포커스·청취는 QA-06/09의 GUI 인수 대상이다.

10×4 격자 가방과 Weapon/Body 슬롯에서 아이템을 선택하면 상세 수치와 가능한 장착/해제/버리기 조작이 나타난다.
New run과 장면 교체는 확인 후 실행한다. 취소하면 이전 일시정지 상태로 돌아간다.
원본 지형 장면은 **Check data directory → Map → Load map → 셀 위치 지정 → Validate and create scene → Load generated scene**으로 만들 수 있다. 아트/오디오 없는 지형 프리뷰이며 원작 화면 전체를 재현하지 않는다. 자세한 절차는 [PLAY-08](docs/migration/PLAY_INTEGRATION.md#play-08-json-수동-작성-없는-첫-지형-장면)을 따른다. 시작 메뉴에서도 **Load original scene JSON / Load remembered scene**을 사용할 수 있다.

원본 scene을 성공적으로 읽은 뒤 Save settings를 누르면 경로가 저장된다. 재실행 후 **Load remembered scene**으로 재사용하며 자동 로드하지 않는다.

원본 캐릭터/몬스터 아트는 **Map → Edit generated artwork** 또는 **Scene art → Open scene JSON for artwork**에서 설정한다. 배우별 다섯 동작의 DCC/COF 경로·레이어·8방향·FPS를 입력하고 기존 DCC-COF 탭에서 미리본다. 검증 후 새 장면 복사본으로 저장하며 원본 파일은 유지한다. [PLAY-09 절차](docs/migration/PLAY_INTEGRATION.md#play-09-캐릭터몬스터-아트-연결-화면).

PLAY-10 이후 같은 목록의 **Guide**는 **Idle** 한 동작과 **Guide fixed facing**을 설정한다. NPC는 정해진 위치에서 대기 애니메이션을 반복하며 Pause에서 멈춘다. 새 복사본을 저장하고 Load saved copy로 연결한다. 설정하지 않으면 원형 표시를 유지한다. [NPC 설정·JSON·준비 상태](docs/migration/PLAY_INTEGRATION.md#play-10-guide-npc-대기-아트). 공개 v0.2.0-rc.3에는 PLAY-08/09/10이 없다.

PLAY-11 이후 **Scene art → HUD artwork settings**에서 소유 자료의 UI 팔레트·DC6 프레임·배치를 지정한다. Decoration/Health/Mana/Menu/Inventory를 지원하며 Preview HUD at 50% health/mana → 새 복사본 저장 → Load saved copy로 연결한다. 게임 아래에 비율을 유지해 표시하며 체력과 기존 메뉴/인벤토리 동작을 사용한다. [설정/범위](docs/migration/PLAY_INTEGRATION.md#play-11-원본-hud-이미지-연결). 마나와 첫 preview 스킬은 PLAY-15에 추가했고 원본 스킬/벨트/UI 전체 재현은 미구현이며 rc.3에는 포함되지 않는다.

PLAY-12 이후 **Scene art → Item artwork settings**에서 Use item artwork·아이템별 사용을 켜고 팔레트/DC6 경로/프레임을 입력한다. Preview item icons → 새 복사본 저장 → Load saved copy로 기존 가방/장비 슬롯에 적용한다. 매핑 없는 항목은 이름으로 표시된다. 격자는 PLAY-14에서 추가했고 원본 전투 수치 이관과 별도이며 rc.3에는 없다. [설정/범위](docs/migration/PLAY_INTEGRATION.md#play-12-원본-아이템-아이콘-연결).


## 실행과 배포

출시 후보는 Actions의 **Release candidate and QA handoff** (`release.yml`)를 사용한다.
`v0.2.0-rc.1` 형식의 버전과 `publish` 여부를 선택한다. 기본값은 발행 없는 검증이며,
발행 시에도 GUI QA 대기 상태의 prerelease만 만든다. [실행 절차와 Grok Bot 설정](docs/migration/RELEASE_QA.md).
CI 다운로드는 `OpenD2-M2-<OS>` 아티팩트 안의 OS별 압축 파일 전체를 풀어 사용한다.

`artifacts/<OS>/` **전체 디렉터리**를 전달한다. 실행 파일 옆의 PCK와 런타임 디렉터리를 함께 배포해야 한다. 플레이어에게 SDK·Godot 편집기·Python·DB 설치를 요구하지 않는 self-contained export를 사용한다. OS 기본 시스템 라이브러리와 그래픽 드라이버는 필요하다.

현재 게임 판정은 이 실행 파일 안에서 `OpenD2.Core`가 수행한다. 외부 게임 서버·로그인·DB는 필요하지 않다. 네트워크 협동 및 그래픽 없는 `OpenD2.Server` .NET 호스트는 M5 예정이며 현재 별도 서버 실행 파일은 없다. 역할과 확장 조건은 [아키텍처 ADR-002](docs/migration/ARCHITECTURE.md#adr-002-로컬-우선-선택적-협동-세션)를 참조한다.

- Linux: `OpenD2.x86_64`
- Windows: `OpenD2.exe`
- macOS: ZIP을 풀어 앱 실행. 현재는 서명·공증 전 개발 빌드이며 일반 사용자용 설치 인수 전이다.

설정은 OS의 `LocalApplicationData/OpenD2/settings.json`에 저장한다. 동일 루트 아래 `saves/`, `cache/`, `logs/`, 생성 장면용 `scenes/`를 사용한다. 설정 저장 시 이전 파일을 `.bak`으로 남긴다. 손상/미래 버전 설정은 자동 덮어쓰지 않는다. 여러 인스턴스의 동시 설정 저장은 아직 지원하지 않는다.

## 구조

| 프로젝트 | 책임 |
|---|---|
| OpenD2.Core | 설정·계측·고정 tick·전투·AI·지역·퀘스트·재생, 엔진 미참조 |
| OpenD2.Assets | 읽기 전용 MPQ·포맷 파서·캐시·충돌 변환, Core만 참조 |
| OpenD2.Npc | 비동기 대화·검증·기본 대사·로컬 추론·선택 모델 설치, Core만 참조 |
| OpenD2.Client | Godot 표현·입력·뷰어·합성 마을/던전·오프라인 실행 |
| OpenD2.AssetAudit | 엔진 없는 리소스 점검 CLI 기반 |
| OpenD2.NpcEval | 실제 모델 선택 다운로드·한국어 의도 평가 CLI |
| OpenD2.Tests | 파일 손상·백업·경로·계측·로그 계약 검증 |

루트 `OpenD2.sln`은 전체 개발용이다. 클라이언트 폴더의 `OpenD2.Client.sln`은 Godot export가 요구하는 솔루션이다. 프로젝트 참조를 바꿀 때 두 솔루션을 함께 갱신한다. `TargetFramework`는 클라이언트 csproj에도 명시해 Godot 자동 마이그레이션이 net8.0을 추가하지 않도록 한다.

빌드 출력·사용자 게임 데이터·추출 캐시는 커밋하지 않는다. 의존성을 바꿀 때 lock 파일을 의도적으로 갱신하고 출처를 [의존성 기록](docs/migration/M0_DEPENDENCIES.md)에 추가한다. 실제 수행한 결과와 남은 인수는 [M0 검증 기록](docs/migration/M0_RESULTS.md)을 참조한다.

몬스터 처치 후 청록색 아이템 표식에 접근해 **F / Pick up nearest**로 줍는다. 아이템 목록에서 선택하고 **Equip selected / Unequip selected / Drop selected**로 장착·해제·버리기를 수행한다. 가방은 10×4 격자이며 기본 검은 1×3, 방어구는 2×3칸이다. 장비는 무기·몸통 두 슬롯이다. 피해 범위와 방어력은 장착 결과에 따라 바뀐다. 아이템·장비도 체크포인트에 포함된다. 저장하지 않은 진행은 앱 종료 시 잃는다.

원본 세이브 사전검사: `dotnet run --project tools/OpenD2.AssetAudit -- --inspect-save /path/to/character.d2s`. v96 헤더/전체 checksum을 읽기 전용으로 검사하며 원본 캐릭터 가져오기나 본문 검증은 지원하지 않는다. 자체 체크포인트와는 별개 형식이다.

Simulation 탭 하단에서 Camp Guide에게 **안녕 / 퀘스트 / 수락 / 완료** 또는 **hello / quest / accept / turn in**을 입력하고 **Talk to Guide**를 누른다. 수락/완료 제안은 **Confirm quest action**으로 확인한 뒤 다음 tick에 판정한다. Pause 상태에서는 Resume 또는 Step이 필요하다. 기본 모드는 고정 대사·3개 의도를 사용하는 오프라인 프리뷰다. NPC-02a는 선택형 로컬 모델로 한국어 의도를 해석하지만 대사는 계속 게임 사실로 만든다. 대화는 저장되지 않고, 새 게임·지역 이동·사망·저장/로드·재생 검사 때 대기 응답과 제안을 취소한다. [구현/검증 기록](docs/migration/NPC_01_RESULTS.md), [후속 모델 도입 계획](docs/migration/NPC_LLM_DESIGN.md). 기본 플레이에 모델·API 키·추론 서버 수동 설치는 필요 없다.


## 선택형 로컬 NPC AI (NPC-02a 실험)

`eng/build-npc.py`는 고정 llama.cpp v0.6.0 소스를 CPU 전용 실행 파일로 빌드한다. CI/export에 실행 파일과 라이선스를 포함하고 모델 가중치는 넣지 않는다. Windows/Linux x64는 AVX2·FMA·F16C, macOS AI는 Apple Silicon을 대상으로 한다. 기본 macOS 게임 패키지는 universal이지만 Intel Mac AI 런타임은 포함하지 않는다. 지원하지 않는 PC도 기본 대사를 쓸 수 있다. GPU 가속과 서명·공증은 별도 인수다.

Simulation 탭 하단에서 모델/용량을 확인하고 **Download model → Start local AI**를 누른다. Qwen3 0.6B Q4는 약 397 MB, 비교용 1.7B Q8은 약 1,834 MB를 다운로드한다(10진 바이트 기준). 인터넷은 설치 시에만 필요하다. 다운로드와 실행은 별도 선택이며 앱을 다시 열면 항상 기본 대사로 시작한다. **Cancel**은 다운로드를 중단하고, 다시 다운로드하면 이어받는다. **Use basic dialogue**는 추론 프로세스를 종료한다. **Remove model**은 선택한 모델/부분 다운로드를 지운다. 모델은 사용자 데이터 루트의 `npc-models/`에 보관한다.

해시/크기 검증을 통과한 모델만 실행한다. 추론은 임시 키로 보호한 `127.0.0.1` 프로세스에 한 요청씩 보내며 외부 API로 대화를 전송하지 않는다. 실패·8초 만료에는 사실 기반 대사를 사용한다. Start 실패나 모델 프로세스 종료 후에는 다시 실행할 수 있다. 이 기능은 의도 오분류가 가능한 실험이며 자유 대화·영속 기억·자율 계획 구현이 아니다. [실측 결과와 남은 인수](docs/migration/NPC_02_RESULTS.md).

개발자가 합성 한국어 100문장을 재현하려면 런타임을 먼저 빌드한다. 아래 `install` 명령은 명시적으로 모델을 다운로드한다. 기본 계약/CI는 모델을 받지 않는다.

```sh
dotnet run --project tools/OpenD2.NpcEval -- install qwen3-06b-q4 .local-tools/npc/models
dotnet run --project tools/OpenD2.NpcEval -- evaluate qwen3-06b-q4 .local-tools/npc/models tools/OpenD2.NpcEval/korean-intents-v1.json artifacts/npc06-evaluation.json
# 비교: 두 명령의 model-id를 qwen3-17b-q8로 변경
```

PLAY-13 이후 **Scene art → Original item definitions**에서 소유 LoD TXT 5종을 읽고 코드/이름을 검색한다. 대상 preview 아이템을 선택해 **Use selected definition and inventory image path**로 연결한 뒤 Item artwork의 팔레트/프레임을 확인하고 미리보기 → 새 복사본 저장 → 로드를 진행한다. 가방 상세 정보에 원본 크기·수치가 참조용으로 표시된다. 전투 수치는 preview 규칙이다. PLAY-14의 선택 설정으로 원본 크기를 격자에 적용할 수 있다. [설정/제약](docs/migration/PLAY_INTEGRATION.md#play-13-원본-아이템-정의-조회와-연결).

PLAY-14 인벤토리에서는 아이템을 드래그하거나 **아이템 선택 → Move selected → 목적 칸 선택**으로 이동한다.
키보드는 Tab/Enter로 같은 동작을 수행하며 칸 버튼에서 Escape로 이동 모드를 취소한다. 목적 칸은 왼쪽 위 기준이다.
빈 장비 칸으로 드래그하면 장착하고 장비에서 가방의 빈 영역으로 드래그하면 지정 위치에 해제한다.
교환은 두 사각형이 모두 들어갈 때만 적용하며 실패하면 원래 위치를 유지한다. 가방 밖으로 끌어내도 삭제하지 않는다.
버리기는 **Drop selected**를 사용한다. 회전·자동 정렬·스택 분할은 미지원이다.

소유 TXT 크기를 쓰려면 Scene art → Original item definitions에서 연결을 설정하고
**Use bound item codes and sizes in the 10×4 bag**를 켠 뒤 새 복사본을 저장한다. 표에서 정의된 크기가 격자를 넘으면 저장을 거부한다.
끄면 기본 preview 크기를 사용한다. 전투·요구치·스택 규칙은 가져오지 않는다.

현재 PLAY-16 저장은 schema 4 / rules 7이다. schema 3 / rules 6는 마나·선택·기존 아이템을 그대로 유지하며 변환한다. schema 2 / rules 5는 가방 배치를 유지하면서 마나 기본값을 추가한다. 이전 schema 1 / rules 4는 이전 상태 hash를 먼저 검증하고 기존 슬롯 순서대로 배치한다.
로드만으로 파일을 바꾸지 않는다. 명시적으로 저장하면 이전 정상 파일은 `.bak`에 남고 새 형식으로 저장된다.
큰 아이템 때문에 들어가지 않으면 변환을 거부한다. 이전 앱으로 짐을 줄이고 저장한 뒤 다시 로드한다.
크기/코드가 다른 장면의 저장은 호환 오류로 보존하며, 새 형식은 이전 앱에서 열 수 없다.
[구현·검증·제약](docs/migration/PLAY_14_RESULTS.md).


PLAY-15의 스킬 목록에서 **Power strike · 12 MP**를 선택하고 근접한 적 옆에서 **Q** 또는 **Cast nearest**를 누른다.
한 번 누를 때마다 한 번 요청하며 가장 가까운 살아 있는 적을 대상으로 한다. 자동 접근은 하지 않는다.
기본 마나 60, 강타 비용 12, 일반 무기 피해 +12, 재사용 대기 1초다. 기존 일반 공격 대기/사거리/벽/피격 중단도 적용한다.
마나는 플레이 중 1초마다 1 회복한다. 메뉴/일시정지는 tick을 멈추므로 회복도 멈춘다.
MP 수치·푸른 바와 부족/대기 안내를 확인한다. HUD artwork에 Mana 역할을 연결하면 해당 DC6가 아래부터 채워진다.
v1/v2/v3 로드는 파일을 바꾸지 않고 메모리에서 검증/변환한다. 명시적 저장 때만 v4로 쓰고 이전 정상 파일을 `.bak`에 남긴다.
백업은 1세대 회전이므로 다음 저장에서 교체된다. 원본 스킬 트리/이펙트/투사체 이관과 별도다.
[판정·저장·검증 범위](docs/migration/PLAY_15_RESULTS.md).


PLAY-16에서는 새 몬스터 처치 시 장비와 물약이 함께 떨어진다. **F**로 가까운 물건부터 줍는다.
짝수 몬스터 ID는 체력 물약(즉시 HP +40), 홀수 ID는 마나 물약(MP +30)을 주는 preview 규칙이다.
가방 물약을 선택해 **Drink selected potion**을 누르거나, 벨트로 드래그/선택 → 목적 슬롯 → **Put selected in belt**로 배치한다.
벨트 슬롯 클릭은 선택이며 **Use (1–4)** 또는 게임 화면에 초점을 둔 **숫자 1–4**가 사용이다. 키를 누르고 있으면 반복 사용하지 않는다.
차 있는 벨트 칸에는 서로 위치를 교환한다. 벨트에서 가방의 빈 칸으로 드래그하거나 **Unequip selected**로 돌려놓을 수 있다.
최대 체력/마나에서는 소비하지 않는다. 한 tick에 성공 사용은 하나이고, 사망/기절/소유권 오류/메뉴·일시정지 사용은 막는다.
물약은 1×1이며 스택·자동 보충·장비에 따른 벨트 줄 확장은 아직 없다. Scene art에서 HealthPotion/ManaPotion DC6를 선택 연결할 수 있다.
이전 저장에서 이미 처치한 몬스터에 물약을 소급 생성하지 않는다. 물약 기록은 저장/재생으로 복제되지 않게 유지한다.
[구현·호환·검증 범위](docs/migration/PLAY_16_RESULTS.md).

PLAY-17에서는 바닥 이름을 클릭하면 그 아이템으로 접근해 한 번 줍는다. 같은 위치의 이름은 겹치지 않게 배치한다.
**L / Loot names**로 표시를 바꾸고, **우클릭**으로 접근을 취소한다. 방향키·다른 행동·메뉴/Escape·지역 이동도 취소한다.
가방 부족/막힌 경로는 메시지를 표시하며 자동 재시도하지 않는다. **F**는 가까운 물건 즉시 줍기로 유지한다.
이름은 지형 위의 선택 오버레이이며 원본 바닥 아트/품질색/글꼴 이관과 별도다.
[검증과 실제 GUI 미실행 범위](docs/migration/PLAY_17_RESULTS.md).
