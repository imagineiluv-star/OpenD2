# M1-07: 맵 테이블과 타일 캐시

2026-10-07, `feat/m1-legacy-assets`. TXT와 명시적 1.10f 맵 BIN 로더, 맵 리소스 참조 검사, 뷰어의 경로 해결과 예산 기반 DT1 픽셀 캐시를 구현했다. 실제 LoD 파일은 제공되지 않았으며 M1 전체 호환 인수는 열려 있다.

## 구현과 사용

- `ExcelTextTable`: 탭 구분 TXT의 열 순서·빈 값·알 수 없는 열·원본 행 번호를 보존한다. 중복/빈 헤더도 위치로 보존하되 이름 조회가 모호하면 실패한다. 짧은 행의 누락된 뒤쪽 값은 빈 문자열로 채우고, 헤더보다 긴 행은 거절한다. CR/LF/CRLF를 읽으며 따옴표는 CSV 인용 문법이 아닌 문자다. BOM 없는 파일은 바이트 보존 Latin-1, UTF-8 BOM 파일은 엄격한 UTF-8로 읽는다. 언어별 코드페이지 표시·UTF-16은 별도 범위다.
- `ExcelBinTable`: DWORD 레코드 수와 정확한 `4 + count × recordSize`를 확인한다. 프로파일을 호출자가 명시하며 입력 사본과 전체 레코드 바이트를 보존한다. 잘림·추가 꼬리·숫자 필드 범위·문자열 NUL 누락을 거절한다. 레코드 크기가 맞는다고 실제 버전을 확인한 것으로 표시하지 않는다.
- `MapTables`: TXT 모드에서 `levels.txt / lvltypes.txt / lvlprest.txt`, BIN 모드에서 `leveldefs.bin / lvltypes.bin / lvlprest.bin`을 읽어 맵 리소스에 필요한 필드만 투영한다. source SHA-256·크기·경로를 기록한다. TXT와 BIN을 자동 혼합하거나 손상 BIN을 TXT로 대체하지 않는다. 모든 읽기는 기존 MPQ 패치 우선순위를 사용한다.
- `Levels.Id → LevelType → LvlTypes 행 번호`와 `LvlPrest.Def / LevelId / Files / Dt1Mask`를 연결한다. LvlTypes의 `Id` 열은 원본 엔진에서 참조 키가 아니므로 행 번호를 사용한다. TXT의 전부 빈 행과 단독 `Expansion` 구분 행은 투영에서 제외한다. BIN leveldefs는 행 번호가 Level ID다.
- `Validate()`는 없는 level/type, 선택했으나 비어 있는 DS1/DT1 슬롯과 빈 mask를 보고한다. 중복 ID, 잘못된 숫자/경로 등 구조 오류는 로드를 실패시킨다. LevelId=0인 조각 preset은 포함 지역이 필요하므로 `context_required` 안내로 남긴다. `Resolve(levelId, presetDef, fileIndex)`에서 실제 지역을 지정해 mask를 검사한다. fileIndex는 API에서 0부터, 화면에서 1부터다.
- `Dt1Mask`는 unsigned 32비트를 전부 사용하며 File 1부터 File 32까지 원래 순서를 유지한다. `0`/빈 경로는 빈 슬롯, 나머지는 `data/global/tiles/` 아래 MPQ 논리 경로로 정규화한다. 경로 이동·절대 경로·잘못된 확장자는 거절한다.

**Map 탭**: TXT 또는 **BIN map tables (1.10f only)** 선택 → Level ID, Preset Def, File slot 입력 → **Resolve table paths** → Act 팔레트 확인 → **Load map**. 선택한 참조만 성공하면 경로를 채우고 전체 표의 오류/문맥 안내 수를 함께 표시한다. 다른 표 행의 오류가 선택한 맵의 실패를 뜻하지는 않는다. 경로 해결 실패는 이전 입력을, 지도 로드 실패는 이전 미리보기를 유지한다. 수동 DS1/DT1 경로 입력도 사용할 수 있다.

CLI:

```sh
dotnet run --project tools/OpenD2.AssetAudit -- --tables-txt /path/to/game
dotnet run --project tools/OpenD2.AssetAudit -- --tables-bin-110f /path/to/game
```

JSON에 선택 모드, source hashes, 행 수, 참조 문제, 필수 MPQ 누락을 출력한다. 종료 0=보고된 오류 없음, 3=필수 아카이브 누락/참조 오류, 1=로드 실패, 2=사용법 오류다. `VersionStatus=unverified`, `Complete=false`, `ResourceExistenceChecked=false`를 유지한다. 테이블 참조 검사는 전체 외부 DS1/DT1의 존재나 디코딩 성공을 보장하지 않는다. 선택 맵은 뷰어 로드 시 실제로 읽고 해독한다.

일반 `--decode`는 Excel TXT의 **구조**를 검사한다. BIN은 자동 추정하지 않아 일반 인벤토리에서 `not_implemented`로 남으며, 명시적 `--tables-bin-110f`가 아래 3개 레코드 스키마를 해석한다. `DecoderVersion=m1.07-1`.

| 1.10f BIN | 레코드 바이트 | 맵 투영 필드 |
|---|---:|---|
| leveldefs.bin | 156 / 0x9C | LevelType at 0x34, ID는 행 번호 |
| lvltypes.bin | 1928 / 0x788 | 32 × 60바이트 DT1 경로 |
| lvlprest.bin | 432 / 0x1B0 | Def, LevelId, Files, 6 × 60바이트 DS1 경로, Dt1Mask |

BIN 컴파일러, levels.bin 전체 필드 해석, 아이템/스킬/몬스터 등 다른 테이블 스키마 및 게임 규칙은 구현하지 않았다. 다음 게임 기능에서 필요한 의미를 추가하며 원시 열/레코드를 버리지 않는다.

## 캐시 소유권과 예산

`TileFrameCache`는 DT1 복원 index 픽셀을 캐시한다. 키는 **원본 SHA-256 + decoder version + indexed 출력 형식 + tile index**다. 색상은 캐시 이후 팔레트로 입히므로 팔레트 변경 시 잘못된 RGBA를 재사용하지 않는다. 맵 조립에 연결되어 다른 맵 로드에서도 같은 원본 타일의 디코딩을 재사용한다. MPQ I/O와 DT1 구조 파싱은 매번 실행하므로 같은 경로의 내용 변경도 새 해시로 감지한다.

- 기본 16 MiB, 최대 256항목 LRU. charge는 index byte 길이 + 키 문자 바이트 + 항목당 512바이트 여유분이다. **RetainedBytes는 회수 정책용 계산값이며 CLR 전체 heap 실측값이 아니다.**
- hit은 최근 사용 순서를 갱신한다. 예산 초과 항목은 반환하되 저장하지 않고, 예산 0은 캐시를 끈다. 실패한 디코딩은 저장하지 않는다.
- private 캐시 사본과 반환 픽셀을 분리한다. 호출자가 화면 픽셀을 바꾸거나 캐시가 회수되어도 다른 활성 장면은 영향을 받지 않는다. 동시 miss는 lock 안에서 직렬화하며 통계를 함께 보호한다.
- UI에서 charge/hit/miss/eviction을 확인하고 **Clear tile cache**로 회수한다. 창 종료 시 cache를 지우고, 종료 후 늦게 끝난 지도 로드도 다시 비운다.
- 활성 장면, 복사 중 임시 버퍼, DT1 입력/메타데이터와 GPU 텍스처는 캐시 예산 밖이다. 기존 지도 입력 64 MiB·선택 픽셀 16,777,216 상한은 별도로 유지한다. GPU 업로드 예산/전역 메모리 예산/지역 스트리밍은 M6 작업이다. 실제 장면의 속도 향상 수치는 아직 측정하지 않았다.

| 테이블 처리 정책 | 상한 |
|---|---:|
| TXT 파일 / 열 / 행 | 8 MiB / 512 / 65,536 |
| TXT 전체 셀 / 셀 문자 | 1,048,576 / 16,384 |
| TXT 헤더 이름 / 한 줄 문자 | 256 / 1,048,576 |
| BIN 파일 / 레코드 | 32 MiB / 65,536 |
| 참조 문제 목록 | 4,096, 초과 시 명시적 실패 |

## 검증

- **80/80 계약 테스트 통과**: 기존 60개 + TXT/BIN·참조·캐시 20개.
- 합성 TXT와 BIN이 동일한 명시적 DS1/DT1 계획을 내는지 비교하고 bit31, signed DWORD, 빈 슬롯, 단독 Expansion 행, 중복/누락 참조, 경로 이동을 검사했다.
- 3개 BIN 스키마의 모든 잘린 prefix, 레코드 수/크기 불일치, 꼬리, NUL 누락과 TXT 인코딩/행·셀 예산을 검사했다.
- 합성 MPQ에서 테이블 읽기·TXT 감사·원본 불변·손상 패치 우선순위를 검사했다. 캐시 hit, 내용 변경, 타일 구분, LRU 순서, 예산/항목 수, 실패 미저장, 동시 접근, 사본 격리와 활성 장면 유지도 검증했다.
- Linux 전체 빌드: 경고/오류 0. Godot import·헤드리스 시작·Linux Release export 통과. 시작 시 합성 테이블→경로 계획→지도/캐시 hit을 검사하고 `OPEND2_M107_TABLE_CACHE_READY`를 출력한다.
- 원격 3개 OS CI와 master 병합 결과는 아래 반영 기록에 갱신한다.
- 실제 1.10f 표/지도, GUI 입력·DPI, Windows/macOS 사용자 설치, 장시간 메모리/성능 인수는 미수행이다.

## 반영과 다음 작업

M0~M1-06은 [PR #1](https://github.com/imagineiluv-star/OpenD2/pull/1)로 master에 병합했다(`eda78022db75c75425730fce705ab065808e8404`). master push에도 3개 OS CI를 실행하도록 변경했다. 기능 브랜치 검증 후 M1-07 PR을 master에 병합한다.

다음 코드는 **M2-01 고정 tick·명령·게임 상태·난수·이벤트 경계**다. M1 전체 호환 완료를 기다린다는 의미가 아니라, 합성 콘텐츠로 코어를 진행하면서 실제 데이터 인수와 추가 스키마 지원을 별도로 유지한다.

형식 비교 출처: 기존 OpenD2 `DataTables.cpp` / `D2DataTables.hpp`, [D2MOO LevelsTbls.cpp](https://github.com/ThePhrozenKeep/D2MOO/blob/5596f5cb6c5251a0a07c6637d26458b06099d516/source/D2Common/src/DataTbls/LevelsTbls.cpp), 같은 커밋의 `DataTbls.cpp` / `LevelsTbls.h`. 소스·원본 게임 표·DLL을 복사하지 않았으며 [출처 기록](../../THIRD_PARTY_NOTICES.md)을 갱신했다.
