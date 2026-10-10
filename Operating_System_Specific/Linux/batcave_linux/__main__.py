import argparse
import ctypes.util
import json
import os
import platform
import ssl
import sys

from . import __version__


def diagnose():
    result = {"version": __version__, "platform": platform.system(), "python": platform.python_version(),
              "tls13": ssl.HAS_TLSv1_3, "libei": bool(ctypes.util.find_library("ei")), "desktopPortal": False}
    try:
        import gi
        gi.require_version("Gtk", "4.0")
        gi.require_version("Secret", "1")
        from gi.repository import Gio, GLib, Gtk
        result["gtk"] = Gtk.get_major_version()
        bus = Gio.bus_get_sync(Gio.BusType.SESSION, None)
        reply = bus.call_sync("org.freedesktop.portal.Desktop", "/org/freedesktop/portal/desktop",
                  "org.freedesktop.DBus.Properties", "GetAll", GLib.Variant("(s)", ("org.freedesktop.portal.RemoteDesktop",)),
                  GLib.VariantType.new("(a{sv})"), Gio.DBusCallFlags.NONE, 2000, None).unpack()[0]
        result["desktopPortal"] = reply.get("version", 0) >= 2 and reply.get("AvailableDeviceTypes", 0) & 3 == 3
        result["portalVersion"] = reply.get("version", 0)
    except Exception:
        result["reason"] = "A running desktop session with GTK 4, libsecret and the RemoteDesktop portal is required."
    print(json.dumps(result, indent=2))
    return 0 if result["desktopPortal"] and result["tls13"] and result["libei"] else 2


def main(argv=None):
    parser = argparse.ArgumentParser(description="BatCave for Linux")
    parser.add_argument("--version", action="version", version=__version__)
    parser.add_argument("--diagnose", action="store_true", help="Check capabilities without creating an identity or sharing input")
    args = parser.parse_args(argv)
    if args.diagnose: return diagnose()
    if sys.platform != "linux": parser.error("This agent requires Linux.")
    if os.getuid() == 0: parser.error("Run BatCave inside your normal desktop user session, not as root.")
    if not os.environ.get("DBUS_SESSION_BUS_ADDRESS"): parser.error("A desktop session bus is required.")
    # No core dump containing TLS keys or private input state.
    import resource
    resource.setrlimit(resource.RLIMIT_CORE, (0, 0))
    try:
        from .desktop import Application
        return Application().run([sys.argv[0]])
    except (ImportError, OSError, RuntimeError, ValueError):
        print("BatCave could not start. Run --diagnose to check its desktop dependencies.", file=sys.stderr)
        return 2


if __name__ == "__main__": raise SystemExit(main())
