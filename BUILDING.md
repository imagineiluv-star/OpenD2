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
python eng/validate.py --export Linux
# Windows x64: --export Windows
# macOS arm64: --export macOS
```

bootstrap은 공식 Godot 배포본을 내려받아 `eng/toolchain.json`의 SHA-512로 검사한다. SDK는 별도 설치한다. bootstrap 출력의 실행 파일로 `src/OpenD2.Client/project.godot`를 열면 된다. 기존 Godot 설치를 사용하려면 `python eng/validate.py --godot <실행파일경로> --export Linux`를 사용한다.

`eng/validate.py`는 참조 경계 검사 → locked restore → 전체 Debug 빌드 → 147개 계약 테스트 → Godot import → 헤드리스 시작 검사 → 선택한 OS export 순으로 실행한다. Godot이 오류를 출력하고도 종료 코드 0을 반환하는 경우도 실패로 취급한다. 작은 프로젝트의 재현성을 위해 MSBuild 병렬도를 제한했다.

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

**Simulation** 탭에서 그리드를 클릭하고 방향키로 이동한다. 초록색 **Camp Guide** 근처에서 **E / Interact**로 퀘스트를 수락한다. 금색 **Cellar** 포털 근처에서 E로 던전에 들어가 **Space / Attack nearest**로 세 몬스터를 처치한다. 던전 입구의 **Camp** 포털로 돌아와 Guide에게 E로 완료를 보고하면 체력을 한 번 회복한다. 지역을 오가도 체력·몬스터 위치·사망·퀘스트는 현재 실행 중에 유지된다. 공격 중에는 상호작용이 실패할 수 있으므로 Space를 놓고 E를 누른다.

회색 벽과 보라색 미확인 셀은 이동을 막는다. **Pause / Step one tick / Signal / Verify replay**로 상태와 재생을 검사한다. 사망 후 또는 Seed 변경 후에는 **New run**으로 처음부터 시작한다. 파일 세이브는 아직 없다. 조작·판정 순서·예산·제한은 [M2-03 기록](docs/migration/M2_03_RESULTS.md)을 참조한다.

## 실행과 배포

`artifacts/<OS>/` **전체 디렉터리**를 전달한다. 실행 파일 옆의 PCK와 런타임 디렉터리를 함께 배포해야 한다. 플레이어에게 SDK·Godot 편집기·Python·DB 설치를 요구하지 않는 self-contained export를 사용한다. OS 기본 시스템 라이브러리와 그래픽 드라이버는 필요하다.

현재 게임 판정은 이 실행 파일 안에서 `OpenD2.Core`가 수행한다. 외부 게임 서버·로그인·DB는 필요하지 않다. 네트워크 협동 및 그래픽 없는 `OpenD2.Server` .NET 호스트는 M5 예정이며 현재 별도 서버 실행 파일은 없다. 역할과 확장 조건은 [아키텍처 ADR-002](docs/migration/ARCHITECTURE.md#adr-002-로컬-우선-선택적-협동-세션)를 참조한다.

- Linux: `OpenD2.x86_64`
- Windows: `OpenD2.exe`
- macOS: ZIP을 풀어 앱 실행. 현재는 서명·공증 전 개발 빌드이며 일반 사용자용 설치 인수 전이다.

설정은 OS의 `LocalApplicationData/OpenD2/settings.json`에 저장한다. 동일 루트 아래 `saves/`, `cache/`, `logs/`를 사용한다. 설정 저장 시 이전 파일을 `.bak`으로 남긴다. 손상/미래 버전 설정은 자동 덮어쓰지 않는다. 여러 인스턴스의 동시 설정 저장은 아직 지원하지 않는다.

## 구조

| 프로젝트 | 책임 |
|---|---|
| OpenD2.Core | 설정·계측·고정 tick·전투·AI·지역·퀘스트·재생, 엔진 미참조 |
| OpenD2.Assets | 읽기 전용 MPQ·포맷 파서·캐시·충돌 변환, Core만 참조 |
| OpenD2.Client | Godot 표현·입력·뷰어·합성 마을/던전·오프라인 실행 |
| OpenD2.AssetAudit | 엔진 없는 리소스 점검 CLI 기반 |
| OpenD2.Tests | 파일 손상·백업·경로·계측·로그 계약 검증 |

루트 `OpenD2.sln`은 전체 개발용이다. 클라이언트 폴더의 `OpenD2.Client.sln`은 Godot export가 요구하는 솔루션이다. 프로젝트 참조를 바꿀 때 두 솔루션을 함께 갱신한다. `TargetFramework`는 클라이언트 csproj에도 명시해 Godot 자동 마이그레이션이 net8.0을 추가하지 않도록 한다.

빌드 출력·사용자 게임 데이터·추출 캐시는 커밋하지 않는다. 의존성을 바꿀 때 lock 파일을 의도적으로 갱신하고 출처를 [의존성 기록](docs/migration/M0_DEPENDENCIES.md)에 추가한다. 실제 수행한 결과와 남은 인수는 [M0 검증 기록](docs/migration/M0_RESULTS.md)을 참조한다.
