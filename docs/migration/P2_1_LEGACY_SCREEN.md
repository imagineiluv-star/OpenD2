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
