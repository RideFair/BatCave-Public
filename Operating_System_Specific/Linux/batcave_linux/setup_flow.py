"""Setup navigation chooses an action; it never grants trust or input itself."""
from dataclasses import dataclass

# Titles are centralized so native platforms can agree on the same small flow.
STEP_TITLES = ("Permissions", "Nearby computers", "Arrange screens", "Link devices")


@dataclass(frozen=True)
class SetupState:
    identity_ready: bool = False
    desktop_ready: bool = False
    permission_pending: bool = False
    permission_ready: bool = False
    peer_selected: bool = False
    address_valid: bool = False
    connecting: bool = False
    connected: bool = False
    armed: bool = False
    source: bool = False
    listen: bool = False
    permission_supported: bool = True
    enable_pending: bool = False
    nearby_selected: bool = False
    nearby_open: bool = False
    pairing_busy: bool = False
    confirmation: bool = False
    confirmation_sent: bool = False


@dataclass(frozen=True)
class Action:
    label: str
    operation: str
    enabled: bool = True


def primary_action(step, state):
    if not 0 <= step < len(STEP_TITLES): raise ValueError("Unknown setup step")
    if not state.identity_ready: return Action("Opening your device…", "none", False)
    if step == 0:
        if state.permission_ready and state.desktop_ready: return Action("Nearby computers", "pair")
        if state.permission_pending: return Action("Waiting for permission…", "none", False)
        return Action("Grant access", "grant", state.desktop_ready and state.permission_supported)
    if step == 1:
        if state.confirmation:
            return Action("Waiting for the other approval…", "none", False) if state.confirmation_sent else Action("Codes match · Link and share", "approve")
        if state.pairing_busy: return Action("Checking the computer…", "none", False)
        if state.nearby_selected and state.nearby_open: return Action("Link computer", "link")
    if state.armed: return Action("Pause sharing", "pause")
    if state.enable_pending: return Action("Starting sharing…", "none", False)
    if state.connected:
        if not state.permission_ready or not state.desktop_ready:
            if state.permission_pending: return Action("Waiting for permission…", "none", False)
            return Action("Allow keyboard and mouse", "grant", state.desktop_ready and state.permission_supported)
        return Action("Start sharing" if state.source else "Enable input", "start")
    if state.connecting: return Action("Linking devices…", "none", False)
    if state.peer_selected: return Action("Reconnect", "connect", state.listen or state.address_valid)
    return Action("Search again", "search") if step == 1 else Action("Nearby computers", "pair")
