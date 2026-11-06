#!/usr/bin/env python3
"""
G350 screen grabber.

Captures the console's display and writes raw BGRA frames to stdout using a
small length-prefixed protocol, so the reader never has to guess boundaries:

    G350FRAME <width> <height> <nbytes>\n
    <nbytes of BGRA pixels>

Two capture backends, in order:

 1. DRM/KMS via libdrm. Needed for EmulationStation and RetroArch/SDL2, which
    render through DRM rather than fbdev. The buffer is mapped, copied, and
    unmapped immediately so games are never blocked or left with a pinned
    CMA buffer.

 2. /dev/fb0. Used for the plain text console and as a fallback when DRM is
    unavailable.

Neither backend needs root: /dev/fb0 is root:video rw-rw---- and /dev/dri/card0
is the same, and the stock 'ark' user is in the video group. Opening DRM
read-only avoids contesting DRM mastership with a running game.

Modes:
    --once            write exactly one frame and exit (screenshots)
    --fps N           frames per second when streaming (default 10)
    --loop            keep streaming until stdin closes or the pipe breaks
"""

import argparse
import ctypes
import mmap
import os
import sys
import time

# ----------------------------------------------------------------- DRM types


class drmModeModeInfo(ctypes.Structure):
    _fields_ = [
        ("clock", ctypes.c_uint32),
        ("hdisplay", ctypes.c_uint16),
        ("hsync_start", ctypes.c_uint16),
        ("hsync_end", ctypes.c_uint16),
        ("htotal", ctypes.c_uint16),
        ("hskew", ctypes.c_uint16),
        ("vdisplay", ctypes.c_uint16),
        ("vsync_start", ctypes.c_uint16),
        ("vsync_end", ctypes.c_uint16),
        ("vtotal", ctypes.c_uint16),
        ("vscan", ctypes.c_uint16),
        ("vrefresh", ctypes.c_uint32),
        ("flags", ctypes.c_uint32),
        ("type", ctypes.c_uint32),
        ("name", ctypes.c_char * 32),
    ]


class drmModeCrtc(ctypes.Structure):
    _fields_ = [
        ("crtc_id", ctypes.c_uint32),
        ("buffer_id", ctypes.c_uint32),
        ("x", ctypes.c_uint32),
        ("y", ctypes.c_uint32),
        ("width", ctypes.c_uint32),
        ("height", ctypes.c_uint32),
        ("mode_valid", ctypes.c_int),
        ("mode", drmModeModeInfo),
        ("gamma_size", ctypes.c_int),
    ]


class drmModeFB(ctypes.Structure):
    _fields_ = [
        ("fb_id", ctypes.c_uint32),
        ("width", ctypes.c_uint32),
        ("height", ctypes.c_uint32),
        ("pitch", ctypes.c_uint32),
        ("bpp", ctypes.c_uint32),
        ("depth", ctypes.c_uint32),
        ("handle", ctypes.c_uint32),
    ]


class drmModeRes(ctypes.Structure):
    _fields_ = [
        ("count_fbs", ctypes.c_int),
        ("fbs", ctypes.POINTER(ctypes.c_uint32)),
        ("count_crtcs", ctypes.c_int),
        ("crtcs", ctypes.POINTER(ctypes.c_uint32)),
        ("count_connectors", ctypes.c_int),
        ("connectors", ctypes.POINTER(ctypes.c_uint32)),
        ("count_encoders", ctypes.c_int),
        ("encoders", ctypes.POINTER(ctypes.c_uint32)),
        ("min_width", ctypes.c_uint32),
        ("max_width", ctypes.c_uint32),
        ("min_height", ctypes.c_uint32),
        ("max_height", ctypes.c_uint32),
    ]


# ----------------------------------------------------------------- backends


class DrmCapture:
    """Copies the current scanout buffer without pinning it."""

    def __init__(self):
        self.ok = False
        self.fd = -1
        self.crtc_id = 0
        self.lib = None
        try:
            self.lib = ctypes.CDLL("libdrm.so.2")
            self.lib.drmModeGetResources.restype = ctypes.POINTER(drmModeRes)
            self.lib.drmModeGetCrtc.restype = ctypes.POINTER(drmModeCrtc)
            self.lib.drmModeGetFB.restype = ctypes.POINTER(drmModeFB)
            self.lib.drmModeFreeResources.argtypes = [ctypes.POINTER(drmModeRes)]
            self.lib.drmModeFreeCrtc.argtypes = [ctypes.POINTER(drmModeCrtc)]
            self.lib.drmModeFreeFB.argtypes = [ctypes.POINTER(drmModeFB)]
            self.lib.drmPrimeHandleToFD.restype = ctypes.c_int
        except Exception:
            return

        try:
            # Read-only: never takes mastership away from a running game.
            fd = os.open("/dev/dri/card0", os.O_RDONLY | os.O_CLOEXEC)
            res = self.lib.drmModeGetResources(fd)
            if not res:
                os.close(fd)
                return
            try:
                if res.contents.count_crtcs > 0:
                    self.crtc_id = res.contents.crtcs[0]
                    self.fd = fd
                    self.ok = self.crtc_id != 0
            finally:
                self.lib.drmModeFreeResources(res)
            if not self.ok:
                os.close(fd)
        except Exception:
            self.ok = False

    def grab(self):
        """Returns (bytes, width, height, pitch) or None."""
        if not self.ok:
            return None
        crtc = None
        try:
            crtc = self.lib.drmModeGetCrtc(self.fd, self.crtc_id)
            if not crtc:
                return None
            buffer_id = crtc.contents.buffer_id
        finally:
            if crtc:
                self.lib.drmModeFreeCrtc(crtc)

        if not buffer_id:
            return None

        fb = self.lib.drmModeGetFB(self.fd, buffer_id)
        if not fb:
            return None
        try:
            handle = fb.contents.handle
            pitch = fb.contents.pitch
            width = fb.contents.width
            height = fb.contents.height
        finally:
            self.lib.drmModeFreeFB(fb)

        if not handle or not pitch or not width or not height:
            return None

        pfd = ctypes.c_int(-1)
        ret = self.lib.drmPrimeHandleToFD(self.fd, handle, 0, ctypes.byref(pfd))
        if ret != 0 or pfd.value < 0:
            return None

        try:
            size = pitch * height
            mm = mmap.mmap(pfd.value, size, mmap.MAP_SHARED, mmap.PROT_READ)
            try:
                return (bytes(mm[:size]), width, height, pitch)
            finally:
                mm.close()
        except Exception:
            return None
        finally:
            try:
                os.close(pfd.value)
            except OSError:
                pass


class FbCapture:
    """Reads /dev/fb0 directly."""

    def __init__(self):
        self.ok = False
        self.width = self.height = 0
        self.pitch = 0
        try:
            self.width, self.height = [
                int(v) for v in
                open("/sys/class/graphics/fb0/virtual_size").read().strip().split(",")
            ]
            self.pitch = int(open("/sys/class/graphics/fb0/stride").read().strip())
            self.file = open("/dev/fb0", "rb")
            self.ok = True
        except Exception:
            self.ok = False

    def grab(self):
        if not self.ok:
            return None
        try:
            self.file.seek(0)
            data = self.file.read(self.pitch * self.height)
            if len(data) != self.pitch * self.height:
                return None
            return (data, self.width, self.height, self.pitch)
        except Exception:
            return None


# ------------------------------------------------------------------- output


def pack(data, width, height, pitch):
    """Trim any row padding so the consumer always gets width*4 per row."""
    row = width * 4
    if pitch == row:
        return data
    out = bytearray(row * height)
    for y in range(height):
        src = y * pitch
        out[y * row:(y + 1) * row] = data[src:src + row]
    return bytes(out)


def emit(out, data, width, height, pitch):
    payload = pack(data, width, height, pitch)
    out.write(b"G350FRAME %d %d %d\n" % (width, height, len(payload)))
    out.write(payload)
    out.flush()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--once", action="store_true")
    ap.add_argument("--fps", type=float, default=10.0)
    args = ap.parse_args()

    drm = DrmCapture()
    fb = FbCapture()
    out = sys.stdout.buffer
    delay = 1.0 / max(1.0, args.fps)

    last = None
    last_dims = (0, 0, 0)

    while True:
        start = time.time()
        frame = drm.grab() if drm.ok else None
        if frame is None:
            frame = fb.grab() if fb.ok else None

        if frame is not None:
            data, w, h, pitch = frame
            # A fully blank framebuffer is normal while DRM owns the display, so
            # keep the last good frame instead of streaming black. The check must
            # cover the WHOLE frame: sampling only the first few hundred bytes
            # wrongly rejects real frames whose top rows happen to be black.
            if any(data):
                last = data
                last_dims = (w, h, pitch)

        if last is not None:
            try:
                emit(out, last, *last_dims)
            except (BrokenPipeError, OSError):
                break

        if args.once:
            break

        spent = time.time() - start
        if spent < delay:
            time.sleep(delay - spent)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        pass
