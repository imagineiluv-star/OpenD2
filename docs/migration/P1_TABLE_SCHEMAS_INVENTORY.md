# Excel 표 의미 스키마와 M4-01 콘텐츠 인벤토리 (정품 1.11b 기준, 2026-10-16)

`TableSchemas`: 엔진이 읽는 16개 표(skills, monstats, superuniques, levels, weapons, armor, misc, itemtypes, uniqueitems, setitems, runes, charstats, missiles, states, objects, treasureclassex)의 열을 정수, 0/1 플래그, 텍스트로 정의하고 필수 여부와 키 열을 지정한다. 키가 비어 있거나 `Expansion`인 행은 구분 행으로 센다. 중복 키는 경고로만 남긴다. 정품에도 중복이 있다: monstats `cr_lancer8`, uniqueitems `Azurewrath`와 `Rainbow Facet`×7, runes `Runeword95`.

`CheckBin`: .bin 레이아웃은 버전마다 달라서 모든 버전에 공통으로 성립하는 것만 검사한다. 레코드 크기가 나누어떨어지는지, 그리고 BIN 개수가 첫 열이 정확히 `Expansion`인 행(대소문자 구분)을 뺀 TXT 행 수와 같은지 본다. objgroup.txt의 `EXPANSION` 행은 BIN에 남아 있다. 정품 BIN 69개 모두 이 규칙과 맞는다.

`ContentInventoryBuilder` / `AssetAudit --content-inventory`: 아래 수치는 모두 데이터 파일에서 센다. 1.11b에는 퀘스트 표가 없어서 퀘스트는 string TBL의 `qstsa<막>q<번호>` 키로 센다.

Rows는 구분 행을 뺀 모든 레코드 행, Playable은 플레이어가 실제로 만나는 콘텐츠다. 필터 기준은 `ContentInventoryBuilder.Definitions`에 있다.

| 항목 | Rows | Playable | Playable 기준 |
|---|---|---|---|
| 스킬 | 357 | 210 (7직업×30) | charclass가 있는 행 |
| 몬스터 | 734 | 612 | enabled=1, npc≠1, killable=1 (NPC, 함정, 동물, 스포너, 더미 제외) |
| 슈퍼유니크 | 66 | 66 | 전체 행 |
| 기본 아이템 | 654 (306/202/146) | 597 (297/202/98) | spawnable=1, quest 값 없음 |
| 유니크 | 401 | 385 | enabled=1 |
| 세트 | 아이템 127, 세트 정의 32 | 127 | |
| 룬워드 | 169 | 78 | complete=1 |
| 퀘스트 | 27 | 27 (막별 6/6/6/3/6) | string TBL의 qstsa 키 |
| 지역 | 136 | 132 (막별 39/35/28/6/24) + Uber 4 | Id>0, Uber 이벤트 지역('Act 5 - Pandemonium*')은 따로 셈 |

구분 행 기준은 두 가지다. 인벤토리와 스키마는 키가 비었거나 `Expansion`(대소문자 무시)인 행을 제외한다. BIN 개수는 게임 컴파일러 동작을 따라 첫 열이 정확히 `Expansion`인 행만 뺀다.
