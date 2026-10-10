"""Result-boundary contracts; fixture JSON is not real MPQ evidence."""
import argparse
import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

QA = Path(__file__).parent / 'qa'
sys.path.insert(0, str(QA))
import run_mpq_action as action


def scene():
    return {'Installation': {'MissingArchives': sorted(action.MISSING), 'UnclassifiedArchives': []},
            'ContentId': action.CONTENT_ID,
            'Readiness': {'QuestLoopReachable': True, 'Issues': ['actor_artwork_missing:1', 'actor_artwork_missing:2']},
            'Maps': [{'Check': {'Width': 57, 'Height': 41, 'MissingTiles': 0, 'DuplicateKeys': 77}}]}


class MpqActionTests(unittest.TestCase):
    def test_terrain_scope_keeps_known_missing_archives_explicit(self):
        self.assertEqual([], action.terrain_errors(scene(), 3))
        self.assertTrue(action.terrain_errors(scene(), 0))
        self.assertTrue(action.terrain_errors(scene(), 1))

    def test_new_scene_failures_are_not_accepted_as_demo_limitations(self):
        changes = [lambda r: r.update(ContentId='changed'),
                   lambda r: r['Readiness'].update(QuestLoopReachable=False),
                   lambda r: r['Readiness']['Issues'].append('quest_giver_unreachable'),
                   lambda r: r['Maps'][0]['Check'].update(MissingTiles=1),
                   lambda r: r['Installation']['MissingArchives'].append('d2data.mpq'),
                   lambda r: r['Installation'].update(UnclassifiedArchives=['extra.mpq'])]
        for change in changes:
            value = scene(); change(value)
            with self.subTest(value=value):
                self.assertTrue(action.terrain_errors(value, 3))

    def execute(self, mode, report, code=3, stderr='', music_code=0, music_stderr=''):
        temp = tempfile.TemporaryDirectory(); self.addCleanup(temp.cleanup)
        root = Path(temp.name)
        args = argparse.Namespace(mode=mode, work=root/'input', output=root/'reports',
                                  installer=root/'demo.exe', dotnet='dotnet')
        def result(command, **kwargs):
            if '--check-demo-music' in command:
                return subprocess.CompletedProcess(command, music_code, json.dumps({'Status': 'PASS',
                    'Scope': 'actual_demo_pcm_cursor', 'SourceBytes': 42270644,
                    'EntireTrackChecked': True, 'LoopChecked': True, 'SceneMusicLoaded': True,
                    'SceneContentId': action.CONTENT_ID, 'Gui': 'NOT_RUN', 'SpeakerOutput': 'NOT_RUN'}), music_stderr)
            return subprocess.CompletedProcess(command, code, json.dumps(report), stderr)
        with patch.object(action, 'checked_installer'), patch.object(action, 'extract', return_value=[]), \
             patch.object(action.subprocess, 'run', side_effect=result):
            result = action.run(args)
        return result, json.loads((args.output/'summary.json').read_text()), args.output

    def test_terrain_pass_never_promotes_gui_or_full_audit(self):
        code, summary, _ = self.execute('terrain', scene())
        self.assertEqual(0, code)
        self.assertEqual(3, summary['raw_exit_code'])
        self.assertEqual('PASS', summary['status'])
        self.assertEqual('NOT_RUN', summary['gui_qa'])
        self.assertEqual('NOT_RUN', summary['full_audit'])
        self.assertEqual('NOT_VERIFIED', summary['full_compatibility'])

    def test_stderr_fails_terrain_and_is_preserved(self):
        code, summary, output = self.execute('terrain', scene(), stderr='ERROR: decoder failure')
        self.assertEqual(3, code)
        self.assertEqual('FAIL', summary['status'])
        self.assertIn('ERROR:', (output/'audit.stderr.txt').read_text())

    def test_full_audit_does_not_waive_missing_archives_or_decode_errors(self):
        clean = {'Installation': {'MissingArchives': [], 'Profile': 'demo-1.04'}, 'Archives': [], 'Entries': []}
        for raw_code, report in [(3, clean), (0, {**clean, 'Installation': {'MissingArchives': [], 'Profile': 'lod-1.10f'}}),
                                 (0, {**clean, 'Installation': {'MissingArchives': [], 'Profile': 'demo-1.04', 'UnclassifiedArchives': ['extra.mpq']}}), (0, {**clean, 'Installation': {'MissingArchives': ['d2exp.mpq']}}),
                                 (0, {**clean, 'Archives': [{'ErrorCode': 'read_error'}]}),
                                 (0, {**clean, 'Entries': [{'SourceArchive': 'd2sfx.mpq', 'LogicalPath': 'test.wav', 'Size': 72, 'ErrorCode': 'decode_error'}]})]:
            with self.subTest(code=raw_code, report=report):
                code, summary, _ = self.execute('full-audit', report, raw_code)
                self.assertEqual(3, code)
                self.assertEqual('FAIL', summary['full_audit'])
        code, summary, _ = self.execute('full-audit', clean, 0)
        self.assertEqual(0, code)
        self.assertEqual('NOT_VERIFIED', summary['full_compatibility'])

    def test_music_probe_failure_is_not_promoted_to_full_qa(self):
        clean = {'Installation': {'MissingArchives': [], 'Profile': 'demo-1.04'}, 'Archives': [], 'Entries': []}
        for code, stderr in [(3, ''), (0, 'ERROR: cursor')]:
            result, summary, output = self.execute('full-audit', clean, 0, music_code=code, music_stderr=stderr)
            self.assertEqual(3, result)
            self.assertEqual('FAIL', summary['status'])
            self.assertEqual('NOT_RUN', summary['gui_qa'])
            self.assertTrue((output/'music.json').is_file())
            self.assertEqual(stderr, (output/'music.stderr.txt').read_text())

    def test_invalid_installer_is_rejected(self):
        with tempfile.TemporaryDirectory() as root:
            path = Path(root)/'demo.exe'; path.write_bytes(b'not a verified installer')
            with self.assertRaisesRegex(ValueError, 'SHA256'):
                action.checked_installer(path)

    def test_input_cannot_be_in_uploaded_reports(self):
        with tempfile.TemporaryDirectory() as root:
            output = Path(root)/'reports'
            args = argparse.Namespace(work=output/'input', output=output)
            with self.assertRaises(ValueError):
                action.run(args)
            self.assertFalse(output.exists())


if __name__ == '__main__':
    unittest.main()
