# ONLINE-04: 서버 연결, TLS와 저지연 전송 검토

검토일: 2026-10-10. 기본안은 **가까운 지역의 공개 전용 서버에 클라이언트가 직접 접속**하는 구조다.
계정/로비에는 HTTPS를 유지하고, 다음 구현은 WSS 서버 푸시, 게임 전송의 성능 비교 후보는
암호화 UDP다. VPN이나 공유기 자동 설정을 플레이어의 필수 조건으로 두지 않는다.
WSS/UDP/자동 포트포워딩/중계는 아직 구현하지 않았다. 아래 선택은 코드·공식 문서에 기반한
설계 판단이며 WAN 성능 측정 결과가 아니다.

## 현재 어떻게 연결되는가

`OnlinePanel` → `OnlineClient`의 HTTP JSON 요청 → ASP.NET Core/Kestrel → 방별 Core 시뮬레이션이다.
서버는 40ms마다 계산한다(25Hz). 일반 UI는 요청이 진행 중이지 않을 때 약 100ms마다 입력을
보내거나 상태를 조회한다(최대 약 10Hz, 응답 지연이 있으면 더 낮아진다).
HTTP 연결은 재사용하며 조회마다 TLS 연결을 새로 만드는 구조는 아니다.
현재 자체 전투장은 원본 Battle.net 프로토콜을 사용하지 않는다.

| 환경 | 연결 주소와 필요한 설정 | 공유기/VPN |
| --- | --- | --- |
| 같은 PC의 개발 검사 | 기본 `http://127.0.0.1:5080` | 불필요. 일반 클라이언트는 PC당 하나 |
| 같은 LAN의 서로 다른 PC | 서버 LAN 주소에 맞는 HTTPS 인증서, 서버 방화벽의 해당 TCP 포트 허용 | 보통 공유기 포트포워딩 불필요. 무선 단말 격리 등은 별도 |
| 공개 전용 서버 | DNS와 HTTPS 인증서, 서버/클라우드 방화벽에서 접속 포트 허용 | 플레이어 쪽 포트포워딩/VPN 불필요 |
| 집 PC에서 인터넷 서버 호스팅 | 공인 주소에서 서버 PC로 포트 전달, 서버 방화벽, 맞는 인증서 | 호스트 쪽 설정 필요. CGNAT이면 공유기 설정만으로 해결되지 않을 수 있음 |
| 사설 VPN을 이용한 개발 인수 | VPN 주소로 HTTPS 접속, 해당 이름/IP를 포함한 인증서 | 선택 사항. 현재 앱에 VPN 기능이 내장된 것은 아님 |

`0.0.0.0`은 서버의 수신 바인딩 값이며 클라이언트에 입력할 목적지 주소가 아니다.
현재 포트 번호는 `--urls`로 운영자가 정한다. 예시는 TCP 5443, 운영 후보는 TCP 443이다.
PC 중복 실행 방지용 loopback TCP 24682는 게임 서버 포트가 아니며 외부에 개방하지 않는다.
서버가 공인 주소를 가진 구조에서는 각 클라이언트가 먼저 나가는 연결을 만들기 때문에
P2P용 홀 펀칭/STUN/TURN이 게임 접속의 필수 구성 요소가 아니다.

## TLS로 서로 다른 PC를 연결하는 절차

1. 같은 Actions 실행의 서버와 각 OS 클라이언트 패키지를 압축 해제한다.
2. 서버의 DNS 이름(또는 실제 접속 IP)이 SAN에 들어간 서버 인증서와 개인 키를 준비한다.
   공개 서비스는 OS에서 신뢰하는 CA를 우선 사용한다. 사설 인수는 운영자가 관리하는 별도 CA를 쓸 수 있다.
3. 서버 전용 비공개 폴더에 암호화한 PFX와 데이터 폴더를 둔다. `/server-private/`는 Git에서 제외한다.
   PFX/키/암호/계정 DB는 Git, Actions 아티팩트 또는 클라이언트 패키지에 넣지 않는다.
4. 서버 계정에만 읽기 권한을 주고 Kestrel의 표준 인증서 설정으로 실행한다. 아래 암호 환경 변수는
   이미 서비스 관리자/비밀 저장소에서 제공한 값이다. 실제 암호를 명령 기록에 입력하지 않는다.

```sh
# Linux/macOS, 서버 패키지 폴더. Password 값은 서비스 환경에서 사전 주입.
export Kestrel__Certificates__Default__Path=/absolute/server-private/server.pfx
./OpenD2.Server --urls https://0.0.0.0:5443 --data /absolute/server-private/realm-data
```

```powershell
# Windows PowerShell. Password 값은 서비스 환경에서 사전 주입.
$env:Kestrel__Certificates__Default__Path = 'C:\OpenD2-private\server.pfx'
.\OpenD2.Server.exe --urls https://0.0.0.0:5443 --data 'C:\OpenD2-private\realm-data'
```

필요한 두 환경 변수는 `Kestrel__Certificates__Default__Path`와
`Kestrel__Certificates__Default__Password`다. 방화벽/라우터/DNS를 이 명령이 자동 구성하지는 않는다.
TLS 종료 프록시를 앞에 배치하려면 신뢰할 프록시 주소/forwarded headers를 별도로 설계한다.
현재 구현은 Kestrel 직접 TLS를 검증하며 임의 프록시 헤더를 신뢰하지 않는다.

5. 두 PC에서 일반 클라이언트를 하나씩 열고 Online 탭에 실제 HTTPS 주소를 입력한다.
   공개 CA 인증서는 `Use system trust`를 사용한다. 사설 CA는 **공개 루트 인증서 한 개의 PEM**을
   받아 `Choose private CA`로 선택한다. 서버 개인 키/PFX를 클라이언트로 옮기지 않는다.
6. `Check server`로 TLS, 프로토콜 1, Core rules 버전을 확인한다. 사설 CA의 표시된 SHA-256을
   운영자에게 별도 경로로 받은 값과 대조한 뒤 로그인한다. OS 전체 신뢰 저장소는 바뀌지 않는다.
   주소/CA 선택은 다음 연결에 적용되며 저장하지 않는다. 기존 세션을 바꾸려면 로그아웃 후 로그인한다.
7. 각기 다른 계정/캐릭터로 같은 방에 참가한다. 원격 평문 HTTP는 계속 거부한다.

선택한 CA만 해당 `OnlineClient` 연결의 신뢰 루트가 된다. 인증서 이름·기간·서명 체인·서버 용도는
.NET TLS 검증을 그대로 따른다. 임의 인증서 허용 콜백이나 `--insecure` 옵션은 없다.
사설 CA 모드는 AIA 다운로드를 끄므로 서버가 필요한 중간 인증서를 제공해야 한다.
인증서 폐기 목록/OCSP 확인은 현재 클라이언트 기본 동작과 같이 활성화하지 않았다.
CA 폐기/회전과 공개 운영의 폐기 확인 정책은 별도 운영 인수 항목이다.

## 속도 중심의 채택 순서

| 후보 | 판단과 다음 조치 |
| --- | --- |
| 가까운 지역의 전용 권위 서버 | 채택할 기본 토폴로지. 서버가 위치/전투/소유권을 결정하고 방은 한 서버가 소유. 지역 선택은 실제 RTT로 결정 |
| HTTPS + WSS | 다음 구현 우선순위. 계정/로비 HTTPS 유지, 게임 상태를 지속 연결로 푸시. 현재 .NET 서버와 Godot C# 모두 적용하기 쉽고 TCP 443 사용 가능. TCP 손실에 따른 대기 문제는 남음 |
| GameNetworkingSockets 암호화 UDP | 게임 전송 성능 실험의 우선 후보. 신뢰/비신뢰 메시지, 암호화, 지연/손실 통계와 lanes 제공. C ABI/C# 연동, 네이티브 라이브러리 3 OS 패키징·종료 수명·라이선스를 검증한 뒤 최종 채택 |
| Godot ENet | 서버도 Godot를 사용할 때 통합 편의가 큼. 현재 서버는 순수 ASP.NET/Core이므로 Godot 고수준 RPC와 바로 호환된다고 볼 수 없음. 엔진 서버 전환 또는 별도 프로토콜 구현 비용까지 비교 |
| LiteNetLib | C# 기반 UDP 비교 후보. 전달 모드·NAT 펀칭은 유용하지만 인증된 암호화와 서버 신원 확인을 별도로 확인해야 함. 자체 암호 프로토콜 제작을 기본안으로 삼지 않음 |
| .NET QUIC | TLS와 스트림 다중화 장점. 현 공식 문서상 macOS는 부분 지원이며 Homebrew libmsquic/환경 설정이 필요. Linux도 네이티브 의존성이 있어 SDK 없는 3 OS 패키징 검증 전에는 기본안에서 보류 |
| Tailscale 등 VPN | 비공개 다중 PC 인수/운영 관리용 선택지. 일반 플레이어의 필수 설치로 채택하지 않음. 직접 경로와 DERP/peer relay 경로를 나눠 실측 |

GameNetworkingSockets 오픈소스 사용만으로 Steam Datagram Relay 사용 권한이 생기지 않는다.
Steam 인증/SDR 서비스 이용 조건과 자체 중계 운영 비용은 별개다. VPN도 직접 경로라면 빠를 수 있고
중계 경로라면 추가 지연이 생길 수 있으므로 “VPN은 항상 느리다” 또는 “UDP면 무조건 빠르다”라고 가정하지 않는다.

WSS 단계부터 입력 sequence/서버 tick, 제한된 송신 큐, 느린 클라이언트 처리, heartbeat/세션 만료,
재접속 후 전체 스냅샷 재동기화를 설계한다. UDP 실험에서는 다음을 유지한다.

- 위치/이동은 최신 순서가 우선인 메시지로, 인벤토리/전리품/방 종료 등은 확인 가능한 신뢰 메시지로 분리한다.
- 짧은 유효기간의 방 참가 티켓을 HTTPS로 발급하고 게임 연결의 서버 신원과 연결한다.
  비밀번호/장기 bearer 토큰을 평문 UDP로 전달하지 않으며 중복·재전송·타 방 패킷은 거부한다.
- 서버 권위와 입력 lease를 유지한다. 표시 보간과 제한된 로컬 예측/서버 보정으로 체감 지연을 개선한다.
  공격 판정/시간 역행 허용 범위는 원본 규칙·악용 방지 테스트와 함께 결정한다.
- 변경 상태만 전송하고 시야/방별 관심 범위를 적용한다. MTU, 송신량, 재전송과 혼잡 제어 예산을 둔다.
- UDP가 차단된 네트워크에는 WSS 대체 경로와 현재 경로/RTT 표시를 제공하는 방안을 검증한다.

## 자동 포트포워딩과 중계

집에서 서버를 여는 기능을 추가한다면 UPnP IGD/NAT-PMP/PCP는 **명시적 선택 기능**으로 검토한다.
가능 여부 탐지, 정확한 포트/기간 표시, 종료 시 해제와 외부 도달성 검증이 필요하다.
라우터 미지원, 이중 NAT/CGNAT, 사업자/학교망 정책까지 자동으로 해결하는 기술은 아니다.
실패 시 공개 호스팅 또는 명시적인 중계를 선택한다. 현재 코드가 공유기를 자동 변경하지는 않는다.
게임의 서버 접속과 음성 P2P는 별개다. ONLINE-06은 WebRTC/Opus와 STUN/TURN을 우선 검토하고,
마이크 동의·방별 권한·음소거 및 게임 tick과 분리된 미디어 처리를 유지한다(미구현).

## 성능 검증과 남은 인수

현재 32방 × 4명 × 초당 10회 조회는 **최대 1,280 요청/s의 산술 추정**이다. 현재 전체 게임 요청
1,000/s와 연결 64개 한도는 이 규모를 보장하지 못한다. 입력 요청도 같은 예산을 소비한다.
한도를 늘리는 변경 전에 서버 푸시·부분 상태 전송·동시성/저장 비용을 측정한다. 이 계산은 부하 실측이 아니다.

| 측정 항목 | 시험 조건/판정 자료 |
| --- | --- |
| 체감 지연 | 입력→서버 적용→다른 화면 표시의 p50/p95/p99, RTT와 분리. 자동 시간 측정과 고속 화면 측정을 구분 |
| 손실/지터 | RTT 20/60/120/200ms, 손실 0/1/3/5%, 지터 0/10/30ms. 재정렬·중복·순간 단절도 별도 |
| 서버 부하 | 방 1개 2/4인부터 32방까지 단계 상승, CPU/메모리/GC, tick p95/p99, 요청 거부/큐 길이 기록 |
| 대역폭/경로 | 클라이언트별 송수신, 전체 서버 송신, TLS/UDP/WSS, 직접/VPN 직접/중계 경로 구분 |
| 접속과 복원 | 차단 UDP, IPv4/IPv6, NAT/CGNAT, Wi-Fi 전환, 재로그인/체크포인트 복원, 장시간 2–4인 |

초기 비교 목표는 4인 방에서 서버 tick p99가 40ms 예산을 넘지 않고, 동일 망 조건에서
표시 지연과 송신량이 현 HTTP 기준보다 개선되는 것이다. 실제 p95/p99 측정 전에는 지연 수치를 약속하지 않는다.
성능 때문에 권한/인증서/오류 로그 검사를 끄지 않는다.

현재 자동 게이트는 임시 사설 CA로 압축 해제한 서버에 접속하고 다음을 검사한다.

- 실제 `OnlineClient`의 TLS 로그인/캐릭터/방, 미신뢰·다른 CA·호스트 불일치·만료 인증서 거부.
- 서버 인증서를 루트 CA로 사용하거나 사설 CA를 평문 HTTP에 사용하는 것과 원격 HTTP 거부.
- 세 OS의 압축 해제한 Godot 클라이언트 2개로 기존 전투/재접속/서버 강제 재시작 시나리오를 HTTPS에서도 실행.
- `tls-contracts.json`, `tls/result.json`, 양쪽 Godot 로그/단계 증거를 Actions에 보존.
  CA/PFX/개인 키/계정 DB는 임시 폴더에서 삭제하고 업로드하지 않는다.

자동 TLS는 loopback 검사다. Linux 창 렌더링 자동 검사는 기존 HTTP 경로이며 수동 GUI와 별도다.
**서로 다른 실제 PC·WAN·사람의 GUI 입력·실제 음성 장치 인수는 NOT_RUN**이다.
실제 인수자는 OS/서버·클라이언트 커밋, 접속 주소와 인증서 지문, 직접/중계 경로, 네트워크 조건을 기록한다.
두 PC에서 각각 정상 클라이언트 하나로 로그인→캐릭터→같은 방→동시 이동/전투/획득→연결 단절→
재로그인→서버 재시작/복원을 실행하고 두 화면과 비밀 값 없는 로그를 첨부한다.
같은 PC의 두 일반 클라이언트를 열어 이 인수를 대체하지 않는다.

## 공식 참고 자료

- Kestrel HTTPS/인증서: https://learn.microsoft.com/en-us/aspnet/core/fundamentals/servers/kestrel/endpoints?view=aspnetcore-10.0
- .NET 연결별 체인 정책: https://learn.microsoft.com/en-us/dotnet/api/system.net.security.sslclientauthenticationoptions.certificatechainpolicy?view=net-10.0
- Godot 멀티플레이/ENet/NAT: https://docs.godotengine.org/en/stable/tutorials/networking/high_level_multiplayer.html
- GameNetworkingSockets: https://github.com/ValveSoftware/GameNetworkingSockets
- LiteNetLib: https://github.com/RevenantX/LiteNetLib
- .NET QUIC 플랫폼 의존성: https://learn.microsoft.com/en-us/dotnet/fundamentals/networking/quic/quic-overview
- Tailscale 연결 경로: https://tailscale.com/docs/reference/connection-types
- Tailscale 성능: https://tailscale.com/docs/reference/best-practices/performance
