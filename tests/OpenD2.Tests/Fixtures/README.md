# Save migration fixtures

These are synthetic saves, not Diablo II data. They were written by the unchanged
Core at master `c58e2352d7128d66f088a84b73b6e18f92bb52b0` using GameSave.Save
(schema 1, rules 4), before the grid migration.

- `save-v1-items.json`: 10×10 open collision grid (region 1), player 1 at (384,384),
  dead monsters 2/3/4 at (640,384), sword 2 in bag slot 6, vest 3 equipped in body
  slot 1, sword 4 on the ground. Queued pickup of item 4: tick 2, sequence 1.
  Tick 0, random state 1. State SHA-256:
  `36a6778a67282174bd2dfc0e5786d6a98806ee5b2a903b6ab94eca0a44295cce`.
- `save-v1-full.json`: same collision/player, eight dead odd-ID monsters 3..17,
  their eight vests in bag slots 0..7, no pending commands, tick 0, random state 1.
  State SHA-256: `6f572bcb122f35cdaf0bd82c517784b9d1c37fbed8303b44156d0edb193f1af3`.
  Valid under old rules; it cannot fit the default 10×4 grid.

The files deliberately retain old state hashes and are never rewritten by the
new serializer. Tests copy them to temporary slots and verify conversion,
command retention, corruption detection, overflow refusal and backup bytes.

`save-v2-items.json` was written by the unchanged Core at master
`df2703450a95ea52e351ba231566134c394fe51f` (schema 2, rules 5). It loaded
`save-v1-items.json` through that version's grid migration, then queued a move of
sword 2 to cell 2 at tick 3 / sequence 2. The original pickup at tick 2 remains.
State SHA-256: `c80438b2b84b066276bb4b85288225ae3ad0c1c187699b9187afc4103f0db8b8`.
PLAY-15 verifies this unchanged checksum before assigning resource defaults.
The same baseline's synthetic PlaySceneContracts.Request content identity is
`cb0c4c18a7fc408b70dec14f2526433447692c8827f5ab52c26ce1a4787b63e8`;
the resource upgrade must preserve it so earlier scene save slots stay reachable.
