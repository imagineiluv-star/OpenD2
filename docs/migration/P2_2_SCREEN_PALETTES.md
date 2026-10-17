# P2-2 화면별 팔레트, PL2 조명·블렌드, 감마

**상태: 부분 완료.** 팔레트 규칙, PL2 조명·블렌드 연결, 감마, act1/units 13,769px 재현은 끝났다.
원작 캡처 대비 조명 MAE ≤ 3 / SSIM ≥ 0.98(Rogue Encampment 고정 지점)은 **P2-2b**로 분리해 월드 렌더 단계에서 한다
([작업계획](WORK_PLAN.md)). 조명 방향(0 = 가장 어두움)과 감마 단계도 아직 원작으로 검증하지 않았다.

## 팔레트 선택 규칙 (`ScreenPalettes`)
원작 클라이언트의 `PAL_*` 사용처(`Shared/D2Shared.hpp`, `Modcode/Client/UI/Menus/*.cpp`, `Panels/*.cpp`)를 따른다.

| 화면 | 팔레트 | 근거 |
|---|---|---|
| trademark | sky | `Trademark.cpp` `SetGlobalPalette(PAL_SKY)` |
| gameselect | units | `OtherMultiplayer.cpp` gameselectscreenEXP.dc6 + `PAL_UNITS` |
| tcpip | units | `TCPIP.cpp` + `PAL_UNITS` |
| charselect (캐릭터 생성 배경) | fechar | `CharCreate.cpp` `SetGlobalPalette(PAL_FECHAR)` |
| charlist (캐릭터 선택 패널) | sky | `Panels/CharSelect.cpp` `SetGlobalPalette(PAL_SKY)` |
| loading | loading | `Loading.cpp` `SetGlobalPalette(PAL_LOADING)` |
| endgame / endgame2 | endgame / endgame2 | `PAL_ENDGAME*` |
| 월드 1~5막 | act1~act5 | `ScreenPalettes.ForAct` |

- `--palette auto`(기본값)는 위 표를 쓴다. 이름을 주면 18개 원작 팔레트 폴더 이름만 받는다.
- 에셋 캐시 PNG는 더 이상 act1로 고정하지 않는다(`AssetCacheOptions.PngPalette = null`). 리소스 경로로 고른다:
  막 폴더(`act1`~`act5`)는 그 막, front-end 캐릭터 생성 그림은 fechar, trademark 그림은 sky, `ui\loading`은 loading, 나머지(UI, 아이템, 유닛)는 units.
  팔레트 규칙 버전(`screen-palettes-1`)이나 강제 팔레트는 캐시 서명에 들어가고, converter는 `p1-mpq-4`로 올려 기존 캐시를 다시 만든다.
  매니페스트 `pngPalettes`에 팔레트별 리소스 수를 기록한다.

## PL2 조명·블렌드 (`LegacyFrameBuffer.Draw(..., LegacyDrawEffect, Pl2Palette)`)
- 8비트 백버퍼에서 원작처럼 계산한다: 소스 인덱스 → PL2 조명 표(0 = 가장 어두움, 31 = 최대 밝기) → 목적지 인덱스와 256x256 블렌드 표(PL2 알파 표 1~3(`Trans25/50/75`, 방향은 아래 참고), 가산, 곱셈).
  덮이지 않은 목적지는 인덱스 0(검정)으로 본다. `ApplyLight`는 화면 전체를 조명 단계로 바꾼다.
- `--light 0-31`은 캡처/원작 모드 화면을 그 팔레트의 `pal.pl2` 조명 표로 바꾼다. units에는 1.11b에 `pal.pl2`가 없으므로 `--palette act1 --light N`처럼 쓴다.
- **Trans25/50/75의 방향은 미검증이다(QA P3-3).** 코드는 `table[source*256 + destination]`로 찾는다. 1.11b 표를 RGB로 재보면 이 순서에서
  첫째 표(`Trans25`, PL2 `+0x3500`)는 **소스 25% + 목적지 75%**, 셋째 표는 소스 75%다. 즉 `Trans25`는 "소스 불투명도 25%"(= 75% 투명)로
  동작하고, 예전 문서의 "25% 투명"과 반대다. 어느 쪽이 원작과 같은지 정하지 못했다:
  - OpenD2 C++(`Engine/Palette.hpp`의 `pTrans25[256][256]`, 초기 `GraphicsManager.cpp`)는 표를 256바이트 행으로 텍스처에 올릴 뿐 렌더에서
    인덱스 순서를 쓰지 않는다. 블렌드 enum(`D2ColorBlending`)에도 25/50/75 구분이 없다.
  - 커뮤니티 자료도 엇갈린다. OpenDiablo2 `pl2`는 세 표를 "25%, 50%, 75% opacity"라고 하고, Phrozen Keep(SVR/Paul Siramy)의 COF 설명은
    첫째 표를 "25% transparency"라고 한다. 어느 쪽도 행이 소스인지 목적지인지 밝히지 않는다.
  - 그래서 코드는 바꾸지 않았다(이름 `Pl2Blend.Alpha25`는 "불투명도 25%" 해석과 맞는다). 원작 캡처(반투명 레이어가 있는 COF, 예: 텔레포트 패드)로
    P2-2b에서 확인한다. `MaxComponent`(엔진 struct의 `pDarkBlend`) 표도 어떤 모델과도 맞지 않아 이름이 미확인이다(QA P3-4).
- 감산(subtractive) 블렌드: 현재 검증된 PL2 레이아웃(`Pl2Palette`, 1.11b 16개 파일로 확인)에는 가산·곱셈·최대 성분 표가 있고 별도 감산 표는 확인되지 않았다.
  그래서 곱셈 표(어둡게 하는 쪽)를 제공하고, 감산 표의 존재 여부는 원작 캡처로 확인이 필요하다.
- 범위: 원작 모드 CPU 경로(`--capture`, `--original-mode`가 쓰는 `LegacyFrameBuffer`)에 연결했다. 개발 도구 미리보기(HUD/아이템/맵 RGBA 텍스처)는 바꾸지 않았다.

## 감마 (`LegacyGamma`, `--gamma 0.5-3.0`)
- 원작은 "gamma" 비디오 옵션을 팔레트 조회 뒤 디스플레이 감마 램프로 적용한다. 같은 위치(최종 RGB)에 `out = round(255 * (in/255)^(1/gamma))`를 적용한다.
- 1.0이 기본값이며 항등이라 기존 캡처는 바뀌지 않는다. 1.14d 슬라이더 단계와 감마 값의 대응은 문서화되지 않아 **추정**이며 원작 캡처로 검증이 필요하다.

## 해시 변화
- gameselect(auto = units): 변화 없음 `9ec1529c…fd02f`.
- charselect 기본값: units → fechar로 바뀌어 RGB 해시가 `c85bab3e…69f5`(units)에서 `aec740fb…8a8f`(fechar)로 바뀐다. 둘 다 독립 파이썬 기준과 100% 일치한다.
- act1 + light 12 gameselect: `47be175a…30f2`(파이썬 기준이 원시 PL2 오프셋 `1024 + level*256`로 직접 계산).
- 에셋 캐시: converter `p1-mpq-4`, UI DC6 PNG가 act1 대신 units로 바뀐다.

## 파이썬 기준 렌더러 (`eng/palette_reference.py`)
- OpenD2 코드를 쓰지 않는 독립 구현이다: DC6를 직접 디코드하고, 800x600에 행 우선으로 이어 붙이고, 원시 `pal.dat`(BGR)와 원시 PL2 조명 오프셋
  `1024 + level*256`으로 RGB를 만든 뒤 SHA-256만 출력한다. 게임 파일은 쓰지 않는다.
- 입력은 저장소 밖에 만든 원본 파일 폴더다(`OpenD2.AssetAudit --build-cache ... --include "data\global\palette" --png none`와
  `--include "data\global\ui\frontend"`의 `raw/`). 이 캐시 목록에는 `pal.pl2`가 없을 수 있어 `--light`에는 `--pl2-file`을 쓴다.
- 1.11b gameselect 배경을 18개 원작 팔레트 전부로 렌더한 해시를 `ScreenPaletteContracts.GameSelectPaletteRgbSha256`에 두고,
  genuine 테스트가 C# 렌더 결과와 100% 같은지 확인한다. 기존 units/act1/fechar/charselect-units 해시도 이 스크립트로 다시 나온다.
- 에셋 캐시는 팔레트를 읽지 못해 PNG를 건너뛰면 `errors`에 팔레트마다 한 줄(건너뛴 개수 포함)을 남긴다.
