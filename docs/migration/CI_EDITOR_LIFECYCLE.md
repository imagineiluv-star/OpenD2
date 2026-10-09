# CI-01 — Godot 편집기 수명 및 macOS 내보내기

작업일: 2026-10-09. 기준 master: `28d6c257fbdc600f8311b6174cfb3748347c5baf`.
작업 브랜치: `fix/godot-editor-lifecycle`.

## 재발 근거

| 실행 | 코드 | macOS 결과 |
|---|---|---|
| [37894721659](https://github.com/imagineiluv-star/OpenD2/actions/runs/37894721659) | `c58e2352` | 317/317 계약·게임 smoke 후 ZIP export 완료 직후 EditorSettings 오류 |
| [37928716624](https://github.com/imagineiluv-star/OpenD2/actions/runs/37928716624) | `df270345` | 334/334 계약 후 import 종료에서 같은 오류 |
| [37932013472](https://github.com/imagineiluv-star/OpenD2/actions/runs/37932013472) | `010c04b1` | 성공 |
| [37936020793](https://github.com/imagineiluv-star/OpenD2/actions/runs/37936020793) | `28d6c257` | 성공; 기존 4.6.3 경합 수정은 없음 |

이전 문서의 120프레임/60fps 지연 후에도 실패했다. 한 번의 성공이나 실패 작업 재실행으로 해결됐다고 판단하지 않는다.

## 원인과 선택

오류는 `ERROR: EditorSettings not instantiated yet when getting setting "export/android/android_sdk_path".`다.
[4.6.3 Android exporter](https://github.com/godotengine/godot/blob/4.6.3-stable/platform/android/export/export_plugin.cpp)의
`initialize()`는 Android 프리셋 유무와 관계없이 감시 스레드를 시작한다. 해당 스레드는 약 3초마다
`get_adb_path()`에서 EditorSettings를 읽는다. 진입 시 한 번만 singleton을 확인하는
[#116515](https://github.com/godotengine/godot/pull/116515)는 후속 종료 경합을 제거하지 않는다.
[EditorNode 소멸 경로](https://github.com/godotengine/godot/blob/4.6.3-stable/editor/editor_node.cpp)는
EditorSettings를 해제하며 Android 플랫폼의 스레드 join은 플랫폼 소멸자에 있다. 이는 실제 로그가
import/export 종료에서 같은 설정 읽기에 실패한 것과 일치한다. 실패 순간의 네이티브 스레드 덤프는 수집하지 않았다.

공식 수정 [#116548](https://github.com/godotengine/godot/pull/116548), merge `2db05b08de983358f9ad12f28280939d997a0be5`는
실행 가능한 Android 프리셋이 있을 때만 스레드를 시작하고 없어지면 중지한다.
[4.7.2 안정판 소스](https://github.com/godotengine/godot/blob/4.7.2-stable/platform/android/export/export_plugin.cpp)에 이 경로가 포함되어 있다.
OpenD2는 Linux/Windows/macOS 프리셋만 사용하므로 해당 스레드가 필요 없다.
이는 desktop-only 경로의 해결이며 향후 Android 프리셋을 추가하면 별도로 재검증해야 한다.

엔진 포크·바이너리 수정이나 더 긴 sleep 대신 공식 Godot **4.7.2 .NET**을 사용한다.
에디터, templates, Godot.NET.Sdk, GodotSharp/Editor/SourceGenerators lock과 프로젝트 feature를 함께 맞춘다.
`eng/toolchain.json`에는 공식 SHA512-SUMS의 해시를 고정한다. bootstrap은 버전별 디렉터리를 사용하므로
4.6.3 캐시가 남아 있어도 다른 실행 파일을 고르지 않는다. .NET SDK 10.0.401과 게임 규칙은 유지한다.

## 검증 흐름

1. `eng/test_godot_process.py`: 오류+exit 0, stderr script 오류, 일반 오류, nonzero exit, timeout과 원문 보존.
2. `eng/check-editor-lifecycle.py`: 매회 임시 프로젝트 `.godot`를 제거한 cold import와 warm export-pack.
   종료 프레임 1/30/120/180을 순환해 이전 polling 주기 주변을 포함한다. 기본 12회씩이며 첫 오류에서 실패한다.
   C# 게임이나 templates가 없는 작은 fixture로 엔진 수명을 분리하되 **.NET 에디터 실행에는 SDK가 필요하다**.
3. 실제 OpenD2 locked restore·빌드·전체 게임 계약·import·헤드리스 smoke·실제 OS release export.
4. export 실행과 별도로 배포 archive 생성 → 새 임시 디렉터리로 추출 → SDK 검색 경로 제거 후 실행.
5. 패키지·SHA256 metadata 아티팩트와 단계별 validation 로그 아티팩트 확인.

import는 리소스 스캔 완료를 기다리는 `--import`를 사용하며 120프레임 지연을 제거했다.
export는 엔진의 `--export-release` 완료/자동 종료를 기다린다. 이전 출력 파일은 먼저 삭제해 낡은 파일이 성공 조건을 채우지 못한다.
모든 `ERROR:`/`SCRIPT ERROR:` 및 비정상 종료는 계속 실패다. timeout도 실패이며 원문을 저장한다.
오류 무시·자동 재실행·검사 생략·continue-on-error는 없다.
`check-editor-lifecycle.py --godot <4.6.3 실행파일> --logs artifacts/baseline-4.6.3 --iterations 24`로
같은 엄격한 검사기를 이전 버전에 실행할 수 있다. 간헐 오류가 관측되지 않은 반복은 재현 성공으로 기록하지 않는다.

## 검증 기록과 인수 경계

최초 로컬 검사: 새 SDK lock restore와 Debug 빌드 성공(경고 0/오류 0), Python 계약 22/22 성공.
3개 OS 결과와 반복 실행 결과는 이 변경 PR과 후속 검증 기록에 실제 실행 링크로 기록한다.

**실제 GUI 플레이: NOT_RUN.** 헤드리스 검사는 합성 게임 경로와 자체 포함 패키지 시작을 확인한다.
화면 렌더링·키보드/마우스·오디오 청취·DPI·멀티 모니터·서명/공증 설치·원본 데이터 호환 인수가 아니다.
macOS universal export는 두 아키텍처를 포함하지만 macos-15 실행은 runner의 arm64 인수다.
Intel macOS의 실제 실행은 별도다.

## 남은 프로젝트 작업

- RELEASE-QA-02: 세 OS 실제 GUI 플레이, Grok Bot 결과 회수, QA-01~10의 증거와 장시간 플레이.
- M1: 소유한 원본 MPQ로 DCC/COF·DT1/DS1·TXT/BIN 변형과 원본 장면·UI·음원을 인수.
- M2-05c: 원본 세이브 캐릭터/아이템 본문 변환. 현재 헤더 사전검사와 자체 저장 호환을 구분.
- PLAY-14~16: 격자 가방·마나/첫 스킬·회복 물약/4칸 벨트 합성 기능의 실제 입력·원본 아트 확인.
- NPC-02: 실제 모델 한국어 품질 및 목표 PC CPU/GPU·지연·메모리 인수; NPC-03 기억/대사 재생 후속.
- M3/M4: 3D 아트·전체 클래스/스킬/몬스터/퀘스트/지역 규칙과 전체 캠페인 호환.
- M5/M6: 협동 서버·동기화·지역 스트리밍은 이후 확장. 이 CI 수정에서 구현 완료로 표시하지 않는다.

상세 체크박스의 기준은 [WORK_PLAN](WORK_PLAN.md) 및 최신 [PLAY_16_RESULTS](PLAY_16_RESULTS.md)다.
