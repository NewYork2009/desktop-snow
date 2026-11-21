// DesktopSnow — снег только на рабочем столе Windows (за окнами приложений).
//
// Как это работает:
//   1. Основной режим «Только рабочий стол»: окно со снегом отправляется в самый
//      низ стека окон и не принимает фокус/клики (WS_EX_NOACTIVATE | WS_EX_TRANSPARENT),
//      поэтому снег виден на рабочем столе и скрывается за любыми открытыми окнами.
//   2. Если получилось, окно дополнительно уходит ещё глубже — в слой обоев
//      (Progman/WorkerW): тогда снег ЗА иконками, как настоящие живые обои.
//      Попытка повторяется каждые 20 секунд, пока не получится.
//   3. Режим «Скринсейвер» (по выбору): снег поверх всех окон.
//
// Сборка: build.bat (компилятор C# встроен в Windows, ничего ставить не нужно).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using SD = System.Drawing;
using SWF = System.Windows.Forms;

namespace DesktopSnow
{
    public class SnowWindow : Window
    {
        [DllImport("user32.dll")] static extern IntPtr SetParent(IntPtr child, IntPtr parent);
        [DllImport("user32.dll")] static extern IntPtr FindWindow(string className, string title);
        [DllImport("user32.dll")] static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string title);
        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int index);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int index, int newLong);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int w, int h, uint flags);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

        [DllImport("kernel32.dll")] static extern IntPtr GetModuleHandle(string name);
        [DllImport("user32.dll")] static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
        [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out int value, int size);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        const int WH_MOUSE_LL = 14;
        const int WS_CAPTION = 0x00C00000;
        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        const int DWMWA_CLOAKED = 14;
        const int WM_MOUSEMOVE = 0x0200;
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_LBUTTONUP = 0x0202;

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X; public int Y; }

        [StructLayout(LayoutKind.Sequential)]
        struct MSLLHOOKSTRUCT
        {
            public POINT pt;
            public uint mouseData;
            public uint flags;
            public uint time;
            public IntPtr extraInfo;
        }

        const int GWL_EXSTYLE = -20;
        const int GWL_STYLE = -16;
        const int WS_CHILD = 0x40000000;
        const int WS_POPUP = unchecked((int)0x80000000);
        const int WS_EX_TRANSPARENT = 0x20;
        const int WS_EX_TOOLWINDOW = 0x80;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_SHOWWINDOW = 0x0040;
        const uint SWP_FRAMECHANGED = 0x0020;
        static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        const string AppName = "DesktopSnow";
        const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";

        static string _logPath;

        static void Log(string message)
        {
            try
            {
                System.IO.File.AppendAllText(_logPath,
                    DateTime.Now.ToString("HH:mm:ss") + "  " + message + Environment.NewLine);
            }
            catch (Exception) { }
        }

        static string FindLogPath()
        {
            try
            {
                string dir = System.IO.Path.GetDirectoryName(ExePath());
                return System.IO.Path.Combine(dir, "DesktopSnow.log");
            }
            catch (Exception)
            {
                return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "DesktopSnow.log");
            }
        }

        static string ExePath()
        {
            return Assembly.GetExecutingAssembly().Location;
        }

        enum SnowMode { DesktopOnly, Screensaver }

        Canvas _canvas;
        DispatcherTimer _timer;
        Stopwatch _clock;
        readonly List<Flake> _flakes = new List<Flake>();
        double _t;
        double _wind;
        SWF.NotifyIcon _tray;
        SWF.MenuItem _miAutostart;
        SWF.MenuItem _miScreensaver;
        SWF.MenuItem _miIcons;
        readonly Random _rng = new Random();
        int _targetCount = 180;
        SnowMode _mode = SnowMode.DesktopOnly;
        bool _attachedBehindIcons;
        bool _userWantsScreensaver;
        IntPtr _hwnd = IntPtr.Zero;

        Polygon _drift;
        double[] _depths;
        double[] _driftTmp;
        int _driftSegment = 16;
        int _driftTick = 0;
        double _groundY;
        const double DriftSoftCap = 15.0;
        double _maxDrift = 35;

        Canvas _train;
        bool _trainActive;
        int _trainDir = 1;
        double _trainX, _trainY;
        const double TrainSpeed = 230.0;
        double _dropTimer;
        double _santaCheckTimer;
        TransformGroup _trainTf;
        RotateTransform _trainRot;
        readonly List<SnowDrop> _drops = new List<SnowDrop>();
        readonly List<Confetto> _confetti = new List<Confetto>();
        readonly List<RotateTransform> _legTfs = new List<RotateTransform>();
        readonly List<RotateTransform> _headTfs = new List<RotateTransform>();
        RotateTransform _armTf;
        double _trailTimer;
        SnowDrop _grabbed;
        double _grabLastX;
        double _grabLastY;
        uint _grabLastTime;
        double _flingVx;
        double _flingVy;
        double _grabOffX;
        double _grabOffY;
        double _angVel;
        int _grabFxCooldown;
        Canvas _grinch;
        RotateTransform _grinchRot;
        Canvas _grinchLoot;
        double _grinchIdleTimer = 20.0;
        Window _roofWindow;
        Canvas _roofCanvas;
        IntPtr _roofHwnd = IntPtr.Zero;
        EnumWindowsProc _enumProc;
        readonly List<IntPtr> _enumList = new List<IntPtr>();
        readonly List<RECT> _enumRects = new List<RECT>();
        readonly List<Roof> _roofs = new List<Roof>();
        readonly HashSet<IntPtr> _roofSet = new HashSet<IntPtr>();
        bool _grinchActive;
        bool _grinchDead;
        bool _grinchPending;
        int _grinchDir = 1;
        int _grinchSteals;
        double _grinchX;
        double _grinchDelay;
        double _grinchPhase;
        double _grinchSink;
        double _grinchStealTimer;
        HookProc _hookProc;
        IntPtr _hook = IntPtr.Zero;

        class SnowDrop
        {
            public bool IsTree;
            public bool Stuck;
            public bool Popped;
            public bool Held;
            public bool Flung;
            public IntPtr RoofHwnd;
            public Canvas Host;
            public double X;
            public double Y;
            public double PrevY;
            public double Vx;
            public double Vy;
            public double Sink;
            public double Rot;
            public double RotSpeed;
            public double RotTarget;
            public RotateTransform Rtf;
            public Canvas Shape;
        }

        class Roof
        {
            public IntPtr Hwnd;
            public double L;
            public double T;
            public double R;
            public double B;
            public int Z;
            public double[] Depths;
            public Polygon Poly;
        }

        class Confetto
        {
            public double X;
            public double Y;
            public double Vx;
            public double Vy;
            public double Life;
            public FrameworkElement Shape;
        }

        class Flake
        {
            public double X;
            public double Y;
            public double BaseX;
            public double R;
            public double Speed;
            public double SwayAmp;
            public double Phase;
            public double SwaySpeed;
            public double Opacity;
            public Ellipse Shape;
        }

        public SnowWindow()
        {
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            AllowsTransparency = true;
            Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            _canvas = new Canvas();
            Content = _canvas;
            Loaded += OnLoaded;
            SystemEvents.DisplaySettingsChanged += OnDisplayChanged;
        }

        void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                _hwnd = new WindowInteropHelper(this).Handle;
                Log("program start");
                ApplyExtendedStyles();
                ApplyDesktopOnlyMode();
                CreateRoofWindow();
                InstallClickHook();
                SetupTray();
                EnsureAutostart();
                StartAnimation();
                Log("started, mode: " + _mode + ", behindIcons: " + _attachedBehindIcons);
                _tray.ShowBalloonTip(4000, "DesktopSnow",
                    "Снег запущен (только рабочий стол). Управление: значок возле часов.",
                    SWF.ToolTipIcon.Info);
            }
            catch (Exception ex)
            {
                Log("OnLoaded error: " + ex);
                System.Windows.MessageBox.Show(ex.ToString(), "DesktopSnow: error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        void ApplyExtendedStyles()
        {
            int ex = GetWindowLong(_hwnd, GWL_EXSTYLE);
            ex |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLong(_hwnd, GWL_EXSTYLE, ex);
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        // ГЛАВНЫЙ РЕЖИМ: снег виден только на рабочем столе, за окнами программ.
        // Надёжный способ: окно в самый низ стека окон. Прикрепление к слою обоев
        // доступно вручную через меню (экспериментальный пункт).
        void ApplyDesktopOnlyMode()
        {
            _mode = SnowMode.DesktopOnly;
            Topmost = false;
            PlaceAtWindowStackBottom();
        }

        bool TryAttachBehindIcons()
        {
            try
            {
                IntPtr progman = FindWindow("Progman", null);
                Log("progman: " + progman);
                if (progman == IntPtr.Zero) return false;

                IntPtr workerw = IntPtr.Zero;
                uint[] wakeParams = new uint[] { 0x0, 0xD };
                for (int attempt = 0; attempt < wakeParams.Length && workerw == IntPtr.Zero; attempt++)
                {
                    SendMessage(progman, 0x052C, new IntPtr((long)wakeParams[attempt]), IntPtr.Zero);
                    System.Threading.Thread.Sleep(300);
                    EnumWindows(delegate(IntPtr top, IntPtr lp)
                    {
                        IntPtr defview = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                        if (defview != IntPtr.Zero)
                        {
                            workerw = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                        }
                        return true;
                    }, IntPtr.Zero);
                    Log("variant " + attempt + " -> workerw: " + workerw);
                }

                if (workerw == IntPtr.Zero)
                {
                    SetParent(_hwnd, progman);
                    RepositionToVirtualScreen();
                    Log("attached to Progman (snow behind app windows, above wallpaper)");
                    _attachedBehindIcons = false;
                }
                else
                {
                    SetParent(_hwnd, workerw);
                    RepositionToVirtualScreen();
                    Log("attached to WorkerW (snow behind desktop icons)");
                    _attachedBehindIcons = true;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log("attach failed: " + ex.Message);
                return false;
            }
        }

        void RepositionToVirtualScreen()
        {
            SetWindowPos(_hwnd, IntPtr.Zero,
                (int)SystemParameters.VirtualScreenLeft, (int)SystemParameters.VirtualScreenTop,
                (int)SystemParameters.VirtualScreenWidth, (int)SystemParameters.VirtualScreenHeight,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        // Запасной, но полноценный способ: низ стека окон.
        // Любое открытое окно оказывается выше снега; сам снег клики не ловит.
        void PlaceAtWindowStackBottom()
        {
            _attachedBehindIcons = false;
            SetParent(_hwnd, IntPtr.Zero);
            Topmost = false;
            int style = GetWindowLong(_hwnd, GWL_STYLE);
            style &= ~WS_CHILD;
            style |= WS_POPUP;
            SetWindowLong(_hwnd, GWL_STYLE, style);
            RepositionToVirtualScreen();
            SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            Log("placed at bottom of window stack (snow only on desktop)");
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
            if (_mode == SnowMode.DesktopOnly) ApplyDesktopOnlyMode();
        }

        void SetupTray()
        {
            _miAutostart = new SWF.MenuItem("Включён вместе с ПК (автозапуск)", OnClickAutostart);
            _miScreensaver = new SWF.MenuItem("Скринсейвер: снег поверх всех окон", OnClickScreensaver);
            _miIcons = new SWF.MenuItem("Эксперимент: снег за иконками", OnClickBehindIcons);

            SWF.ContextMenu menu = new SWF.ContextMenu();
            menu.MenuItems.Add(_miScreensaver);
            menu.MenuItems.Add(_miIcons);
            menu.MenuItems.Add(new SWF.MenuItem("Вызвать Санту", delegate
            {
                if (!_trainActive) SpawnTrain();
            }));
            menu.MenuItems.Add(new SWF.MenuItem("Больше снега", delegate { _targetCount += 80; TrimFlakes(); }));
            menu.MenuItems.Add(new SWF.MenuItem("Меньше снега", delegate { _targetCount = Math.Max(20, _targetCount - 80); TrimFlakes(); }));
            menu.MenuItems.Add(new SWF.MenuItem("-"));
            menu.MenuItems.Add(new SWF.MenuItem("Диагностика (открыть лог)", delegate
            {
                try { System.Diagnostics.Process.Start("notepad.exe", _logPath); }
                catch (Exception) { }
            }));
            menu.MenuItems.Add(_miAutostart);
            menu.MenuItems.Add(new SWF.MenuItem("Выход", delegate { Quit(); }));

            _tray = new SWF.NotifyIcon();
            _tray.Icon = MakeIcon();
            _tray.Text = "DesktopSnow — снег на рабочем столе";
            _tray.ContextMenu = menu;
            _tray.Visible = true;
            SyncTrayCheckState();
        }

        void SyncTrayCheckState()
        {
            if (_miScreensaver == null || _miAutostart == null || _miIcons == null) return;
            _miScreensaver.Checked = (_mode == SnowMode.Screensaver);
            _miIcons.Checked = _attachedBehindIcons;
            _miAutostart.Checked = IsAutostartEnabled();
        }

        SD.Icon MakeIcon()
        {
            SD.Bitmap bmp = new SD.Bitmap(16, 16);
            using (SD.Graphics g = SD.Graphics.FromImage(bmp))
            {
                g.Clear(SD.Color.Transparent);
                g.FillEllipse(SD.Brushes.White, 3, 2, 10, 12);
                g.FillEllipse(SD.Brushes.White, 2, 6, 12, 3);
            }
            return SD.Icon.FromHandle(bmp.GetHicon());
        }

        void OnClickAutostart(object sender, EventArgs e)
        {
            if (IsAutostartEnabled()) RemoveAutostart();
            else AddAutostart();
            SyncTrayCheckState();
        }

        void OnClickScreensaver(object sender, EventArgs e)
        {
            if (_mode == SnowMode.DesktopOnly)
            {
                _mode = SnowMode.Screensaver;
                _userWantsScreensaver = true;
                SetParent(_hwnd, IntPtr.Zero);
                Topmost = true;
                SetWindowPos(_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }
            else
            {
                _userWantsScreensaver = false;
                ApplyDesktopOnlyMode();
            }
            Log("mode -> " + _mode + ", behindIcons: " + _attachedBehindIcons);
            SyncTrayCheckState();
        }

        void OnClickBehindIcons(object sender, EventArgs e)
        {
            if (_mode == SnowMode.Screensaver)
            {
                _userWantsScreensaver = false;
                _mode = SnowMode.DesktopOnly;
                Topmost = false;
            }

            if (_attachedBehindIcons)
            {
                PlaceAtWindowStackBottom();
                _tray.ShowBalloonTip(4000, "DesktopSnow",
                    "Снег возвращён в режим «только рабочий стол».", SWF.ToolTipIcon.Info);
            }
            else if (TryAttachBehindIcons())
            {
                _tray.ShowBalloonTip(4000, "DesktopSnow",
                    "Получилось: снег теперь за иконками рабочего стола.", SWF.ToolTipIcon.Info);
            }
            else
            {
                PlaceAtWindowStackBottom();
                _tray.ShowBalloonTip(5000, "DesktopSnow",
                    "Windows не дал спрятать снег за иконки. Оставлен режим «только рабочий стол».",
                    SWF.ToolTipIcon.Warning);
            }
            SyncTrayCheckState();
        }

        static bool IsAutostartEnabled()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
            {
                if (key == null) return false;
                object v = key.GetValue(AppName);
                return v != null;
            }
        }

        static void AddAutostart()
        {
            using (RegistryKey key = Registry.CurrentUser.CreateSubKey(RunKeyPath))
            {
                key.SetValue(AppName, "\"" + ExePath() + "\"");
            }
        }

        static void RemoveAutostart()
        {
            using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
            {
                if (key != null) key.DeleteValue(AppName, false);
            }
        }

        void EnsureAutostart()
        {
            if (!IsAutostartEnabled())
            {
                AddAutostart();
            }
        }

        void Quit()
        {
            if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
            _tray.Visible = false;
            Application.Current.Shutdown();
        }

        void InstallClickHook()
        {
            try
            {
                _hookProc = MouseHookProc;
                _hook = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
                Log("mouse hook: " + _hook);
            }
            catch (Exception ex)
            {
                Log("hook failed: " + ex.Message);
            }
        }

        IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                MSLLHOOKSTRUCT info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                if (msg == WM_LBUTTONDOWN)
                {
                    double mx = info.pt.X - SystemParameters.VirtualScreenLeft;
                    double my = info.pt.Y - SystemParameters.VirtualScreenTop;
                    if (_grinchActive && !_grinchDead && _grinch != null)
                    {
                        double gl = Canvas.GetLeft(_grinch), gt = Canvas.GetTop(_grinch);
                        if (mx >= gl - 8 && mx <= gl + 72 && my >= gt - 8 && my <= gt + 80)
                        {
                            Dispatcher.BeginInvoke((Action)delegate { GrinchDie(); });
                            return (IntPtr)1;
                        }
                    }
                    SnowDrop hit = FindClickableDrop(mx, my);
                    if (hit != null)
                    {
                        Dispatcher.BeginInvoke((Action)delegate { PopDrop(hit); });
                        return (IntPtr)1;
                    }
                    if (_grabbed == null)
                    {
                        SnowDrop tree = FindGrabbableTree(mx, my);
                        if (tree != null)
                        {
                            Dispatcher.BeginInvoke((Action)delegate { GrabTree(tree, mx, my); });
                            return (IntPtr)1;
                        }
                    }
                }
                else if (msg == WM_MOUSEMOVE && _grabbed != null)
                {
                    double mx = info.pt.X - SystemParameters.VirtualScreenLeft;
                    double my = info.pt.Y - SystemParameters.VirtualScreenTop;
                    uint tstamp = info.time;
                    Dispatcher.BeginInvoke((Action)delegate { MoveGrabbed(mx, my, tstamp); });
                }
                else if (msg == WM_LBUTTONUP && _grabbed != null)
                {
                    Dispatcher.BeginInvoke((Action)delegate { ReleaseGrabbed(); });
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        SnowDrop FindClickableDrop(double mx, double my)
        {
            for (int i = 0; i < _drops.Count; i++)
            {
                SnowDrop d = _drops[i];
                if (d.Popped || d.IsTree || !d.Stuck) continue;
                double l = Canvas.GetLeft(d.Shape), t = Canvas.GetTop(d.Shape);
                if (mx >= l - 8 && mx <= l + d.Shape.Width + 8 && my >= t - 8 && my <= t + d.Shape.Height + 8)
                {
                    return d;
                }
            }
            return null;
        }

        SnowDrop FindGrabbableTree(double mx, double my)
        {
            for (int i = 0; i < _drops.Count; i++)
            {
                SnowDrop d = _drops[i];
                if (!d.IsTree || d.Popped || d.Held || !d.Stuck) continue;
                double l = Canvas.GetLeft(d.Shape), t = Canvas.GetTop(d.Shape);
                if (mx >= l - 8 && mx <= l + d.Shape.Width + 8 && my >= t - 8 && my <= t + d.Shape.Height + 8)
                {
                    return d;
                }
            }
            return null;
        }

        void GrabTree(SnowDrop d, double mx, double my)
        {
            _grabbed = d;
            d.Held = true;
            d.Flung = false;
            d.Stuck = true;
            d.Vx = 0.0;
            d.Vy = 0.0;
            d.RotTarget = d.Rot;
            d.Sink = 0.0;
            d.RotSpeed = 0.0;
            System.Windows.Controls.Panel.SetZIndex(d.Shape, 85);
            SetHeldPosition(d, mx, my);
            _grabOffX = d.X - mx;
            _grabOffY = d.Y - my;
            _grabLastX = mx;
            _grabLastY = my;
            _grabLastTime = 0;
            _flingVx = 0.0;
            _flingVy = 0.0;
            _angVel = 0.0;
            _grabFxCooldown = 0;
            SnowSplash(d.X + d.Shape.Width / 2.0, d.Y + d.Shape.Height - 4.0, 10);
            SnowSplash(d.X + d.Shape.Width / 2.0, d.Y + d.Shape.Height - 4.0, 6);
            Log("tree grabbed");
        }

        void SetHeldPosition(SnowDrop d, double mx, double my)
        {
            d.X = mx + _grabOffX;
            d.Y = my + _grabOffY;
        }

        void MoveGrabbed(double mx, double my, uint tstamp)
        {
            SnowDrop d = _grabbed;
            if (d == null || !d.Held) return;
            double dx = mx - _grabLastX;
            SetHeldPosition(d, mx, my);

            if (tstamp > _grabLastTime)
            {
                double sdt = (tstamp - _grabLastTime) / 1000.0;
                if (sdt > 0.004 && sdt < 0.4)
                {
                    _flingVx = _flingVx * 0.6 + (dx / sdt) * 0.4;
                    _flingVy = _flingVy * 0.6 + ((my - _grabLastY) / sdt) * 0.4;
                }
                _grabLastTime = tstamp;
            }
            _grabLastX = mx;
            _grabLastY = my;

            _angVel = Math.Max(-900.0, Math.Min(900.0, _angVel + dx * 4.0));
        }

        void ReleaseGrabbed()
        {
            SnowDrop d = _grabbed;
            if (d == null) return;
            _grabbed = null;
            d.Held = false;
            d.Stuck = false;
            d.Flung = true;
            d.RotTarget = d.Rot;
            d.RotSpeed = _angVel;
            double max = 1300.0;
            d.Vx = Math.Max(-max, Math.Min(max, _flingVx));
            d.Vy = Math.Max(-max, Math.Min(max, _flingVy * 0.8));
            System.Windows.Controls.Panel.SetZIndex(d.Shape, 70);
            Log("tree thrown, v=" + Math.Round(Math.Sqrt(d.Vx * d.Vx + d.Vy * d.Vy)));
        }

        void KnockDownTrain(double cx, double cy)
        {
            _canvas.Children.Remove(_train);
            _train = null;
            _trainActive = false;
            _trainRot = null;
            ConfettiBurst(cx, cy, 40);
            SnowSplash(cx, cy, 20);
            for (int i = 0; i < 4; i++)
            {
                SpawnDrop(cx - 60.0 + _rng.NextDouble() * 120.0, cy,
                    -180.0 + _rng.NextDouble() * 360.0, -60.0 + _rng.NextDouble() * 100.0, false);
            }
            DepositSnow(cx, 10.0);
            Log("santa downed");
        }

        void StartAnimation()
        {
            InitDrift();
            AddFlakes(_targetCount);
            _clock = Stopwatch.StartNew();
            _timer = new DispatcherTimer(DispatcherPriority.Render);
            _timer.Interval = TimeSpan.FromMilliseconds(16);
            _timer.Tick += Tick;
            _timer.Start();
        }

        void InitDrift()
        {
            int segments = (int)(W() / _driftSegment) + 2;
            _depths = new double[segments];
            _maxDrift = 35.0;
            _groundY = SystemParameters.WorkArea.Bottom - SystemParameters.VirtualScreenTop;

            _drift = new Polygon();
            _drift.Fill = new LinearGradientBrush(
                Color.FromRgb(245, 250, 255), Color.FromRgb(225, 235, 250), 90.0);
            _drift.Opacity = 0.95;
            System.Windows.Controls.Panel.SetZIndex(_drift, 50);
            _canvas.Children.Insert(0, _drift);
            UpdateDriftShape();
        }

        int DriftIndexFor(double x)
        {
            int idx = (int)(x / _driftSegment);
            if (idx < 0) idx = 0;
            if (idx >= _depths.Length) idx = _depths.Length - 1;
            return idx;
        }

        void DepositSnow(double x, double amount)
        {
            double share = amount / _depths.Length;
            for (int i = 0; i < _depths.Length; i++)
            {
                _depths[i] += share * 0.7;
            }

            int idx = DriftIndexFor(x);
            for (int d = -3; d <= 3; d++)
            {
                int j = idx + d;
                if (j < 0 || j >= _depths.Length) continue;
                _depths[j] += amount * 0.3 / (1 + d * d);
            }
        }

        void UpdateDriftShape()
        {
            PointCollection pts = new PointCollection();
            pts.Add(new Point(0, _groundY));
            for (int i = 0; i < _depths.Length; i++)
            {
                double wobble = 1.0 + 0.05 * Math.Sin(i * 0.31 + _t * 0.4);
                double baseLine = Math.Min(1.2, _depths[i] * 0.25);
                pts.Add(new Point(i * _driftSegment, _groundY - _depths[i] * wobble - baseLine));
            }
            pts.Add(new Point(W(), _groundY));
            _drift.Points = pts;
        }

        void AddFlakes(int count)
        {
            for (int i = 0; i < count; i++) AddFlake(true);
        }

        void AddFlake(bool randomY)
        {
            Flake f = new Flake();
            f.R = 1.0 + _rng.NextDouble() * 3.2;
            f.Speed = 15 + _rng.NextDouble() * 45 + (f.R - 1.0) * 12;
            f.SwayAmp = 10 + _rng.NextDouble() * 40;
            f.Phase = _rng.NextDouble() * Math.PI * 2;
            f.SwaySpeed = 0.5 + _rng.NextDouble() * 1.5;
            f.Opacity = 0.35 + _rng.NextDouble() * 0.6;
            f.BaseX = _rng.NextDouble() * (W() + 80);
            f.X = f.BaseX;
            f.Y = randomY ? _rng.NextDouble() * (H() + 80) : -10 - _rng.NextDouble() * 40;

            Ellipse shape = new Ellipse();
            shape.Width = f.R * 2;
            shape.Height = f.R * 2;
            shape.Fill = Brushes.White;
            shape.Opacity = f.Opacity;
            f.Shape = shape;

            _flakes.Add(f);
            _canvas.Children.Add(shape);
        }

        void TrimFlakes()
        {
            while (_flakes.Count > _targetCount)
            {
                Flake last = _flakes[_flakes.Count - 1];
                _canvas.Children.Remove(last.Shape);
                _flakes.RemoveAt(_flakes.Count - 1);
            }
        }

        void Tick(object sender, EventArgs e)
        {
            double dt = Math.Min((double)_clock.ElapsedMilliseconds / 1000, 0.05);
            _clock.Restart();
            _t += dt;
            _wind = Math.Sin(_t / 6.5) * 18 + Math.Sin(_t / 2.3) * 6;

            if (_flakes.Count < _targetCount && _rng.NextDouble() < 0.4) AddFlake(false);

            double width = SystemParameters.VirtualScreenWidth;

            for (int i = 0; i < _depths.Length; i++)
            {
                double excess = _depths[i] - DriftSoftCap;
                if (excess > 0.0)
                {
                    _depths[i] -= (excess * 0.3) * dt;
                }
                if (_depths[i] > _maxDrift) _depths[i] = _maxDrift;
            }

            // сглаживание: холмы превращаются в плавные дуги, а не треугольники
            if (_driftTmp == null || _driftTmp.Length != _depths.Length)
            {
                _driftTmp = new double[_depths.Length];
            }
            for (int pass = 0; pass < 2; pass++)
            {
                for (int i = 0; i < _depths.Length; i++) _driftTmp[i] = _depths[i];
                for (int i = 1; i < _depths.Length - 1; i++)
                {
                    _depths[i] = (_driftTmp[i - 1] + 2.0 * _driftTmp[i] + _driftTmp[i + 1]) * 0.25;
                }
            }
            for (int i = 0; i < _depths.Length; i++)
            {
                if (_depths[i] < 0) _depths[i] = 0;
            }

            _driftTick++;
            if (_driftTick % 6 == 0) UpdateDriftShape();
            RefreshRoofs();
            if (_roofHwnd != IntPtr.Zero && _driftTick % 300 == 0) ApplyRoofStyles(_roofHwnd);

            if (_mode == SnowMode.DesktopOnly && _driftTick % 300 == 0 && !_attachedBehindIcons)
            {
                SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }

            for (int i = _flakes.Count - 1; i >= 0; i--)
            {
                Flake f = _flakes[i];
                double oldY = f.Y;
                f.Phase += dt * f.SwaySpeed;
                f.Y += f.Speed * dt;
                f.X = f.BaseX + Math.Sin(f.Phase) * f.SwayAmp + _wind;

                bool roofLanded = false;
                for (int ri = 0; ri < _roofs.Count; ri++)
                {
                    Roof r = _roofs[ri];
                    if (r.Depths == null) continue;
                    if (f.X >= r.L && f.X <= r.R && oldY <= r.T && f.Y >= r.T)
                    {
                        DepositRoof(r, f.X, f.R * 5.0);
                        roofLanded = true;
                        break;
                    }
                }

                if (!roofLanded && f.Y >= _groundY - _depths[DriftIndexFor(f.X)])
                {
                    DepositSnow(f.X, f.R * 5.0);
                    roofLanded = true;
                }

                if (roofLanded)
                {
                    f.BaseX = _rng.NextDouble() * (width + 80);
                    f.Y = -10 - _rng.NextDouble() * 40;
                    f.X = f.BaseX;
                }
            }

            for (int i = _flakes.Count - 1; i >= 0; i--)
            {
                Flake f = _flakes[i];
                Canvas.SetLeft(f.Shape, f.X - f.R);
                Canvas.SetTop(f.Shape, f.Y - f.R);
            }

            SantaTick(dt);
            GrinchTick(dt);
        }

        void SantaTick(double dt)
        {
            _santaCheckTimer += dt;
            if (_santaCheckTimer >= 30.0)
            {
                _santaCheckTimer = 0.0;
                if (!_trainActive && _rng.NextDouble() < 0.25)
                {
                    SpawnTrain();
                }
            }

            if (_trainActive)
            {
                _trainX += _trainDir * TrainSpeed * dt;
                double bobY = _trainY + Math.Sin(_t * 0.9) * 48.0;
                Canvas.SetLeft(_train, _trainDir > 0 ? _trainX : _trainX - 170);
                Canvas.SetTop(_train, bobY);
                if (_trainRot != null)
                {
                    _trainRot.Angle = Math.Sin(_t * 1.5) * 14.0 * _trainDir;
                }

                for (int i = 0; i < _legTfs.Count; i++)
                    _legTfs[i].Angle = Math.Sin(_t * 14.0 + i * 1.7) * 22.0;
                for (int i = 0; i < _headTfs.Count; i++)
                    _headTfs[i].Angle = Math.Sin(_t * 2.5 + i * 1.3) * 9.0;
                if (_armTf != null)
                    _armTf.Angle = -25.0 + Math.Sin(_t * 3.4) * 40.0;

                _trailTimer -= dt;
                if (_trailTimer <= 0.0)
                {
                    _trailTimer = 0.09;
                    double bx = (_trainDir > 0) ? _trainX + 6.0 : _trainX - 6.0;
                    Confetto c = new Confetto();
                    c.X = bx + (_rng.NextDouble() * 20.0 - 10.0);
                    c.Y = bobY + 58.0 + (_rng.NextDouble() * 10.0 - 5.0);
                    c.Vx = -_trainDir * (50.0 + _rng.NextDouble() * 90.0);
                    c.Vy = -30.0 + _rng.NextDouble() * 70.0;
                    c.Life = 0.3 + _rng.NextDouble() * 0.35;
                    bool gold = _rng.NextDouble() < 0.5;
                    c.Shape = new Ellipse
                    {
                        Width = 2.0 + _rng.NextDouble() * 1.8,
                        Height = 2.0 + _rng.NextDouble() * 1.8,
                        Fill = new SolidColorBrush(gold ? Color.FromRgb(235, 195, 70) : Color.FromRgb(250, 250, 255))
                    };
                    System.Windows.Controls.Panel.SetZIndex(c.Shape, 60);
                    Canvas.SetLeft(c.Shape, c.X);
                    Canvas.SetTop(c.Shape, c.Y);
                    _confetti.Add(c);
                    _canvas.Children.Add(c.Shape);
                }

                _dropTimer -= dt;
                if (_dropTimer <= 0.0)
                {
                    _dropTimer = 0.5 + _rng.NextDouble() * 0.6; // гирлянда подарков и ёлок
                    double dx = _trainX + 55 + _rng.NextDouble() * 60;
                    bool tree = _rng.NextDouble() < 0.18;
                    SpawnDrop(dx, bobY + 40, _trainDir * 40.0 + _wind, 0.0, tree);
                }

                if ((_trainDir > 0 && _trainX > W() + 60) || (_trainDir < 0 && _trainX < -260))
                {
                    _canvas.Children.Remove(_train);
                    _train = null;
                    _trainActive = false;
                    _trainRot = null;
                    Log("santa pass finished");
                }
            }

            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                SnowDrop d = _drops[i];
                if (d.Popped)
                {
                    _drops.RemoveAt(i);
                    continue;
                }

                double landY = _groundY - _depths[DriftIndexFor(d.X)];
                double h = d.IsTree ? 60.0 : 28.0;

                if (d.Held)
                {
                    _angVel -= _angVel * 1.8 * dt;
                    d.Rot += _angVel * dt;
                    if (d.Rtf != null) d.Rtf.Angle = d.Rot;
                    _grabFxCooldown--;
                    if (Math.Abs(_angVel) > 280.0 && _grabFxCooldown <= 0)
                    {
                        _grabFxCooldown = 6;
                        SnowSplash(d.X + d.Shape.Width / 2.0, d.Y + d.Shape.Height - 4.0, 4);
                    }
                }
                else if (!d.Stuck)
                {
                    d.PrevY = d.Y;
                    d.Vy += 500.0 * dt;
                    d.X += d.Vx * dt;
                    d.Y += d.Vy * dt;
                    if (d.IsTree && !d.Flung)
                    {
                        double diff = d.RotTarget - d.Rot;
                        double step = 260.0 * dt;
                        if (Math.Abs(diff) <= step) d.Rot = d.RotTarget;
                        else d.Rot += Math.Sign(diff) * step;
                    }
                    else
                    {
                        d.Rot += d.RotSpeed * dt;
                    }
                    if (d.Rtf != null) d.Rtf.Angle = d.Rot;

                    if (d.Flung && d.IsTree && _trainActive && _train != null)
                    {
                        double tcx = d.X + d.Shape.Width / 2.0;
                        double tcy = d.Y + d.Shape.Height / 2.0;
                        double tl = Canvas.GetLeft(_train);
                        double tt = Canvas.GetTop(_train);
                        double tcx2 = tl + 170.0 * 1.5 / 2.0;
                        double tcy2 = tt + 80.0 * 1.5 / 2.0;
                        if (Math.Abs(tcx - tcx2) < 140.0 && Math.Abs(tcy - tcy2) < 70.0)
                        {
                            RemoveShape(d.Shape);
                            _drops.RemoveAt(i);
                            KnockDownTrain(tcx2, tcy2);
                            continue;
                        }
                    }

                    if (d.Flung && (d.X < -400.0 || d.X > W() + 400.0 || d.Y < -900.0))
                    {
                        RemoveShape(d.Shape);
                        _drops.RemoveAt(i);
                        continue;
                    }

                    bool landedOnRoof = false;
                    if (d.RoofHwnd == IntPtr.Zero)
                    {
                        Roof hitRoof = FindLandingRoof(d, h);
                        if (hitRoof != null)
                        {
                            LandDropOnRoof(d, hitRoof, h);
                            landedOnRoof = true;
                        }
                    }

                    if (!landedOnRoof && d.Y + h >= landY)
                    {
                        bool wasFlung = d.Flung;
                        d.Stuck = true;
                        d.Flung = false;
                        d.Sink = d.IsTree ? 8.0 : 6.0;
                        d.Y = landY - h + d.Sink;
                        if (d.IsTree && !wasFlung) d.Rot = d.RotTarget;
                        d.RotSpeed = 0.0;
                        if (d.Rtf != null) d.Rtf.Angle = d.Rot;
                        if (d.Host != _canvas)
                        {
                            Canvas prevHost = d.Shape.Parent as Canvas;
                            if (prevHost != null) prevHost.Children.Remove(d.Shape);
                            _canvas.Children.Add(d.Shape);
                            d.Host = _canvas;
                        }
                        System.Windows.Controls.Panel.SetZIndex(d.Shape, 40);
                        SnowSplash(d.X + d.Shape.Width / 2.0, landY, 14);
                        DepositSnow(d.X, 5.0);
                    }
                }
                else if (d.RoofHwnd != IntPtr.Zero)
                {
                    Roof rr = RoofByHwnd(d.RoofHwnd);
                    if (rr == null)
                    {
                        d.RoofHwnd = IntPtr.Zero;
                        d.Stuck = false;
                        if (d.IsTree) d.RotTarget = d.Rot;
                        d.Host = _canvas;
                        Canvas prevHost = d.Shape.Parent as Canvas;
                        if (prevHost != null && prevHost != _canvas)
                        {
                            prevHost.Children.Remove(d.Shape);
                            _canvas.Children.Add(d.Shape);
                        }
                        System.Windows.Controls.Panel.SetZIndex(d.Shape, 70);
                    }
                    else
                    {
                        d.Sink += (d.IsTree ? 0.35 : 0.6) * dt;
                        d.Y = rr.T - h + d.Sink;
                        if (d.Sink >= h)
                        {
                            RemoveShape(d.Shape);
                            _drops.RemoveAt(i);
                            continue;
                        }
                    }
                }
                else
                {
                    double sinkSpeed = d.IsTree ? 0.35 : 0.6; // скорость закапывания
                    d.Sink += sinkSpeed * dt;
                    d.Y = landY - h + d.Sink;
                    if (d.Sink >= h)
                    {
                        RemoveShape(d.Shape);
                        _drops.RemoveAt(i);
                        continue;
                    }
                }
                Canvas.SetLeft(d.Shape, d.X);
                Canvas.SetTop(d.Shape, d.Y);
            }

            UpdateConfetti(dt);
        }

        void SpawnTrain()
        {
            _trainDir = (_rng.NextDouble() < 0.5) ? 1 : -1;
            _legTfs.Clear();
            _headTfs.Clear();
            _armTf = null;
            _trailTimer = 0.0;
            if (!_grinchActive && _rng.NextDouble() < 0.4)
            {
                _grinchPending = true;
                _grinchDelay = 2.0 + _rng.NextDouble() * 6.0;
            }
            _trainX = (_trainDir > 0) ? -260.0 : W() + 260.0;
            _trainY = 50 + _rng.NextDouble() * (H() * 0.25);
            _dropTimer = 0.6; // гирлянда подарков и ёлок

            _train = BuildTrain();
            _trainTf = new TransformGroup();
            _train.RenderTransformOrigin = new Point(0.5, 0.5);
            _trainTf.Children.Add(new ScaleTransform(1.5, 1.5));
            if (_trainDir < 0)
            {
                _trainTf.Children.Add(new ScaleTransform(-1, 1));
            }
            _trainRot = new RotateTransform(0);
            _trainTf.Children.Add(_trainRot);
            _train.RenderTransform = _trainTf;

            Canvas.SetLeft(_train, _trainDir > 0 ? _trainX : _trainX - 170);
            Canvas.SetTop(_train, _trainY);
            System.Windows.Controls.Panel.SetZIndex(_train, 70);
            _canvas.Children.Add(_train);
            _trainActive = true;
            Log("santa pass started");
        }

        Canvas BuildTrain()
        {
            Canvas train = new Canvas();
            train.Width = 170;
            train.Height = 80;
            Brush fur = new SolidColorBrush(Color.FromRgb(107, 66, 38));
            Brush dark = new SolidColorBrush(Color.FromRgb(63, 39, 18));
            Brush red = new SolidColorBrush(Color.FromRgb(204, 34, 34));
            Brush gold = new SolidColorBrush(Color.FromRgb(230, 190, 60));

            // олени: тело, шея, голова, ветвистые рога, длинные ноги
            int[] deerX = new int[] { 76, 110, 144 };
            foreach (int cx in deerX)
            {
                train.Children.Add(ShapeAt(new Ellipse { Width = 26, Height = 11, Fill = fur }, cx - 13, 34));
                train.Children.Add(ShapeAt(new Ellipse { Width = 12, Height = 5, Fill = new SolidColorBrush(Color.FromRgb(230, 205, 170)) }, cx - 7, 38));

                Canvas head = new Canvas();
                head.Children.Add(new Line { X1 = cx + 10, Y1 = 37, X2 = cx + 16, Y2 = 24, Stroke = fur, StrokeThickness = 4 });
                head.Children.Add(ShapeAt(new Ellipse { Width = 9, Height = 9, Fill = fur }, cx + 13, 18));

                Polyline antlers = new Polyline();
                antlers.Points = new PointCollection
                {
                    new Point(cx + 17, 18), new Point(cx + 20, 6), new Point(cx + 24, 11),
                    new Point(cx + 27, 3)
                };
                antlers.Stroke = dark;
                antlers.StrokeThickness = 2;
                head.Children.Add(antlers);

                Polyline antlers2 = new Polyline();
                antlers2.Points = new PointCollection(new Point[]
                {
                    new Point(cx + 17, 18), new Point(cx + 13, 7)
                });
                antlers2.Stroke = dark;
                antlers2.StrokeThickness = 2;
                head.Children.Add(antlers2);

                head.Children.Add(ShapeAt(new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(Color.FromRgb(230, 40, 40)) }, cx + 21, 21));

                RotateTransform nod = new RotateTransform(0);
                nod.CenterX = cx + 10;
                nod.CenterY = 37;
                head.RenderTransform = nod;
                _headTfs.Add(nod);
                train.Children.Add(head);

                double[][] legPts = new double[][]
                {
                    new double[] { cx - 9, 44, cx - 11, 58 },
                    new double[] { cx - 2, 44, cx + 0, 58 },
                    new double[] { cx + 9, 44, cx + 12, 57 }
                };
                foreach (double[] lp in legPts)
                {
                    Line leg = new Line
                    {
                        X1 = lp[0],
                        Y1 = lp[1],
                        X2 = lp[2],
                        Y2 = lp[3],
                        Stroke = dark,
                        StrokeThickness = 2.5
                    };
                    RotateTransform rt = new RotateTransform(0);
                    rt.CenterX = lp[0];
                    rt.CenterY = lp[1];
                    leg.RenderTransform = rt;
                    _legTfs.Add(rt);
                    train.Children.Add(leg);
                }
            }

            // поводья
            Polyline reins = new Polyline();
            reins.Points = new PointCollection(new Point[]
            {
                new Point(44, 32), new Point(80, 30), new Point(114, 30), new Point(148, 30)
            });
            reins.Stroke = new SolidColorBrush(Color.FromRgb(90, 58, 26));
            reins.StrokeThickness = 1.5;
            train.Children.Add(reins);

            // мешок с подарками
            train.Children.Add(ShapeAt(new Ellipse { Width = 18, Height = 20, Fill = new SolidColorBrush(Color.FromRgb(138, 90, 43)) }, -8, 18));
            train.Children.Add(ShapeAt(new Ellipse { Width = 8, Height = 4, Fill = new SolidColorBrush(Color.FromRgb(110, 70, 30)) }, -3, 16));

            // сани
            train.Children.Add(ShapeAt(new Rectangle { Width = 46, Height = 18, RadiusX = 6, RadiusY = 6, Fill = red }, 0, 28));
            train.Children.Add(ShapeAt(new Rectangle { Width = 46, Height = 4, Fill = gold }, 0, 28));

            Polyline runner = new Polyline();
            runner.Points = new PointCollection(new Point[]
            {
                new Point(2, 56), new Point(38, 56), new Point(46, 44)
            });
            runner.Stroke = gold;
            runner.StrokeThickness = 2;
            train.Children.Add(runner);

            // Санта: тулуп с опушкой, ремень, борода, колпак
            train.Children.Add(ShapeAt(new Ellipse { Width = 15, Height = 14, Fill = red }, 14, 14));
            train.Children.Add(ShapeAt(new Rectangle { Width = 15, Height = 3, Fill = Brushes.White }, 14, 14));
            train.Children.Add(ShapeAt(new Rectangle { Width = 15, Height = 2.5, Fill = new SolidColorBrush(Color.FromRgb(40, 40, 40)) }, 14, 22));
            train.Children.Add(ShapeAt(new Rectangle { Width = 3, Height = 3, Fill = gold }, 20, 21.5));

            train.Children.Add(ShapeAt(new Ellipse { Width = 9, Height = 9, Fill = new SolidColorBrush(Color.FromRgb(240, 200, 160)) }, 16, 3));
            train.Children.Add(ShapeAt(new Ellipse { Width = 11, Height = 8, Fill = Brushes.White }, 14, 7));

            train.Children.Add(ShapeAt(new Rectangle { Width = 13, Height = 3.5, Fill = Brushes.White }, 13, 0));
            Polygon hat = new Polygon();
            hat.Points = new PointCollection(new Point[]
            {
                new Point(13, 2), new Point(25, 2), new Point(31, -8)
            });
            hat.Fill = red;
            train.Children.Add(hat);
            train.Children.Add(ShapeAt(new Ellipse { Width = 4.5, Height = 4.5, Fill = Brushes.White }, 29, -8));

            Canvas armCv = new Canvas();
            armCv.Children.Add(new Line
            {
                X1 = 22, Y1 = 20, X2 = 35, Y2 = 6,
                Stroke = red, StrokeThickness = 4.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round
            });
            armCv.Children.Add(ShapeAt(new Ellipse { Width = 5, Height = 5, Fill = Brushes.White }, 32, 3));
            _armTf = new RotateTransform(0);
            _armTf.CenterX = 22;
            _armTf.CenterY = 20;
            armCv.RenderTransform = _armTf;
            train.Children.Add(armCv);

            return train;
        }

        FrameworkElement ShapeAt(FrameworkElement el, double x, double y)
        {
            Canvas.SetLeft(el, x);
            Canvas.SetTop(el, y);
            return el;
        }

        void SpawnDrop(double x, double y, double vx, double vy, bool tree)
        {
            SnowDrop d = new SnowDrop();
            d.IsTree = tree;
            d.X = x;
            d.Y = y;
            d.PrevY = y;
            d.Vx = vx;
            d.Vy = vy;
            if (tree)
            {
                d.RotTarget = (_rng.NextDouble() < 0.5) ? 0.0 : (180.0 + (_rng.NextDouble() < 0.5 ? 0.0 : -180.0));
                d.RotSpeed = 0.0;
                d.Rot = (_rng.NextDouble() < 0.5) ? -18.0 : 18.0;
            }
            else
            {
                d.RotTarget = 0.0;
                d.RotSpeed = -110.0 + _rng.NextDouble() * 220.0;
            }
            d.Rtf = new RotateTransform(d.Rot);
            d.Shape = tree ? BuildTree() : BuildGift();
            d.Shape.RenderTransformOrigin = new Point(0.5, 0.5);
            d.Shape.RenderTransform = d.Rtf;
            d.Host = _canvas;
            Canvas.SetLeft(d.Shape, x);
            Canvas.SetTop(d.Shape, y);
            System.Windows.Controls.Panel.SetZIndex(d.Shape, 70);
            _drops.Add(d);
            _canvas.Children.Add(d.Shape);
        }

        Canvas BuildGift()
        {
            Color[] boxColors = new Color[]
            {
                Color.FromRgb(204, 34, 34),
                Color.FromRgb(34, 90, 200),
                Color.FromRgb(40, 160, 60),
                Color.FromRgb(230, 140, 20),
                Color.FromRgb(170, 40, 180)
            };

            Canvas gift = new Canvas();
            gift.Width = 34;
            gift.Height = 28;

            Color cc = boxColors[_rng.Next(0, boxColors.Length)];
            gift.Children.Add(ShapeAt(new Rectangle { Width = 28, Height = 22, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(cc) }, 3, 5));
            gift.Children.Add(ShapeAt(new Rectangle { Width = 5, Height = 22, Fill = new SolidColorBrush(Color.FromRgb(250, 240, 200)) }, 14.5, 5));
            gift.Children.Add(ShapeAt(new Rectangle { Width = 28, Height = 5, Fill = new SolidColorBrush(Color.FromRgb(250, 240, 200)) }, 3, 13));
            gift.Children.Add(ShapeAt(new Ellipse { Width = 7, Height = 7, Fill = new SolidColorBrush(Color.FromRgb(250, 240, 200)) }, 13.5, 1));

            return gift;
        }

        Canvas BuildTree()
        {
            Canvas tree = new Canvas();
            tree.Width = 48;
            tree.Height = 60;
            Brush green = new SolidColorBrush(Color.FromRgb(30, 110, 50));
            Brush snowCap = new SolidColorBrush(Color.FromRgb(240, 248, 255));

            tree.Children.Add(ShapeAt(new Rectangle { Width = 8, Height = 12, Fill = new SolidColorBrush(Color.FromRgb(101, 67, 33)) }, 20, 48));

            Polygon t1 = new Polygon();
            t1.Points = new PointCollection(new Point[] { new Point(24, 18), new Point(6, 48), new Point(42, 48) });
            t1.Fill = green;
            tree.Children.Add(t1);

            Polygon t2 = new Polygon();
            t2.Points = new PointCollection(new Point[] { new Point(24, 2), new Point(9, 30), new Point(39, 30) });
            t2.Fill = green;
            tree.Children.Add(t2);

            Polygon cap = new Polygon();
            cap.Points = new PointCollection(new Point[] { new Point(24, 2), new Point(17, 14), new Point(31, 14) });
            cap.Fill = snowCap;
            tree.Children.Add(cap);

            return tree;
        }

        void RemoveShape(FrameworkElement el)
        {
            Canvas parent = el.Parent as Canvas;
            if (parent != null) parent.Children.Remove(el);
        }

        void CreateRoofWindow()
        {
            try
            {
                _enumProc = RoofEnumCallback;
                _roofWindow = new Window();
                _roofWindow.WindowStyle = WindowStyle.None;
                _roofWindow.ResizeMode = ResizeMode.NoResize;
                _roofWindow.ShowInTaskbar = false;
                _roofWindow.ShowActivated = false;
                _roofWindow.AllowsTransparency = true;
                _roofWindow.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
                _roofWindow.Left = SystemParameters.VirtualScreenLeft;
                _roofWindow.Top = SystemParameters.VirtualScreenTop;
                _roofWindow.Width = SystemParameters.VirtualScreenWidth;
                _roofWindow.Height = SystemParameters.VirtualScreenHeight;
                _roofWindow.Topmost = true;
                _roofCanvas = new Canvas();
                _roofWindow.Content = _roofCanvas;
                _roofWindow.Loaded += delegate
                {
                    _roofHwnd = new WindowInteropHelper(_roofWindow).Handle;
                    ApplyRoofStyles(_roofHwnd);
                    Log("roof window ready");
                };
                _roofWindow.Show();
            }
            catch (Exception ex)
            {
                Log("roof window error: " + ex.Message);
            }
        }

        void ApplyRoofStyles(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            ex |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
            SetWindowLong(hwnd, GWL_EXSTYLE, ex);
            SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }

        bool RoofEnumCallback(IntPtr hWnd, IntPtr lParam)
        {
            try
            {
                if (hWnd == _hwnd || hWnd == _roofHwnd) return true;
                if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return true;
                int style = GetWindowLong(hWnd, GWL_STYLE);
                if ((style & WS_CAPTION) != WS_CAPTION) return true;
                int ex = GetWindowLong(hWnd, GWL_EXSTYLE);
                if ((ex & WS_EX_TOOLWINDOW) != 0) return true;
                int cloaked;
                if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out cloaked, 4) == 0 && cloaked != 0) return true;
                RECT e;
                if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out e, Marshal.SizeOf(typeof(RECT))) != 0)
                {
                    if (!GetWindowRect(hWnd, out e)) return true;
                }
                double w = e.Right - e.Left;
                double hh = e.Bottom - e.Top;
                if (w < 60 || hh < 60) return true;
                _enumList.Add(hWnd);
                _enumRects.Add(e);
            }
            catch (Exception) { }
            return true;
        }

        int IndexOfHwnd(IntPtr h)
        {
            for (int k = 0; k < _enumList.Count; k++)
            {
                if (_enumList[k] == h) return k;
            }
            return -1;
        }

        Geometry ComputeRoofClip(Roof roof)
        {
            Rect band = new Rect(roof.L - 8.0, roof.T - 100.0, (roof.R - roof.L) + 16.0, 112.0);
            Geometry clip = new RectangleGeometry(band);
            for (int k = 0; k < _enumList.Count && k < roof.Z; k++)
            {
                RECT e = _enumRects[k];
                Rect win = new Rect(
                    e.Left - SystemParameters.VirtualScreenLeft,
                    e.Top - SystemParameters.VirtualScreenTop,
                    e.Right - e.Left,
                    e.Bottom - e.Top);
                if (!win.IntersectsWith(band)) continue;
                win.Inflate(2.0, 2.0);
                clip = new CombinedGeometry(GeometryCombineMode.Exclude, clip, new RectangleGeometry(win));
            }
            return clip;
        }

        void RefreshRoofs()
        {
            if (_roofCanvas == null || _enumProc == null) return;
            _enumList.Clear();
            _enumRects.Clear();
            EnumWindows(_enumProc, IntPtr.Zero);

            _roofSet.Clear();
            for (int k = 0; k < _enumList.Count; k++)
            {
                RECT e = _enumRects[k];
                double w = e.Right - e.Left;
                double hh = e.Bottom - e.Top;
                if (w >= 100.0 && hh >= 80.0 && !(w >= W() - 40.0 && hh >= H() - 100.0))
                {
                    _roofSet.Add(_enumList[k]);
                }
            }

            for (int i = _roofs.Count - 1; i >= 0; i--)
            {
                Roof r = _roofs[i];
                int idx = IndexOfHwnd(r.Hwnd);
                if (idx < 0 || !_roofSet.Contains(r.Hwnd))
                {
                    ReleaseRoofSnow(r);
                    MoveRoofDropsOff(r);
                    if (r.Poly != null) _roofCanvas.Children.Remove(r.Poly);
                    _roofs.RemoveAt(i);
                    continue;
                }

                RECT e = _enumRects[idx];
                r.Z = idx;
                double nl = e.Left - SystemParameters.VirtualScreenLeft;
                double nt = e.Top - SystemParameters.VirtualScreenTop;
                double wpx = e.Right - e.Left;
                if (Math.Abs(nl - r.L) > 2.0 || Math.Abs(nt - r.T) > 2.0)
                {
                    ReleaseRoofSnow(r);
                    MoveRoofDropsOff(r);
                    r.L = nl;
                    r.T = nt;
                    r.R = nl + wpx;
                    r.B = nt + (e.Bottom - e.Top);
                    r.Depths = new double[Math.Max(2, (int)(wpx / _driftSegment) + 2)];
                    if (r.Poly != null) r.Poly.Points = new PointCollection();
                }
                else
                {
                    r.R = r.L + wpx;
                    r.B = r.T + (e.Bottom - e.Top);
                    if (r.Depths == null)
                        r.Depths = new double[Math.Max(2, (int)(wpx / _driftSegment) + 2)];
                }
            }

            for (int k = 0; k < _enumList.Count; k++)
            {
                IntPtr h = _enumList[k];
                if (!_roofSet.Contains(h)) continue;
                bool known = false;
                for (int i = 0; i < _roofs.Count; i++)
                {
                    if (_roofs[i].Hwnd == h) { known = true; break; }
                }
                if (!known)
                {
                    RECT e = _enumRects[k];
                    Roof r = new Roof();
                    r.Hwnd = h;
                    r.Z = k;
                    r.L = e.Left - SystemParameters.VirtualScreenLeft;
                    r.T = e.Top - SystemParameters.VirtualScreenTop;
                    r.R = e.Right - SystemParameters.VirtualScreenLeft;
                    r.B = e.Bottom - SystemParameters.VirtualScreenTop;
                    r.Depths = new double[Math.Max(2, (int)((r.R - r.L) / _driftSegment) + 2)];
                    _roofs.Add(r);
                }
            }

            if (_driftTick % 3 == 0)
            {
                RoofMeltAndSmooth();
                UpdateRoofShapes();
            }
        }

        void ReleaseRoofSnow(Roof roof)
        {
            if (roof.Depths == null || _roofCanvas == null) return;
            int spawned = 0;
            for (int i = 0; i < roof.Depths.Length && spawned < 64; i++)
            {
                if (roof.Depths[i] < 1.5) continue;
                int n = 1 + (int)(roof.Depths[i] / 5.0);
                for (int k = 0; k < n && spawned < 64; k++)
                {
                    Confetto c = new Confetto();
                    c.X = roof.L + i * _driftSegment + _rng.NextDouble() * _driftSegment;
                    c.Y = roof.T - roof.Depths[i] * 0.5;
                    c.Vx = -80.0 + _rng.NextDouble() * 160.0;
                    c.Vy = 20.0 + _rng.NextDouble() * 50.0;
                    c.Life = 0.6 + _rng.NextDouble() * 0.6;
                    Ellipse puff = new Ellipse
                    {
                        Width = 2.5 + _rng.NextDouble() * 3.0,
                        Height = 2.5 + _rng.NextDouble() * 3.0,
                        Fill = new SolidColorBrush(Color.FromRgb(250, 252, 255))
                    };
                    c.Shape = puff;
                    System.Windows.Controls.Panel.SetZIndex(puff, 90);
                    Canvas.SetLeft(puff, c.X);
                    Canvas.SetTop(puff, c.Y);
                    _confetti.Add(c);
                    _roofCanvas.Children.Add(puff);
                    spawned++;
                }
                roof.Depths[i] = 0.0;
            }
        }

        void RoofPuff(double x, double y, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Confetto c = new Confetto();
                double side = (_rng.NextDouble() < 0.5) ? -1.0 : 1.0;
                c.X = x + side * _rng.NextDouble() * 10.0;
                c.Y = y - 2.0 - _rng.NextDouble() * 6.0;
                c.Vx = side * (40.0 + _rng.NextDouble() * 110.0);
                c.Vy = -40.0 - _rng.NextDouble() * 80.0;
                c.Life = 0.5 + _rng.NextDouble() * 0.4;
                Ellipse puff = new Ellipse
                {
                    Width = 2.5 + _rng.NextDouble() * 2.5,
                    Height = 2.5 + _rng.NextDouble() * 2.5,
                    Fill = new SolidColorBrush(Color.FromRgb(250, 252, 255))
                };
                c.Shape = puff;
                System.Windows.Controls.Panel.SetZIndex(puff, 80);
                Canvas.SetLeft(puff, c.X);
                Canvas.SetTop(puff, c.Y);
                _confetti.Add(c);
                _roofCanvas.Children.Add(puff);
            }
        }

        void MoveRoofDropsOff(Roof roof)
        {
            for (int i = 0; i < _drops.Count; i++)
            {
                SnowDrop d = _drops[i];
                if (d.RoofHwnd != roof.Hwnd || d.Held) continue;
                d.RoofHwnd = IntPtr.Zero;
                d.Stuck = false;
                if (d.IsTree) d.RotTarget = d.Rot;
                d.Host = _canvas;
                Canvas prev = d.Shape.Parent as Canvas;
                if (prev != null && prev != _canvas)
                {
                    prev.Children.Remove(d.Shape);
                    _canvas.Children.Add(d.Shape);
                }
                System.Windows.Controls.Panel.SetZIndex(d.Shape, 70);
            }
        }

        void RoofMeltAndSmooth()
        {
            double dt = 0.16;
            for (int ri = 0; ri < _roofs.Count; ri++)
            {
                double[] dep = _roofs[ri].Depths;
                if (dep == null) continue;
                for (int i = 0; i < dep.Length; i++)
                {
                    double excess = dep[i] - DriftSoftCap;
                    if (excess > 0.0) dep[i] -= (excess * 0.3) * dt;
                    if (dep[i] > _maxDrift) dep[i] = _maxDrift;
                    if (dep[i] < 0) dep[i] = 0;
                }
                double[] tmp = new double[dep.Length];
                for (int i = 0; i < dep.Length; i++) tmp[i] = dep[i];
                for (int i = 1; i < dep.Length - 1; i++)
                {
                    dep[i] = (tmp[i - 1] + 2.0 * tmp[i] + tmp[i + 1]) * 0.25;
                }
            }
        }

        void UpdateRoofShapes()
        {
            if (_roofCanvas == null) return;
            for (int ri = 0; ri < _roofs.Count; ri++)
            {
                Roof r = _roofs[ri];
                if (r.Depths == null) continue;
                if (r.Poly == null)
                {
                    r.Poly = new Polygon();
                    r.Poly.Fill = new SolidColorBrush(Color.FromRgb(245, 250, 255));
                    r.Poly.Opacity = 0.95;
                    System.Windows.Controls.Panel.SetZIndex(r.Poly, 50);
                    _roofCanvas.Children.Add(r.Poly);
                }
                PointCollection pts = new PointCollection();
                pts.Add(new Point(r.L - 3, r.T + 3));
                for (int i = 0; i < r.Depths.Length; i++)
                {
                    pts.Add(new Point(r.L + i * _driftSegment, r.T - r.Depths[i]));
                }
                pts.Add(new Point(r.R + 3, r.T + 3));
                pts.Add(new Point(r.R + 3, r.T + 5));
                pts.Add(new Point(r.L - 3, r.T + 5));
                r.Poly.Points = pts;
                Geometry clip = ComputeRoofClip(r);
                r.Poly.Clip = clip;
            }

            for (int i = 0; i < _drops.Count; i++)
            {
                SnowDrop dr = _drops[i];
                if (dr.Held || dr.RoofHwnd == IntPtr.Zero)
                {
                    if (dr.Shape.Clip != null) dr.Shape.Clip = null;
                    continue;
                }
                Roof owner = RoofByHwnd(dr.RoofHwnd);
                dr.Shape.Clip = (owner != null) ? ComputeRoofClip(owner) : null;
            }
        }

        void DepositRoof(Roof roof, double x, double amount)
        {
            if (roof.Depths == null) return;
            double share = amount / roof.Depths.Length;
            for (int i = 0; i < roof.Depths.Length; i++)
            {
                roof.Depths[i] += share * 0.7;
            }
            int idx = (int)((x - roof.L) / _driftSegment);
            if (idx < 0) idx = 0;
            if (idx >= roof.Depths.Length) idx = roof.Depths.Length - 1;
            for (int d = -2; d <= 2; d++)
            {
                int j = idx + d;
                if (j < 0 || j >= roof.Depths.Length) continue;
                roof.Depths[j] += amount * 0.3 / (1 + d * d);
            }
        }

        Roof RoofByHwnd(IntPtr h)
        {
            for (int i = 0; i < _roofs.Count; i++)
            {
                if (_roofs[i].Hwnd == h) return _roofs[i];
            }
            return null;
        }

        Roof FindLandingRoof(SnowDrop d, double h)
        {
            if (_roofCanvas == null) return null;
            double cx = d.X + d.Shape.Width / 2.0;
            double prevBottom = d.PrevY + h;
            double bottom = d.Y + h;
            for (int i = 0; i < _roofs.Count; i++)
            {
                Roof r = _roofs[i];
                if (r.Depths == null) continue;
                if (cx >= r.L && cx <= r.R && prevBottom <= r.T + 4.0 && bottom >= r.T - r.Depths[Math.Max(0, Math.Min(r.Depths.Length - 1, (int)((cx - r.L) / _driftSegment)))] )
                {
                    return r;
                }
            }
            return null;
        }

        void LandDropOnRoof(SnowDrop d, Roof roof, double h)
        {
            d.Stuck = true;
            d.Flung = false;
            d.RotSpeed = 0.0;
            d.RoofHwnd = roof.Hwnd;
            d.Sink = d.IsTree ? 6.0 : 4.0;
            d.Y = roof.T - h + d.Sink;
            d.Host = _roofCanvas;
            Canvas prev = d.Shape.Parent as Canvas;
            if (prev != null && prev != _roofCanvas) prev.Children.Remove(d.Shape);
            _roofCanvas.Children.Add(d.Shape);
            System.Windows.Controls.Panel.SetZIndex(d.Shape, 45);
            RoofPuff(d.X + d.Shape.Width / 2.0, roof.T, 8);
        }

        bool HasGroundGift()
        {
            for (int i = 0; i < _drops.Count; i++)
            {
                SnowDrop d = _drops[i];
                if (!d.IsTree && d.Stuck && !d.Popped && d.RoofHwnd == IntPtr.Zero) return true;
            }
            return false;
        }

        void SnowSplash(double x, double y, int count)
        {
            for (int i = 0; i < count; i++)
            {
                Confetto c = new Confetto();
                double side = (_rng.NextDouble() < 0.5) ? -1.0 : 1.0;
                double speed = 60.0 + _rng.NextDouble() * 170.0;
                c.X = x + side * _rng.NextDouble() * 10.0;
                c.Y = y - 4.0 - _rng.NextDouble() * 8.0;
                c.Vx = side * speed;
                c.Vy = -60.0 - _rng.NextDouble() * 100.0;
                c.Life = 0.6 + _rng.NextDouble() * 0.5;
                Ellipse puff = new Ellipse
                {
                    Width = 3.0 + _rng.NextDouble() * 3.5,
                    Height = 3.0 + _rng.NextDouble() * 3.5,
                    Fill = new SolidColorBrush(Color.FromRgb(250, 252, 255))
                };
                puff.Opacity = 0.95;
                c.Shape = puff;
                System.Windows.Controls.Panel.SetZIndex(puff, 80);
                Canvas.SetLeft(c.Shape, c.X);
                Canvas.SetTop(c.Shape, c.Y);
                _confetti.Add(c);
                _canvas.Children.Add(c.Shape);
            }
        }

        Canvas BuildGrinch()
        {
            Canvas g = new Canvas();
            g.Width = 64;
            g.Height = 72;
            Brush fur = new SolidColorBrush(Color.FromRgb(96, 130, 52));
            Brush face = new SolidColorBrush(Color.FromRgb(130, 170, 70));
            Brush darkgreen = new SolidColorBrush(Color.FromRgb(36, 54, 20));

            g.Children.Add(ShapeAt(new Ellipse { Width = 34, Height = 40, Fill = fur }, 12, 28));
            g.Children.Add(ShapeAt(new Ellipse { Width = 18, Height = 26, Fill = face }, 20, 34));
            g.Children.Add(ShapeAt(new Ellipse { Width = 28, Height = 24, Fill = face }, 20, 4));
            g.Children.Add(ShapeAt(new Ellipse { Width = 6, Height = 5, Fill = new SolidColorBrush(Color.FromRgb(240, 220, 80)) }, 27, 12));
            g.Children.Add(ShapeAt(new Ellipse { Width = 6, Height = 5, Fill = new SolidColorBrush(Color.FromRgb(240, 220, 80)) }, 36, 12));
            g.Children.Add(ShapeAt(new Ellipse { Width = 2.5, Height = 2.5, Fill = Brushes.Black }, 29, 13.5));
            g.Children.Add(ShapeAt(new Ellipse { Width = 2.5, Height = 2.5, Fill = Brushes.Black }, 38, 13.5));

            Polyline brows = new Polyline();
            brows.Points = new PointCollection(new Point[] { new Point(26, 10), new Point(31, 12), new Point(35, 10) });
            brows.Stroke = darkgreen;
            brows.StrokeThickness = 1.8;
            g.Children.Add(brows);

            Polyline grin = new Polyline();
            grin.Points = new PointCollection(new Point[] { new Point(28, 22), new Point(31, 25), new Point(34, 22), new Point(37, 25), new Point(40, 22) });
            grin.Stroke = new SolidColorBrush(Color.FromRgb(25, 12, 12));
            grin.StrokeThickness = 1.6;
            g.Children.Add(grin);

            Polyline tuft = new Polyline();
            tuft.Points = new PointCollection(new Point[] { new Point(32, 4), new Point(35, -3), new Point(38, 5) });
            tuft.Stroke = darkgreen;
            tuft.StrokeThickness = 2;
            g.Children.Add(tuft);

            g.Children.Add(new Line { X1 = 42, Y1 = 36, X2 = 54, Y2 = 46, Stroke = fur, StrokeThickness = 3.5 });
            g.Children.Add(new Line { X1 = 22, Y1 = 64, X2 = 18, Y2 = 72, Stroke = darkgreen, StrokeThickness = 3 });
            g.Children.Add(new Line { X1 = 34, Y1 = 64, X2 = 38, Y2 = 72, Stroke = darkgreen, StrokeThickness = 3 });

            Canvas loot = BuildGift();
            loot.Opacity = 0.95;
            loot.Visibility = Visibility.Collapsed;
            _grinchLoot = loot;
            g.Children.Add(ShapeAt(loot, 44, 34));

            return g;
        }

        void SpawnGrinch()
        {
            _grinch = BuildGrinch();
            _grinchDir = (_rng.NextDouble() < 0.5) ? 1 : -1;
            _grinchX = (_grinchDir > 0) ? -90.0 : W() + 26.0;
            TransformGroup tg = new TransformGroup();
            if (_grinchDir < 0) tg.Children.Add(new ScaleTransform(-1, 1));
            _grinchRot = new RotateTransform(0);
            tg.Children.Add(_grinchRot);
            _grinch.RenderTransformOrigin = new Point(0.5, 0.5);
            _grinch.RenderTransform = tg;
            Canvas.SetLeft(_grinch, _grinchX);
            Canvas.SetTop(_grinch, _groundY - 72.0);
            System.Windows.Controls.Panel.SetZIndex(_grinch, 60);
            _canvas.Children.Add(_grinch);
            _grinchActive = true;
            _grinchDead = false;
            _grinchSink = 0.0;
            _grinchSteals = 0;
            _grinchStealTimer = 1.0;
            _grinchPhase = 0.0;
            Log("grinch appeared");
        }

        void GrinchDie()
        {
            if (_grinch == null || _grinchDead || !_grinchActive) return;
            _grinchDead = true;
            if (_grinchLoot != null) _grinchLoot.Visibility = Visibility.Collapsed;
            double landY = _groundY - _depths[DriftIndexFor(_grinchX + 32.0)];
            SnowSplash(_grinchX + 32.0, landY, 10);
            Log("grinch downed");
        }

        void GrinchTick(double dt)
        {
            if (_grinchPending)
            {
                _grinchDelay -= dt;
                if (_grinchDelay <= 0.0)
                {
                    _grinchPending = false;
                    SpawnGrinch();
                }
            }
            _grinchIdleTimer -= dt;
            if (_grinchIdleTimer <= 0.0)
            {
                _grinchIdleTimer = 18.0 + _rng.NextDouble() * 22.0;
                if (!_grinchActive && !_grinchPending && HasGroundGift() && _rng.NextDouble() < 0.55)
                {
                    SpawnGrinch();
                }
            }
            if (!_grinchActive || _grinch == null) return;

            double w = 64.0, h = 72.0;
            double landY = _groundY - _depths[DriftIndexFor(_grinchX + w / 2.0)];

            if (_grinchDead)
            {
                if (_grinchRot.Angle < 94.0)
                {
                    _grinchRot.Angle = Math.Min(94.0, _grinchRot.Angle + 260.0 * dt);
                }
                else
                {
                    _grinchSink += 10.0 * dt;
                    if (_grinchSink >= h)
                    {
                        _canvas.Children.Remove(_grinch);
                        _grinch = null;
                        _grinchActive = false;
                        Log("grinch buried");
                        return;
                    }
                }
                Canvas.SetLeft(_grinch, _grinchX);
                Canvas.SetTop(_grinch, landY - h + _grinchSink);
                if (_driftTick % 10 == 0) SnowSplash(_grinchX + w / 2.0, landY, 3);
                return;
            }

            _grinchPhase += dt * 8.0;
            _grinchX += _grinchDir * 95.0 * dt;
            double bob = Math.Sin(_grinchPhase * 2.0) * 1.5;
            Canvas.SetLeft(_grinch, _grinchX);
            Canvas.SetTop(_grinch, landY - h + bob);

            _grinchStealTimer -= dt;
            if (_grinchStealTimer <= 0.0)
            {
                _grinchStealTimer = 0.45;
                double handX = _grinchX + ((_grinchDir > 0) ? w + 6.0 : -6.0);
                for (int i = 0; i < _drops.Count; i++)
                {
                    SnowDrop d = _drops[i];
                    if (d.IsTree || d.Popped || d.Held || !d.Stuck || d.RoofHwnd != IntPtr.Zero) continue;
                    double dcx = d.X + d.Shape.Width / 2.0;
                    if (Math.Abs(dcx - handX) < 30.0)
                    {
                        _canvas.Children.Remove(d.Shape);
                        d.Popped = true;
                        _grinchLoot.Visibility = Visibility.Visible;
                        _grinchSteals++;
                        SnowSplash(dcx, landY, 6);
                        Log("grinch stole a gift");
                        break;
                    }
                }
            }

            if ((_grinchDir > 0 && _grinchX > W() + 40.0) || (_grinchDir < 0 && _grinchX < -140.0))
            {
                _canvas.Children.Remove(_grinch);
                _grinch = null;
                _grinchActive = false;
                Log("grinch left");
            }
        }

        void UpdateConfetti(double dt)
        {
            for (int i = _confetti.Count - 1; i >= 0; i--)
            {
                Confetto c = _confetti[i];
                c.Life -= dt;
                if (c.Life <= 0.0 || c.Y > H() + 10)
                {
                    RemoveShape(c.Shape);
                    _confetti.RemoveAt(i);
                    continue;
                }
                c.Vy += 350.0 * dt;
                c.X += c.Vx * dt;
                c.Y += c.Vy * dt;
                c.Shape.Opacity = Math.Min(1.0, c.Life);
                Canvas.SetLeft(c.Shape, c.X);
                Canvas.SetTop(c.Shape, c.Y);
            }
        }

        void PopDrop(SnowDrop d)
        {
            if (d.Popped) return;
            d.Popped = true;
            double cx = Canvas.GetLeft(d.Shape) + d.Shape.Width / 2.0;
            double cy = Canvas.GetTop(d.Shape) + d.Shape.Height / 2.0;
            RemoveShape(d.Shape);
            ConfettiBurst(cx, cy, 26);
        }

        void ConfettiBurst(double cx, double cy, int count)
        {
            Color[] palette = new Color[]
            {
                Color.FromRgb(230, 40, 40), Color.FromRgb(40, 110, 230),
                Color.FromRgb(250, 200, 40), Color.FromRgb(50, 190, 70),
                Color.FromRgb(240, 120, 40), Color.FromRgb(230, 70, 180)
            };

            for (int i = 0; i < count; i++)
            {
                Confetto c = new Confetto();
                double angle = _rng.NextDouble() * Math.PI * 2.0;
                double speed = 60.0 + _rng.NextDouble() * 180.0;
                c.X = cx;
                c.Y = cy;
                c.Vx = Math.Cos(angle) * speed;
                c.Vy = Math.Sin(angle) * speed - 90.0;
                c.Life = 1.0 + _rng.NextDouble() * 0.8;
                c.Shape = new Rectangle
                {
                    Width = 6,
                    Height = 5,
                    Fill = new SolidColorBrush(palette[_rng.Next(0, palette.Length)])
                };
                System.Windows.Controls.Panel.SetZIndex(c.Shape, 80);
                Canvas.SetLeft(c.Shape, c.X);
                Canvas.SetTop(c.Shape, c.Y);
                _confetti.Add(c);
                _canvas.Children.Add(c.Shape);
            }
        }

        double W() { return SystemParameters.VirtualScreenWidth; }
        double H() { return SystemParameters.VirtualScreenHeight; }

        [STAThread]
        public static void Main()
        {
            _logPath = FindLogPath();
            try
            {
                SWF.Application.EnableVisualStyles();
                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                SnowWindow window = new SnowWindow();
                app.Run(window);
                Log("normal exit");
            }
            catch (Exception ex)
            {
                Log("FATAL: " + ex);
                System.Windows.MessageBox.Show(ex.ToString(), "DesktopSnow: error",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
}
