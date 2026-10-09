# OpenD2 출시 후보 사용 테스트

함께 전달한 **특정 Release URL / release-manifest.json**의 버전·커밋만 시험한다.
최신 태그를 임의 선택하거나 소스를 다시 빌드하지 않는다. 이 지침은 QA용이며 게임 내부 NPC 모델과 별개다.
기본 QA-01~06/08에는 게임 서버 연결이나 원본 디아블로 파일이 필요하지 않다.
원본 장면 QA-09는 사용자가 제공한 게임 데이터와 검증된 scene JSON이 있을 때만 수행한다.

## 실행 규칙

1. `release-manifest.json`과 `SHA256SUMS`를 읽고 OS/CPU에 맞는 파일을 내려받아 SHA-256과 크기를 비교한다.
   `qa-result-template.json`을 복사해 결과 파일로 사용한다. OS, CPU, GPU/렌더러, 화면 크기,
   SDK 설치 유무, 버전·커밋·파일 해시를 기록한다.
2. 1회 최대 20분, 같은 실패 재시도 최대 1회. 자동 반복·릴리즈 발행·소스 변경은 하지 않는다.
   QA-08의 10분 경계 확인 시간을 확보한다. 시간이 부족하면 남은 항목을 NOT_RUN으로 기록하고
   별도 요청된 다음 회차에서 같은 버전/프로필로 계속한다. QA-10은 별도 2시간 시험 요청이 있어야 실행한다.
   모델 다운로드는 QA-07을 명시적으로 요청받은 경우만 수행한다. 기본 시험의 추가 가중치 다운로드는 0회다.
3. 기존 사용자 데이터는 변경하지 않는다. 별도 테스트 계정/프로필을 사용한다.
   Linux는 새로운 디렉터리를 `XDG_DATA_HOME`으로 지정해 앱을 실행할 수 있다.
   Windows/macOS는 별도 테스트 계정을 사용한다. SDK가 있다면 'SDK 없는 PC 인수'는 BLOCKED다.
4. 전체 압축을 풀고 **배포 실행 파일**을 일반 창으로 실행한다. `--headless`, `--smoke-test`,
   Godot 편집기, `dotnet run`으로 GUI 시험을 대체하지 않는다. Linux 실행 권한이 없다면
   패키지 실패로 기록하며 몰래 chmod로 고친 뒤 통과시키지 않는다.
5. 실제 키보드/마우스로 조작한다. 저장 파일 수정·게임 명령 직접 주입으로 진행하지 않는다.
   디스플레이·그래픽 드라이버·네이티브 실행 제한 때문에 창이 안 뜨면 BLOCKED로 기록한다.
6. stdout/stderr, 사용자 데이터 루트의 `logs/`, 화면·입력 이력을 보존한다.
   스크린샷/영상에는 시험 게임 창만 포함한다. 개인 파일·계정 토큰은 결과에 포함하지 않는다.

## 시나리오

| ID | 실제 조작 | 확인할 결과 |
|---|---|---|
| QA-01 | 새 프로필에서 앱 실행. 게임 데이터 경로·모델은 비운다. | Simulation 시작 메뉴가 표시되고 Continue/Load가 비활성화됨. New game으로 시작한 뒤 기본 기능 조작. 첫 실행 오류·권한 요청 기록 |
| QA-02 | New game으로 시작한 Simulation 탭에서 Show diagnostics 활성화 → Seed 1 → New run → 확인 → 그리드 클릭 → 방향키 이동. Camp Guide 근처에서 E. 금색 Cellar 포털에서 E. Space로 몬스터 3마리 처치. Camp로 귀환해 Guide에게 E. | 체력 바/HP 숫자·지역·퀘스트 단계·처치 수·완료와 체력 회복. 다시 상호작용해 중복 완료가 발생하지 않는지 확인 |
| QA-03 | 청록색 아이템 근처에서 F. 가방 슬롯 선택 → Equip selected → Unequip selected → Drop selected. | 가방·장비·능력 표시가 조작과 일치. 버튼별 전후 화면 기록 |
| QA-04 | Show diagnostics 활성화 → Pause → Save checkpoint. tick·State hash·위치·체력·퀘스트·아이템 기록. 종료 후 같은 프로필로 재실행 → 시작 메뉴의 Load checkpoint → Continue current session → Pause. | 불러온 직후 메뉴에 머물며 자동 진행하지 않음. 이어하기 후 체력·퀘스트·아이템 유지 확인(진행한 tick은 별도 기록). 정확한 hash는 플레이 화면 Pause → Load checkpoint → 확인에서 대조. Resume으로 정상 진행 |
| QA-05 | New run 확인 후 Guide 근처 이동. '안녕', '퀘스트', '수락'을 Talk to Guide로 전송. | 기본 대사와 제안 표시. Confirm quest action 전에는 수락이 적용되지 않으며 확인 후 다음 tick에 적용 |
| QA-06 | Menu/Esc에서 5초 대기 후 Continue로 복귀하고 대기 시간만큼 게임이 빨라지지 않는지 확인. Pause → Menu → Return to paused session의 Pause 유지 확인. 메뉴 New game 확인창 취소/확인과 진행 중 Load checkpoint 취소 확인. 창 크기 변경, 탭 이동, 그리드 재클릭, 대화 입력 후 이동·공격 재시도. FPS 30/60, Fullscreen/F11, Show diagnostics, 전체/효과/음악 음량과 Mute audio 변경 → Save settings → 재실행. 합성 전투/드롭/포털 효과음과 Pause·탭·창 포커스 이동 확인. | 한글·HUD·버튼·스크롤 접근, 입력 포커스, 저장한 표시·음량/음소거 설정 복원, 음소거 시 무음·복귀 시 과거 효과 몰림 없음. F11로 전체화면 해제. 실제 시험한 창 크기/DPI만 기록; FPS 제한을 목표 FPS 달성으로 간주하지 않음 |
| QA-07 (선택) | 0.6B 모델 → Download model → Cancel → 이어받기 → Start local AI → 대화 → Use basic dialogue → Remove model. | 진행·취소·이어받기·응답·전환·제거. 미지원 CPU는 BLOCKED. 큰 비교 모델은 별도 요청 없이 다운로드 금지 |
| QA-08 | 같은 run에서 15,000 tick을 넘기도록 일반 창에서 10분 이상 진행. 경계 전후 이동·공격 → Verify replay → Save checkpoint → 종료/재실행 → 시작 메뉴 Load checkpoint → Continue current session. | 자동 정지 없이 tick 증가, Replay window 기준점/rolled 증가, replay 일치, 체력·퀘스트·아이템 유지와 저장 복원. 벽시계 시각과 경계 전후 화면 기록 |
| QA-09 (원본 장면 별도 인수) | 제공된 게임 경로 지정 → Check data directory → 시작 메뉴 Load original scene JSON → 기존 세션이 있으면 전환 확인 → Continue current session. 성공 후 Save settings → 재실행 → 시작 메뉴 Load remembered scene 확인. 알려진 지면/벽 클릭, 8방향 이동, 공격/피격/사망, NPC 수락→포털→목표 처치→귀환, 저장/재실행/같은 scene 재선택/로드. Audio 매핑이 있으면 원본 효과음·지역 음악 전환/loop, 무음 지역, 잘못된 WAV 로드 실패 후 이전 세션도 확인. | 원본 지형·팔레트·캐릭터 프레임·방향·가림, 충돌/코너, 퀘스트 왕복과 scene 저장 슬롯 확인. 실제 확인한 클래스/지역/동작만 결과에 기록 |
| QA-10 (별도 시간 승인) | 일반 창으로 2시간 반복 이동·지역 전환·전투·저장 복원. 시작/중간/끝의 메모리·tick p99·프레임/오류 기록. | 크래시·입력 정지·지속 메모리 증가 여부. 시험 OS/CPU/GPU와 실제 활성 플레이 시간을 기록. 빠른 tick 반복/헤드리스는 증거로 사용 금지 |

음향 판정은 실제 캡처/청취 증거가 필요하다. 도구/장치가 없으면 해당 항목을 BLOCKED로 기록하며 화면/헤드리스 marker만으로 음향 PASS를 주장하지 않는다. 원본 Audio 매핑이 없으면 음악/원본 효과 인수도 별도 BLOCKED다.

QA-03에서는 가방 8칸/장비 2칸 선택·상세 수치·장착 후 선택 유지·버린 항목 선택 해제와 버튼 비활성화를 확인한다.

전투에 Pause/Step one tick을 사용했다면 기록한다. 핵심 기능에서 실패하면 이후 불가능한 항목은 BLOCKED다.
실패를 숨기기 위해 New run을 반복하지 않는다. 원본 캠페인·3D 고품질 그래픽·다른 OS·GPU 성능·장시간
안정성을 이번 Linux 시험의 통과 범위에 넣지 않는다.

QA-08의 tick/Replay window/Verify replay는 Show diagnostics를 켜서 확인한다.
QA-08은 Pause/Step one tick이나 tick 직접 주입으로 시간을 채우지 않는다. 테스트 도중 사망해
경계를 확인할 수 없으면 안전한 Camp에서 같은 run을 이어가되 실제 확인 범위를 기록한다.
이 시험은 10분 기록 경계의 동작 확인이며 QA-10의 2시간 안정성 인수를 대체하지 않는다.

QA-09 자료 준비는 `LEGACY_PLAY_SETUP.md`를 따른다. 개발자가 `--check-play-ready`로 생성한
리포트, scene JSON SHA-256, ContentId, 프로필과 사용 리소스의 경로/해시를 결과에 연결한다.
원본 자료가 없으면 QA-09와 legacy_scene_qa를 BLOCKED로 기록한다. 자동 다운로드하지 않는다.
원본 MPQ/추출 리소스를 공개 GitHub·CI·결과 첨부에 올리지 않는다. 개인 설치 경로도 제거한다.
현재 전투/아이템은 preview 규칙이므로 원본 이미지가 보이더라도 original_rules_validated는 false다.

## 결과

- `qa-result.json`: 템플릿 필드를 채운다. 상태는 PASS / FAIL / BLOCKED / NOT_RUN 중 하나다.
  각 항목에 기대 결과·관찰 결과·증거 파일 상대 경로를 기록한다. 미실행 항목은 PASS 금지.
- `action-log.txt`: UTC 시각, 키/마우스 입력, 관찰 결과와 재시도 여부.
- `screenshots/`, 가능하면 짧은 영상, `logs/`: 실제 파일을 함께 첨부한다.
- schema 2의 `status_scope=synthetic_preview`를 유지한다. 필수 QA-01~06/08 전부 PASS이고
  증거가 있을 때만 `status=PASS`다. 실패가 있으면 FAIL, 환경에 막혔으면 BLOCKED, 미완료면 NOT_RUN이다.
- `legacy_scene_qa.status`는 QA-09와 같은 결과이며 해당 ContentId의 장면에만 적용한다.
  `two_hour_soak.status`는 QA-10과 같은 결과이며 실제 시간/증거를 별도로 기록한다.
  기본 status가 PASS여도 이 두 상태를 자동으로 PASS로 바꾸지 않는다. 선택 항목 생략은 한계로 명시한다.
- 대화에 결과·증거를 첨부한다. GitHub 댓글·이슈·릴리즈 수정은 별도 지시 없이 하지 않는다.
  현재 Actions에 결과 자동 회수/정식 릴리즈 승격은 없다. 봇의 PASS는 사람의 인수 검토 자료다.

PLAY-08 이후 빌드에서는 QA-09 준비 과정에서 Map의 실제 경로/팔레트를 Load map으로 확인하고, Cell X,Y로 서로 다른 연결된 위치 3개를 지정해 Validate and create scene → Load generated scene을 확인한다. 막힌 위치/범위 밖 위치/변경된 데이터 폴더/없는 리소스는 기존 세션·파일을 유지하는지 확인한다. 생성 장면의 임시 캐릭터는 원본 아트 인수 PASS 증거가 아니며, 전체 QA-09에는 아트가 설정된 별도 scene을 사용한다. 시작 메뉴 로드 완료 후에는 Continue 전까지 tick이 진행하지 않아야 한다.

PLAY-09 이후에는 Map의 Edit generated artwork 또는 Scene art에서 소유 자료의 검증된 scene을 열고 Player/Monster별 다섯 동작·레이어·방향·FPS를 입력한다. 배우 전환 시 미완성 입력 유지, Inspect this motion → DCC-COF 방향/프레임 확인, 폼 복귀, Validate and save a new scene copy → Load saved copy를 시험한다. 잘못된 방향/누락된 COF 레이어/데이터 폴더 변경은 저장 실패해야 하며 원본 JSON/MPQ/기존 세이브가 바뀌면 FAIL이다. 다른 scene 열기 취소/실패 시 이전 폼 유지도 확인한다. 아트가 모두 로드되어도 실제 프레임·색상·방향·가림을 대조하기 전에는 QA-09 PASS로 처리하지 않는다.

PLAY-10 이후에는 Scene art의 Guide를 선택해 Idle 경로/레이어·8방향·FPS와 Guide fixed facing을 설정한다. 다른 배우 전환·복사본 저장·재열기 후 입력이 유지되는지 확인한다. Load saved copy 후 NPC 위치·대기 동작·색상·고정 방향, 플레이어 앞뒤/벽과의 가림, Pause/Menu 정지·Continue 재개, 저장/로드와 기존 대화·퀘스트 상호작용을 확인한다. NPC 아트를 끈 복사본은 원형 표시로 돌아가야 한다. 실제 NPC 파일이 없으면 해당 시각 검사를 BLOCKED로 기록한다. ReadyForAllSpritesGuiCheck는 준비 상태이며 GUI PASS가 아니다. 기존 --check-play-ready 종료 0만으로 NPC 아트까지 설정됐다고 판단하지 않는다.

PLAY-11 이후에는 HUD artwork settings에서 소유 자료의 DC6 프레임/팔레트·캔버스·배치를 설정하고 50% 체력 미리보기 → 새 복사본 저장 → 로드를 확인한다. Health의 전체/부분/0 표시와 HP 수치, Menu로 Pause/Continue, Inventory 열기/닫기 및 기존 텍스트 버튼을 확인한다. 창 폭/전체화면/DPI 변경 시 비율·중앙 여백·실제 클릭 위치, 잘못된 프레임 로드 후 이전 장면/미리보기 유지, 복사본 재열기·HUD 비활성화 시 기본 체력 바 복원을 확인한다. 원본 마나·스킬·벨트·글꼴·인벤토리 전체 외형은 현재 미구현이다. HUD 설정 또는 합성 headless marker만으로 원본 UI 대조 PASS를 기록하지 않는다.


PLAY-12 이후에는 Scene art → Item artwork settings에서 소유 자료의 아이템 DC6/팔레트/프레임을 지정한다.
미리보기 → 새 복사본 저장 → 로드 후 줍기/장착/해제/버리기/다시 줍기에 따라 아이콘·이름·툴팁과 슬롯 선택이 맞는지 확인한다.
매핑 없는 항목의 이름 표시, 빈 슬롯의 이미지 제거, 크기/DPI 변경 시 비율/텍스트 접근, 잘못된 프레임의 이전 미리보기/세션 유지,
설정 재열기/비활성화 및 합성 장면 전환 시 아이콘 제거를 확인한다. 아이템 경로는 추측하지 않는다.
현재 훈련용 검/방어구·8칸 가방 규칙이며 원작 다중 칸 격자·드래그/드롭·장비별 캐릭터 외형은 미구현이다.
합성 headless 결과나 설정 저장만으로 원본 인벤토리 대조 PASS를 기록하지 않는다.
