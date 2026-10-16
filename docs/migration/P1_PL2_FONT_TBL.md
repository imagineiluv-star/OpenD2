# PL2 조명 팔레트와 폰트 TBL (정품 1.11b 기준, 2026-10-16)

## pal.pl2 (`Pl2Palette`, Engine/Palette.cpp:30 TODO)
순서: 기본 팔레트 256×RGBA(1024) → 조명 32×256 → 인벤토리 색 16×256 → 선택 강조 256 → 알파 블렌드 3×256×256(25/50/75%) → 가산 256×256 → 곱셈 256×256 → 색조 111×256 → R/G/B 톤 각 256 → 미확인 14×256 → 최대 성분 256×256 → 어두운 이동 256 → 글자색 N×RGB → 글자색 이동 N×256.
정품 16개 중 15개는 N=13(443,175바이트), `loading\pal.pl2`만 N=12(442,916바이트)다. 기본 팔레트 RGB는 같은 폴더의 `pal.dat`(BGR)와 모두 일치한다.

## 폰트 TBL (`FontTable`, Engine/TBL_Font.hpp:13 FIXME)
정품 폰트 표 28개(latin 14, kor 14)로 확인했다.
- 헤더: `Woo!`, WORD 버전(1), WORD 예약(0), **WORD 글리프 수**(latin 256, kor 11,374. 기존 헤더의 dwLocale은 잘못된 해석), BYTE 줄 높이, BYTE 미확인(1/2/7/8/9/12/20 값, 그대로 노출).
- 글리프 14바이트: WORD 문자 코드, BYTE 0, BYTE 진행 폭, BYTE 높이(항상 줄 높이), BYTE 플래그(항상 1), WORD 0, **WORD DC6 프레임 번호**(기존 nImageIndex+nChar, 항상 글리프 순번), DWORD 0(런타임 아틀라스 자리).
- `fonter.tbl`, `default.tbl`은 텍스트 목록이라 글리프 표가 아니다.
