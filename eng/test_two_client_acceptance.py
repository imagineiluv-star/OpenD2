"""Regression gates for incomplete or misleading two-client evidence."""
import json
from pathlib import Path
import tempfile
import unittest
import uuid
from two_client_acceptance import COMMON, INVITE, validate_evidence


class AcceptanceEvidenceTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.path = Path(self.directory.name)
        room = str(uuid.uuid4())
        self.results = {}
        for role in ('host', 'guest'):
            stages = COMMON | INVITE | ({'invite_generated', 'room_invite_generated'} if role == 'host' else {'invited_room_joined'})
            self.results[role] = dict(role=role, mode='open', result='PASS', acceptance=sorted(stages),
                room=room, character=str(uuid.uuid4()), manual_gui='NOT_RUN', multi_pc='NOT_RUN', wan='NOT_RUN',
                observations='room-websocket-stream', http_state_polling=False)

    def validate(self):
        for role, result in self.results.items():
            (self.path / (role + '-result.json')).write_text(json.dumps(result), encoding='utf-8')
        return validate_evidence(self.path, 'open', True)

    def test_complete_evidence(self):
        self.assertEqual(set(self.validate()), {'host', 'guest'})

    def test_guest_reconnect_cannot_be_skipped(self):
        self.results['guest']['acceptance'].remove('guest_reconnect')
        with self.assertRaisesRegex(ValueError, 'guest_reconnect'):
            self.validate()

    def test_tls_cannot_pass_without_invite_and_trust(self):
        for stage in ('invited_room_joined', 'ca_mismatch_rejected', 'server_checked'):
            with self.subTest(stage=stage):
                self.results['guest']['acceptance'].remove(stage)
                with self.assertRaises(ValueError):
                    self.validate()
                self.results['guest']['acceptance'].append(stage)

    def test_unrelated_rooms_or_shared_character_rejected(self):
        for key in ('room', 'character'):
            original = self.results['guest'][key]
            self.results['guest'][key] = str(uuid.uuid4()) if key == 'room' else self.results['host'][key]
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.validate()
            self.results['guest'][key] = original

    def test_wrong_mode_failed_client_or_polling_rejected(self):
        for key, value in (('mode', 'realm'), ('result', 'FAIL'), ('http_state_polling', True)):
            original = self.results['guest'][key]
            self.results['guest'][key] = value
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.validate()
            self.results['guest'][key] = original

    def test_physical_claims_rejected(self):
        for key in ('multi_pc', 'wan', 'manual_gui'):
            self.results['guest'][key] = 'PASS'
            with self.subTest(key=key), self.assertRaises(ValueError):
                self.validate()
            self.results['guest'][key] = 'NOT_RUN'


if __name__ == '__main__':
    unittest.main()
