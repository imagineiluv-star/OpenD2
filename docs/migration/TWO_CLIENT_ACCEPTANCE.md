# Realm / Open 두 클라이언트 인수 검증

이 절차는 최신 소스의 실제 Godot 프로세스 2개와 실제 서버를 사용한다.
자동 검증은 **한 장비의 루프백 HTTPS/WSS** 범위다. 서로 다른 PC, 실제 LAN/WAN,
클립보드·파일 선택 대화상자·마우스/키보드 조작 인수와 구분한다.

## 자동 실행

Python 3.12+, `global.json`의 .NET SDK, 같은 커밋으로 준비한 Godot/클라이언트와
self-contained 서버가 필요하다. 저장소 루트에서 실행한다. 개발 소스 준비:

```sh
python eng/bootstrap.py
python eng/build-native.py
python eng/build-npc.py
dotnet build src/OpenD2.Client -c Debug -m:1
dotnet publish src/OpenD2.Server -c Release -r linux-x64 --self-contained true -o artifacts/acceptance-server -m:1
python eng/two_client_acceptance.py --server-exe artifacts/acceptance-server/OpenD2.Server --godot auto
```

Windows는 `win-x64`와 `OpenD2.Server.exe`, Apple Silicon macOS는 `osx-arm64`를 사용한다.
Godot 프로젝트를 처음 준비했다면 `.local-tools/godot-path.txt`에 기록된 실행 파일로
`--headless --path src/OpenD2.Client --import`를 먼저 실행한다.
배포 패키지는 같은 커밋의 `artifacts/packages/package-<preset>.json`과 압축 파일을 준비한다.

```sh
python eng/two_client_acceptance.py --server-exe artifacts/acceptance-server/OpenD2.Server --client-packages artifacts/packages --preset Linux
```

`--windowed`는 두 TLS 시나리오의 화면을 자동 렌더링하고 단계별 PNG를 남긴다.
실제 디스플레이 또는 Xvfb가 필요하며 수동 GUI 인수로 계산하지 않는다.
기존 3 OS `Migration validation`은 `pack-online-server.py` → `check-online.py`를 통해
아래 강화된 시나리오를 자동 수행한다. 전체 래퍼를 CI에서 중복 실행하지 않는다.

| 검사 | 실제 확인 내용 |
|---|---|
| 일반 실행 정책 | 중복 클라이언트 거부, 프로필 변경·테스트 환경변수만으로 우회 불가, 원격 smoke 거부, 정상/비정상 종료 뒤 잠금 회수 |
| 접속 초대 | 호스트 v1 JSON 생성 → 두 프로세스 적용, 이전 CA·암호 초기화, 잘못된 JSON은 기존 설정 보존 |
| CA·주소·모드 | CA 미선택/불일치 거부, 적용 후 주소/모드 수정 거부, 정상 CA로 제품의 Check server 동작 실행 |
| TLS 거부 검사 | 신뢰되지 않는 CA, 다른 호스트 SAN, 만료 인증서, leaf를 CA로 사용, 원격 HTTP 거부 |
| 로그인·캐릭터 | 독립 계정 등록/로그인, 각 계정 소유 캐릭터 생성·선택 |
| 동일 방 | 호스트가 생성한 v2 방 초대를 게스트가 적용·재로그인·선택 후 Join invited room으로 참가, 결과 room ID 일치·character ID 구분 |
| 전투·아이템 | 호스트 공격, 게스트 스킬/마나, 몬스터 사망/드롭, 단일 소유자 획득, 상대 아이템 탈취 거부를 양쪽 WSS에서 관측 |
| 호스트/게스트 재접속 | 각각 transport dispose로 연결 단절 → 상대가 이동 lease 정지 확인 → 재로그인/같은 캐릭터·방·actor·입력 순번·전체 아이템 복구 → 새 입력을 상대 stream에서 관측 |
| 서버 강제 재시작 | 저장 후 서버 kill/restart, 두 클라이언트 재로그인/입장, 위치·체력·아이템 복구 |
| Open 추가 검증 | 프로필/소유 아이템 파일 반출입, 개인/LAN 호스트 UI와 TLS 생명주기 |

연결 해제는 클라이언트 transport 종료 모의다. 케이블 분리, Wi-Fi 변경, 패킷 손실이나
게스트 OS 강제 종료를 대신하지 않는다. 초대 JSON은 자동화용 파일로 전달하므로
클립보드 동작도 별도다. v1은 서버 정보, v2는 방 위치이며 입장 권한을 부여하지 않는다.
master의 v3 단일 사용·만료 초대 권한 검사는 기존 TLS 계약에 포함된다.

## 결과와 실패 판정

- `artifacts/online/two-client-acceptance/result.json`: 실행 커밋, dirty 여부, OS, 시각, 두 모드 결과와 증거 경로.
- `run-<id>/baseline/`: 일반 중복 실행 차단과 기존 HTTP 회귀 검사.
- `run-<id>/tls-contracts.json`: 실제 TLS·초대·방 stream·호스트 계약.
- `run-<id>/realm/`, `run-<id>/open/`: 단계별 결과, 양쪽 프로세스 로그, 상태 스냅샷, 선택적 PNG.
- 한 단계라도 빠지거나 실패하면 종료 코드가 0이 아니며 종합 결과는 `FAIL`이다.
  필수 단계 이름과 양쪽 room/character ID를 검증한다. 실행별 새 경로로 이전 성공 자료의 재사용을 방지한다.
- 인증서 개인키/PFX, 비밀번호, bearer token, 계정 DB는 임시 디렉터리에만 두고 삭제한다.
  결과 파일의 v1/v2 초대에는 공개 테스트 주소·CA 지문·방 ID만 있다.
- `physical_multi_pc`, `wan`, `manual_gui`, `clipboard_gestures`는 자동으로 `NOT_RUN`이다.
  NAT 매핑·중계는 이 작업에서 구현하지 않는다. 루프백 결과로 이 값을 PASS로 바꾸지 않는다.

## 실제 두 PC / WAN 인수 (현재 NOT_RUN)

2026-10-11 로컬 Linux 검증: master `544702b`에서 출발한 이 변경으로 Core 387/387,
Python 59개, 온라인 계약, 실제 TLS 계약, 일반 실행 잠금, Realm/Open Godot 2프로세스
전체 흐름이 통과했다. C# 클라이언트·서버 빌드 경고/오류는 0이다. 로컬 Godot 검사는
헤드리스이며 기존 MPQ/NPC 런타임을 사용했다. 3 OS 배포본 검증의 최종 결과는 PR CI를 따른다.

각 PC에는 일반 클라이언트 **하나만** 실행한다. 검증 환경변수나 `--online-smoke`를
원격 주소로 사용하지 않는다. 호스트 PC는 서버 프로세스와 클라이언트 하나를 실행할 수 있다.
같은 커밋의 패키지를 양쪽에 준비하고 호스트/게스트 OS·빌드 해시·네트워크·시각을 기록한다.

1. Realm은 `ONLINE_NETWORK.md`의 전용 HTTPS 서버 절차로 준비한다.
   Open은 제품의 Start LAN host를 사용해 실제 NIC 주소, 포트, 해당 주소가 SAN에 든 PFX와
   공개 CA를 선택한다. 비밀번호·개인키는 게스트에게 보내지 않는다.
2. 호스트의 접속 초대와 공개 CA를 게스트에게 전달한다. 별도 경로로 SHA-256을 비교한다.
   게스트는 Apply invite → CA 선택 → Check server를 실제 UI로 수행한다.
3. 각자 로그인·자신의 캐릭터 선택 후 호스트가 방을 만들고 방 초대를 전달한다.
   게스트의 Join invited room과 두 화면의 같은 방/서로 다른 캐릭터를 확인한다.
4. 공격·스킬·드롭·획득·소유권을 양쪽 화면과 로그로 확인한다.
5. 게스트 네트워크를 끊었다 복구한다. 호스트가 계속 동작하며 게스트 이동이 멈추는지,
   게스트 재로그인/같은 방 재입장 후 캐릭터·아이템·새 입력이 정상인지 확인한다.
   게스트 프로그램 종료/재실행도 별도로 반복한다.
6. 호스트 연결 해제와 서버 재시작을 각각 수행한다. Open 호스트 프로그램 종료 시
   소유 서버도 종료되는 동작은 서버 재시작 항목으로 기록한다.
7. 양쪽 PC에서 두 번째 일반 클라이언트가 차단되는지 확인한다.
8. WAN은 실제 다른 네트워크에서 반복한다. 공개 주소/방화벽/수동 포트 매핑을 먼저 준비하고,
   접근 불가능 상태를 자동 NAT/중계 성공으로 기록하지 않는다. 지연·손실·재접속 시간도 측정한다.

| 실환경 매트릭스 | Realm | Open | 증거 |
|---|---|---|---|
| Windows ↔ Linux LAN | NOT_RUN | NOT_RUN | 미수집 |
| Windows ↔ macOS LAN | NOT_RUN | NOT_RUN | 미수집 |
| Linux ↔ macOS LAN | NOT_RUN | NOT_RUN | 미수집 |
| 서로 다른 네트워크 WAN | NOT_RUN | NOT_RUN | 미수집 |
| 30분 이상 플레이/재접속 반복 | NOT_RUN | NOT_RUN | 미수집 |

실환경 기록에는 관측한 단계별 PASS/FAIL/NOT_RUN, 양쪽 패키지 해시와 로그/스크린샷 경로,
끊김 방식과 재접속 소요시간을 남긴다. 개인키·계정 비밀번호·세션 토큰은 포함하지 않는다.
