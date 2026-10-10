# Diablo III 참고 렌더링 고도화 계획

조사일: 2026-10-09. 상태: 조사·설계이며 아래 효과의 OpenD2 구현/성능 측정 완료가 아니다.
Diablo III 리소스 또는 엔진을 가져오는 계획이 아니라 공개 제작 기법을 적용하는 계획이다.

## 확인한 근거와 범위

| 자료 | 확인한 내용 | 해석 한계 |
| --- | --- | --- |
| Christian Lichtner, Blizzard, GDC 2012, [The Art of Diablo 3](https://gdcvault.com/play/1015306/the-art-of-diablo) | 고정 카메라를 활용한 회화적 스타일과 게임플레이를 지원하는 아트 방향 | 공개 세션 소개를 확인했다. 전체 영상 시청이나 엔진 소스 확인은 하지 않았다. |
| Julian Love, Blizzard, GDC 2013, [The VFX of Diablo](https://gdcvault.com/play/1017660/Technical-Artist-Bootcamp-The-VFX) | VFX·테크니컬 아트 제작 세션과 발표자/주제 확인 | 세션 소개만으로 구체적인 GPU 패스 구성을 단정하지 않는다. |
| Nick Seavert, JangaFX, 2026-03-02, [Exploring and Modernizing The VFX Methods of Diablo 3](https://jangafx.com/insights/diablo-3-vfx-experiments) | 저자의 직접 재현 실험: 여러 속도/스케일의 노이즈 곱셈, 마스크, 중간 명도 제어, 입자별 위상 변화, 발광 합성 | D3 발표를 참고한 Unreal 재현 실험이며 원본 D3 셰이더와 동일하다는 증거가 아니다. 현대화 부분은 저자의 제안이다. |
| Godot 공식 [렌더러 비교](https://docs.godotengine.org/en/stable/tutorials/rendering/renderers.html), [CanvasItem shader](https://docs.godotengine.org/en/stable/tutorials/shaders/shader_reference/canvas_item_shader.html) | Compatibility/Forward+/Mobile 선택과 CanvasItem 블렌드 모드 | 문서는 stable 기준이므로 실제 고정 버전 4.7.2와 각 OS GPU에서 재확인한다. |

확인되지 않은 D3 내부 구현을 deferred/PBR/특정 그림자 알고리즘으로 표기하지 않는다.
MPQ/CASC는 파일 저장 계층이며 화면 렌더링 방식이 아니다. CASC 읽기를 추가해도 D3 모델·재질·
애니메이션·맵·효과 디코딩과 장면 구성이 자동 해결되지 않는다.

## OpenD2 현재 구조에 적용

현재 `src/OpenD2.Client/project.godot`는 `gl_compatibility`를 사용하며 원본 지형/캐릭터는
DS1/DT1 및 DC6/DCC/COF의 색인 영상·팔레트를 해석한다. 먼저 원본 팔레트·피벗·가림 순서·
방향·프레임 타이밍을 검증하고 그 위에 선택적 효과를 얹는다. 자동 3D 모델 변환으로 표현하지 않는다.
Core의 충돌·전투·RNG·저장 규칙은 시각 효과에 의존하지 않게 유지한다.

| 순서/ID | 구현 제안 | 완료 판단 |
| --- | --- | --- |
| P0 / VIS-01 | 실제 자료의 고정 카메라 기준 장면과 원본 모드 캡처 확보. 캐릭터/배경/상호작용 대상의 명도·실루엣·선택 윤곽선 조절을 별도 표시 옵션으로 설계 | 동일 시점/카메라에서 캡처 비교. 효과 OFF가 원본 표시 기준과 일치하고 클릭 영역·가림 순서 유지 |
| P1 / VIS-02 | 직접 생성한 노이즈 2~3층을 UV 이동·배율 변화로 합성하는 연기/포털 셰이더. 모양 마스크, 중간 명도, 입자별 위상/속도 범위 노출 | 장시간 반복의 위상 줄무늬·검은 공백·경계 노출 확인. 이동/정지/시간 초기화 동작 검사 |
| P1 / VIS-03 | 가산 효과만 겹쳤을 때의 백색 포화를 줄이는 premultiplied-alpha 합성 실험. 소스 RGB의 premultiply 규약과 밝기 범위를 명시 | 검정/밝은 배경과 1/8/32개 겹침에서 가장자리 검은 띠·과포화·정렬 오류 검사. D3 Blend-Add와 같다는 이름/주장은 사용하지 않음 |
| P1 / VIS-04 | 타격·위험 예고·회복 효과를 형태/움직임/밝기 계층으로 구분. 색만으로 정보를 전달하지 않음 | 흑백에서도 위험 영역·행동 결과 식별. 적/아이템 이름과 타격 지점이 효과에 가려지지 않음 |
| P2 / VIS-05 | 효과 동시 수·화면 점유·텍스처 메모리 예산, 카메라 밖 정지/제거, 품질별 제한과 오브젝트 재사용 | 1080p 밀집 전투에서 효과 ON/OFF CPU/GPU p50/p95/p99, draw calls, 메모리, 투명 오버드로 기록 |
| P3 / VIS-06 | 원본 2D 검증 이후 별도 3D 장면으로 Forward+/Mobile 비교. 제작/사용 권한이 확보된 메시·리깅·재질을 명시적으로 연결 | Windows/Linux/macOS 네이티브 실행·장면 캡처·측정 후 렌더러 결정. 기본 Compatibility 경로 변경은 별도 검증 필요 |

위 단계는 새 계획이다. 구현 완료 체크박스로 기록하지 않는다.

## 초기 성능 목표와 시험 조건

- 목표: 기준 장치의 1920×1080에서 60 FPS, 전체 프레임 p95 16.7 ms 이내. 이는 측정 결과가 아니다.
- 추가 VFX GPU 시간은 초기 목표 2 ms 이내로 두고 실제 장치에서 조정한다. CPU/GPU 시간을 분리한다.
- 장치·OS·GPU·드라이버·렌더러·출력 해상도·배율·VSync·효과 수를 반드시 기록한다.
- Apple Silicon 네이티브와 Windows/Linux 실제 GPU를 각각 시험한다. Parallels 결과는 별도 항목이다.
- 워밍업 후 동일 입력 재생 60초, 저/고품질 비교, 최소 30분 메모리 안정성 시험을 수행한다.
- 시뮬레이션 상태 해시와 저장 결과는 효과 ON/OFF에서 일치해야 한다.
- 헤드리스 셰이더 로드/로그 검사는 배포 안정성 증거다. 실제 색·블렌딩·GPU 성능·GUI 인수의 대체가 아니다.

## 데이터 연동과 선행 작업

공개 D2 데모에서 읽힌 자료는 부분 콘텐츠 검증용이다. LoD 전체, 한글 자료, 원작 규칙 또는
D3 리소스 지원으로 확장 해석하지 않는다. 실제 데이터 결과는 별도 `DEMO_MPQ_RESULTS.md`에 기록한다.
P0의 장면 검증을 먼저 마치고 VIS-02/03 프로토타입을 작은 독립 브랜치로 진행한다.
현재 요청은 조사 결과의 계획 반영까지이며 이 문서가 새로운 렌더러 구현 완료를 뜻하지 않는다.
