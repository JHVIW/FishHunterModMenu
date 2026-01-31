"""
Fish Hunters - Memory Scanner & Editor
Runtime memory scanner for Fish Hunters: Most Lethal Fishing Simulator.
Requires: pip install pymem
Run as Administrator.
"""

import ctypes
import ctypes.wintypes
import struct
import threading
import tkinter as tk
from tkinter import ttk, messagebox

import pymem

PROCESS_NAME = "FishTmp.exe"


class MemoryScanner:
    MEM_COMMIT = 0x1000
    PAGE_READWRITE = 0x04
    PAGE_WRITECOPY = 0x08
    PAGE_EXECUTE_READWRITE = 0x40
    PAGE_EXECUTE_WRITECOPY = 0x80
    READABLE = PAGE_READWRITE | PAGE_WRITECOPY | PAGE_EXECUTE_READWRITE | PAGE_EXECUTE_WRITECOPY

    class MEMORY_BASIC_INFORMATION(ctypes.Structure):
        _fields_ = [
            ("BaseAddress", ctypes.c_void_p),
            ("AllocationBase", ctypes.c_void_p),
            ("AllocationProtect", ctypes.wintypes.DWORD),
            ("RegionSize", ctypes.c_size_t),
            ("State", ctypes.wintypes.DWORD),
            ("Protect", ctypes.wintypes.DWORD),
            ("Type", ctypes.wintypes.DWORD),
        ]

    FORMATS = {
        "i16": "<h", "i32": "<i", "i64": "<q",
        "f32": "<f", "f64": "<d",
    }

    def __init__(self, pm: pymem.Pymem):
        self.pm = pm
        self.handle = pm.process_handle
        self.addresses: list[int] = []
        self.snapshot: dict[int, bytes] = {}
        self._fmt = "<i"
        self._dtype = "i32"

    def _regions(self):
        kernel32 = ctypes.windll.kernel32
        mbi = self.MEMORY_BASIC_INFORMATION()
        mbi_size = ctypes.sizeof(mbi)
        addr = 0
        while addr < 0x7FFFFFFFFFFF:
            if kernel32.VirtualQueryEx(self.handle, ctypes.c_void_p(addr), ctypes.byref(mbi), mbi_size) == 0:
                break
            base = mbi.BaseAddress or 0
            size = mbi.RegionSize or 0
            if mbi.State == self.MEM_COMMIT and mbi.Protect & self.READABLE and not mbi.Protect & 0x100:
                yield base, size
            addr = base + max(size, 0x1000)

    def _set_type(self, dtype: str):
        self._dtype = dtype
        self._fmt = self.FORMATS[dtype]

    def _pack(self, value):
        return struct.pack(self._fmt, float(value) if "f" in self._dtype else int(value))

    def first_scan(self, value, dtype="i32", progress_cb=None) -> int:
        self._set_type(dtype)
        target = self._pack(value)
        self.addresses = []
        self.snapshot = {}
        regions = list(self._regions())
        for idx, (base, size) in enumerate(regions):
            if progress_cb and idx % 200 == 0:
                progress_cb(idx / len(regions))
            try:
                buf = self.pm.read_bytes(base, size)
            except Exception:
                continue
            offset = 0
            while (offset := buf.find(target, offset)) != -1:
                self.addresses.append(base + offset)
                offset += 1
        if progress_cb:
            progress_cb(1.0)
        return len(self.addresses)

    def snapshot_scan(self, dtype="i32", progress_cb=None) -> int:
        self._set_type(dtype)
        size = struct.calcsize(self._fmt)
        self.addresses = []
        self.snapshot = {}
        regions = list(self._regions())
        for idx, (base, region_size) in enumerate(regions):
            if progress_cb and idx % 200 == 0:
                progress_cb(idx / len(regions))
            try:
                buf = self.pm.read_bytes(base, region_size)
            except Exception:
                continue
            for offset in range(0, len(buf) - size + 1, size):
                addr = base + offset
                self.addresses.append(addr)
                self.snapshot[addr] = buf[offset:offset + size]
        if progress_cb:
            progress_cb(1.0)
        return len(self.addresses)

    def next_scan(self, value) -> int:
        target = self._pack(value)
        size = struct.calcsize(self._fmt)
        self.addresses = [a for a in self.addresses if self._safe_read(a, size) == target]
        return len(self.addresses)

    def filter_scan(self, mode: str, value=None) -> int:
        fmt, size = self._fmt, struct.calcsize(self._fmt)
        surviving, new_snap = [], {}
        for addr in self.addresses:
            raw = self._safe_read(addr, size)
            if raw is None:
                continue
            if mode == "exact":
                if raw == self._pack(value):
                    surviving.append(addr)
                    new_snap[addr] = raw
            elif (old := self.snapshot.get(addr)) is not None:
                cur, prev = struct.unpack(fmt, raw)[0], struct.unpack(fmt, old)[0]
                keep = (
                    (mode == "changed" and cur != prev) or
                    (mode == "unchanged" and cur == prev) or
                    (mode == "increased" and cur > prev) or
                    (mode == "decreased" and cur < prev)
                )
                if keep:
                    surviving.append(addr)
                    new_snap[addr] = raw
        self.addresses, self.snapshot = surviving, new_snap
        return len(surviving)

    def read_value(self, addr: int):
        size = struct.calcsize(self._fmt)
        return struct.unpack(self._fmt, self.pm.read_bytes(addr, size))[0]

    def write_value(self, addr: int, value):
        data = self._pack(value)
        self.pm.write_bytes(addr, data, len(data))

    def _safe_read(self, addr, size):
        try:
            return self.pm.read_bytes(addr, size)
        except Exception:
            return None


class App:
    BG = "#1a1a2e"
    BG2 = "#16213e"
    ACCENT = "#0f3460"
    HL = "#e94560"
    FG = "#eaeaea"
    DIM = "#8888aa"
    GREEN = "#53d769"

    def __init__(self):
        self.pm = None
        self.scanner = None
        self.frozen: dict[int, threading.Event] = {}

        self.root = tk.Tk()
        self.root.title("Fish Hunters - Memory Scanner")
        self.root.geometry("620x700")
        self.root.configure(bg=self.BG)
        self.root.resizable(False, False)

        self._style()
        self._header()
        self._scanner_ui()
        self._results_ui()
        self._actions_ui()
        self._connect()

    def _style(self):
        s = ttk.Style()
        s.theme_use("clam")
        s.configure("Treeview", background=self.BG, foreground=self.FG,
                     fieldbackground=self.BG, font=("Consolas", 10))
        s.configure("Treeview.Heading", background=self.ACCENT,
                     foreground="white", font=("Segoe UI", 10, "bold"))
        s.configure("TCombobox", fieldbackground=self.BG,
                     background=self.ACCENT, foreground=self.FG)
        s.configure("TProgressbar", troughcolor=self.BG, background=self.HL)

    def _header(self):
        h = tk.Frame(self.root, bg=self.HL, height=48)
        h.pack(fill="x")
        h.pack_propagate(False)
        tk.Label(h, text="FISH HUNTERS", font=("Segoe UI", 16, "bold"),
                 bg=self.HL, fg="white").pack(side="left", padx=16)
        tk.Label(h, text="MEMORY SCANNER", font=("Segoe UI", 16),
                 bg=self.HL, fg="#ffccd5").pack(side="left")
        self.status = tk.StringVar(value="Connecting...")
        tk.Label(h, textvariable=self.status, font=("Segoe UI", 10),
                 bg=self.HL, fg="white").pack(side="right", padx=16)

    def _scanner_ui(self):
        f = tk.LabelFrame(self.root, text=" Value Scanner ", font=("Segoe UI", 11, "bold"),
                          bg=self.BG2, fg=self.HL, bd=0, padx=12, pady=10)
        f.pack(fill="x", padx=16, pady=(12, 6))

        r1 = tk.Frame(f, bg=self.BG2)
        r1.pack(fill="x", pady=(0, 6))
        tk.Label(r1, text="Value:", font=("Segoe UI", 10), bg=self.BG2, fg=self.FG).pack(side="left")
        self.val_entry = tk.Entry(r1, font=("Consolas", 12), width=15, bg=self.BG,
                                  fg=self.HL, insertbackground=self.HL, bd=0)
        self.val_entry.pack(side="left", padx=(8, 12))
        tk.Label(r1, text="Type:", font=("Segoe UI", 10), bg=self.BG2, fg=self.FG).pack(side="left")
        self.dtype = tk.StringVar(value="i32")
        ttk.Combobox(r1, textvariable=self.dtype, width=6,
                     values=["i16", "i32", "i64", "f32", "f64"], state="readonly").pack(side="left", padx=8)

        r2 = tk.Frame(f, bg=self.BG2)
        r2.pack(fill="x", pady=(0, 4))
        self.scan_btn = self._btn(r2, "First Scan", self.HL, self._first_scan)
        self.next_btn = self._btn(r2, "Next Scan", self.ACCENT, self._next_scan, state="disabled")
        self._btn(r2, "Reset", self.BG, self._reset, fg=self.DIM)

        r3 = tk.Frame(f, bg=self.BG2)
        r3.pack(fill="x", pady=(2, 4))
        tk.Label(r3, text="Filter:", font=("Segoe UI", 9), bg=self.BG2, fg=self.DIM).pack(side="left")
        for label, mode in [("Changed", "changed"), ("Unchanged", "unchanged"),
                            ("Increased", "increased"), ("Decreased", "decreased")]:
            self._btn(r3, label, self.ACCENT, lambda m=mode: self._filter(m), size=9, px=8, py=2)

        self.scan_info = tk.StringVar()
        tk.Label(f, textvariable=self.scan_info, font=("Segoe UI", 10),
                 bg=self.BG2, fg=self.DIM).pack(anchor="w")
        self.progress = ttk.Progressbar(f, length=200, mode="determinate")
        self.progress.pack(fill="x", pady=(4, 0))

    def _results_ui(self):
        f = tk.LabelFrame(self.root, text=" Results ", font=("Segoe UI", 11, "bold"),
                          bg=self.BG2, fg=self.HL, bd=0, padx=12, pady=10)
        f.pack(fill="both", expand=True, padx=16, pady=6)
        self.tree = ttk.Treeview(f, columns=("addr", "value", "status"),
                                 show="headings", height=10)
        self.tree.heading("addr", text="Address")
        self.tree.heading("value", text="Value")
        self.tree.heading("status", text="Status")
        self.tree.column("addr", width=180)
        self.tree.column("value", width=120)
        self.tree.column("status", width=100)
        sb = ttk.Scrollbar(f, orient="vertical", command=self.tree.yview)
        self.tree.configure(yscrollcommand=sb.set)
        self.tree.pack(side="left", fill="both", expand=True)
        sb.pack(side="right", fill="y")

    def _actions_ui(self):
        f = tk.LabelFrame(self.root, text=" Actions ", font=("Segoe UI", 11, "bold"),
                          bg=self.BG2, fg=self.HL, bd=0, padx=12, pady=10)
        f.pack(fill="x", padx=16, pady=(6, 12))
        r = tk.Frame(f, bg=self.BG2)
        r.pack(fill="x")
        tk.Label(r, text="New value:", font=("Segoe UI", 10), bg=self.BG2, fg=self.FG).pack(side="left")
        self.new_val = tk.Entry(r, font=("Consolas", 12), width=15, bg=self.BG,
                                fg=self.GREEN, insertbackground=self.GREEN, bd=0)
        self.new_val.pack(side="left", padx=(8, 12))
        self._btn(r, "Write", self.GREEN, self._write, fg="black")
        self._btn(r, "Freeze", "#4fc3f7", self._freeze, fg="black")
        self._btn(r, "Refresh", self.ACCENT, self._refresh)

    def _btn(self, parent, text, bg, cmd, fg="white", state="normal", size=10, px=14, py=5):
        b = tk.Button(parent, text=text, font=("Segoe UI", size, "bold"), bg=bg, fg=fg,
                      activebackground=bg, bd=0, padx=px, pady=py, command=cmd, state=state)
        b.pack(side="left", padx=(6, 0))
        return b

    def _connect(self):
        try:
            self.pm = pymem.Pymem(PROCESS_NAME)
            self.scanner = MemoryScanner(self.pm)
            self.status.set(f"Connected (PID {self.pm.process_id})")
        except Exception as e:
            self.status.set("Not connected")
            messagebox.showerror("Error", f"Cannot attach to {PROCESS_NAME}:\n{e}")

    def _parse(self, entry):
        t = entry.get().strip()
        return float(t) if "f" in self.dtype.get() else int(t)

    def _first_scan(self):
        if not self.scanner:
            return
        text = self.val_entry.get().strip()
        has_val = len(text) > 0
        if has_val:
            try:
                value = self._parse(self.val_entry)
            except ValueError:
                return messagebox.showwarning("Error", "Enter a valid number.")
        self.scan_btn.config(state="disabled")
        self.scan_info.set("Scanning..." if has_val else "Snapshot scan (all values)...")

        def run():
            cb = lambda p: self.root.after(0, lambda: self.progress.config(value=p * 100))
            count = (self.scanner.first_scan(value, self.dtype.get(), cb) if has_val
                     else self.scanner.snapshot_scan(self.dtype.get(), cb))
            self.root.after(0, lambda: self._done(count))

        threading.Thread(target=run, daemon=True).start()

    def _next_scan(self):
        if not self.scanner:
            return
        text = self.val_entry.get().strip()
        if not text:
            return messagebox.showwarning("Error", "Enter a value, or use the filter buttons.")
        try:
            value = self._parse(self.val_entry)
        except ValueError:
            return messagebox.showwarning("Error", "Enter a valid number.")
        count = (self.scanner.filter_scan("exact", value) if self.scanner.snapshot
                 else self.scanner.next_scan(value))
        self._done(count)

    def _filter(self, mode):
        if not self.scanner or not self.scanner.addresses:
            return messagebox.showinfo("Info", "Run a First Scan first.")
        self.scan_info.set("Filtering...")
        self._done(self.scanner.filter_scan(mode))

    def _done(self, count):
        self.scan_btn.config(state="normal")
        self.next_btn.config(state="normal")
        self.scan_info.set(f"{count:,} addresses found")
        self.progress.config(value=100)
        self._refresh()

    def _reset(self):
        if self.scanner:
            self.scanner.addresses = []
            self.scanner.snapshot = {}
        self.next_btn.config(state="disabled")
        self.scan_info.set("")
        self.progress.config(value=0)
        for ev in self.frozen.values():
            ev.set()
        self.frozen.clear()
        self._refresh()

    def _refresh(self):
        self.tree.delete(*self.tree.get_children())
        if not self.scanner:
            return
        for addr in self.scanner.addresses[:200]:
            try:
                val = self.scanner.read_value(addr)
            except Exception:
                val = "?"
            st = "Frozen" if addr in self.frozen else ""
            self.tree.insert("", "end", iid=str(addr), values=(f"0x{addr:X}", val, st))
        if len(self.scanner.addresses) > 200:
            self.tree.insert("", "end", values=(f"... and {len(self.scanner.addresses) - 200} more", "", ""))

    def _selected(self):
        addrs = []
        for item in self.tree.selection():
            try:
                addrs.append(int(item))
            except ValueError:
                pass
        return addrs

    def _write(self):
        addrs = self._selected()
        if not addrs:
            return messagebox.showinfo("Info", "Select an address first.")
        try:
            value = self._parse(self.new_val)
        except ValueError:
            return messagebox.showwarning("Error", "Enter a valid number.")
        for addr in addrs:
            try:
                self.scanner.write_value(addr, value)
            except Exception as e:
                messagebox.showerror("Error", f"Write failed at 0x{addr:X}:\n{e}")
        self._refresh()

    def _freeze(self):
        addrs = self._selected()
        if not addrs:
            return messagebox.showinfo("Info", "Select an address first.")
        for addr in addrs:
            if addr in self.frozen:
                self.frozen.pop(addr).set()
            else:
                try:
                    val = self._parse(self.new_val) if self.new_val.get().strip() else self.scanner.read_value(addr)
                except Exception:
                    val = self.scanner.read_value(addr)
                ev = threading.Event()
                self.frozen[addr] = ev

                def loop(a=addr, v=val, e=ev):
                    while not e.is_set():
                        try:
                            self.scanner.write_value(a, v)
                        except Exception:
                            break
                        e.wait(0.1)

                threading.Thread(target=loop, daemon=True).start()
        self._refresh()

    def run(self):
        self.root.mainloop()
        for ev in self.frozen.values():
            ev.set()


if __name__ == "__main__":
    App().run()
