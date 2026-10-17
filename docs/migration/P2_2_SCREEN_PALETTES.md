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
- 8비트 백버퍼에서 원작처럼 계산한다: 소스 인덱스 → PL2 조명 표(0 = 가장 어두움, 31 = 최대 밝기) → 목적지 인덱스와 256x256 블렌드 표(25/50/75% 투명, 가산, 곱셈).
  덮이지 않은 목적지는 인덱스 0(검정)으로 본다. `ApplyLight`는 화면 전체를 조명 단계로 바꾼다.
- `--light 0-31`은 캡처/원작 모드 화면을 그 팔레트의 `pal.pl2` 조명 표로 바꾼다. units에는 1.11b에 `pal.pl2`가 없으므로 `--palette act1 --light N`처럼 쓴다.
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
