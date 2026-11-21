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

        const int WH_MOUSE_LL = 14;
        const int WM_LBUTTONDOWN = 0x0201;

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
        readonly List<Drop> _drops = new List<Drop>();
        readonly List<Confetto> _confetti = new List<Confetto>();
        HookProc _hookProc;
        IntPtr _hook = IntPtr.Zero;

        class Drop
        {
            public bool IsTree;
            public bool Stuck;
            public bool Popped;
            public double X;
            public double Y;
            public double Vx;
            public double Vy;
            public double Sink;
            public double Rot;
            public double RotSpeed;
            public RotateTransform Rtf;
            public Canvas Shape;
        }

        class Confetto
        {
            public double X;
            public double Y;
            public double Vx;
            public double Vy;
            public double Life;
            public Rectangle Shape;
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
            if (nCode >= 0 && wParam.ToInt32() == WM_LBUTTONDOWN)
            {
                MSLLHOOKSTRUCT info = (MSLLHOOKSTRUCT)Marshal.PtrToStructure(lParam, typeof(MSLLHOOKSTRUCT));
                double mx = info.pt.X - SystemParameters.VirtualScreenLeft;
                double my = info.pt.Y - SystemParameters.VirtualScreenTop;
                Drop hit = FindClickableDrop(mx, my);
                if (hit != null)
                {
                    Dispatcher.BeginInvoke((Action)delegate { PopDrop(hit); });
                    return (IntPtr)1;
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        Drop FindClickableDrop(double mx, double my)
        {
            for (int i = 0; i < _drops.Count; i++)
            {
                Drop d = _drops[i];
                if (d.Popped || d.IsTree || !d.Stuck) continue;
                double l = Canvas.GetLeft(d.Shape), t = Canvas.GetTop(d.Shape);
                if (mx >= l - 6 && mx <= l + 30 && my >= t - 6 && my <= t + 26)
                {
                    return d;
                }
            }
            return null;
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

            if (_mode == SnowMode.DesktopOnly && _driftTick % 300 == 0 && !_attachedBehindIcons)
            {
                SetWindowPos(_hwnd, HWND_BOTTOM, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
            }

            for (int i = _flakes.Count - 1; i >= 0; i--)
            {
                Flake f = _flakes[i];
                f.Phase += dt * f.SwaySpeed;
                f.Y += f.Speed * dt;
                f.X = f.BaseX + Math.Sin(f.Phase) * f.SwayAmp + _wind;

                if (f.Y >= _groundY - _depths[DriftIndexFor(f.X)])
                {
                    DepositSnow(f.X, f.R * 5.0);
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
                Drop d = _drops[i];
                if (d.Popped)
                {
                    _drops.RemoveAt(i);
                    continue;
                }

                double landY = _groundY - _depths[DriftIndexFor(d.X)];
                double h = d.IsTree ? 44.0 : 20.0;

                if (!d.Stuck)
                {
                    d.Vy += 500.0 * dt;
                    d.X += d.Vx * dt;
                    d.Y += d.Vy * dt;
                    d.Rot += d.RotSpeed * dt;
                    if (d.Rtf != null) d.Rtf.Angle = d.Rot;
                    if (d.Y + h >= landY)
                    {
                        d.Stuck = true;
                        d.Sink = 0.0;
                        d.Y = landY - h;
                        System.Windows.Controls.Panel.SetZIndex(d.Shape, 40);
                    }
                }
                else
                {
                    double sinkSpeed = d.IsTree ? 0.35 : 0.6; // скорость закапывания
                    d.Sink += sinkSpeed * dt;
                    d.Y = landY - h + d.Sink;
                    if (d.Sink >= h)
                    {
                        _canvas.Children.Remove(d.Shape);
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

                Line neck = new Line { X1 = cx + 10, Y1 = 37, X2 = cx + 16, Y2 = 24, Stroke = fur, StrokeThickness = 4 };
                train.Children.Add(neck);

                train.Children.Add(ShapeAt(new Ellipse { Width = 9, Height = 9, Fill = fur }, cx + 13, 18));

                Polyline antlers = new Polyline();
                antlers.Points = new PointCollection
                {
                    new Point(cx + 17, 18), new Point(cx + 20, 6), new Point(cx + 24, 11),
                    new Point(cx + 27, 3)
                };
                antlers.Stroke = dark;
                antlers.StrokeThickness = 2;
                train.Children.Add(antlers);

                Polyline antlers2 = new Polyline();
                antlers2.Points = new PointCollection(new Point[]
                {
                    new Point(cx + 17, 18), new Point(cx + 13, 7)
                });
                antlers2.Stroke = dark;
                antlers2.StrokeThickness = 2;
                train.Children.Add(antlers2);

                train.Children.Add(ShapeAt(new Ellipse { Width = 4, Height = 4, Fill = new SolidColorBrush(Color.FromRgb(230, 40, 40)) }, cx + 21, 21));

                Polyline legs = new Polyline();
                legs.Points = new PointCollection(new Point[]
                {
                    new Point(cx - 9, 44), new Point(cx - 11, 58),
                    new Point(cx + 2, 44), new Point(cx + 0, 58),
                    new Point(cx + 9, 44), new Point(cx + 12, 57)
                });
                legs.Stroke = dark;
                legs.StrokeThickness = 2;
                train.Children.Add(legs);
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
            Drop d = new Drop();
            d.IsTree = tree;
            d.X = x;
            d.Y = y;
            d.Vx = vx;
            d.Vy = vy;
            d.RotSpeed = -110.0 + _rng.NextDouble() * 220.0;
            d.Rtf = new RotateTransform(0);
            d.Shape = tree ? BuildTree() : BuildGift();
            d.Shape.RenderTransformOrigin = new Point(0.5, 0.5);
            d.Shape.RenderTransform = d.Rtf;
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
            gift.Width = 24;
            gift.Height = 20;

            Color cc = boxColors[_rng.Next(0, boxColors.Length)];
            gift.Children.Add(ShapeAt(new Rectangle { Width = 20, Height = 16, RadiusX = 2, RadiusY = 2, Fill = new SolidColorBrush(cc) }, 2, 3));
            gift.Children.Add(ShapeAt(new Rectangle { Width = 4, Height = 16, Fill = new SolidColorBrush(Color.FromRgb(250, 240, 200)) }, 10, 3));
            gift.Children.Add(ShapeAt(new Rectangle { Width = 20, Height = 4, Fill = new SolidColorBrush(Color.FromRgb(250, 240, 200)) }, 2, 9));
            gift.Children.Add(ShapeAt(new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(Color.FromRgb(250, 240, 200)) }, 9, 0.5));

            return gift;
        }

        Canvas BuildTree()
        {
            Canvas tree = new Canvas();
            tree.Width = 34;
            tree.Height = 44;
            Brush green = new SolidColorBrush(Color.FromRgb(30, 110, 50));
            Brush snowCap = new SolidColorBrush(Color.FromRgb(240, 248, 255));

            tree.Children.Add(ShapeAt(new Rectangle { Width = 6, Height = 9, Fill = new SolidColorBrush(Color.FromRgb(101, 67, 33)) }, 14, 35));

            Polygon t1 = new Polygon();
            t1.Points = new PointCollection(new Point[] { new Point(17, 14), new Point(5, 35), new Point(29, 35) });
            t1.Fill = green;
            tree.Children.Add(t1);

            Polygon t2 = new Polygon();
            t2.Points = new PointCollection(new Point[] { new Point(17, 2), new Point(7, 22), new Point(27, 22) });
            t2.Fill = green;
            tree.Children.Add(t2);

            Polygon cap = new Polygon();
            cap.Points = new PointCollection(new Point[] { new Point(17, 2), new Point(12, 11), new Point(22, 11) });
            cap.Fill = snowCap;
            tree.Children.Add(cap);

            return tree;
        }

        void UpdateConfetti(double dt)
        {
            for (int i = _confetti.Count - 1; i >= 0; i--)
            {
                Confetto c = _confetti[i];
                c.Life -= dt;
                if (c.Life <= 0.0 || c.Y > H() + 10)
                {
                    _canvas.Children.Remove(c.Shape);
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

        void PopDrop(Drop d)
        {
            if (d.Popped) return;
            d.Popped = true;
            double cx = Canvas.GetLeft(d.Shape) + 12;
            double cy = Canvas.GetTop(d.Shape) + 10;
            _canvas.Children.Remove(d.Shape);

            Color[] palette = new Color[]
            {
                Color.FromRgb(230, 40, 40), Color.FromRgb(40, 110, 230),
                Color.FromRgb(250, 200, 40), Color.FromRgb(50, 190, 70),
                Color.FromRgb(240, 120, 40), Color.FromRgb(230, 70, 180)
            };

            for (int i = 0; i < 26; i++)
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
                    Width = 5,
                    Height = 4,
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
