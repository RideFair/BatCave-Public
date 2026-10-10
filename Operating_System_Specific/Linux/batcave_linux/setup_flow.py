"""Setup navigation chooses an action; it never grants trust or input itself."""
from dataclasses import dataclass

# Titles are centralized so native platforms can agree on the same small flow.
STEP_TITLES = ("Permissions", "Pair computers", "Arrange screens", "Link devices")


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


@dataclass(frozen=True)
class Action:
    label: str
    operation: str
    enabled: bool = True


def primary_action(step, state):
    if not 0 <= step < len(STEP_TITLES): raise ValueError("Unknown setup step")
    if not state.identity_ready: return Action("Opening your device…", "none", False)
    if step == 0:
        if state.permission_ready and state.desktop_ready: return Action("Continue", "next")
        if state.permission_pending: return Action("Waiting for permission…", "none", False)
        return Action("Grant access", "grant", state.desktop_ready)
    if step == 1:
        return Action("Continue", "next") if state.peer_selected else Action("Import Windows card", "import")
    if state.armed: return Action("Pause sharing", "pause")
    if not state.permission_ready or not state.desktop_ready:
        if state.permission_pending: return Action("Waiting for permission…", "none", False)
        return Action("Grant access", "permission", state.desktop_ready)
    if not state.peer_selected: return Action("Pair computers", "pair")
    if state.connected: return Action("Link devices", "next") if step == 2 else Action("Enable Linux input", "start")
    if state.connecting: return Action("Linking devices…", "none", False)
    return Action("Connect securely" if step == 2 else "Reconnect", "connect", state.address_valid)
