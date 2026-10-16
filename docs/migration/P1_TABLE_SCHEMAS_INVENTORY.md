# Excel 표 의미 스키마와 M4-01 콘텐츠 인벤토리 (정품 1.11b 기준, 2026-10-16)

`TableSchemas`: 엔진이 읽는 16개 표(skills, monstats, superuniques, levels, weapons, armor, misc, itemtypes, uniqueitems, setitems, runes, charstats, missiles, states, objects, treasureclassex)의 열을 정수, 0/1 플래그, 텍스트로 정의하고 필수 여부와 키 열을 지정한다. 키가 비어 있거나 `Expansion`인 행은 구분 행으로 센다. 중복 키는 경고로만 남긴다. 정품에도 중복이 있다: monstats `cr_lancer8`, uniqueitems `Azurewrath`와 `Rainbow Facet`×7, runes `Runeword95`.

`CheckBin`: .bin 레이아웃은 버전마다 달라서 모든 버전에 공통으로 성립하는 것만 검사한다. 레코드 크기가 나누어떨어지는지, 그리고 BIN 개수가 첫 열이 정확히 `Expansion`인 행(대소문자 구분)을 뺀 TXT 행 수와 같은지 본다. objgroup.txt의 `EXPANSION` 행은 BIN에 남아 있다. 정품 BIN 69개 모두 이 규칙과 맞는다.

`ContentInventoryBuilder` / `AssetAudit --content-inventory`: 아래 수치는 모두 데이터 파일에서 센다. 1.11b에는 퀘스트 표가 없어서 퀘스트는 string TBL의 `qstsa<막>q<번호>` 키로 센다.

| 항목 | 1.11b |
|---|---|
| 스킬 | 357 (플레이어 210 = 7직업×30) |
| 몬스터 | 734 (enabled 703), 슈퍼유니크 66 |
| 기본 아이템 | 654 (무기 306, 방어구 202, 기타 146) |
| 유니크 | 401 (enabled 385), 세트 아이템 127 |
| 룬워드 | 169 행, complete 78 |
| 퀘스트 | 27 (막별 6/6/6/3/6) |
| 지역 | 136 (막별 39/35/28/6/28) |
