import json
import os
import cherrypy
import threading
import time

from cuesdk import (
    CueSdk,
    CorsairDeviceFilter,
    CorsairDeviceType,
    CorsairError,
    CorsairLedColor,
    CorsairLedId_Keyboard,
    CorsairAccessLevel,
    CorsairSessionState,
)

SCRIPT_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_PATH = os.path.join(SCRIPT_DIR, "config.json")

def load_config():
    with open(CONFIG_PATH, "r", encoding="utf-8") as f:
        return json.load(f)

def resolve_key_ids(key_names):
    ids, missing = [], []
    for name in key_names:
        if hasattr(CorsairLedId_Keyboard, name):
            ids.append(getattr(CorsairLedId_Keyboard, name))
        else:
            missing.append(name)
    if missing:
        raise ValueError(f"Unknown key names in config: {missing}")
    return ids

def normalize_rgba(color):
    if not isinstance(color, (list, tuple)) or len(color) != 4:
        raise ValueError("color must be [r,g,b,a]")
    r, g, b, a = (int(x) for x in color)
    for v in (r, g, b, a):
        if v < 0 or v > 255:
            raise ValueError("RGBA values must be 0..255")
    return (r, g, b, a)

class IcueController:
    def __init__(self, key_ids, color_rgba, refresh_enabled=False):
        self.sdk = CueSdk()
        self.device_id = None

        self._ready = threading.Event()
        self._last_state = None
        self._lock = threading.Lock()

        self.key_ids = key_ids
        self.color = color_rgba

        self._refresh_enabled = refresh_enabled
        self._refresh_thread = None
        self._want_on = False
        self._running = True

    def _on_state_changed(self, evt):
        state = getattr(evt, "state", evt)
        self._last_state = state
        if state == CorsairSessionState.CSS_Connected:
            self._ready.set()

    def ensure_connected(self, timeout_sec=10):
        with self._lock:
            if self.device_id is not None:
                return

        self._ready.clear()
        err = self.sdk.connect(self._on_state_changed)
        if err != CorsairError.CE_Success:
            raise RuntimeError(f"connect failed: {err}")

        if not self._ready.wait(timeout=timeout_sec):
            raise RuntimeError(f"SDK not connected after {timeout_sec}s; last_state={self._last_state}")

        devices, err = self.sdk.get_devices(
            CorsairDeviceFilter(device_type_mask=CorsairDeviceType.CDT_Keyboard)
        )
        if err != CorsairError.CE_Success or not devices:
            raise RuntimeError(f"get_devices failed: {err}")

        with self._lock:
            self.device_id = devices[0].device_id

    def _set_keys_once(self, rgba):
        did = self.device_id
        r, g, b, a = rgba
        leds = [CorsairLedColor(kid, r, g, b, a) for kid in self.key_ids]
        err = self.sdk.set_led_colors(did, leds)
        if err != CorsairError.CE_Success:
            raise RuntimeError(f"set_led_colors failed: {err}")

    def heb(self):
        self.ensure_connected()
        did = self.device_id

        err = self.sdk.request_control(did, CorsairAccessLevel.CAL_Shared)
        if err != CorsairError.CE_Success:
            err2 = self.sdk.request_control(did, CorsairAccessLevel.CAL_ExclusiveLightingControl)
            if err2 != CorsairError.CE_Success:
                raise RuntimeError(f"request_control failed: shared={err}, exclusive={err2}")

        self._set_keys_once(self.color)

        with self._lock:
            self._want_on = True
        self._maybe_start_refresher()

    def eng(self):
        with self._lock:
            did = self.device_id

        if did is None:
            return

        # Clear overlay then release
        try:
            self._set_keys_once((0, 0, 0, 0))
        except Exception:
            pass

        try:
            self.sdk.release_control(did)
            time.sleep(0.05)
        finally:
            with self._lock:
                self._want_on = False

    def _maybe_start_refresher(self):
        if not self._refresh_enabled:
            return
        if self._refresh_thread and self._refresh_thread.is_alive():
            return
        self._refresh_thread = threading.Thread(target=self._refresh_loop, daemon=True)
        self._refresh_thread.start()

    def _refresh_loop(self):
        while self._running:
            with self._lock:
                want = self._want_on
                did = self.device_id
            if want and did is not None:
                try:
                    self._set_keys_once(self.color)
                except Exception:
                    pass
                time.sleep(0.25)
            else:
                time.sleep(0.5)

    def status(self):
        with self._lock:
            return {
                "connected": self.device_id is not None,
                "device_id": self.device_id,
                "want_on": self._want_on,
                "refresh_enabled": self._refresh_enabled,
                "last_state": str(self._last_state),
                "keys": len(self.key_ids),
                "color": list(self.color),
            }

class Api:
    def __init__(self, ctrl: IcueController):
        self.ctrl = ctrl

    @cherrypy.expose
    @cherrypy.tools.json_out()
    def status(self):
        return {"ok": True, "status": self.ctrl.status()}

    @cherrypy.expose
    @cherrypy.tools.json_out()
    def heb(self):
        self.ctrl.heb()
        return {"ok": True}

    @cherrypy.expose
    @cherrypy.tools.json_out()
    def eng(self):
        self.ctrl.eng()
        return {"ok": True}

def main():
    cfg = load_config()

    host = cfg["Connection"].get("host", "127.0.0.1")
    port = int(cfg["Connection"].get("port", 47655))

    heb_cfg = cfg["Hebrew"]
    key_ids = resolve_key_ids(heb_cfg.get("keys", []))
    if not key_ids:
        raise ValueError("Hebrew.keys must contain at least one key")
    color = normalize_rgba(heb_cfg.get("color", [0, 0, 255, 255]))
    refresh_enabled = bool(heb_cfg.get("refresh_enabled", False))

    ctrl = IcueController(key_ids, color, refresh_enabled=refresh_enabled)
    api = Api(ctrl)

    cherrypy.config.update({
        "server.socket_host": host,
        "server.socket_port": port,
        "log.screen": True,
        "engine.autoreload.on": False,
        "request.process_request_body": False
    })

    print(f"Loaded {CONFIG_PATH}")
    print(f"Listening on http://{host}:{port}")
    cherrypy.quickstart(api, "/")

if __name__ == "__main__":
    main()
