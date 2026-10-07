# M0 구현·검증 기록

기록일: 2026-10-07. 브랜치: `feat/m0-foundation`.

## 구현

- Godot 4.6.3 .NET / .NET SDK 10.0.401 / net10.0 고정.
- Core, Assets, Client, AssetAudit, Tests의 5개 프로젝트와 전체 솔루션.
- 회전하는 3D 물체, 경로 선택, 설정 저장, FPS·frame p95·관리 힙 표시가 있는 오프라인 최소 앱.
- OS 사용자 데이터 경로, 버전 있는 설정, 임시 파일 교체·이전 백업, 손상/미래 버전 설정 보호.
- JSONL 세션 로그(최근 10개), 600개 표본을 유지하는 프레임 계측.
- 디렉터리 접근 확인용 CLI. MPQ 분석·버전 호환 검증은 아직 없다.
- 의존성 lock, 공식 Godot SHA-512 검증 bootstrap, 엔진 참조 경계 검사, 3개 OS CI.

## 실제 검증 증거

[GitHub Actions 실행 #1](https://github.com/imagineiluv-star/OpenD2/actions/runs/37612460513)

검증 커밋: `730f1b5434014207fe8aaaba2a3c875aa07af555`. 이후 문서 수정은 실행 코드와 별개다.

| 검증 | 결과 |
|---|---|
| 엔진 없이 Core/Assets 및 계약 테스트 Release 빌드 | 로컬 Linux 성공, 경고/오류 0 |
| 계약 테스트 | 9/9 통과 |
| 전체 솔루션 Debug 빌드 / locked restore | 성공 |
| Godot import / 헤드리스 앱 시작 | OPEND2_M0_READY 확인 |
| Linux 독립 실행 패키지 | SDK 경로와 DOTNET_ROOT 없이 헤드리스 시작 성공 |
| Linux x64 export | 로컬 및 ubuntu-24.04 CI 성공 |
| Windows x64 export | 로컬 교차 내보내기 및 windows-2025 CI 성공 |
| macOS universal export (arm64 포함) | 로컬 교차 내보내기 및 macos-15 CI 성공 |
| CI의 빌드·9개 테스트·헤드리스 시작·export | 3개 운영체제 모두 성공, 전체 1분 51초 |
| CI 산출물 | Linux 63.3 MB / Windows 71.8 MB / macOS 125 MB, 보관 14일 |
| bootstrap | Linux에서 공식 해시 확인 및 설치 성공; 3개 CI에서도 성공 |

계약 테스트: 사용자 경로 분리, 기본 설정, 한글 경로와 백업 왕복, 잘못된 설정의 원본 보호, 미래 스키마 보호, 손상/대용량 설정 거부, 디렉터리 검증, 계측 창 크기·p95, JSON 로그·세션 보존 수.

재실행 명령은 [BUILDING](../../BUILDING.md)에 있다. Godot export는 오류를 출력하고 종료 코드 0을 반환한 사례가 있어 검증 스크립트가 출력 오류도 검사한다.

## 해결한 빌드 문제

1. Godot import가 csproj에 net8.0을 자동 추가: Client csproj에 net10.0 명시.
2. Godot export가 클라이언트 폴더의 솔루션을 요구: 전용 솔루션 추가.
3. 제한된 실행 환경에서 MSBuild 병렬 restore 실패: 공통 props에서 병렬 restore/build 제한.
4. export가 일반 개발용 NuGet lock을 변경: export별 lock을 obj로 분리.
5. macOS arm64 단독 템플릿 부재: 공식 universal 템플릿과 ETC2/ASTC import 설정 사용.

## 남은 인수와 후속 작업

- Windows/macOS 일반 사용자 PC에서 GUI·DPI·경로 선택·권한·업데이트·제거 시험은 미수행. CI 헤드리스 실행과 구분한다.
- macOS 코드 서명/공증 및 Windows 서명·설치 프로그램은 아직 없다. 현재 산출물은 개발 검증용이다.
- 실제 게임 tick과 tick p95 연결은 M2 시뮬레이션 구현 때 진행한다. M0 화면의 frame p95는 게임 tick 지연이나 목표 PC의 성능 보장을 뜻하지 않는다.
- RegionId 및 지역 상태 저장 계약도 실제 M2 상태 모델과 함께 도입한다. M0에 미사용 프레임워크는 추가하지 않았다.
- 엔진/런타임 자체의 소스 재빌드는 미수행. 자체 C# DLL 소스, 타사 고정 버전·소스 경로·빌드 안내를 관리한다.
- CI의 현재 action major 버전에는 Node 20 폐기 경고가 있으며 GitHub가 Node 24로 실행해 통과했다. action 버전 갱신은 후속 유지보수 항목이다.
- 원본 MPQ 미제공: 리소스 이관·파서·실제 게임 플레이는 아직 구현하지 않았다. 다음은 M1-01/M1-02다.

M0 기반 코드는 실행 가능하지만 게임 마이그레이션 전체 완료, 리저렉션 수준 그래픽, 오픈월드 또는 일반 사용자 설치 인수 완료를 의미하지 않는다.
