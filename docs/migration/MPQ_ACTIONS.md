# GitHub Actions 실제 MPQ 검사

Actions → **MPQ data validation** → **Run workflow** → master와 mode를 선택한다.
Windows 2025, Ubuntu 24.04, macOS 15에서 같은 공개 데모를 검증한다. 비밀 키나 개인 MPQ 업로드는 필요 없다.

| 모드 | 검사/종료 조건 | 판정 한계 |
| --- | --- | --- |
| `terrain` | 공개 데모 설치 파일 SHA256/크기, 내부 MPQ 6개 SHA256, 고정 마을 장면 ContentId, 57×41 지도·타일 누락 0·동선 검증. 알려진 LoD 누락 5개 및 배우 아트 미설정 2개와 정확히 일치해야 함 | 해당 지형 연결의 부분 검사. raw scene exit 3은 summary에 유지. full audit와 GUI는 NOT_RUN |
| `full-audit` (수동 기본값) | 실제 MPQ 전체 `AssetAudit --decode`. 원래 종료 코드·아카이브 읽기/디코딩 오류·필수 자료 누락에 따라 실패 | 현재 공개 데모는 LoD 파일 누락 및 WAV 거부 3건으로 **실패가 예상됨**. 실패를 정상으로 바꾸지 않음 |

PR에서 이 워크플로 또는 관련 QA 파일이 바뀌면 `terrain`을 자동 실행한다.
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
`--mode full-audit`의 현재 종료 3은 예상된 미해결 결과이며 생략하거나 성공으로 취급하지 않는다.
