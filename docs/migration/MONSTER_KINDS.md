# 몬스터 종류와 레벨 (M5-05a)

**상태: 구조·로더·서버 연동 구현됨. 원본 `monstats.txt` 대조와 몬스터별 전투 수치는 미구현.** 몬스터가 "무엇인지"(종류 코드)와 그 종류의 레벨·처치 경험치를
표로 정의하고, 처치 경험치를 이 표에서 가져온다. 이전의 임시 규칙("경험치 = 몬스터 최대 체력")은 없어졌다.

> **이 PC에는 Diablo II 설치본이 없어 원본 `monstats.txt`와 한 번도 대조하지 않았다.** 내장 `preview` 표의 두 종류(`preview-grunt` Lv 1 / 60, `preview-brute` Lv 2 / 100)는
> 구조를 시험하기 위한 가상의 값이다. 로더가 읽는 열의 의미(`Id`, `NameStr`, `Level`, `Exp`)는 문서 기억에 근거한 가정이며 `unverified`다.

## 구조

| 요소 | 위치 | 역할 |
| --- | --- | --- |
| `MonsterDefinition(Code, Name, Level, Experience)` | Core `Monsters.cs` | 한 종류. `Name`은 표시 문장이 아니라 이름 키(`NameStr`)다 — 문장 해석은 TBL을 읽는 후속 단계 |
| `MonsterTable` | Core | 종류 모음. 코드는 대소문자 무시·유일, 코드 `[A-Za-z0-9_-]`(안쪽 공백 허용, 최대 32), 레벨 0–255, 경험치 0–100,000,000, 최대 4,096종. `Preview`는 가상 표 |
| `MonsterRoster` | Core | **방의 몬스터 하나하나가 어떤 종류인지**(엔티티 ID → 코드). 시뮬레이션 밖, 콘텐츠 옆에 둔다 |
| `MonsterTables.Load(read)` | Assets | 소유자의 `monstats.txt` → `MonsterTable`. 파일 크기 한도·SHA-256 출처·줄 번호가 있는 거부. `AssetAudit --monsters-txt <게임 데이터 디렉터리>`가 JSON으로 출력 |
| `Arena.Roster`, `CooperativeWorld.Roster` | Online | 내장 두 방의 몬스터 100·101 = `preview-grunt`, `preview-brute` |
| `MonsterLabels.Describe` | Online | 방 화면의 "Monsters here: Preview grunt Lv 1 60/60 · …" 문구. 표시 전용 |

몬스터 종류를 `EntityState`에 넣지 않았다. 그러면 엔티티 직렬화, 상태 해시, 저장(`GameSave`·`realm.json`), 규칙 버전, 월드 콘텐츠 해시가 모두 바뀌어
이미 만든 방과 저장을 이관해야 한다. 종류는 서버가 처치 순간에 콘텐츠의 배치(roster)에서 찾아 쓰므로 **시뮬레이션·저장·해시·프로토콜 프레임은 그대로**다.
대신 종류는 시뮬레이션 상태가 아니므로 오프라인 저장에는 담기지 않고, 몬스터가 스스로 종류를 알지는 못한다(이름·레벨은 표시와 경험치에만 쓴다).

## 서버

- **경험치**: 몬스터가 죽으면 배치에서 종류를 찾고, 표의 `Experience`를 [같은 지역의 생존한 직업 캐릭터 전원](CHARACTER_PROGRESSION.md)에게 지급한다. 배치나 표에 없는 몬스터는 추측 없이 **아무것도 주지 않는다**(내장 방에서는 일어날 수 없다).
- **시작 검증**: `Realm`은 만들 때 내장 방(Arena, 협동 월드)의 모든 몬스터가 표에 있는 종류인지, 배치가 몬스터가 아닌 것을 가리키지 않는지 확인하고 아니면 `InvalidDataException`으로 **저장소를 만들기 전에** 시작을 거부한다.
  그래서 원본 표를 넣으면(`new Realm(..., monsters: …)`) 내장 가상 월드의 종류가 없어 시작되지 않는다 — 원본 표는 원본 월드의 배치와 함께 들어와야 한다.
- **조회**: `GET /v1/monsters`(로그인 필요)가 서버 표를 코드 순으로 돌려준다(`code`, `name`, `level`, `experience`). `/health.monsterKinds: 1`, 클라이언트 `SupportsMonsterKinds`·`ListMonsters`.
- **클라이언트(Godot 개발 패널)**: 방이 시작되면 상세 문구에 내 지역의 살아 있는 몬스터를 종류 이름·레벨·체력으로 보여 준다(`Monsters here: …`, 최대 8마리). 알 수 없는 종류는 이름 없이 체력만 보인다.

## 하지 않은 것

- 원본 `monstats.txt` 대조(위 경고), 서버가 설치본에서 표를 읽기, `NameStr`을 TBL로 풀어 실제 이름을 보이기.
- **레벨의 사용처**: 레벨은 표시와 검증용으로만 있다. 원본의 경험치 보정(캐릭터와 몬스터의 레벨 차이), 난이도(Normal/Nightmare/Hell의 `Level(N)`·`Exp(N)`·`Level(H)`·`Exp(H)` — 로더는 Normal 열만 읽는다), 보스·정예·챔피언 보정, 파티 분배 보정은 규칙을 확인할 수 없어 넣지 않았다.
- 몬스터별 체력·피해·방어·명중·저항·드롭·AI·속도: 지금처럼 콘텐츠가 엔티티에 직접 정한다.
- 시뮬레이션 안의 종류(`EntityState`)와 오프라인 플레이의 종류/경험치, 방 안 HUD의 경험치, 몬스터 이름을 화면에 직접 그리기(문구만).

## 검증

- Core `MonsterContracts`(테스트 5개): 가상 표, 표 생성 거부(코드·이름·레벨·경험치·중복·빈 표·초과), 배치 규칙과 표 대조, `monstats.txt` 로드(Expansion 표시 행·빈 숫자·출처 해시)와 거부(중복·숫자·코드·열·빈 표·크기·프로필).
- 온라인 `MonsterKindContracts`: 내장 방이 가상 표로 덮임, 덮이지 않는 표는 저장소 생성 전에 거부, 표 조회와 로그인 요구, 서로 다른 표 값(7/0, 0/9, 7/9)에서 처치 지급액이 표를 따름(Realm·Open), 방 문구(지역·생존·정렬·대소문자·상한).
- `CharacterProgressContracts`: 두 종류의 몬스터를 모두 처치하면 60 + 100이 지급되고 방을 닫을 때 정산된다.
- 변이 시험과 실제 서버 TLS 계약 결과는 PR 본문.
