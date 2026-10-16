# P2-1 원작 프레임 버퍼, 해상도, 캡처 하네스

- `LegacyFrameBuffer`(src/OpenD2.Assets/LegacyScreen.cs): 800x600 또는 640x480 고정 크기의 8비트 인덱스 표면. CPU로만 그린다. 아무것도 그리지 않은 픽셀은 순흑 (0,0,0)이다.
- `LetterboxLayout.Fit`: 창에 들어가는 가장 큰 정수 배율을 고르고(최소 1) 가운데에 놓는다. 남는 영역은 검정이다.
  - 1920x1080 창에서 800x600은 **1배**다. 2배에는 1600x1200이 필요하다. 640x480은 2배다.
  - 감사 문서의 "1920x1080에서 2배"는 640x480에만 맞는다.
- 클라이언트 `--original-mode [--scene gameselect|charselect] [--resolution 800x600|640x480] [--palette units]`
  - 개발 도구 UI, 3D 카메라, 1152x720 스트레치 없이 `Legacy/LegacyScreen`만 띄운다.
  - SubViewport와 Nearest 필터, 정수 배율, 검정 레터박스를 쓴다.
- 헤드리스 캡처는 두 경로가 있고 결과는 같다. 같은 커밋, 데이터, 요청이면 PNG 바이트가 동일하다.
  - `OpenD2.ScreenDiff --capture <scene> --out <file>`
  - `godot --headless -- --capture <scene> --out <file>`
- 출력 파일은 Git 체크아웃 밖에만 쓸 수 있다. `.gitignore`에 캡처 패턴을 추가했다.
- `OpenD2.ScreenDiff --diff a.png b.png`: 픽셀 일치율, MAE(채널당 0~255), SSIM(BT.601 휘도, 8x8 창, stride 4)을 JSON으로 출력한다. 임계값 옵션이 있다.
- 기준값(해시만 저장): gameselectscreenexp.dc6의 RGB SHA-256
  - units `9ec1529c…`, act1 `4bc657d0…`. 독립 Python 렌더러 결과와 같다.
  - act1과 units는 13,769픽셀이 다르다.
