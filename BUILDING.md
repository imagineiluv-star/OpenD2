# C# / Godot 마이그레이션 개발

기준: `master`, M2 작업: `feat/m2-playable-slice`. MPQ 읽기·인벤토리, Palette·text TBL·DC6·DCC·COF·DT1·DS1 파서, TXT/1.10f 맵 BIN 로더·참조 검사·타일 캐시와 이미지/애니메이션/지도 뷰어를 구현했다. M2-01~03 고정 tick·충돌·근접 전투·AI·재생에 합성 마을·던전 이동, 지역 상태 보존과 NPC 퀘스트 한 흐름을 연결했다. 실제 원본 캠페인과 고품질 아트는 후속 범위다. 기존 C++ 빌드와 별도로 운영한다.

## 개발자 설치

- .NET SDK **10.0.401** (`global.json`, 정확한 버전 필요)
- Python **3.12+**, Git
- CMake **3.25+**, C++17 컴파일러 (Windows: Visual Studio C++ Build Tools, macOS: Xcode Command Line Tools, Linux: GCC/Clang)
- Godot **4.6.3 .NET** 및 같은 버전의 .NET export templates

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

`eng/validate.py`는 참조 경계 검사 → locked restore → 전체 Debug 빌드 → 268개 계약 테스트 → Godot import → 헤드리스 시작 검사 → 선택한 OS export·SDK 검색 경로를 제거한 배포본 실행 순으로 실행한다. Godot이 오류를 출력하고도 종료 코드 0을 반환하는 경우도 실패로 취급한다. 작은 프로젝트의 재현성을 위해 MSBuild 병렬도를 제한했다.

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

가방 8칸과 Weapon/Body 슬롯에서 아이템을 선택하면 상세 수치와 가능한 장착/해제/버리기 조작이 나타난다.
New run과 장면 교체는 확인 후 실행한다. 취소하면 이전 일시정지 상태로 돌아간다.
원본 지형 장면은 **Check data directory → Map → Load map → 셀 위치 지정 → Validate and create scene → Load generated scene**으로 만들 수 있다. 아트/오디오 없는 지형 프리뷰이며 원작 화면 전체를 재현하지 않는다. 자세한 절차는 [PLAY-08](docs/migration/PLAY_INTEGRATION.md#play-08-json-수동-작성-없는-첫-지형-장면)을 따른다. 시작 메뉴에서도 **Load original scene JSON / Load remembered scene**을 사용할 수 있다.

원본 scene을 성공적으로 읽은 뒤 Save settings를 누르면 경로가 저장된다. 재실행 후 **Load remembered scene**으로 재사용하며 자동 로드하지 않는다.

원본 캐릭터/몬스터 아트는 **Map → Edit generated artwork** 또는 **Scene art → Open scene JSON for artwork**에서 설정한다. 배우별 다섯 동작의 DCC/COF 경로·레이어·8방향·FPS를 입력하고 기존 DCC-COF 탭에서 미리본다. 검증 후 새 장면 복사본으로 저장하며 원본 파일은 유지한다. [PLAY-09 절차](docs/migration/PLAY_INTEGRATION.md#play-09-캐릭터몬스터-아트-연결-화면).

PLAY-10 이후 같은 목록의 **Guide**는 **Idle** 한 동작과 **Guide fixed facing**을 설정한다. NPC는 정해진 위치에서 대기 애니메이션을 반복하며 Pause에서 멈춘다. 새 복사본을 저장하고 Load saved copy로 연결한다. 설정하지 않으면 원형 표시를 유지한다. [NPC 설정·JSON·준비 상태](docs/migration/PLAY_INTEGRATION.md#play-10-guide-npc-대기-아트). 공개 v0.2.0-rc.3에는 PLAY-08/09/10이 없다.

PLAY-11 이후 **Scene art → HUD artwork settings**에서 소유 자료의 UI 팔레트·DC6 프레임·배치를 지정한다. Decoration/Health/Menu/Inventory를 지원하며 Preview HUD at 50% health → 새 복사본 저장 → Load saved copy로 연결한다. 게임 아래에 비율을 유지해 표시하며 체력과 기존 메뉴/인벤토리 동작을 사용한다. [설정/범위](docs/migration/PLAY_INTEGRATION.md#play-11-원본-hud-이미지-연결). 마나·스킬·벨트·원작 UI 전체 재현은 미구현이며 rc.3에는 포함되지 않는다.

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

몬스터 처치 후 청록색 아이템 표식에 접근해 **F / Pick up nearest**로 줍는다. 아이템 목록에서 선택하고 **Equip selected / Unequip selected / Drop selected**로 장착·해제·버리기를 수행한다. 가방은 8칸이며 장비는 무기·몸통 두 슬롯이다. 피해 범위와 방어력은 장착 결과에 따라 바뀐다. 아이템·장비도 체크포인트에 포함된다. 저장하지 않은 진행은 앱 종료 시 잃는다.

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
