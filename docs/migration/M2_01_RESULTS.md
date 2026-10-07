# M2-01: 고정 tick·명령·상태·난수·재생

2026-10-07, `feat/m2-playable-slice`, 기준 master `94851ec9f344438d8aacf312b57fe5a1df8db511`. 엔진 독립 시뮬레이션 기반을 구현하고 Godot 검사 화면에 연결했다. 현재 조작은 합성 자유 이동이며 충돌·전투·몬스터·실제 게임 콘텐츠는 M2-02 이후 작업이다.

## 구현한 실행 경계

- `GameSimulation`이 정수 상태·명령 큐·난수·tick 이벤트를 소유한다. 단일 소유 스레드가 `Submit`/`Step`을 실행한다. 렌더링·벽시계·파일 읽기·외부 callback은 게임 판정에 들어가지 않는다. Core에 엔진/네트워크 패키지를 추가하지 않았다.
- `EntityId`와 `RegionId`는 별도 값이다. 초기 entity는 ID 순서로 정렬하고 중복/0 ID, 잘못된 좌표·방향, 개수 초과를 거절한다. 외부 초기 배열의 후속 변경이 세션을 바꾸지 않는다.
- `SetMove`는 지속 이동 의도, `Signal`은 난수 색상 이벤트를 검사하는 개발용 명령이다. 매 tick에서 예약 명령을 `(Tick, ActorId, Sequence)` 순으로 적용한 뒤 entity ID 순서로 이동한다. 같은 tick에 같은 entity의 SetMove가 여러 번 있으면 마지막 순번이 적용된다. Signal은 명령 처리 시점의 위치를 기록하며 이동 이벤트보다 먼저 나온다.
- 좌표는 tile당 256 정수 단위다. 축 이동 32단위/tick, 대각선 각 축 23단위/tick으로 검사한다. ±16,777,216 범위에서 clamp한다. 대각선 속도는 정수 근사이며 원본 게임의 이동 속도·길 찾기·충돌 구현이 아니다.
- `SimulationRandom`은 기존 OpenD2 `D2Common_Math.cpp`의 uint32 xorshift 전이를 사용한다. seed/state=0은 거절하고 상태를 노출한다. 범위 변환은 16비트 축소 없이 rejection sampling을 적용한다. .NET `Random`/시스템 시계/렌더용 난수에 의존하지 않으며 원본 Diablo RNG 호환을 주장하지 않는다.
- `Entities`와 `Events`는 다음 `Step`까지만 빌려 읽는 span이다. 이후 보관하려면 `CaptureSnapshot`의 소유 사본을 사용한다. snapshot은 rules version, tick, 난수, entity 값, 승인 순번/target tick, 예약 명령을 포함한다. 렌더 프레임마다 전체 snapshot을 복제하지 않는다.

## 명령 승인과 작업 예산

`GameCommand`는 target Tick, Sequence, Actor, Region, Kind, X/Y를 갖는다. 같은 actor의 Sequence는 증가해야 하며 예약 target tick은 감소할 수 없다. 순번 간격은 허용한다. 서로 다른 actor의 도착 순서는 적용 순서를 바꾸지 않는다. 거절된 명령은 큐·순번·상태·난수를 바꾸지 않으므로 여유 확보 후 같은 순번으로 재시도할 수 있다.

| 항목 | 정책 |
|---|---|
| entity | 1..1,024개, 전역 ID 중복 금지 |
| 전체 예약 명령 | 최대 4,096개 |
| 한 tick 명령 | 최대 128개 |
| 예약 시간 | 현재보다 미래, 최대 250 tick 앞 |
| tick 이벤트 | 최대 entity 수 + 128, 배열 재사용 |
| 재생 | 최대 16,384개 기록 / 100,000 tick |
| 화면 녹화 | 최대 4,096개 명령 / 15,000 tick(10분) |

잘못된 명령 필드, 알 수 없는 actor, 다른 region, 지난 tick, 미래 범위 초과, 재사용 순번, 예약 tick 역전, tick/global 큐 초과를 `CommandResult`로 구분한다. 아직 사용자 인증·네트워크 재전송·소유권 검증·실시간 region 전환을 제공하는 프로토콜은 아니다.

## 고정 시간과 지연 처리

`FixedTickClock`은 정확한 40ms(25Hz) 정수 시간 누산기를 사용한다. 한 프레임의 입력 시간은 최대 250ms, 따라잡기는 최대 5 tick이다. 그 이후 남은 완전 tick 시간은 `DroppedTime`으로 계측하고 부분 시간은 `Alpha` 보간에 남긴다. **버리는 것은 벽시계 시간이며 게임 tick 번호나 예약 명령을 건너뛰지 않는다.**

예: 1초 정지 후 호출하면 tick 5개(200ms)를 실행하고 790ms를 계측하며 10ms를 보간에 남긴다. 과부하 후 게임 시간이 벽시계보다 느려지는 오프라인 정책이다. 협동 동기화/lockstep 정책으로 사용하지 않는다. 음수 시간·재진입은 거절한다. callback 실패 시 clock을 fault 상태로 두고 명시적 Reset 전까지 재사용하지 않는다. 이는 callback의 외부 부작용을 되돌리는 트랜잭션은 아니다.

## 재생과 결정성 검증

- `RecordedCommand`에 **승인 당시 완료 tick**을 기록한다. `Replay`는 시작 seed/entity와 기록에서 새 세션을 만들어 같은 제출 시점을 복원한다. 종료 시점에 아직 예약된 명령도 보존한다. 정렬되지 않은 기록·거절되는 명령·작업 예산 초과는 실패하고 기존 세션은 수정하지 않는다.
- `ComputeStateHash`는 rules v1, tick, RNG, ID 순 entity의 region/좌표/의도/입력 watermark, 적용 순 예약 명령을 little-endian 정수로 인코딩해 SHA-256을 계산한다. 벽시계·성능 통계·화면 보간·이벤트를 읽은 횟수는 포함하지 않는다. 진단 시에만 호출하며 할당이 있으므로 매 렌더 프레임 호출하지 않는다.
- 고정 seed=1, actor=7, region=3에서 tick 1 우측 이동, tick 2 Signal, tick 3 위 이동, tick 4까지 진행한 독립 기준은 `(64,-64)`, RNG `270369`, hash `53616bc70a74537bca1340fa599766ccdbce8323c9d25f3581ea9b8c81bda61b`다. 실제 규칙을 바꾸면 RulesVersion과 기준을 함께 검토한다.
- 이번 결정성 범위는 현재 정수 시뮬레이션·명령·난수와 합성 검증이다. 향후 전투, 리소스 생성, 플랫폼 물리까지 이미 결정적이라는 뜻은 아니다. snapshot 디스크 포맷·복원·원본 세이브 가져오기는 M2-05다.

## 검사 화면과 계측

**Simulation** 탭에서:

1. Seed를 입력하고 **New run**으로 시작한다. Seed 변경만으로 실행 중 난수 상태를 바꾸지 않는다.
2. 그리드를 클릭한 뒤 방향키로 이동한다. 입력은 다음 tick의 명령으로 전달한다. 입력 포커스가 다른 컨트롤로 옮겨지거나 창 포커스를 잃으면 정지 명령을 예약한다. 다른 탭에서는 tick을 진행하지 않는다.
3. **Signal**은 코어가 결정한 6색 중 하나의 이벤트를 화면에 반영한다.
4. **Pause / Resume**, **Step one tick**으로 상태를 검사한다. 일시정지 시 정지 명령은 다음 tick에 처리된다. 화면은 이전/현재 좌표를 보간하고 일시정지/재개 시 보간 시작점을 맞춘다.
5. **Verify replay**는 실행을 일시정지하고 별도 세션을 배경 작업에서 재생해 전체 상태 해시를 비교한다. 결과는 `Replay matched` 또는 오류로 표시하며 활성 세션은 유지한다. 종료한 화면에 늦은 결과를 적용하지 않는다.

Tick/region/entity/좌표/명령 수/난수/p99/버린 시간/hash를 표시한다. tick 처리 시간은 Client의 Stopwatch로 측정하고 600개 샘플 창에서 p99를 계산한다. 프레임 p95와 managed heap 계측은 유지한다. 1초 간격 `simulation_metrics`와 시작/재생 결과를 기존 세션 JSONL 로그에 기록한다. 시간·로그·보간을 바꿔도 게임 상태 hash는 변하지 않는다.

## 검증과 인수 범위

- **104/104 계약 테스트**: 이전 80개 + 시뮬레이션 24개. RNG 고정 벡터/재개/범위 tail rejection, tick 경계/지연 상한/초대형 시간/재진입/fault, ID·초기 값·큐·순번·지역·명령 검증, 정수 이동·이벤트 순서·snapshot 소유권을 검사했다.
- 동일 총시간을 10+10ms 또는 7+13ms 프레임으로 나눠도 50 tick의 상태와 이벤트 열이 같음을 확인했다. 5,000 tick 기록 재생과 미래 예약 명령을 가진 중간 시점 재생을 검사했다.
- 1,024 entity + 128 Signal의 1 tick에서 1,152 이벤트가 안전하게 생성되는지 검사했다. 한 entity의 지속 이동은 워밍업 후 1,000 tick의 `Step`에서 현재 스레드 관리 할당 증가 0바이트를 확인했다. 전체 게임 성능 측정이나 모든 API 무할당 보장은 아니다.
- Linux 전체 Debug 빌드(경고/오류 0), Godot import·헤드리스 실행·Release export를 검사했다. 합성 명령→상태→난수→재생 hash→좌표 보간 smoke와 실제 `_Process`→clock→Step 연결을 각각 `OPEND2_M201_SIMULATION_READY`, `OPEND2_M201_TICK_LOOP_READY`로 확인한다.
- Linux 배포본을 SDK 경로 없이 120프레임 실행하여 기존 리소스 마커와 새 시뮬레이션·실제 tick 루프 마커를 확인했다.
- 코드 커밋 `532929b583ec57956f7e5c9d71862753b986093d`의 **Windows x64·Linux x64·macOS universal CI 모두 첫 시도 성공**, OS별 M2 배포 파일 3개 생성. [push CI](https://github.com/imagineiluv-star/OpenD2/actions/runs/37674479742)와 [PR CI](https://github.com/imagineiluv-star/OpenD2/actions/runs/37674488132)에서 동일한 104개 계약·golden state·시작 루프·export를 검증했다. 실제 게임 전체의 플랫폼 결정성을 의미하지 않는다.
- 실제 게임 데이터, GUI 키보드·마우스·DPI 수동 조작, Windows/macOS 사용자 설치, 전투 부하·장시간 성능은 미수행이다. 기존 M1의 형식 제한과 실데이터 인수도 유지한다.

## 다음 작업

**M2-02**: M1 지도 충돌을 Core의 엔진 독립 판정에 연결하고, 이동/정지/벽·미확인 셀, 공격·피격·사망과 최소 몬스터 AI를 합성 맵에서 구현한다. 게임 규칙이 바뀌면 rules version과 replay 기준을 갱신한다. 이어 M2-03에서 실제 지역 흐름으로 확장한다.

master 반영 PR은 [#3](https://github.com/imagineiluv-star/OpenD2/pull/3)이다. 위 코드 커밋의 검증 이후 결과 기록 변경은 문서만 포함한다. 자체 DLL 소스/고정 도구·의존성 빌드는 모두 저장소에 유지하며 추가 런타임 의존성은 없다.
