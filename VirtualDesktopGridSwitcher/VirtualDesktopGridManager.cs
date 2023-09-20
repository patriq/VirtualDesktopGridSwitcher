﻿using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using GlobalHotkeyWithDotNET;
using VirtualDesktopGridSwitcher.Settings;
using WindowsDesktop;

namespace VirtualDesktopGridSwitcher {

    class VirtualDesktopGridManager: IDisposable {

        private SettingValues settings;
        private SysTrayProcess sysTrayProcess;

        private Dictionary<Guid, int> desktopNumberLookup;
        private Guid[] desktopIdLookup;
        private IntPtr[] activeWindows;
        private IntPtr[] lastActiveBrowserWindows;
        private IntPtr lastMoveOnNewWindowHwnd;
        private int lastMoveOnNewWindowOpenedFromDesktop;
        private DateTime lastMoveOnNewWindowOpenedTime = DateTime.MinValue;

        private IntPtr movingWindow = IntPtr.Zero;
        private IntPtr activatingBrowserWindow = IntPtr.Zero;

        private WinAPI.WinEventDelegate foregroundWindowChangedDelegate;
        private IntPtr fgWindowHook;

        Mutex callbackMutex = new Mutex();

        public VirtualDesktopGridManager(SysTrayProcess sysTrayProcess, SettingValues settings)
        {
            this.settings = settings;
            this.sysTrayProcess = sysTrayProcess;

            foregroundWindowChangedDelegate = new WinAPI.WinEventDelegate(ForegroundWindowChanged);
            fgWindowHook = WinAPI.SetWinEventHook(WinAPI.EVENT_SYSTEM_FOREGROUND, WinAPI.EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, foregroundWindowChangedDelegate, 0, 0, WinAPI.WINEVENT_OUTOFCONTEXT);

            Start();
        }

        public void Dispose()
        {
            Stop();

            WinAPI.UnhookWinEvent(fgWindowHook);
        }

        private bool Start() {
            try {
                var curDesktops = VirtualDesktop.GetDesktops().ToList();

                while (curDesktops.Count() > settings.Rows * settings.Columns) {
                    var last = curDesktops.Last();
                    last.Remove();
                    curDesktops.Remove(last);
                }

                while (curDesktops.Count() < settings.Rows * settings.Columns) {
                    curDesktops.Add(VirtualDesktop.Create());
                }

                var desktops = VirtualDesktop.GetDesktops();
                desktopIdLookup = desktops.Select(d => d.Id).ToArray();
                desktopNumberLookup = new Dictionary<Guid, int>();
                int index = 0;
                desktops.ToList().ForEach(d => desktopNumberLookup.Add(d.Id, index++));

                this._current = desktopNumberLookup[VirtualDesktop.Current.Id];

                activeWindows = new IntPtr[desktops.Length];
                lastActiveBrowserWindows = new IntPtr[desktops.Length];

                VirtualDesktop.CurrentChanged += VirtualDesktop_CurrentChanged;
            } catch { }

            sysTrayProcess.ShowIconForDesktop(Current);

            settings.Apply += Restart;

            try {
                RegisterHotKeys();
            } catch {
                return false;
            }

            return true;
        }

        private void Stop() {
            settings.Apply -= Restart;
            UnregisterHotKeys();
            if (desktopIdLookup != null) {
                VirtualDesktop.CurrentChanged -= VirtualDesktop_CurrentChanged;
                desktopIdLookup = null;
                desktopNumberLookup = null;
                activeWindows = null;
                lastActiveBrowserWindows = null;
            }
        }

        public bool Restart()
        {
            Stop();
            return Start();
        }
        
        public int DesktopCount { 
            get {
                return settings.Rows * settings.Columns;
            }
        }

        private void VirtualDesktop_CurrentChanged(object sender, VirtualDesktopChangedEventArgs e) {
            var newDesktop = desktopNumberLookup[e.NewDesktop.Id];
            Debug.WriteLine("Switched to " + newDesktop);

            lock (callbackMutex) {
                this._current = newDesktop;
                sysTrayProcess.ShowIconForDesktop(Current);

                if (VirtualDesktop.Current == e.NewDesktop) {
                    DoActivateBrowser(newDesktop);
                } else {
                    Debug.WriteLine("Skipped OnDesktopChanged()");
                }
            }
        }

        private void DoActivateBrowser(int newDesktop) {
            var browserInfo = settings.GetBrowserToActivateInfo();
            if (movingWindow == IntPtr.Zero || !IsWindowDefaultBrowser(movingWindow, browserInfo)) {

                var fgHwnd = WinAPI.GetForegroundWindow();
                var lastActiveWindow = activeWindows[Current];

                if (browserInfo != null) {
                    if (lastActiveBrowserWindows[Current] != activeWindows[Current]) {
                        FindActivateBrowserWindow(lastActiveBrowserWindows[Current], browserInfo);
                    }
                }

                if (!ActivateWindow(lastActiveWindow)) {
                    if (fgHwnd != WinAPI.GetForegroundWindow()) {
                        Debug.WriteLine("Reactivate " + Current + " " + fgHwnd);
                        WinAPI.SetForegroundWindow(fgHwnd);
                    }
                }
            }

            movingWindow = IntPtr.Zero;
        }

        private class BrowserWinInfo {
            public IntPtr last;
            public IntPtr top;
            public IntPtr topOnDesktop;
        }

        private bool IterateFindTopLevelBrowserOnCurrentDesktop(IntPtr hwnd, BrowserWinInfo browserWinInfo, SettingValues.BrowserInfo browserInfo) {
            if (hwnd == IntPtr.Zero) {
                // None left to check
                return false;
            }

            browserWinInfo.last = hwnd;
            if (browserWinInfo.top == IntPtr.Zero && IsWindowDefaultBrowser(hwnd, browserInfo)) {
                browserWinInfo.top = hwnd;
                if (WindowIsOnCurrentDesktop(hwnd)) {
                    // Already top level so nothing to do
                    browserWinInfo.topOnDesktop = IntPtr.Zero;
                    return false;
                }
            }

            if (WindowIsOnCurrentDesktop(hwnd) && IsWindowDefaultBrowser(hwnd, browserInfo)) {
                browserWinInfo.topOnDesktop = hwnd;
                return false;
            }
            // Keep going
            return true;
        }

        private bool WindowIsOnCurrentDesktop(IntPtr hwnd) {
            return VirtualDesktop.FromHwnd(hwnd)?.Id == desktopIdLookup[Current];
        }

        private bool FindActivateBrowserWindow(IntPtr hwnd, SettingValues.BrowserInfo browserInfo)
        {
            if (WinAPI.IsWindow(hwnd)) {
                if (WindowIsOnCurrentDesktop(hwnd)) {
                    Debug.WriteLine("Activate Known Browser " + Current + " " + hwnd);
                    ActivateBrowserWindow(hwnd);
                    return true;
                }
            }

            // Our last active record was not valid so find topmost on this desktop 
            BrowserWinInfo browserWinInfo = new BrowserWinInfo(); ;
            bool notFinished = true;
            do {
                var window = WinAPI.FindWindowEx(IntPtr.Zero, browserWinInfo.last, browserInfo.ClassName, null);
                notFinished = IterateFindTopLevelBrowserOnCurrentDesktop(window, browserWinInfo, browserInfo);
            } while (notFinished);
            if (browserWinInfo.topOnDesktop != IntPtr.Zero) {
                Debug.WriteLine("Activate Unknown Browser " + Current + " " + browserWinInfo.topOnDesktop);
                ActivateBrowserWindow(browserWinInfo.topOnDesktop);
                return true;
            }

            return false;
        }

        private void ActivateBrowserWindow(IntPtr hwnd) {
            WinAPI.WindowPlacement winInfo = new WinAPI.WindowPlacement();
            WinAPI.GetWindowPlacement(hwnd, ref winInfo);

            activatingBrowserWindow = hwnd;

            //var prevHwnd = hwnd;
            //do {
            //    prevHwnd = WinAPI.GetWindow(prevHwnd, WinAPI.GWCMD.GW_HWNDPREV);
            //} while (prevHwnd != IntPtr.Zero && VirtualDesktop.FromHwnd(prevHwnd) != desktops[Current]);

            if (hwnd != WinAPI.GetForegroundWindow()) {
                WinAPI.SetForegroundWindow(hwnd);
            }
            if (winInfo.ShowCmd == WinAPI.ShowWindowCommands.ShowMinimized) {
                WinAPI.ShowWindow(hwnd, WinAPI.ShowWindowCommands.Restore);
                WinAPI.ShowWindow(hwnd, winInfo.ShowCmd);
            }
            Thread.Sleep(50);

            //if (prevHwnd != IntPtr.Zero) {
            //    Debug.WriteLine("Browser behind " + prevHwnd + " " + GetWindowExeName(prevHwnd) + " " + GetWindowTitle(prevHwnd));
            //    WinAPI.SetWindowPos(hwnd, prevHwnd, 0, 0, 0, 0, WinAPI.SWPFlags.SWP_NOMOVE | WinAPI.SWPFlags.SWP_NOSIZE | WinAPI.SWPFlags.SWP_NOACTIVATE);
            //}
        }

        private bool ActivateWindow(IntPtr hwnd) {
            if (hwnd != IntPtr.Zero) {
                var desktop = VirtualDesktop.FromHwnd(hwnd);
                if (desktop != null && desktop == VirtualDesktop.Current) {
                    if (hwnd != WinAPI.GetForegroundWindow()) {
                        Debug.WriteLine("Activate " + Current + " " + hwnd);
                        WinAPI.SetForegroundWindow(hwnd);
                    }
                    return true;
                }
            }
            return false;
        }

        void ForegroundWindowChanged(IntPtr hWinEventHook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint dwEventThread, uint dwmsEventTime) {
            lock (callbackMutex) {

                if (desktopIdLookup != null) {

                    var windowDesktopNo = Current;
                    var windowDesktop = VirtualDesktop.FromHwnd(hwnd);
                    if (windowDesktop != null) {
                        windowDesktopNo = desktopNumberLookup[windowDesktop.Id];
                    }

                    if (IsMoveOnNewWindowType(hwnd)) {
                        if (windowDesktopNo != Current) {
                            Debug.WriteLine("Opened MoveOnNewWindow from " + Current + " to " + windowDesktopNo);
                            lastMoveOnNewWindowHwnd = hwnd;
                            lastMoveOnNewWindowOpenedFromDesktop = Current;
                            lastMoveOnNewWindowOpenedTime = DateTime.Now;
                        } else {
                            var delay = (DateTime.Now - lastMoveOnNewWindowOpenedTime).TotalMilliseconds;
                            if (delay < settings.MoveOnNewWindowDetectTimeoutMs &&
                                lastMoveOnNewWindowOpenedFromDesktop != Current) {

                                if (lastMoveOnNewWindowHwnd == hwnd || GetWindowTitle(hwnd) == ("Opening - " + GetWindowTitle(lastMoveOnNewWindowHwnd))) {
                                    Debug.WriteLine((int)delay + " Reset Move New Window Timeout");
                                    lastMoveOnNewWindowOpenedTime = DateTime.Now;
                                    return;
                                } else {

                                    lastMoveOnNewWindowHwnd = IntPtr.Zero;
                                    lastMoveOnNewWindowOpenedTime = DateTime.MinValue;

                                    Debug.WriteLine((int)delay + " Move New Window " + hwnd + " " + GetWindowTitle(hwnd) + " to " + lastMoveOnNewWindowOpenedFromDesktop);
                                    int retryTime = 0;
                                    while (!MoveWindow(hwnd, lastMoveOnNewWindowOpenedFromDesktop) && retryTime < (settings.MoveOnNewWindowDetectTimeoutMs + 300)) {
                                        // Need a little time before MoveToDesktop will recognise new window
                                        int addTime = 100;
                                        Thread.Sleep(addTime);
                                        retryTime += addTime;
                                    }

                                    return;
                                }
                            } else if (lastMoveOnNewWindowOpenedTime != DateTime.MinValue) {
                                Debug.WriteLine("Timeout New Window Move " + delay);
                                lastMoveOnNewWindowOpenedTime = DateTime.MinValue;
                            }
                        }
                    }

                    if (windowDesktop != null && movingWindow == IntPtr.Zero && hwnd != activatingBrowserWindow) {
                        activeWindows[windowDesktopNo] = hwnd;
                    }

                    Debug.WriteLine("Foreground " + Current + " " + (windowDesktop != null ? windowDesktopNo.ToString() : "?") + " " + hwnd + " " + GetWindowTitle(hwnd));

                    if (hwnd == activatingBrowserWindow || IsWindowDefaultBrowser(hwnd, settings.GetBrowserToActivateInfo())) {
                        Debug.WriteLine("Browser " + Current + " " + desktopNumberLookup[VirtualDesktop.Current.Id] + " " + windowDesktopNo + " " + hwnd);
                        lastActiveBrowserWindows[windowDesktopNo] = hwnd;
                    }

                    if (hwnd == activatingBrowserWindow) {
                        activatingBrowserWindow = IntPtr.Zero;
                    }
                }
                //ReleaseModifierKeys();
            }
        }

        private static string GetWindowTitle(IntPtr hwnd) {
            StringBuilder title = new StringBuilder(1024);
            WinAPI.GetWindowText(hwnd, title, title.Capacity);
            return title.ToString();
        }

        private static string GetWindowExeName(IntPtr hwnd) {
            uint pid = 0;
            WinAPI.GetWindowThreadProcessId(hwnd, out pid);

            IntPtr pic = WinAPI.OpenProcess(WinAPI.ProcessAccessFlags.All, true, (int)pid);
            if (pic == IntPtr.Zero) {
                return null;
            }

            StringBuilder exeDevicePath = new StringBuilder(1024);
            if (WinAPI.GetProcessImageFileName(pic, exeDevicePath, exeDevicePath.Capacity) == 0) { 
                return null;
            }
            var exeName = Path.GetFileName(exeDevicePath.ToString());

            return exeName;
        }

        private bool IsWindowDefaultBrowser(IntPtr hwnd, SettingValues.BrowserInfo browserInfo) {
            var exename = GetWindowExeName(hwnd);
            return (browserInfo != null && exename != null && exename.ToUpper() == browserInfo.ExeName.ToUpper());
        }

        private bool IsMoveOnNewWindowType(IntPtr hwnd) {
            var exename = GetWindowExeName(hwnd);
            return (
                exename != null &&
                settings.MoveOnNewWindowExeNames != null &&
                settings.MoveOnNewWindowExeNames.Any(n => n.ToUpper() == exename.ToUpper()));
        }

        private void ToggleWindowSticky(IntPtr hwnd) {
            try {
                if (VirtualDesktop.IsPinnedWindow(hwnd)) {
                    VirtualDesktop.UnpinWindow(hwnd);
                } else {
                    VirtualDesktop.PinWindow(hwnd);
                }
            } catch { }
        }

        private static bool IsWindowTopMost(IntPtr hWnd) {
            return WinAPI.GetWindowLongPtr(hWnd, WinAPI.GWL_EXSTYLE).AND(WinAPI.WS_EX_TOPMOST) == WinAPI.WS_EX_TOPMOST;
        }

        private void ToggleWindowAlwaysOnTop(IntPtr hwnd) {
            WinAPI.SetWindowPos(hwnd,
              IsWindowTopMost(hwnd) ? WinAPI.HWND_NOTOPMOST : WinAPI.HWND_TOPMOST,
              0, 0, 0, 0,
              WinAPI.SWPFlags.SWP_SHOWWINDOW | WinAPI.SWPFlags.SWP_NOSIZE | WinAPI.SWPFlags.SWP_NOMOVE);
        }

        private int _current;
        public int Current {
            get {
                return _current;
            }
            private set {
                if (desktopIdLookup != null) {
                    _current = value;
                    VirtualDesktop.FromId(desktopIdLookup[value]).Switch();
                }
            }
        }

        public int Left {
            get {
                if (ColumnOf(Current - 1) < ColumnOf(Current)) {
                    return Current - 1;
                } else {
                    if (settings.WrapAround) {
                        return Current + settings.Columns - 1;
                    } else {
                        return Current;
                    }
                }
            }
        }

        public int Right {
            get {
                if (ColumnOf(Current + 1) > ColumnOf(Current)) {
                    return Current + 1;
                } else {
                    if (settings.WrapAround) {
                        return Current - settings.Columns + 1;
                    } else {
                        return Current;
                    }
                }
            }
        }

        public int Up {
            get {
                if (RowOf(Current - settings.Columns) < RowOf(Current)) {
                    return Current - settings.Columns;
                } else {
                    if (settings.WrapAround) {
                        return ((settings.Rows-1) * settings.Columns) + ColumnOf(Current);
                    } else {
                        return Current;
                    }
                }
            }
        }

        public int Down {
            get {
                if (RowOf(Current + settings.Columns) > RowOf(Current)) {
                    return Current + settings.Columns;
                } else {
                    if (settings.WrapAround) {
                        return ColumnOf(Current);
                    } else {
                        return Current;
                    }
                }
            }
        }

        public void Switch(int index) {
            var activeHwnd = WinAPI.GetForegroundWindow();
            if (activeHwnd != activatingBrowserWindow) {
                activeWindows[Current] = activeHwnd;
            }
            WinAPI.PostMessage(activeHwnd, WinAPI.WM_KILLFOCUS, IntPtr.Zero, IntPtr.Zero);
            Debug.WriteLine("Switch Active " + Current + " (" + activeWindows[Current] + ") to " + index + " (" + activeWindows[index] + ")");
            Current = index;
        }

        public void Move(int index) {
            var hwnd = WinAPI.GetForegroundWindow();
            if (hwnd != IntPtr.Zero) {
                if (IsWindowDefaultBrowser(hwnd, settings.GetBrowserToActivateInfo())) {
                    for (int i = 0; i < lastActiveBrowserWindows.Length; ++i) {
                        if (lastActiveBrowserWindows[i] == hwnd) {
                            Debug.WriteLine("Browser " + i + " cleared");
                            lastActiveBrowserWindows[i] = IntPtr.Zero;
                        }
                    }
                }
            }

            MoveWindow(hwnd, index);
        }

        private bool MoveWindow(IntPtr hwnd, int index) {
            if (hwnd != IntPtr.Zero) {
                for (int i = 0; i < activeWindows.Length; ++i) {
                    if (activeWindows[i] == hwnd) {
                        activeWindows[i] = IntPtr.Zero;
                    }
                }

                movingWindow = hwnd;

                Debug.WriteLine("Move " + hwnd + " from " + Current + " to " + index);

                try {
                    VirtualDesktop.MoveToDesktop(hwnd, VirtualDesktop.FromId(desktopIdLookup[index]));
                    activeWindows[index] = hwnd;
                    if (hwnd != WinAPI.GetForegroundWindow()) {
                        WinAPI.SetForegroundWindow(hwnd);
                    }
                    Current = index;
                } catch {
                    return false;
                }
                return true;
            }
            return false;
        }

        private int ColumnOf(int index) {
            return ((index + settings.Columns) % settings.Columns);
        }

        private int RowOf(int index) {
            return ((index / settings.Columns) + settings.Rows) % settings.Rows;
        }

        private List<Hotkey> hotkeys;

        private void RegisterHotKeys() {
            
            hotkeys = new List<Hotkey>();

            if (settings.ArrowKeysEnabled) {
                RegisterSwitchDirHotkey(Keys.Left, delegate { this.Switch(Left); });
                RegisterSwitchDirHotkey(Keys.Right, delegate { this.Switch(Right); });
                RegisterSwitchDirHotkey(Keys.Up, delegate { this.Switch(Up); });
                RegisterSwitchDirHotkey(Keys.Down, delegate { this.Switch(Down); });

                RegisterMoveDirHotkey(Keys.Left, delegate { Move(Left); });
                RegisterMoveDirHotkey(Keys.Right, delegate { Move(Right); });
                RegisterMoveDirHotkey(Keys.Up, delegate { Move(Up); });
                RegisterMoveDirHotkey(Keys.Down, delegate { Move(Down); });
            }

            RegisterSwitchDirHotkey(settings.LeftKey, delegate { this.Switch(Left); });
            RegisterSwitchDirHotkey(settings.RightKey, delegate { this.Switch(Right); });
            RegisterSwitchDirHotkey(settings.UpKey, delegate { this.Switch(Up); });
            RegisterSwitchDirHotkey(settings.DownKey, delegate { this.Switch(Down); });

            RegisterMoveDirHotkey(settings.LeftKey, delegate { Move(Left); });
            RegisterMoveDirHotkey(settings.RightKey, delegate { Move(Right); });
            RegisterMoveDirHotkey(settings.UpKey, delegate { Move(Up); });
            RegisterMoveDirHotkey(settings.DownKey, delegate { Move(Down); });

            if (settings.NumbersEnabled) {
                for (int keyNumber = 1; keyNumber <= Math.Min(DesktopCount, 9); ++keyNumber) {
                    var desktopIndex = keyNumber - 1;
                    Keys keycode =
                        (Keys)Enum.Parse(typeof(Keys), "D" + keyNumber.ToString());

                    RegisterSwitchPosHotkey(keycode, delegate { this.Switch(desktopIndex); });
                    RegisterMovePosHotkey(keycode, delegate { this.Move(desktopIndex); });
                }
            }

            if (settings.FKeysEnabled) {
                for (int keyNumber = 1; keyNumber <= Math.Min(DesktopCount, 12); ++keyNumber) {
                    var desktopIndex = keyNumber - 1;
                    Keys keycode =
                        (Keys)Enum.Parse(typeof(Keys), "F" + keyNumber.ToString());

                    RegisterSwitchPosHotkey(keycode, delegate { this.Switch(desktopIndex); });
                    RegisterMovePosHotkey(keycode, delegate { this.Move(desktopIndex); });
                }
            }

            for (int i = 0; i < settings.DesktopKeys.Count; ++i) {
                var desktopIndex = i;
                RegisterSwitchPosHotkey(settings.DesktopKeys[i], delegate { this.Switch(desktopIndex); });
                RegisterMovePosHotkey(settings.DesktopKeys[i], delegate { this.Move(desktopIndex); });
            }

            RegisterToggleStickyHotKey();
            RegisterToggleAlwaysOnTopHotKey();
        }

        private void RegisterSwitchDirHotkey(Keys keyCode, HandledEventHandler action) {
            RegisterHotkey(keyCode, settings.SwitchDirModifiers, settings.SwitchDirEnabled, "switch by direction", action);
        }

        private void RegisterMoveDirHotkey(Keys keyCode, HandledEventHandler action) {
            RegisterHotkey(keyCode, settings.MoveDirModifiers, settings.MoveDirEnabled, "move by direction", action);
        }

        private void RegisterSwitchPosHotkey(Keys keyCode, HandledEventHandler action) {
            RegisterHotkey(keyCode, settings.SwitchPosModifiers, settings.SwitchPosEnabled, "switch by position", action);
        }

        private void RegisterMovePosHotkey(Keys keyCode, HandledEventHandler action) {
            RegisterHotkey(keyCode, settings.MovePosModifiers, settings.MovePosEnabled, "move by position", action);
        }

        private void RegisterToggleStickyHotKey() {
            RegisterHotkey(
                settings.StickyWindowHotKey.Key,
                settings.StickyWindowHotKey.Modifiers,
                settings.StickyWindowEnabled,
                "toggle sticky window",
                delegate { ToggleWindowSticky(WinAPI.GetForegroundWindow()); });
        }

        private void RegisterToggleAlwaysOnTopHotKey() {
            RegisterHotkey(
                settings.AlwaysOnTopHotkey.Key,
                settings.AlwaysOnTopHotkey.Modifiers,
                settings.AlwaysOnTopEnabled,
                "toggle window always on top",
                delegate { ToggleWindowAlwaysOnTop(WinAPI.GetForegroundWindow()); });
        }

        private void RegisterHotkey(Keys keyCode, SettingValues.Modifiers modifiers, bool enabled, string desc, HandledEventHandler action) {
            if (!enabled || keyCode == Keys.None)
                return;

            Hotkey hk = new Hotkey() {
                Control = modifiers.Ctrl,
                Windows = modifiers.Win,
                Alt = modifiers.Alt,
                Shift = modifiers.Shift,
                KeyCode = keyCode
            };
            if (hotkeys.Any(h => h.ToString() == hk.ToString())) {
                MessageBox.Show($"Hotkey {hk} has multiple assignments and will not be registered for {desc}",
                                "Warning",
                                MessageBoxButtons.OK,
                                MessageBoxIcon.Warning);

            } else {
                hk.Pressed += action;
                if (hk.Register(null)) {
                    hotkeys.Add(hk);
                } else {
                    MessageBox.Show($"Failed to register hotkey {hk} for {desc}." + Environment.NewLine +
                                    Environment.NewLine +
                                    "It is probably being used by another program - often graphics software.",
                                    "Warning",
                                    MessageBoxButtons.OK,
                                    MessageBoxIcon.Warning);
                }
            }
        }

        private void UnregisterHotKeys() {
            hotkeys.ForEach(hk => hk.Unregister());
            hotkeys = null;
        }


        private void ReleaseModifierKeys() {
            const int WM_KEYUP = 0x101;
            const int VK_SHIFT = 0x10;
            const int VK_CONTROL = 0x11;
            const int VK_MENU = 0x12;
            const int VK_LWIN = 0x5B;
            const int VK_RWIN = 0x5C;

            var activeHWnd = WinAPI.GetForegroundWindow();
            if (IsKeyPressed(WinAPI.GetAsyncKeyState(VK_MENU))) {
                WinAPI.PostMessage(activeHWnd, WM_KEYUP, (IntPtr)VK_MENU, (IntPtr)0xC0380001);
                WinAPI.PostMessage(activeHWnd, WM_KEYUP, (IntPtr)VK_MENU, (IntPtr)0xC1380001);
            }
            if (IsKeyPressed(WinAPI.GetAsyncKeyState(VK_CONTROL))) {
                WinAPI.PostMessage(activeHWnd, WM_KEYUP, (IntPtr)VK_CONTROL, (IntPtr)0xC01D0001);
                WinAPI.PostMessage(activeHWnd, WM_KEYUP, (IntPtr)VK_CONTROL, (IntPtr)0xC11D0001);
            }
            if (IsKeyPressed(WinAPI.GetAsyncKeyState(VK_SHIFT))) {
                WinAPI.PostMessage(activeHWnd, WM_KEYUP, (IntPtr)VK_SHIFT, (IntPtr)0xC02A0001);
                WinAPI.PostMessage(activeHWnd, WM_KEYUP, (IntPtr)VK_SHIFT, (IntPtr)0xC0360001);
            }
            if (IsKeyPressed(WinAPI.GetAsyncKeyState(VK_LWIN))) {
                WinAPI.PostMessage(activeHWnd, WM_KEYUP, (IntPtr)VK_LWIN, (IntPtr)0xC15B0001);
            }
            if (IsKeyPressed(WinAPI.GetAsyncKeyState(VK_RWIN))) {
                WinAPI.PostMessage(activeHWnd, WM_KEYUP, (IntPtr)VK_RWIN, (IntPtr)0xC15C0001);
            }
        }

        private bool IsKeyPressed(short keystate) {
            return (keystate & 0x8000) != 0;
        }
    }

}
