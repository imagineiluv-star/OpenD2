"""Hand-pinned Godot scene uids/unique_ids (kept fixed for reproducible exports) must never collide."""
from collections import Counter
from pathlib import Path
import re
import unittest

CLIENT = Path(__file__).resolve().parent.parent / "src" / "OpenD2.Client"
UID_CHARS = re.compile(r"uid://[a-y0-8]+")  # Godot's UID alphabet omits z and 9


class SceneUidTests(unittest.TestCase):
    def resources(self):
        return sorted(p for p in CLIENT.rglob("*") if p.suffix in (".tscn", ".tres") and ".godot" not in p.parts)

    def test_resource_and_script_uids_are_unique_and_well_formed(self):
        declared = []
        for path in self.resources():
            header = path.read_text(encoding="utf-8").splitlines()[0]
            match = re.search(r'\buid="(uid://[^"]+)"', header)
            self.assertIsNotNone(match, f"{path.name} has no pinned uid")
            declared.append((match.group(1), path.name))
        for path in sorted(CLIENT.rglob("*.uid")):
            if ".godot" not in path.parts:
                declared.append((path.read_text(encoding="utf-8").strip(), path.name))
        for uid, name in declared:
            self.assertRegex(uid, UID_CHARS, name)
            self.assertEqual(uid, UID_CHARS.fullmatch(uid).group(0), name)
        duplicates = [uid for uid, count in Counter(uid for uid, _ in declared).items() if count > 1]
        self.assertEqual([], duplicates)

    def test_node_unique_ids_are_unique_across_scenes(self):
        ids = []
        for path in self.resources():
            ids += [(value, path.name) for value in re.findall(r"\bunique_id=(\d+)", path.read_text(encoding="utf-8"))]
        self.assertTrue(ids)
        duplicates = [value for value, count in Counter(value for value, _ in ids).items() if count > 1]
        self.assertEqual([], duplicates, ids)


if __name__ == "__main__":
    unittest.main()
