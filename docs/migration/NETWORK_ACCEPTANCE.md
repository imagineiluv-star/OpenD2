# TLS 통신 장애와 제한된 부하 검증

`eng/check-network.py`는 실제 self-contained 서버와 production `OnlineClient` 두 개를
사용한다. Realm/Open 각각 게스트 경로에 **암호화된 바이트만 전달하는 loopback TCP 프록시**를
두며 TLS 종료/복호화/인증서 검증 우회를 하지 않는다. 일반 Godot 클라이언트의 PC당 1개
정책과 테스트 예외 코드는 변경하지 않는다.

## 실행

같은 커밋의 .NET SDK와 서버 실행 파일을 준비한 뒤 저장소 루트에서 실행한다.

```sh
dotnet publish src/OpenD2.Server -c Release -r linux-x64 --self-contained true -o artifacts/network-server -m:1
python eng/check-network.py --server-exe artifacts/network-server/OpenD2.Server --seconds 30
```

Windows는 `win-x64`와 `OpenD2.Server.exe`, Apple Silicon macOS는 `osx-arm64`를 사용한다.
`--seconds`는 모드별 **마지막 지속 실행 구간**의 길이이며 1–3600초다. 기본은 30초다.
인증서·서버·방 설정 및 기존 TLS 계약 수행 시간은 추가된다. `--output`으로 증거 경로를
바꿀 수 있다. 각 실행은 새 `run-<id>`를 사용하며 비밀 키·PFX·암호·계정 DB는 임시 폴더에서 삭제한다.

기존 `--tls-server` 계약과 3 OS 패키지 CI에도 각 모드 5초 지속 실행을 포함한
전체 장애 시나리오를 연결했다. 별도의 중복 CI 작업은 만들지 않았다.

## 자동 판정

1. 직접 연결 호스트와 프록시 경유 게스트가 동일 CA를 검증하고 같은 방의 WSS를 구독한다.
2. 암호화 바이트를 최대 1024바이트로 나누고 각 쓰기에 5/10/15ms 지연을 순환 적용한다.
   실제 지연 쓰기가 발생하고, 입력 접수·상대 이동 관측·v2 변경분 복원이 계속되는지 확인한다.
3. 게스트의 서버→클라이언트 전달을 중단하고 이동 배치를 보낸다. 호스트 WSS로 배치가
   적용됐음을 확인하지만 게스트 HTTP 응답은 아직 완료되지 않아야 한다.
4. 그 상태에서 호스트의 tick이 계속 진행하고 HTTP API도 응답하며, 게스트의 갱신되지 않은
   이동 lease가 만료되는지 확인한다. 프록시 내부에서 실제 대기 중인 전달 작업도 확인한다.
5. 게스트의 기존 HTTP/WSS TCP 연결을 함께 끊는다. 입력 응답·WSS가 실제 전송 오류를
   알린 뒤 재로그인·같은 캐릭터/방/actor 재입장·전체 기준 프레임 수신을 확인한다.
   입력 순번이 정확히 2만큼 증가해야 하므로 응답 유실 입력의 누락·중복 접수를 검출한다.
   재접속 후 새 이동 입력도 상대 WSS에서 관측해야 한다.
6. 지연 경로를 다시 켜고 지정 시간 동안 중립 이동 입력과 지속 WSS 수신을 반복한다.
   두 클라이언트가 동일 tick의 동일 상태 해시를 받는지 매 입력마다 비교한다.

대기에는 명시적인 상한이 있다. 실패·단계 누락·모드 불일치·측정 시간 부족이면 종료 코드가
0이 아니며 최종 결과는 FAIL이다. 프록시의 연결 수와 버퍼, 관측 상태 이력도 제한한다.

## 증거와 측정 범위

`run-<id>/result.json`에 커밋/dirty 여부·시각·Realm/Open 결과를 기록한다.
`network-realm.json`, `network-open.json`에는 단계·프록시 지연/정체/단절 횟수,
전달 암호화 바이트 수·WSS 프레임 수·동일 tick 해시 일치 횟수·복구 시간을 남긴다.

마지막 지속 실행 구간의 HTTP 요청 시작→응답 완료 p50/p95/max와 서버 프로세스 CPU 시간,
표본 WorkingSet 최댓값을 기록한다. CPU/메모리는 시험 서버 프로세스 전체 값이며 클라이언트
비용이나 GC/tick 실행시간이 아니다. 프로세스 통계 접근이 제한된 환경은
`server_process_metrics: UNAVAILABLE`과 null 값으로 남기며 수집 성공으로 표시하지 않는다. 프록시 바이트 수는 TCP/TLS를 통해 전달한 암호문 바이트이며
NIC 패킷/헤더·재전송량을 포함하지 않는다. 지연 수치에 성능 SLA를 적용하지 않는다.

이 테스트는 Godot 화면이나 사람이 조작하는 GUI를 실행하지 않는다. 두 Godot 프로세스의
초대·게임플레이·재접속은 [별도 인수 실행기](TWO_CLIENT_ACCEPTANCE.md)가 담당한다.

## 미검증 항목

- 바이트 분할·지연은 IP 패킷 분할/손실, 지정 RTT/지터 또는 실제 WAN 에뮬레이션이 아니다.
- 짧은 downstream 정체는 느린 소비자 격리를 확인하지만 서버 송신 버퍼 포화·혼잡 제어
  상한이나 느린 수신자 강제 퇴출을 입증하지 않는다.
- 32개 스트림/다중 방 용량, GC/tick 실행시간, 실제 패킷 손실·재정렬·중복은 별도다.
- 물리적 두 PC LAN/WAN, 실제 Wi-Fi 변경·공유기/NAT, 수동 GUI, 2시간 이상 장시간 인수는
  **NOT_RUN**이다. `--seconds`를 늘려도 이런 항목을 자동으로 PASS 처리하지 않는다.
