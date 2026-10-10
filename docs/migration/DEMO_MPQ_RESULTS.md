# 공개 Diablo II 데모 MPQ 실데이터 검사

실행일: 2026-10-10 UTC. 코드: `80c2c35e648821a720b6d2aa8af16554602fcb5f`.
환경: Linux x64, .NET SDK 10.0.401, 저장소의 고정 StormLib native backend.
판정: **부분 디코딩/장면 연결 및 데이터 이관 성공. 전체 호환 인수 실패. GUI NOT_RUN.**

## 자료 출처

Blizzard의 [구형 다운로드 목록](https://classic.battle.net/files.shtml)에 공개 데모가 안내되어 있다.
현재 원본 다운로드 경로에서 확보하지 못해 [RGB Classic Games의 v1.04 Shareware 보존본](https://www.classicdosgames.com/game/Diablo_II.html)을 사용했다.
직접 다운로드: `https://www.classicdosgames.com/files/games/blizzard/DiabloIIDemo.exe`.
정품 전체 게임 또는 LoD 설치본이 아니다. 설치 EXE는 실행하지 않고 내부 MPQ를 읽기 전용으로 열었다.

- 크기: 138,309,685 bytes
- MD5: `9ae5033551a078937cd5d1f388cd8438` — 보존 사이트 및 ModDB 데모 목록과 일치
- SHA256: `89352716523e474514553e2092a1ae9349c5c7ff9e79c7861dd65fe19be88b61`

공개 체크섬 일치는 Blizzard 서명 검증을 의미하지 않는다. 설치 아카이브에서 실제 논리 경로
`SetupDat\\Files\\<파일명>`으로 6개 MPQ를 추출했다. 원본 바이너리/추출 데이터는 Git·Actions 아티팩트에 넣지 않았다.

| MPQ | bytes | SHA256 |
| --- | ---: | --- |
| d2data.mpq | 44301122 | 82ed65b7f574234a22a36abb4a6d6a1e7f8bebc4746192f39cdb6603ee382d49 |
| d2char.mpq | 20786750 | d3786b7dd6901197b2a163e263321f169de72fa59dd518e74b4d2466b8874cb7 |
| d2sfx.mpq | 10887212 | 1652c791ca1874be8126f549c478f29810e354ee10720b5afc89a9fbef2934c4 |
| d2speech.mpq | 21214310 | f777ef69b3438cf991eec35b1ccfa196cfb4da563ae9b6848cc6881d5538cd8f |
| d2music.mpq | 32743265 | 631172d59cc4a8d9b42faade73b194140b6a327811ea556562df9c89f857a694 |
| patch_d2.mpq | 1342817 | f3dedcab99cd0fe213500f9bce0d09f675cb392de9e99ebdb7b5d956a17af7e6 |

## 검사 결과와 실패 유지

6개 아카이브 열기/해시 검사 성공. 알려진 경로를 보충한 인벤토리 5,118개 중 4,026개 디코딩 검증:
DCC 2,151, DC6 1,051, WAV 775, Excel TXT 17, DS1 10, TBL 10, DT1 9, COF 2, 팔레트 1.
1,089개는 미지원/미분류, 3개는 거부됐다. 가상 파일명과 중복 항목을 포함하므로 전체 고유 콘텐츠 수가 아니다.

AssetAudit 종료 코드는 **3**이다. 다음 실패를 제거하거나 정상으로 바꾸지 않았다.

- 현재 요청 프로파일 `lod-1.10f`의 필수 파일 d2exp/d2video/d2xmusic/d2xtalk/d2xvideo가 없다.
  데모 자료의 버전은 출처상 1.04이며, 이 프로파일 요청이 LoD 호환 또는 버전 인증을 뜻하지 않는다.
- d2music의 가상 이름 `file00000004.wav`: 42,270,644 bytes로 현재 32 MiB 디코더 입력 예산 초과.
- d2sfx의 `data/global/sfx/cursor/curindx.wav`, `wavindx.wav`: 각각 72 bytes이며 RIFF 헤더가 아니다.
  확장자는 WAV지만 실제 역할은 미확인이다. 파일 손상으로 단정하거나 무시하지 않는다.

## 지도·장면·이관

`data/global/tiles/act1/town/townn1.ds1`과 Act1 팔레트, 원본 lvltypes TXT의 Town 행에 있는
9개 DT1(Floor, Objects, Fence, River, stonewall, trees, Outdoors/Objects, TreeGroups, Bridge)을 연결했다.

- 지도 57×41, 참조 타일 누락 **0**, 중복 타일 키 **77**, 보행 가능 subcell 15,266.
- 중복 키의 현재 결정적 선택이 원작의 변형 선택 규칙과 같은지는 미검증.
- LegacySceneSetup으로 빈 보행 영역에 테스트 배우/NPC를 배치하고 LegacyPlayScene을 로드했다.
  실제 원작 배치·AI·퀘스트 재현이 아닌 OpenD2 장면 연결 검사다.
- QuestLoopReachable=true, 시뮬레이션 250틱 실행. 배우 2명의 아트가 연결되지 않아
  ReadyForSceneGuiCheck=false, OriginalRulesValidated=false, GUI NOT_RUN.
- 로컬 등록 → ZIP pack → 다른 vault unpack → 해시 재검증 성공.
  원본/복원 경로에서 같은 장면 ContentId:
  `dd0e5d1e3a8fd5509a8c683abda366e1ade3f03f444b38e012521a1b08301d57`.
- dataset ID: `43cf5ee2b9bafbec35cdddf15439be0736634c8937b91e55bfdfe9ed327ba369`.
  선언 버전 `demo-1.04-declared`, 언어 en. 무결성 성공은 호환성 승격이 아니다.

지형을 CPU로 디코딩한 참고 이미지는 실제 Godot GUI 캡처나 플레이 테스트가 아니다.
이번 실데이터 실행 환경은 Linux뿐이며 Windows/macOS 실데이터 실행 결과로 확장하지 않는다.
3 OS CI의 합성 계약·내보내기·배포 압축/해제·헤드리스 검증은 별도 증거다.

## 남은 작업

- [ ] 두 cursor 인덱스의 형식을 확인하고 오디오 분류 규칙 설계. 실제 WAV 검증은 유지.
- [ ] 긴 음악의 스트리밍/예산 정책 설계 및 초과·잘림 회귀 검사.
- [ ] 명시적 데모 프로파일/전체 LoD 프로파일 구분. 필수 파일 검사를 완화해 전체 통과시키지 않음.
- [ ] 캐릭터 COF/DCC 방향·피벗·장비 레이어, NPC/HUD/아이템 아트 연결.
- [ ] Windows/Linux/macOS 실제 데이터 GUI 플레이, 원작 비교, 30분 안정성 기록.
- [ ] 사용자 보유 LoD 자료로 전체 필수 아카이브·한글·원작 규칙 검사.
- [ ] [D3 렌더링 참고 계획](D3_RENDERING_REVIEW.md)의 VIS-01 기준 화면 이후 VIS-02/03 실험.
