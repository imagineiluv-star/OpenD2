# M1-05: DCC / COF animation preview

2026-10-07, `feat/m1-legacy-assets`. 일반 top-down DCC와 COF 레이어 합성의 코드·합성 검증을 구현했다. 실제 LoD 1.10f 콘텐츠 인수 및 아래 형식 제한은 남아 있으며 M1 전체 완료가 아니다.

## 구현

- `DccAnimation`: 소유한 입력 사본, 방향 오프셋 검사, LSB 비트 읽기, signed 좌표, optional data 건너뛰기, 독립 비트 스트림 경계 검사.
- 방향별 두 단계 복원: 셀 색상표(delta/raw, 부분 mask)를 먼저 복원하고 pixel selector를 읽는다. equal-cell 재사용·좌표 이동·크기 변경 시 초기화, 1/2비트 selector, 단색 fill, 최대 5×5 경계 셀을 처리한다.
- `CofAnimation`: 버전 20의 28바이트 헤더, 9바이트 레이어, 프레임 이벤트, 방향/프레임별 component 순서를 읽는다. 중복 component와 잘못된 순서를 거절한다. Speed는 원본 ushort로 보존하고 재생 속도로 단정하지 않는다.
- `AnimationClip`: COF 순서와 DCC 좌표로 합성한다. index 0을 투명으로 처리하고 클립 전체의 고정 캔버스·기준점을 사용해 프레임 크기 변화로 인한 위치 흔들림을 방지한다.
- Godot `DCC-COF` 탭: 선택 방향 로드, Play/Pause, 프레임 이동, 수동 Preview FPS. 파일 읽기·방향 디코딩은 작업 스레드에서 수행한다. 재생 중 파일 IO나 재디코딩을 하지 않고 하나의 GPU 텍스처를 갱신한다. 창을 닫는 동안 로딩이 끝나도 제거된 UI에 접근하지 않는다.
- `AssetAudit --decode`: DCC 모든 방향 복원 및 COF 구조 검사를 추가했다. `DecoderVersion=m1.05-1`. COF validated는 외부 DCC 조합까지 검증했다는 뜻이 아니다. Complete=false와 실제 데이터 버전 unverified를 유지한다.

## 사용

1. 왼쪽에서 원본 게임 폴더를 선택한다.
2. DCC-COF 탭의 첫 칸에 MPQ 내부 `.dcc` 또는 `.cof` 경로, 두 번째 칸에 팔레트 경로를 입력한다.
3. COF는 세 번째 칸에 **component 번호=DCC 논리 경로**를 한 줄씩 지정한다. 0=HD, 1=TR, 2=LG, 3=RA, 4=LA, 5=RH, 6=LH, 7=SH, 8~15=S1~S8. COF에 선언된 모든 component를 정확히 한 번씩 지정해야 한다.
4. Direction을 고르고 Load selected direction을 누른다. 방향은 파일 내부 순서다. 장비/종류/동작에 따른 파일명 추론과 게임 방위각 매핑은 아직 적용하지 않는다.
5. Play/Pause 또는 Frame을 사용한다. Preview FPS는 미리보기 설정이며 공격 속도 등 게임 규칙과 무관하다.

시작 화면의 두 레이어 샘플은 직접 생성한 DCC/COF 바이트다. Blizzard 게임 파일을 포함하지 않으며 실제 게임 호환 증거가 아니다.

```sh
python eng/build-native.py
python eng/validate.py --export Linux
dotnet run --project tests/OpenD2.Tests -c Release
dotnet run --project tools/OpenD2.AssetAudit -- --decode /path/to/DiabloII known-paths.txt
```

## 예산과 제한

- DCC 입력 32 MiB, 방향 32, 전체 프레임 4,096. 방향별 원본 픽셀 합 16,777,216, 캔버스 최대 4,096×4,096, 셀 300,000. 모든 비트 읽기는 해당 방향/하위 스트림 범위 안으로 제한한다.
- COF 최대 16레이어·32방향·255프레임. 합성 원본 픽셀 합과 결과 전체 프레임 픽셀 합은 각각 16,777,216 이하. 뷰어의 COF+DCC 누적 입력은 64 MiB 이하. 이는 현재 도구의 처리 정책이다.
- DCC bottom-up 프레임은 **명시적 오류**다. 근거 없는 수직 반전으로 대신 처리하지 않는다. optional bytes의 내용, coded-size 메타데이터의 의미 및 바이트 패딩 값은 검증하지 않는다.
- COF 특수 길이 변형, PL2 기반 투명/색상 효과, 그림자 생성, 장비 선택/파일 경로 자동 조합, DCC↔COF 방향·프레임 수 재매핑은 미지원이다. 뷰어는 수가 맞지 않거나 OverrideTransparency가 켜진 조합을 거절한다. Shadow/Selectable/DrawEffect/Events는 메타데이터로 보존하며 게임 동작을 실행하지 않는다.
- 실제 MPQ와 픽셀 대조, 실제 장비 조합, OS별 GUI/DPI·설치 인수는 미수행이다. 프레임 RGBA 변환은 미리보기 용도이며 플레이 중 대규모 캐시/아틀라스 최적화는 M1-07 이후 별도 측정한다.

## 검증

- 40/40 계약 테스트: 이전 27개 + DCC/COF/합성 12개 + MPQ 통합 1개.
- 명시한 예상 픽셀과 비교: signed 원점, 다중 방향, raw/delta 및 부분 mask, 1/2비트 selector, 단색 fill, equal-cell 이동/크기 변경, 5픽셀 셀, 25셀 격자. 모든 바이트 prefix 잘림, 메타데이터/순서/스트림 오류, 좌표·메모리 예산 거절을 포함한다.
- COF 합성의 프레임별 순서, 투명 구멍, 고정 원점, 누락 매핑·미지원 효과 실패 및 MPQ 원본 불변을 확인한다.
- Linux 전체 빌드·Godot import·헤드리스·export 통과. SDK 경로 없는 Linux 배포 실행에서 기존 시작 마커와 `OPEND2_M105_ANIMATION_READY`를 확인했다. Godot 검사는 합성 DCC 두 방향→COF 두 레이어→두 번째 프레임 텍스처와 alpha까지 확인한다.
- 원격 CI 결과: 게시 후 기록.

## 출처와 다음 작업

기존 GPL-3.0 저장소의 `Engine/DCC.cpp/.hpp`(Necrolis, SVR, Paul Siramy 및 eezstreet 출처 표기)와 `Engine/COF.hpp` 구조를 C#으로 재구성했다. 외부 비교 대상은 OpenDiablo2/dcc `16ddc7029d0bf7a90e59f22ace61a17d604722cc`, OpenDiablo2/cof `180eb64494ba142c2b2bb6281f74c4fb1275bfa8`의 소스다. 외부 Go 코드·게임 샘플·패키지를 복사하거나 새 런타임 의존성으로 추가하지 않았다.

다음 코드 단계는 **M1-06 DT1/DS1 지도·레이어·충돌 뷰어**다. 병행 인수 잔건은 실제 1.10f 데이터의 픽셀 대조와 M1-05 미지원 변형·효과·재매핑 호환 범위 확정이다.
