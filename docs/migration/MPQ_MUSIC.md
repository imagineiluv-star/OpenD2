# MPQ 음악 버퍼 재생 (2026-10-10)

## 변경

- 효과음은 기존 32 MiB 입력/PCM, 10초 제한을 유지한다. 음악은 별도 ParseMusic 경로로
  입력/PCM 각각 64 MiB까지 허용한다. 은행 전체 입력 및 보관 PCM 예산 각각 64 MiB도 유지한다.
- 음악을 메모리에 먼저 읽고 검증한 후, PcmLoop가 1,024프레임 단위로 Godot
  AudioStreamGenerator에 공급한다. 파일 I/O는 프레임 경로에 없다. 전체 음악을
  AudioStreamWav로 다시 복제하지 않는다. 이는 디스크/MPQ 스트리밍 구현은 아니다.
  더 큰 설치본 음원은 별도 비동기 MPQ 스트리밍 작업으로 남긴다.
- 0.25초 엔진 버퍼, 초당 원래 샘플률, mono→stereo 복제, PCM16 float 변환과
  정확한 반복 경계, pause/resume, mute/volume, region change, stop/dispose를 검증한다.
- 오디오는 시뮬레이션/세이브 ContentId를 바꾸지 않는다.

## 실제 자료와 검증 구분

로컬 `--check-demo-music` 결과: d2music.mpq 안의 고정 해시 file00000004.wav,
42,270,644바이트, 22,050 Hz stereo PCM16, 10,567,616프레임.
모든 프레임을 실제 클라이언트용 PCM cursor로 통과시킨 뒤 PCM16으로 되돌린 SHA256은
원본 샘플과 일치했다. CI 검사는 추가로 실제 named town1.wav를 장면 경로로 로드하고 ContentId 불변을 확인한다. 반복/reset 경계도 통과했다.
PCM SHA256: 1fefe03f1674938679be9bc856debe5a0eade3c1d8b0f6ba63a5dbc4077bcc77.

| 검사 | 범위 |
| --- | --- |
| Actual MPQ full-audit + music.json | 실제 전체 트랙 샘플 전수/순서/반복; 스피커/GUI NOT_RUN |
| Migration validation audio smoke | 합성 WAV로 실제 Godot generator 공급/정지/재개/음소거/지역 교체 검사 |
| QA kit --music / MPQ-06 | 실제 마을 음악 GUI·청취 인수; 아직 NOT_RUN |

로컬 Client 빌드와 새 C# PCM 계약은 통과했다. 로컬 전체 계약은 설치되지 않은 NPC 런타임
1건으로 완료하지 못했으며 원격 CI가 해당 런타임을 빌드하고 전체 검증한다.
후속 로컬 실행 서버 연결이 끊겨 CI를 최종 기준으로 삼는다. 최종 결과는 해당 PR/Actions에 기록한다.

QA 준비: 기존 mpq_handoff.py 명령에 `--music`을 추가한다. 새 음악 장면도 기본 장면과
ContentId가 같으며 scene JSON 해시는 별도로 기록한다. 실제 게임 화면/음향 검증은
[MPQ_GROK_TASK](../../eng/qa/MPQ_GROK_TASK.md)를 따른다.

## 참고

- Godot AudioStreamGenerator: https://docs.godotengine.org/en/stable/classes/class_audiostreamgenerator.html
- Godot AudioStreamGeneratorPlayback: https://docs.godotengine.org/en/stable/classes/class_audiostreamgeneratorplayback.html

## 남은 작업

실제 장치 청취, 장시간 underrun/CPU 실측, 모든 OS GUI, 64 MiB 초과 음원의 MPQ I/O 스트리밍.
