import unittest
from dataclasses import replace
from batcave_linux.setup_flow import STEP_TITLES, SetupState, primary_action


class SetupFlowTests(unittest.TestCase):
    def test_nearby_without_permissions_or_platform_selection(self):
        state = SetupState(identity_ready=True, nearby_open=True)
        self.assertEqual(STEP_TITLES[1], "Nearby computers")
        self.assertEqual(primary_action(1, state).operation, "search")
        self.assertEqual(primary_action(1, replace(state, nearby_selected=True)).operation, "link")
        self.assertEqual(primary_action(1, replace(state, pairing_busy=True)).operation, "none")
        self.assertFalse(state.permission_ready or state.peer_selected or state.armed)

    def test_confirmation_explicitly_labels_one_use_sharing(self):
        state = SetupState(identity_ready=True, confirmation=True)
        self.assertEqual(primary_action(1, state).label, "Codes match · Link and share")
        self.assertEqual(primary_action(1, state).operation, "approve")
        self.assertFalse(primary_action(1, replace(state, confirmation_sent=True)).enabled)
        self.assertFalse(state.peer_selected or state.armed)

    def test_connection_and_reconnect_do_not_arm_input(self):
        state = SetupState(identity_ready=True, desktop_ready=True, peer_selected=True, address_valid=True)
        self.assertEqual(primary_action(1, state).operation, "connect")
        self.assertEqual(primary_action(1, replace(state, connecting=True)).operation, "none")
        linked = replace(state, connected=True)
        self.assertEqual(primary_action(1, linked).operation, "grant")
        linked = replace(linked, permission_ready=True)
        self.assertEqual(primary_action(1, linked).label, "Enable input")
        self.assertEqual(primary_action(1, replace(linked, source=True)).label, "Start sharing")
        self.assertFalse(linked.armed)
        self.assertEqual(primary_action(1, replace(linked, armed=True)).operation, "pause")

    def test_navigation_does_not_grant_permissions_or_trust(self):
        state = SetupState(identity_ready=True)
        self.assertFalse(primary_action(0, state).enabled)
        self.assertFalse(primary_action(0, replace(state, desktop_ready=True, permission_supported=False)).enabled)
        ready = replace(state, desktop_ready=True, permission_ready=True)
        self.assertEqual(primary_action(0, ready).operation, "pair")
        self.assertEqual(primary_action(2, state).operation, "pair")
        self.assertFalse(state.permission_ready or state.peer_selected or state.armed)
        with self.assertRaises(ValueError): primary_action(4, state)
