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
  - 비정수 sharp와 nearest는 콘텐츠 rect 기준으로 MAE ≤ 0.5, SSIM ≥ 0.999, 채널별 최대 오차 ≤ 1을 만족해야 한다(nearest는 최대 오차 조건 대신 정확 일치율 ≥ 99.95%, 8x8 블록당 불일치 ≤ 16, 2x2 불일치 덩어리 없음, 불일치 픽셀은 CPU 이웃 8픽셀 중 하나와 같아야 함). 또한 레터박스 막대는 모두 0이어야 한다.
- 창이 800x600보다 작으면 sharp와 linear는 box filter(면적 평균)로 줄여 aliasing을 막는다. nearest는 요청대로 점 샘플링한다.
- sharp의 정수 확대 배수는 CPU와 Godot가 같은 함수 `LetterboxLayout.SharpPrescale`(반올림된 rect 기준 ceil)로 계산한다.

## 기준 해시 (gameselect, units, 1.11b)
| 대상 | 명령 | RGB 픽셀 SHA-256 |
|---|---|---|
| 800x600 내부 버퍼 | `--capture gameselect` | `9ec1529c911158ec76fc96854bb774f8edeb660b2884d014d554ff6ffd4fd02f` |
| 1920x1080 창 (기본 fit 1.8배 + sharp, CPU) | `--capture gameselect --window 1920x1080` | `6f647b62846a234f74912114af224094dc76189e362f0e87cb8e5998b040c1ec` |

PNG 바이트가 아니라 디코딩된 RGB 픽셀을 해시한다(런타임 zlib이 바뀌어도 깨지지 않음). 두 해시는 `LegacyScreenContracts.Genuine`에 고정돼 있다. 이 검사는 **로컬 QA 전용**이다. `D2_DATA_PATH`가 없으면(CI) 실행되지 않는다. GPU 창 샷은 드라이버에 따라 ±1씩 달라지므로 해시를 고정하지 않고 아래 허용치로 비교한다. 내부 버퍼 해시는 표시 경로(fit, sharp)를 검증하지 않는다.

## GPU 화면 비교 임계값
- 실제 GPU(Godot) 창과 CPU `--window` 결과는 비정수 배율에서 채널값이 ±1 다르다. 1920x1080에서 일치율 89.5%, MAE 0.071, SSIM 0.9995였다.
- GPU 창 비교는 `ScreenDiff.CompareGpuWindow`를 쓴다. 콘텐츠 rect(1920x1080이면 1440x1080@240,0)만 잘라서 지표를 계산하고, 막대 영역은 모든 픽셀이 0인지 따로 확인한다. 막대를 포함하면 SSIM이 부풀고 MAE가 희석되기 때문이다.
- 임계값은 `GpuWindowMaxMae = 0.5`, `GpuWindowMinSsim = 0.999`, `GpuWindowMaxChannelError = 1`이다. 비정수 nearest는 소스 픽셀 경계의 부동소수 동률 때문에 픽셀 전체가 뒤집힌다(실측: 1700x1000에서 99.973% 일치, 최대 오차 184). 그래서 최대 오차 조건 대신 `GpuWindowNearestMinMatch = 99.95`%, 8x8 블록당 불일치 ≤ `GpuWindowNearestMaxBlockMismatches = 16`, 2x2 불일치 덩어리 금지, 불일치 픽셀은 CPU 이웃 값과 같아야 한다는 조건을 적용한다(CLI `--nearest`). CLI는 `--diff <gpu.png> <cpu.png> --gpu-window [--screen 800x600|640x480] [--scale fit|integer]`이며, `--min-match/--max-mae/--min-ssim`과 함께 쓰면 exit 2로 거부한다. 창 관리자가 창을 요청 크기보다 줄이면 Godot 케이스는 SKIPPED(사유 표시)되고, `OPEND2_REQUIRE_GODOT_WINDOW=1`이면 실패한다. `GodotWindowContracts`도 같은 함수를 쓴다.
- 콘텐츠 rect만 잰 실측값(1920x1080 실제 GPU 샷): 일치율 86.05%, MAE 0.094, SSIM 0.99937, 최대 채널 오차 1, 막대 모두 0 → 통과.
- SSIM은 근사값이다. BT.601 휘도에 8x8 균등 창, stride 4를 쓰며, 색차 오차는 MAE와 최대 오차로 잡는다.
- `GodotWindowContracts`는 정품 데이터, 디스플레이, Godot 바이너리, Godot 빌드가 모두 있어야 실행되는 **로컬 QA 전용** 테스트다. 케이스는 1700x1000 4개와 1920x1080(fit sharp/nearest) 2개다. 환경이 없으면 각 케이스를 사유와 함께 `SKIPPED`로 표시하고 요약에 `N skipped`로 집계한다. 통과로 세지 않는다. `OPEND2_REQUIRE_GODOT_WINDOW=1`이면 환경이 없을 때 실패로 처리한다.
- 정수 배율, 1배 미만 경로, 내부 버퍼 비교는 기본값(100% 일치)을 쓴다.
