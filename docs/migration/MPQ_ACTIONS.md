# GitHub Actions 실제 MPQ 검사

Actions → **MPQ data validation** → **Run workflow** → master와 mode를 선택한다.
Windows 2025, Ubuntu 24.04, macOS 15에서 같은 공개 데모를 검증한다. 비밀 키나 개인 MPQ 업로드는 필요 없다.

| 모드 | 검사/종료 조건 | 판정 한계 |
| --- | --- | --- |
| `terrain` | 공개 데모 설치 파일 SHA256/크기, 내부 MPQ 6개 SHA256, 고정 마을 장면 ContentId, 57×41 지도·타일 누락 0·동선 검증. 알려진 LoD 누락 5개 및 배우 아트 미설정 2개와 정확히 일치해야 함 | 해당 지형 연결의 부분 검사. raw scene exit 3은 summary에 유지. full audit와 GUI는 NOT_RUN |
| `full-audit` (수동 기본값) | `AssetAudit --decode-demo`. 데모 필수 MPQ 6개·읽기/형식 오류·예상 밖 아카이브·stderr·종료 코드가 모두 정상이어야 통과 | 명시적 demo-1.04 인벤토리 검사. 미지원 형식/전체 경로 포괄성/버전 호환성/실제 음향·GUI는 별도 |

PR에서 이 워크플로 또는 관련 QA 파일이 바뀌면 `terrain`과 `full-audit`를 각각 3 OS에서 자동 실행한다.
이 자동 검사의 녹색은 전체 LoD/원본 아트/음향/GUI 인수가 아니다. 기존 Migration validation의
Godot ERROR 검사·계약·3 OS 내보내기·압축 해제 실행 검사는 유지한다.

## 자료와 결과

고정 URL의 공개 데모를 임시 폴더에 내려받아 크기와 SHA256을 확인한다.
설치 EXE는 실행하지 않고, 기존 MPQ backend로 알려진 `SetupDat/Files` 항목 6개만 읽는다.
각 MPQ도 고정 해시와 일치해야 한다. 파일이 바뀌거나 출처 다운로드가 실패하면 워크플로가 실패한다.
검증을 통과시키기 위해 다른 자료를 자동 선택하거나 해시 검사를 완화하지 않는다.
출처·해시·미해결 항목은 [DEMO_MPQ_RESULTS.md](DEMO_MPQ_RESULTS.md)를 참고한다.

각 OS의 `MPQ-<mode>-<os>` 아티팩트에 다음 자료가 남는다.

- `summary.json` / `summary.md`: 모드, 검사 범위, 상태, 원래 종료 코드, 커밋, MPQ 해시, GUI 미실행 상태
- `audit.json`: 실제 AssetAudit JSON (개인 입력 루트만 치환)
- `audit.stderr.txt`: 오류 출력 원문 (개인 입력 루트만 치환)

실패 때도 보고서를 보존한다. 다운로드/빌드 이전 실패는 해당 Actions 로그를 확인한다.
원본 MPQ·설치 파일·추출 게임 이미지·개인 설치 경로는 아티팩트에 넣지 않는다.
실행 후 임시 원본 자료를 제거한다. 이 워크플로는 게임 창·그록봇을 실행하지 않으며,
실제 GUI 시험은 [MPQ_GROK_TASK](../../eng/qa/MPQ_GROK_TASK.md)의 별도 절차로 수행한다.

## 로컬 동일 검사

Python 3.12+, 고정 .NET SDK와 native backend가 있는 소스 빌드 환경에서:

```sh
python eng/build-native.py
dotnet build tools/OpenD2.AssetAudit -c Release -m:1
python eng/qa/run_mpq_action.py --mode terrain --work /tmp/opend2-demo-input --output artifacts/mpq-qa
```

입력/출력은 서로 겹치지 않는 새 폴더여야 한다. 재실행은 새 경로를 지정한다.
`--installer /path/DiabloIIDemo.exe`는 동일 고정 해시를 가진 로컬 파일로 다운로드만 대체한다.
`--mode full-audit`는 raw exit 0만 허용한다. 기본 `AssetAudit --decode`의 LoD 기준은 유지된다.

## 2026-10-10 전체 검사 수정

실패 실행 38039079223의 세 OS에서 같은 원인을 재현했다. 데모에 LoD 필수 목록을 적용했고,
42,270,644바이트 PCM 음악에 런타임 32 MiB 예산을 적용했으며, RIFF가 아닌 두 72바이트 항목을
확장자만 보고 WAV로 디코딩했다.

- `--decode-demo`만 demo-1.04 필수 목록을 사용한다. 파일명만으로 버전을 인증하지 않는다.
- WAV 구조 검사는 64 MiB 입력 한도 안에서 PCM 형식/길이/청크/정렬/샘플률을 검사하며,
  PCM 출력 배열을 추가 생성하지 않는다. 모든 MPQ I/O 예산은 유지한다.
  게임 재생용 `PcmWave.Parse`의 기존 32 MiB 입력/출력 제한은 유지된다. 대용량 음악 재생 지원은 별도 작업이다.
- d2sfx.mpq의 `data/global/sfx/cursor/curindx.wav`, `wavindx.wav`는 데모 모드에 한해서
  `demo_opaque_record`로 분류한다. 각 72바이트와 고정 SHA256이 정확히 일치해야
  `opaque_integrity_validated`가 된다. 의미/내용 해독은 하지 않으며 오디오 검증으로 집계하지 않는다.
  다른 경로/아카이브/프로필에는 이 분류를 적용하지 않는다. 바이트가 바뀌면 실패한다.
- 로컬 실제 데모 검사: 5,105개 항목 중 형식 검증 4,014개, opaque 무결성 2개,
  미지원 1,089개. 오류 0, 종료 0. `Complete=false`, 전체 호환성 NOT_VERIFIED, GUI NOT_RUN.
- 단위 테스트의 의도적인 FAIL 출력은 실패 입력을 거부하는 테스트 자료다.
  실제 검사 결과는 `Fetch pinned public demo and validate actual MPQs` 단계와 보고서로 구분한다.

이전 DEMO_MPQ_RESULTS.md는 수정 전 실행 기록이다. 원본 게임 데이터는 Git/아티팩트에 추가하지 않는다.

## 음악 재생 경로 검사

full-audit 성공 후 같은 실제 MPQ로 `--check-demo-music`를 실행한다. 42MB 음악 전체의
PCM cursor 출력/반복 경계를 검증하며 실패/오류 출력은 작업 실패다.
`music.json`, `music.stderr.txt`를 추가 보존한다. GUI/스피커 청취는 NOT_RUN이다.
합성 WAV의 Godot generator 제어는 Migration validation에서 별도로 검사한다.
[MPQ_MUSIC](MPQ_MUSIC.md)에 메모리 예산과 한계를 기록한다.
