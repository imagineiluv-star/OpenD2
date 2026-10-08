# OpenD2 출시 후보 사용 테스트

함께 전달한 **특정 Release URL / release-manifest.json**의 버전·커밋만 시험한다.
최신 태그를 임의 선택하거나 소스를 다시 빌드하지 않는다. 이 지침은 QA용이며 게임 내부 NPC 모델과 별개다.
게임 서버 연결이나 원본 디아블로 파일은 필요하지 않다.

## 실행 규칙

1. `release-manifest.json`과 `SHA256SUMS`를 읽고 OS/CPU에 맞는 파일을 내려받아 SHA-256과 크기를 비교한다.
   `qa-result-template.json`을 복사해 결과 파일로 사용한다. OS, CPU, GPU/렌더러, 화면 크기,
   SDK 설치 유무, 버전·커밋·파일 해시를 기록한다.
2. 1회 최대 20분, 같은 실패 재시도 최대 1회. 자동 반복·릴리즈 발행·소스 변경은 하지 않는다.
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
| QA-01 | 새 프로필에서 앱 실행. 게임 데이터 경로·모델은 비운다. | 기본 화면과 Simulation 탭 표시, 기본 기능 조작. 첫 실행 오류·권한 요청 기록 |
| QA-02 | Simulation에서 Seed 1 → New run → 그리드 클릭 → 방향키 이동. Camp Guide 근처에서 E. 금색 Cellar 포털에서 E. Space로 몬스터 3마리 처치. Camp로 귀환해 Guide에게 E. | 퀘스트 단계·처치 수·완료와 체력 회복. 다시 상호작용해 중복 완료가 발생하지 않는지 확인 |
| QA-03 | 청록색 아이템 근처에서 F. 목록 선택 → Equip selected → Unequip selected → Drop selected. | 가방·장비·능력 표시가 조작과 일치. 버튼별 전후 화면 기록 |
| QA-04 | Pause → Save checkpoint. tick·State hash·위치·체력·퀘스트·아이템 기록. 종료 후 같은 프로필로 재실행 → Load checkpoint. | 로드가 일시정지 상태이며 기록한 상태와 일치. Resume으로 정상 진행 |
| QA-05 | New run 후 Guide 근처 이동. '안녕', '퀘스트', '수락'을 Talk to Guide로 전송. | 기본 대사와 제안 표시. Confirm quest action 전에는 수락이 적용되지 않으며 확인 후 다음 tick에 적용 |
| QA-06 | 창 크기 변경, 탭 이동, 그리드 재클릭, 대화 입력 후 이동·공격 재시도. | 한글 표시, 버튼 접근, 입력 포커스, 오류 안내. 시험한 창 크기만 기록 |
| QA-07 (선택) | 0.6B 모델 → Download model → Cancel → 이어받기 → Start local AI → 대화 → Use basic dialogue → Remove model. | 진행·취소·이어받기·응답·전환·제거. 미지원 CPU는 BLOCKED. 큰 비교 모델은 별도 요청 없이 다운로드 금지 |

전투에 Pause/Step one tick을 사용했다면 기록한다. 핵심 기능에서 실패하면 이후 불가능한 항목은 BLOCKED다.
실패를 숨기기 위해 New run을 반복하지 않는다. 원본 캠페인·3D 고품질 그래픽·다른 OS·GPU 성능·장시간
안정성을 이번 Linux 시험의 통과 범위에 넣지 않는다.

## 결과

- `qa-result.json`: 템플릿 필드를 채운다. 상태는 PASS / FAIL / BLOCKED / NOT_RUN 중 하나다.
  각 항목에 기대 결과·관찰 결과·증거 파일 상대 경로를 기록한다. 미실행 항목은 PASS 금지.
- `action-log.txt`: UTC 시각, 키/마우스 입력, 관찰 결과와 재시도 여부.
- `screenshots/`, 가능하면 짧은 영상, `logs/`: 실제 파일을 함께 첨부한다.
- 필수 QA-01~06 전부 PASS이고 증거가 있을 때만 전체 PASS다. 실패가 있으면 FAIL,
  환경에 막혔으면 BLOCKED, 미완료면 NOT_RUN이다. 선택 QA-07 생략은 한계로 명시한다.
- 대화에 결과·증거를 첨부한다. GitHub 댓글·이슈·릴리즈 수정은 별도 지시 없이 하지 않는다.
  현재 Actions에 결과 자동 회수/정식 릴리즈 승격은 없다. 봇의 PASS는 사람의 인수 검토 자료다.
