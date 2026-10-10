import unittest
from dataclasses import replace
from batcave_linux.setup_flow import STEP_TITLES, SetupState, primary_action


class SetupFlowTests(unittest.TestCase):
    def test_all_platforms_and_source_roles_still_require_explicit_start(self):
        for operating_system in ("Windows", "Mac", "Linux"):
            state = SetupState(identity_ready=True, desktop_ready=True, permission_ready=True,
                               peer_selected=False, pair_import_ready=True, source=True, peer_os=operating_system)
            self.assertEqual(primary_action(1, state).label, "Add " + operating_system + " computer")
            state = replace(state, peer_selected=True, listen=True)
            self.assertEqual(primary_action(3, state).label, "Wait for computer")
            self.assertTrue(primary_action(3, state).enabled)
            linked = replace(state, connected=True)
            self.assertEqual(primary_action(3, linked).label, "Start sharing")
            self.assertFalse(linked.armed)
            unavailable = replace(linked, permission_ready=False, permission_supported=False)
            self.assertFalse(primary_action(3, unavailable).enabled)
            self.assertFalse(primary_action(0, unavailable).enabled)
    def test_link_requires_permission_peer_and_address(self):
        state = SetupState(identity_ready=True)
        self.assertFalse(primary_action(3, state).enabled)
        state = replace(state, desktop_ready=True, permission_ready=True)
        self.assertEqual(primary_action(3, state).operation, "pair")
        pending = replace(state, permission_ready=False, permission_pending=True)
        self.assertFalse(primary_action(3, pending).enabled)
        self.assertEqual(primary_action(3, pending).operation, "none")
        state = replace(state, peer_selected=True)
        self.assertFalse(primary_action(3, state).enabled)
        state = replace(state, address_valid=True)
        self.assertEqual(primary_action(3, state).operation, "connect")
        state = replace(state, connecting=True)
        self.assertFalse(primary_action(3, state).enabled)
        linked = replace(state, connecting=False, connected=True)
        self.assertEqual(primary_action(2, linked).operation, "next")
        self.assertEqual(primary_action(3, linked).operation, "start")

    def test_connected_is_idle_and_reconnect_requires_fresh_start(self):
        state = SetupState(True, True, False, True, True, True, False, True, False)
        self.assertEqual(primary_action(3, state).label, "Enable Linux input")
        self.assertEqual(primary_action(3, replace(state, armed=True)).operation, "pause")
        self.assertEqual(primary_action(3, replace(state, connected=False)).operation, "connect")
        self.assertEqual(primary_action(3, replace(state, connected=False, connecting=True)).operation, "none")
        self.assertFalse(primary_action(3, replace(state, desktop_ready=False)).enabled)

    def test_navigation_never_approves_or_arms(self):
        state = SetupState(identity_ready=True)
        self.assertEqual(STEP_TITLES[3], "Link devices")
        self.assertFalse(primary_action(0, state).enabled)
        self.assertEqual(primary_action(0, replace(state, desktop_ready=True, permission_ready=True)).operation, "next")
        self.assertEqual(primary_action(1, state).operation, "export")
        prepared = replace(state, pair_import_ready=True)
        self.assertEqual(primary_action(1, prepared).operation, "import")
        self.assertEqual(primary_action(3, replace(prepared, desktop_ready=True, permission_ready=True)).operation, "pair")
        self.assertFalse(prepared.peer_selected or prepared.armed)
        self.assertFalse(state.permission_ready or state.peer_selected or state.armed)
        with self.assertRaises(ValueError): primary_action(4, state)
