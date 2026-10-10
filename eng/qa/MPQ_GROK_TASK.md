# 실제 MPQ 기반 그록봇 지형 QA

범위: 공개 Diablo II 1.04 데모의 **마을 지형 + OpenD2 preview 규칙**. 전체 게임/LoD 인수가 아니다.
키트는 MPQ·설치 EXE·추출 이미지를 포함하지 않는다. 사용자가 제공한 6개 데모 MPQ 폴더를 읽기 전용으로 사용한다.
이번 키트는 Linux에서 디코딩·연결 검증한 장면을 제공한다. 다른 OS의 실제 시각 결과는 봇이 기록한다.

## 준비 (개발자 또는 봇 호스트)

같은 Actions 실행의 OS별 `OpenD2-M2-*` 아티팩트를 내려받아 바깥 ZIP을 푼다.
내부의 OS용 게임 압축 파일, `package-<platform>.json`, `mpq-qa-kit.zip`을 사용한다.
정식 후보 Release의 파일을 사용할 때는 SHA256SUMS도 대조한다. 다른 실행의 키트/패키지를 섞지 않는다.
키트를 새 폴더에 풀고 Python 3.12 이상으로 실행한다. 게임 자체는 Python/SDK가 필요 없다.

```sh
python mpq_handoff.py --data "/private/demo/mpq" --package "/downloads/OpenD2-ci-linux-x64.tar.gz" --package-metadata "/downloads/package-Linux.json" --output "/private/qa-run-01"
```

Windows는 `py -3` 또는 `python`, macOS는 `python3`로 실행하고 실제 ZIP 및 메타데이터 경로를 지정한다.
`--data`는 이관 vault의 `mpq` 하위 폴더도 가능하다. 경로는 호스트 로컬에서만 전달하고 공개 결과에 넣지 않는다.
6개 MPQ 이름·크기·SHA256, 추가 패치/중복, 패키지 크기·해시, 키트/패키지 커밋을 검사한다.
종료 0은 **인계 준비 성공**이다. MPQ 디코딩/GUI PASS가 아니다. 파일이 다르면 고치거나 우회하지 말고 BLOCKED로 기록한다.
검사 후 원본을 바꾸지 않는다. 다른 PC로 이관하면 그 PC에서 다시 실행한다.

## 그록봇에게 전달할 작업

검증한 **같은 빌드**의 게임 패키지, 생성된 QA 폴더, 호스트의 MPQ 폴더 위치를 사용한다.
외부 봇에게 이 문서를 전달하는 것과 봇 실행은 별도다. 실행 가능한 데스크톱이 없으면 GUI BLOCKED다.
기존 프로필을 건드리지 않도록 별도 테스트 계정을 사용한다. Linux는 별도 XDG_DATA_HOME을 사용할 수 있다.

1. 게임 압축 전체를 새 폴더에 풀고 일반 창으로 실행한다. SDK 설치·소스 재빌드·headless로 대체하지 않는다.
2. 설정에서 MPQ 폴더 선택 → Check data directory. LoD 파일 누락 경고를 캡처하고 유지한다.
   이 경고를 전체 PASS로 바꾸지 않는다. 제공 장면은 필요한 지형 자료만 직접 로드한다.
3. 시작 메뉴 `Load original scene JSON` → QA 폴더의 `scene.json` 선택.
   로드 후 Continue 이전에는 tick이 정지하는지 확인하고 화면을 캡처한다.
4. Continue → 지형/천막/울타리/수레/팔레트 확인, 지면·벽 클릭, 8방향 이동, 가림/충돌을 확인한다.
   플레이어·몬스터·가이드는 임시 표시다. 원작 캐릭터 애니메이션 PASS로 기록하지 않는다.
5. 가이드 근처 E로 상호작용하고 preview 퀘스트를 수락한다. 몬스터와 전투 후 가이드 복귀/완료를 확인한다.
   몬스터는 초기 위치에서 12 navigation cells 떨어져 있어 접근 전에는 감지 범위 밖이다. 이 장면에는 포털이 없다. 포털을 찾거나 JSON을 고쳐 시험하지 않는다.
6. Pause → 저장 → 종료 → 같은 프로필·장면 로드 → 체크포인트 복원. 화면의 Content 앞 16자리와 상태를 기록한다. 전체 ContentId는 logs의 legacy_scene_loaded 이벤트에서 확인한다.
   예상 ContentId는 `mpq-qa-result.json`에 있다. 일치하지 않으면 FAIL이다.
7. 창 크기·전체화면(F11)·메뉴/Pause/Continue를 확인한다. 로그의 ERROR/SCRIPT ERROR는 실패로 보존한다.
   원본 음악/캐릭터/HUD/아이템 아트는 미설정으로 BLOCKED다. 소리를 들었다고 원본 음향 PASS로 바꾸지 않는다.

회차 최대 20분, 같은 실패 재시도 1회. 시간 부족은 NOT_RUN, 환경 제한은 BLOCKED, 실제 결함은 FAIL.
반복 초기화로 실패를 감추지 않는다. 원본 MPQ/설정/세이브를 직접 수정하지 않는다.

## 결과 인계

`mpq-qa-result.json`의 MPQ-01~05 각각 실제 결과·증거 상대 경로, OS/CPU/GPU/해상도·UTC 시작/끝을 채운다.
`action-log.txt`, 게임 창만의 screenshots/영상, logs/를 함께 반환한다. ContentId도 실제 관찰값으로 채운다.
모든 케이스에 실제 창 조작 증거가 있을 때만 scope=`public_demo_terrain_preview`의 status/gui_qa를 PASS로 기록한다.
`mpq_integrity=PASS`를 GUI 결과로 복사하지 않는다. full_lod_compatibility/original_rules_validated는 승격하지 않는다.
전체 QA-09 및 배우/음향 검증은 여전히 별도다. 공개 업로드 전 개인 설치 경로를 제거한다.
MPQ·추출 리소스·개인 파일은 결과 ZIP에 포함하지 않는다. GitHub 게시/릴리즈 변경/다른 사람에게 전송은 하지 않는다.

## 개발자 디코더 재검사 (GUI와 별도)

소스 빌드 환경의 AssetAudit으로 아래 명령을 실행하면 JSON stdout과 stderr를 별도 보존할 수 있다.
.NET SDK와 해당 OS native MPQ backend가 필요하므로 일반 봇 GUI 시험의 필수 설치 단계가 아니다.

```sh
dotnet tools/OpenD2.AssetAudit/bin/Release/net10.0/OpenD2.AssetAudit.dll --check-scene /private/demo/mpq /private/qa-run-01/scene.json
dotnet tools/OpenD2.AssetAudit/bin/Release/net10.0/OpenD2.AssetAudit.dll --decode /private/demo/mpq
```

기존 데모 결과는 두 명령 모두 종료 3이다. 첫 명령은 LoD 필수 자료 누락, 전체 decode에는 WAV 거부 3건도 있다.
종료 3을 0으로 바꾸지 않는다. 재실행했을 때만 full_asset_audit에 실제 결과와 보고서를 연결한다.
데모 출처/해시는 저장소 `docs/migration/DEMO_MPQ_RESULTS.md`에 기록되어 있다.

## 선택 음악 검사 (새 QA kit의 `--music` 옵션)

인계 준비 명령에 `--music`을 추가하면 scene.json에 실제 데모의
`data/global/music/act1/town1.wav`가 연결되고 MPQ-06 청취 사례가 추가된다.
기본 terrain 인계는 그대로 무음/오디오 BLOCKED다. 같은 빌드의 게임과 키트를 사용한다.

MPQ-06: 실제 창에서 음악 시작, Pause 후 정지, Continue 후 같은 트랙 재개,
음량 조절/음소거/복구, 다른 장면/메뉴 종료 시 음악 정지, 재실행을 확인한다.
전체 트랙 반복 경계는 약 4분 이상 재생하며 직접 듣고 기록한다. 디코딩/헤드리스 검사는
스피커 청취 증거가 아니므로 실제로 듣지 못하면 NOT_RUN/BLOCKED를 유지한다.
효과음/캐릭터 음성은 이 장면에 연결되지 않았다. GUI와 audio_qa는 각각 기록한다.

대형 42MB 음악의 자동 전수 PCM 버퍼 검사는 Actions full-audit의 music.json에 별도로 남는다.
이 검사는 가상 파일명과 고정 해시가 일치하는 d2music.mpq 안에서만 수행한다.
