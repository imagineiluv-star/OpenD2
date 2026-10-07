# M1 1차: MPQ 기반 리소스 이관 준비

브랜치 `feat/m1-legacy-assets`, 기준 M0 커밋 `e22684a0c59137d4aadc94fe951800ed888ac8bd`. 원본 C++ 경로는 유지한다. 이번 구현은 M1-01~03이며 M1 전체 완료를 의미하지 않는다.

## 구현

| 항목 | 구현과 남은 인수 |
|---|---|
| M1-01 | LoD 1.10f 목표 프로파일, MPQ 탐색·필수 파일 목록 검사, 클라이언트 경로 저장 후 누락 개수 표시. 실제 패치 버전 식별은 미검증 |
| M1-02 | 고정 StormLib 소스 빌드, OpenD2 소유 C ABI, SafeHandle 기반 읽기 전용 C# 리더. 실제 게임 아카이브 호환성 인수 필요 |
| M1-03 | 아카이브/내용 SHA-256, 정규화 경로 ID, 패치 중복 경로 우선순위, 미지원·읽기 실패·목록 미확인 상태 JSON. 전체 리소스 커버리지 미확정 |

Godot 없이 Assets와 CLI를 사용할 수 있다. 클라이언트 시작 시 네이티브 ABI를 검사한다. 플레이어 배포물에는 native 라이브러리가 포함되고 별도 SDK·서버·DB 설치를 요구하지 않는다. 아직 게임을 플레이하거나 원본 그래픽을 표시하는 단계는 아니다.

## 실행

먼저 루트 BUILDING.md의 개발 환경을 설치하고 `python eng/build-native.py`를 실행한다.

```sh
dotnet run --project tools/OpenD2.AssetAudit -- --probe /path/to/DiabloII
dotnet run --project tools/OpenD2.AssetAudit -- /path/to/DiabloII
dotnet run --project tools/OpenD2.AssetAudit -- /path/to/DiabloII known-paths.txt
```

JSON은 stdout, 치명적 오류는 stderr로 출력한다. 보고서를 파일로 저장할 때 원본 게임 폴더 밖의 새 파일을 사용한다. known-paths.txt는 UTF-8 상대 MPQ 경로를 한 줄씩 적는다. 절대 경로, 상위 경로 이동, 빈 구간은 거절한다. 게임 폴더에는 쓰거나 추출하지 않는다.

종료 코드: 0 = 보고된 읽기 오류·필수 파일 누락 없이 종료, 1 = 치명적 오류, 2 = 잘못된 명령, 3 = 누락 또는 읽기 실패. **0도 버전 호환이나 전체 이관 성공을 뜻하지 않는다.**

## 보고서 해석

- `Profile=lod-1.10f`는 목표이며 `VersionStatus=unverified`다. 알려진 정품 설치 해시와 실제 샘플 대조 전에는 검증됨으로 표시하지 않는다.
- `Complete=false`, `EnumerationComplete=false`: listfile과 supplied known paths만으로 이름 없는 모든 MPQ 항목을 증명할 수 없다. 필요 경로 목록을 추가해 반복 조사한다.
- `Selected`는 원본 OpenD2의 MPQ 우선순위에 따라 먼저 발견한 경로다. 상위 아카이브 읽기 실패가 있으면 잠정값이다. 미분류 MPQ는 마지막에 이름순으로 보고하며 실제 모드 우선순위를 의미하지 않는다.
- 원본별 엔트리를 모두 보존하므로 패치 중복도 확인할 수 있다. ContentId는 정규화 경로의 SHA-256, ContentHash는 압축 해제된 내용의 SHA-256이다.
- `DecodeStatus=not_implemented`, `RuntimeStatus=not_loaded`, `HdReplacementId=null`: 미지원 형식을 정상 디코딩으로 오인하지 않는다.
- 기본 예산: 아카이브 256개, 보고 항목 100,000개, 파일 256 MiB, 성공적으로 해시한 합계 4 GiB. 아카이브와 압축 해제 내용을 모두 합산한다. 총 디스크 I/O·CPU 시간의 강제 격리는 아니며 손상 파일 처리와 네이티브 내부 작업은 별도다. API의 AuditOptions로 조정 가능하다.
- 스캔 중 파일을 교체하지 않아야 한다. 동시 설치 수정에 대한 일관된 스냅샷은 아직 제공하지 않는다.

## 검증

Linux 로컬에서 네이티브 소스 빌드, C# 빌드, 17/17 계약 테스트, Godot import·헤드리스 시작·Linux export를 통과했다. SDK 경로를 제거한 Linux 배포 실행에서도 네이티브 ABI 로딩과 시작 마커를 확인했다.

합성 fixture로 zlib 압축·암호화·다중 섹터 읽기/해시, 대소문자 경로, 목록 없는 MPQ의 알려진 경로, 패치 우선순위, 잘린 아카이브, 읽기 예산, 경로 거부, 핸들 해제를 검사한다. 이것만으로 모든 실제 MPQ 압축 조합·손상 패턴을 검증했다고 보지 않는다.

3개 OS CI는 Windows x64, Linux x64, macOS universal native 빌드와 17개 테스트·Godot 시작·export를 실행한다. 2026-10-07 세 OS 모두 통과했다 ([CI 실행](https://github.com/imagineiluv-star/OpenD2/actions/runs/37618506265), 코드 커밋 `af6e056850a1688de0eb813e70caaea080f0077d`, 2분 45초). 최초 Windows DWORD 포인터 타입 오류를 수정한 뒤 재검증했다. GUI 설치·실제 파일 호환성은 별도 인수다.

## 다음 작업과 리스크

1. M1-04: Palette/TBL/DC6 정상·잘림·범위 초과 fixture와 최소 이미지 뷰어.
2. M1-05: DCC/COF 방향·레이어 합성 및 애니메이션 뷰어.
3. M1-06: DT1/DS1 지도·충돌·레이어 검증.
4. M1-07: TXT/BIN 로딩·교차 참조·예산 기반 캐시.
5. 사용자가 보유한 실제 1.10f 설치에서 인벤토리 수집, 언어·패치·누락 경로 비교. 원본 MPQ나 추출물은 공개 저장소에 커밋하지 않는다.
6. 일반 배포 전 서명/공증, 네이티브 의존성의 bundled codec별 전체 배포 고지 점검, PKWARE 등 실제 압축 조합 회귀 검증, 악성/손상 입력 격리 검토.

전체 리소스 이관·D2R 수준 그래픽·오픈월드·서버 부하 성능은 이번 구현으로 달성된 것이 아니다. 이 단계는 이후 누락 없이 이관 여부를 추적하는 기반이다.
