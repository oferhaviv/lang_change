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

HOST = "127.0.0.1"
PORT = 47655

Q_ID = CorsairLedId_Keyboard.CLK_Q   # your enum uses CLK_Q
BLUE = (0, 0, 255, 255)              # RGBA (alpha 255 = visible)

class IcueController:
    def __init__(self):
        self.sdk = CueSdk()
        self.device_id = None

        self._ready = threading.Event()
        self._last_state = None
        self._lock = threading.Lock()

        # Optional refresher (off by default)
        self._refresh_enabled = False
        self._refresh_thread = None
        self._want_blue = False
        self._running = True

    # ---------- SDK connection ----------
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

    # ---------- Lighting actions ----------
    def _set_q_blue_once(self):
        did = self.device_id
        led = CorsairLedColor(Q_ID, *BLUE)
        err = self.sdk.set_led_colors(did, [led])
        if err != CorsairError.CE_Success:
            raise RuntimeError(f"set_led_colors failed: {err}")

    def heb(self):
        self.ensure_connected()
        did = self.device_id

        # Request shared overlay control
        err = self.sdk.request_control(did, CorsairAccessLevel.CAL_Shared)
        if err != CorsairError.CE_Success:
            # Fallback to exclusive
            err2 = self.sdk.request_control(did, CorsairAccessLevel.CAL_ExclusiveLightingControl)
            if err2 != CorsairError.CE_Success:
                raise RuntimeError(f"request_control failed: shared={err}, exclusive={err2}")

        self._set_q_blue_once()

        # If you later need to keep it above animations, enable refresher:
        with self._lock:
            self._want_blue = True
        self._maybe_start_refresher()

    def eng(self):
        # Fail-safe release (works even if not connected yet)
        with self._lock:
            print (f'system is locked')
            did = self.device_id

        if did is None:
            print('no device id')
            return
        try:
            self.sdk.set_led_colors(did, [CorsairLedColor(Q_ID, 0, 0, 0, 0)])
        except Exception:
            pass
        try:
            self.sdk.release_control(did)
            print('released')
            time.sleep(0.05)
        finally:
            with self._lock:
                self._want_blue = False

    # ---------- Optional refresher ----------
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
                want = self._want_blue
                did = self.device_id
            if want and did is not None:
                # keep Q blue over animations
                try:
                    self._set_q_blue_once()
                except Exception:
                    pass
                time.sleep(0.25)  # 4Hz, very light
            else:
                time.sleep(0.5)

    def status(self):
        with self._lock:
            return {
                "connected": self.device_id is not None,
                "device_id": self.device_id,
                "want_blue": self._want_blue,
                "refresh_enabled": self._refresh_enabled,
                "last_state": str(self._last_state),
            }

CTRL = IcueController()

class Api:
    @cherrypy.expose
    @cherrypy.tools.json_out()
    def status(self):
        try:
            return {"ok": True, "status": CTRL.status()}
        except Exception as e:
            cherrypy.response.status = 500
            return {"ok": False, "error": str(e)}

    @cherrypy.expose
    @cherrypy.tools.json_out()
    def heb(self):
        try:
            CTRL.heb()
            return {"ok": True}
        except Exception as e:
            cherrypy.response.status = 500
            return {"ok": False, "error": str(e)}

    @cherrypy.expose
    @cherrypy.tools.json_out()
    def eng(self):
        try:
            CTRL.eng()
            return {"ok": True}
        except Exception as e:
            cherrypy.response.status = 500
            return {"ok": False, "error": str(e)}

def main():
    cherrypy.config.update({
        "server.socket_host": HOST,
        "server.socket_port": PORT,
        "log.screen": True,
        "engine.autoreload.on": False,
        "request.process_request_body": False,

    })


    cherrypy.quickstart(Api(), "/")

if __name__ == "__main__":
    main()
