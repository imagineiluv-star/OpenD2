# M1-04: Palette / text TBL / DC6

`feat/m1-legacy-assets`에서 M1-01~03에 이어 구현했다. 게임 데이터 없이 재현 가능한 코드·합성 검증 범위 완료이며 실제 1.10f 콘텐츠 인수는 남아 있다.

## 구현과 사용

- `LegacyFormats.cs`: 768바이트 BGR 팔레트→RGB/RGBA, text TBL 헤더·인덱스·활성 항목·문자열 범위, DC6 헤더·방향·프레임·RLE 디코딩.
- DC6는 위/아래 방향, 부호 있는 오프셋, 투명 run을 보존한다. 팔레트 index 0과 투명 픽셀을 구분하는 opacity 배열을 사용한다. 한 번에 한 프레임을 디코딩한다.
- TBL은 원본 key/value 바이트와 항목 인덱스를 보존한다. `entry.Value(encoding)`처럼 언어별 인코딩을 명시한다. 중복 키를 임의로 버리거나 UTF-8이라고 단정하지 않는다.
- `AssetDecoders.cs`: 디코더 선택, MPQ 우선순위 읽기. 상위 MPQ가 손상돼 열리지 않으면 하위 버전으로 조용히 대체하지 않는다.
- `AssetAudit --decode`: 인벤토리 해시를 유지하면서 디코딩 검사 결과를 추가한다. 기본 실행은 기존처럼 해시 조사만 한다.
- Godot 오른쪽 DC6 뷰어: 게임 폴더 선택 → DC6 논리 경로 및 palette 경로 입력 → Load → 프레임 번호 선택. 번호는 방향 우선 순서이며 방향·크기·원본 오프셋을 표시한다. 로딩은 작업 스레드에서, 텍스처 생성은 UI 스레드에서 수행한다.
- 시작 화면의 체크무늬는 직접 생성한 합성 샘플이다. 실제 게임 리소스나 이관 성공 화면이 아니다.

```sh
python eng/build-native.py
python eng/validate.py --export Linux
# Windows / macOS는 --export Windows / --export macOS

dotnet run --project tools/OpenD2.AssetAudit -- --decode /path/to/DiabloII known-paths.txt
```

known-paths.txt는 선택 사항이다. 내부 listfile에 없는 이름을 검사하려면 한 줄씩 MPQ 상대 경로를 제공한다. JSON은 stdout에 출력하며 원본 MPQ를 변경하거나 리소스를 추출하지 않는다. 출력 파일은 게임 폴더 밖에 저장한다.

## 보고서 상태

| DecodeStatus | 의미 |
|---|---|
| not_implemented | 이번 디코더 대상 아님 |
| not_requested | 지원 형식이나 --decode 미지정 |
| read_failed | 해시/읽기 단계 실패로 디코딩 안 함 |
| validated | 현재 파서의 구조·범위 검사 통과, DC6는 모든 프레임 디코딩 통과 |
| failed | 디코딩·읽기 예산 또는 형식 오류. ErrorCode 확인 |

`DecoderVersion=m1.04-1`, `DecodeBytes`는 추가 디코딩 읽기량이며 기존 `HashedBytes`와 합산해 총 예산에 적용한다. `Complete=false`, 버전 `unverified`, `RuntimeStatus=not_loaded`는 유지한다. 인벤토리 검사와 뷰어에서 잠시 표시한 사실을 게임 전체 사용 완료로 취급하지 않는다.

## 한도와 미지원 범위

- 디코더 입력 최대 32 MiB. DC6 방향 32, 총 프레임 4,096, 가로/세로 4,096, 전체 16,777,216픽셀. 겹치는 프레임 범위는 거절한다.
- TBL 해시 슬롯 최대 100,000, key 검색 최대 65,536바이트, 누적 key/value 처리량 32 MiB. 같은 긴 문자열을 반복 참조하는 입력도 한도를 적용한다.
- 이 한도는 현재 처리 정책이며 모든 모드 파일이 잘못됐다는 뜻이 아니다. 초과는 실패로 보고한다.
- text TBL의 CRC·저장 해시 재계산과 언어별 문자 인코딩 확정, 폰트 TBL, PL2 색상 변환은 미구현이다. `validated`는 이 항목까지 검증했다는 뜻이 아니다.
- DC6 NextBlock/종료 패딩의 의미 검증, 프레임 이어붙이기·애니메이션·좌표 배치·장비 합성은 아직 없다. 뷰어는 선택 프레임을 독립적으로 표시한다.
- DCC/COF, DT1/DS1, TXT/BIN은 다음 단계다. 실제 게임 파일과 픽셀 대조, GUI 조작·DPI 인수는 별도 필요하다.

## 검증 기록

로컬 Linux: 27/27 계약 테스트 통과, 전체 Debug 빌드 오류·경고 0, Godot import·헤드리스 뷰어 검사·Linux export 통과. SDK 경로 없이 배포 실행에서도 `OPEND2_M104_PREVIEW_READY`와 기존 시작 마커를 확인했다.

기존 17개에 형식 테스트 8개와 MPQ 통합 2개를 추가했다. BGR/alpha·행 방향·음수 오프셋·다중 방향·잘림·크기/오프셋/인덱스·잘못된 run·문자열 종료·반복 참조 예산·미지원 형식 분류·정상/손상 DC6 보고·원본 파일 불변을 검증한다. Godot 검사에는 합성 DC6→RGBA→ImageTexture 생성과 픽셀 색상 확인을 포함한다.

CLI의 --decode JSON, 게임 파일 누락 시 exit 3, 필수 인수 누락 시 exit 2도 확인했다. 원격 Windows·Linux·macOS CI가 모두 성공했다(2분 41초). 코드 커밋 `fde56655b274dddd84c90f866bc4070ec7408faf`, [CI 실행](https://github.com/imagineiluv-star/OpenD2/actions/runs/37622172703).

## 형식 근거와 다음 작업

원본 저장소의 `Engine/Palette.cpp`, `Renderer_GL.cpp`의 BGR 업로드, `DC6.hpp/cpp`, `TBL_Text.hpp/cpp`와 대조했다. text TBL의 바이트·길이 취급은 OpenDiablo2/tbl_text의 `pkg/tbl.go` 커밋 `b33539952a672f956d6204f2d7d7202d55127a63`도 읽고 비교했다. 외부 코덱 코드나 패키지를 새 의존성으로 복사/추가하지 않았다.

다음 작업은 **M1-05 DCC/COF 방향·레이어 합성과 애니메이션 뷰어**다. M1 전체 완료나 D2R 수준 그래픽 완성을 의미하지 않는다.
