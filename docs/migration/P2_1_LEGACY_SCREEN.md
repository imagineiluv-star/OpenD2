# P2-1 원작 프레임 버퍼, 해상도, 캡처 하네스

- `LegacyFrameBuffer`(src/OpenD2.Assets/LegacyScreen.cs): 800x600 또는 640x480 고정 크기의 8비트 인덱스 표면. CPU로만 그린다. 아무것도 그리지 않은 픽셀은 순흑 (0,0,0)이다.
- `LetterboxLayout.Fit`: 기본값은 비율을 유지하며 창 높이(또는 너비)에 맞추는 fit 배율이다. 1920x1080이면 1.8배, 1440x1080이고 좌우 240px는 검정 필러박스다. 비정수 배율의 기본 필터는 `sharp`다. 먼저 ceil(배율)만큼 nearest로 정수 확대하고(1.8배면 2배, 1600x1200), 그다음 linear로 맞춤 영역까지 줄인다. `--filter nearest`, `--filter linear`은 800x600 버퍼에서 바로 샘플링하는 옵션이다. 정수 배율에서는 세 필터 모두 nearest와 같다.
  - `--scale integer`: 정수 배율(최소 1)의 픽셀 퍼펙트 모드. 1920x1080에서 800x600은 1배, 640x480은 2배다.
  - `--capture`는 배율과 상관없이 800x600(640x480) 내부 버퍼를 저장한다.
- 클라이언트 `--original-mode [--scene gameselect|charselect] [--resolution 800x600|640x480] [--palette units] [--scale fit|integer] [--filter sharp|nearest|linear]`
  - 개발 도구 UI, 3D 카메라, 1152x720 스트레치 없이 `Legacy/LegacyScreen`만 띄운다.
  - SubViewport를 창에 맞춰 늘리고 남는 영역은 검정으로 채운다.
- 헤드리스 캡처는 두 경로가 있고 결과는 같다. 같은 커밋, 데이터, 요청이면 PNG 바이트가 동일하다.
  - `OpenD2.ScreenDiff --capture <scene> --out <file>`
  - `godot --headless -- --capture <scene> --out <file>`
- 출력 파일은 Git 체크아웃 밖에만 쓸 수 있다. `.gitignore`에 캡처 패턴을 추가했다.
- `OpenD2.ScreenDiff --diff a.png b.png`: 픽셀 일치율, MAE(채널당 0~255), SSIM(BT.601 휘도, 8x8 창, stride 4)을 JSON으로 출력한다. 임계값 옵션이 있다.
- 기준값(해시만 저장): gameselectscreenexp.dc6의 RGB SHA-256
  - units `9ec1529c…`, act1 `4bc657d0…`. 독립 Python 렌더러 결과와 같다.
  - act1과 units는 13,769픽셀이 다르다.

## 수락 기준 (fit 기준으로 갱신)
- 기본 fit: 1920x1080 창에서 800x600 화면은 1440x1080 @ x=240(1.8배)에 놓이고, 바깥 영역은 순흑 (0,0,0)이다.
- `--scale integer`: 1920x1080에서 800x600은 1배(560,240), 640x480은 2배다.
- 내부 캡처(`--capture`)는 배율이나 필터와 상관없이 800x600(640x480) 버퍼를 바이트 단위로 고정해 저장한다.
- 실제 창 일치(GodotWindowContracts, 디스플레이가 있을 때만 실행하고 없으면 SKIP)
  - 정수 배율과 1배 미만(CPU box filter를 1:1로 표시)은 CPU `--window`와 100% 같아야 한다.
  - 비정수 sharp와 nearest는 MAE ≤ 0.5, SSIM ≥ 0.99를 만족해야 한다.
- 창이 800x600보다 작으면 sharp와 linear는 box filter(면적 평균)로 줄여 aliasing을 막는다. nearest는 요청대로 점 샘플링한다.
- sharp의 정수 확대 배수는 CPU와 Godot가 같은 함수 `LetterboxLayout.SharpPrescale`(반올림된 rect 기준 ceil)로 계산한다.

## 기준 해시 (gameselect, units, 1.11b)
| 대상 | 명령 | PNG SHA-256 |
|---|---|---|
| 800x600 내부 버퍼 | `--capture gameselect` | `13e758f8a5662907e282f445f62d30ce1bb0bdf1640d37e99b02b4ab450176c0` |
| 1920x1080 창 (기본 fit 1.8배 + sharp, CPU) | `--capture gameselect --window 1920x1080` | `65cac090c4e16c22ddcffb9ca0943d75a8cddd60acd20478207c11a331c8de25` |

같은 커밋에서 두 번 캡처하면 두 해시 모두 바이트가 같다. 내부 버퍼 해시는 표시 경로(fit, sharp)를 검증하지 않는다.

## GPU 화면 비교 임계값
- 실제 GPU(Godot) 창과 CPU `--window` 결과는 비정수 배율에서 채널값이 ±1 다르다. 1920x1080에서 일치율 89.5%, MAE 0.071, SSIM 0.9995였다.
- 그래서 GPU 창 비교에는 `ScreenDiff.GpuWindowMaxMae = 0.5`, `GpuWindowMinSsim = 0.999`를 쓴다. CLI에서는 `--diff a b --gpu-window`이다. `GodotWindowContracts`도 같은 값을 쓴다.
- 정수 배율, 1배 미만 경로, 내부 버퍼 비교는 기본값(100% 일치)을 쓴다.
