# OpenD2 C# / Godot 마이그레이션

작성일: 2026-10-07. 상태: M0 및 M1-01~05 기본 코드·합성 검증 구현. 최신 검증·실데이터 인수 잔건은 [M1_05_RESULTS](M1_05_RESULTS.md) 참조.

## 목표와 완료의 의미

Godot .NET 클라이언트와 엔진 독립 C# 게임 코어로 이전한다. 원본 리소스 호환을 먼저 확보하고, 리저렉션 수준을 지향하는 3D 표현과 지역 스트리밍을 단계적으로 추가한다. 엔진 채택만으로 그래픽 품질이나 오픈월드 성능이 확보되는 것은 아니다.

| 요구사항 | 설계 방향 | 확인 방법 |
|---|---|---|
| 설치가 쉬워야 함 | 엔진·런타임·DB 별도 설치 없는 OS별 배포 패키지 | 개발 도구 없는 PC에서 설치·실행·업데이트·제거 |
| 중앙 서버 부담 최소화 | 오프라인 로컬 시뮬레이션 기본, 협동 플레이 선택 | 오프라인 실행 및 외부 서비스 미의존 확인 |
| 원본 리소스 모두 사용 | 원본 파일 보존, 목록·해시·호환성 현황 관리 | 리소스 인벤토리의 누락·미지원 항목 0건을 최종 목표로 추적 |
| C# 이관 | 게임 규칙·저장·로더를 C#으로 구현 | Godot 없이 코어 빌드·테스트 |
| DLL 유지보수 | 자체 DLL 소스 전부 관리, 외부 의존성 버전·소스·빌드법 고정 | 깨끗한 환경에서 재빌드 |
| 고품질 3D | 원본 콘텐츠 ID에 신규 모델·재질·애니메이션 연결 | 대표 전투 장면의 시각·성능 검증 |
| 오픈월드 확장 | 지역 경계와 저장 계약을 먼저 설계 | 반복 지역 이동·저장 복원·메모리 회수 |

여기서 원본 호환 기준은 우선 OpenD2가 대상으로 삼는 Diablo II LoD 1.10f이다. 다른 버전 및 D2R 리소스/세이브는 별도 호환 프로파일로 검증한다. “리저렉션 수준”은 시각 품질 목표이며 D2R 파일 호환 완료를 뜻하지 않는다. MMORPG와 기존 Battle.net 접속은 초기 범위에 포함하지 않는다.

## 문서 읽는 순서

1. [아키텍처와 기술 결정](ARCHITECTURE.md)
2. [리소스 및 DLL 이관 설계](ASSETS_AND_DEPENDENCIES.md)
3. [단계별 작업계획](WORK_PLAN.md)
4. [검증·배포·인수 기준](VALIDATION.md)

## 이번 변경과 다음 작업

`feat/m1-legacy-assets`에 엔진 독립 C# Core/Assets, 읽기 전용 MPQ와 인벤토리, Palette/TBL/DC6/DCC/COF 파서, Godot 이미지·애니메이션 뷰어 및 3개 OS CI를 구현했다. 게임 플레이는 M2 이후 범위다.

- [개발·실행 방법](../../BUILDING.md)
- [M0 검증 결과와 잔여 인수](M0_RESULTS.md)
- [고정 의존성과 소스 재빌드 기록](M0_DEPENDENCIES.md)

다음 코드 단계는 M1-06 DT1/DS1 지도 뷰어다. M1-05의 특수 변형·효과 및 실제 파일 호환 인수는 남아 있다. 실제 게임 파일을 받기 전에는 synthetic fixture 기반 검사만 수행한다.

## 조사 기준

- OpenD2 정적 검토 기준: `057824439ca145aa8411f9b024bc3fe6aa8fa450`.
- Diablerie 정적 검토 기준: `9e42ef257228257825ebbbeb905d4b1d894b6e98`.
- 원본 게임 파일은 제공되지 않았다. 리소스 전체 목록, 실제 게임 플레이, 그래픽 품질과 운영체제별 설치 인수는 미수행이다. M0 최소 앱 검증은 별도 기록한다.
- 원본 OpenD2의 서버·월드 생성 등에는 미구현 부분이 있으므로 전체 작업은 단순 언어 번역보다 범위가 크다.

## 참고 자료

- [OpenD2 기준 커밋](https://github.com/eezstreet/OpenD2/commit/057824439ca145aa8411f9b024bc3fe6aa8fa450)
- [Diablerie](https://github.com/mofr/Diablerie/tree/9e42ef257228257825ebbbeb905d4b1d894b6e98): C# 로더·게임 구성 참고. Unity 의존 부분 분리 필요.
- [D2MOO](https://github.com/ThePhrozenKeep/D2MOO): 1.10f 게임 규칙 참고. 원본 DLL을 요구하는 실행 구조를 그대로 채택하지 않는다.
- [OpenDiablo2](https://github.com/OpenDiablo2/OpenDiablo2): 포맷·게임 구성 참고. 보관된 프로젝트임을 고려한다.
- [D2SLib](https://github.com/dschu012/D2SLib): 세이브 분석 후보. 목표 버전 지원을 별도 검증한다.
- [StormLib](https://github.com/ladislav-zezula/StormLib): MPQ 구현 후보.
- [Godot 렌더러](https://docs.godotengine.org/en/stable/tutorials/rendering/renderers.html), [3D 파일](https://docs.godotengine.org/en/stable/tutorials/assets_pipeline/importing_3d_scenes/available_formats.html), [LOD](https://docs.godotengine.org/en/stable/tutorials/3d/mesh_lod.html), [C#](https://docs.godotengine.org/en/stable/tutorials/scripting/c_sharp/index.html).

외부 코드를 가져올 때는 해당 시점의 커밋·라이선스·파일별 출처를 기록한다. 참고 저장소를 읽었다는 사실과 그 코드를 실제 채택했다는 사실을 구분한다.
