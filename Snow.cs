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
        readonly List<Gift> _fallingGifts = new List<Gift>();
        readonly List<Canvas> _stuckGifts = new List<Canvas>();

        class Gift
        {
            public double X;
            public double Y;
            public double Vx;
            public double Vy;
            public Canvas Shape;
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
            _tray.Visible = false;
            Application.Current.Shutdown();
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
                Canvas.SetLeft(_train, _trainDir > 0 ? _trainX : _trainX - 150);
                _dropTimer -= dt;
                if (_dropTimer <= 0.0)
                {
                    _dropTimer = 0.5 + _rng.NextDouble() * 0.6; // интервал между подарками
                    double giftX = _trainX + 60 + _rng.NextDouble() * 50;
                    SpawnGift(giftX, _trainY + 30, _trainDir * 30.0 + _wind, 0.0);
                }

                if ((_trainDir > 0 && _trainX > W() + 60) || (_trainDir < 0 && _trainX < -220))
                {
                    _canvas.Children.Remove(_train);
                    _train = null;
                    _trainActive = false;
                    Log("santa pass finished");
                }
            }

            double gravity = 500.0;
            for (int i = _fallingGifts.Count - 1; i >= 0; i--)
            {
                Gift g = _fallingGifts[i];
                g.Vy += gravity * dt;
                g.X += g.Vx * dt;
                g.Y += g.Vy * dt;

                double landY = _groundY - _depths[DriftIndexFor(g.X)];
                if (g.Y >= landY)
                {
                    _fallingGifts.RemoveAt(i);
                    Canvas.SetLeft(g.Shape, g.X);
                    Canvas.SetTop(g.Shape, landY - 9);
                    _stuckGifts.Add(g.Shape);
                    if (_stuckGifts.Count > 20)
                    {
                        Canvas oldest = _stuckGifts[0];
                        _stuckGifts.RemoveAt(0);
                        _canvas.Children.Remove(oldest);
                    }
                    continue;
                }
                Canvas.SetLeft(g.Shape, g.X);
                Canvas.SetTop(g.Shape, g.Y);
            }
        }

        void SpawnTrain()
        {
            _trainDir = (_rng.NextDouble() < 0.5) ? 1 : -1;
            _trainX = (_trainDir > 0) ? -260.0 : W() + 260.0;
            _trainY = 50 + _rng.NextDouble() * (H() * 0.25);
            _dropTimer = 0.6;

            _train = BuildTrain();
            if (_trainDir < 0)
            {
                _train.RenderTransformOrigin = new Point(0.5, 0.5);
                _train.RenderTransform = new ScaleTransform(-1, 1);
            }
            Canvas.SetLeft(_train, _trainDir > 0 ? _trainX : _trainX - 150);
            Canvas.SetTop(_train, _trainY);
            _canvas.Children.Add(_train);
            _trainActive = true;
            Log("santa pass started");
        }

        Canvas BuildTrain()
        {
            Canvas train = new Canvas();
            train.Width = 150;
            train.Height = 70;

            // олени (справа, упряжка летит вправо)
            int[] deerX = new int[] { 62, 92, 122 };
            foreach (int cx in deerX)
            {
                train.Children.Add(ShapeAt(new Ellipse
                {
                    Width = 20, Height = 11,
                    Fill = new SolidColorBrush(Color.FromRgb(107, 66, 38))
                }, cx - 10, 25));

                train.Children.Add(ShapeAt(new Ellipse
                {
                    Width = 7, Height = 7,
                    Fill = new SolidColorBrush(Color.FromRgb(107, 66, 38))
                }, cx + 7, 15));

                Polyline antlers = new Polyline();
                antlers.Points = new PointCollection
                {
                    new Point(cx + 10, 16), new Point(cx + 13, 8),
                    new Point(cx + 16, 12), new Point(cx + 18, 6)
                };
                antlers.Stroke = new SolidColorBrush(Color.FromRgb(63, 39, 18));
                antlers.StrokeThickness = 1.5;
                train.Children.Add(antlers);

                train.Children.Add(ShapeAt(new Ellipse
                {
                    Width = 3.5, Height = 3.5,
                    Fill = new SolidColorBrush(Color.FromRgb(220, 40, 40))
                }, cx + 13, 17));

                Polyline legs = new Polyline();
                legs.Points = new PointCollection
                {
                    new Point(cx - 3, 34), new Point(cx - 6, 42),
                    new Point(cx + 3, 34), new Point(cx + 6, 41)
                };
                legs.Stroke = new SolidColorBrush(Color.FromRgb(63, 39, 18));
                legs.StrokeThickness = 1.5;
                train.Children.Add(legs);
            }

            // поводья
            Polyline reins = new Polyline();
            reins.Points = new PointCollection
            {
                new Point(34, 28), new Point(66, 25), new Point(96, 25),
                new Point(126, 25)
            };
            reins.Stroke = new SolidColorBrush(Color.FromRgb(90, 58, 26));
            reins.StrokeThickness = 1;
            train.Children.Add(reins);

            // мешок с подарками
            train.Children.Add(ShapeAt(new Ellipse
            {
                Width = 14, Height = 16,
                Fill = new SolidColorBrush(Color.FromRgb(138, 90, 43))
            }, -6, 14));

            // сани
            Rectangle sleigh = new Rectangle
            {
                Width = 36, Height = 14,
                RadiusX = 4, RadiusY = 4,
                Fill = new SolidColorBrush(Color.FromRgb(204, 34, 34))
            };
            train.Children.Add(ShapeAt(sleigh, 0, 26));

            Rectangle trim = new Rectangle
            {
                Width = 36, Height = 3,
                Fill = new SolidColorBrush(Color.FromRgb(230, 190, 60))
            };
            train.Children.Add(ShapeAt(trim, 0, 26));

            Polyline runner = new Polyline();
            runner.Points = new PointCollection
            {
                new Point(2, 46), new Point(30, 46), new Point(34, 42)
            };
            runner.Stroke = new SolidColorBrush(Color.FromRgb(230, 190, 60));
            runner.StrokeThickness = 1.5;
            train.Children.Add(runner);

            // Санта
            train.Children.Add(ShapeAt(new Ellipse
            {
                Width = 12, Height = 11,
                Fill = new SolidColorBrush(Color.FromRgb(204, 34, 34))
            }, 20, 16));
            train.Children.Add(ShapeAt(new Ellipse
            {
                Width = 7, Height = 7,
                Fill = new SolidColorBrush(Color.FromRgb(240, 200, 160))
            }, 22, 8));
            train.Children.Add(ShapeAt(new Ellipse
            {
                Width = 8, Height = 6,
                Fill = Brushes.White
            }, 21, 12));

            Polygon hat = new Polygon();
            hat.Points = new PointCollection
            {
                new Point(20, 8), new Point(29, 8), new Point(31, 0)
            };
            hat.Fill = new SolidColorBrush(Color.FromRgb(204, 34, 34));
            train.Children.Add(hat);
            train.Children.Add(ShapeAt(new Ellipse
            {
                Width = 4, Height = 4,
                Fill = Brushes.White
            }, 30, 0));

            return train;
        }

        FrameworkElement ShapeAt(FrameworkElement el, double x, double y)
        {
            Canvas.SetLeft(el, x);
            Canvas.SetTop(el, y);
            return el;
        }

        void SpawnGift(double x, double y, double vx, double vy)
        {
            Gift g = new Gift();
            g.X = x;
            g.Y = y;
            g.Vx = vx;
            g.Vy = vy;
            g.Shape = BuildGift();
            Canvas.SetLeft(g.Shape, x);
            Canvas.SetTop(g.Shape, y);
            _fallingGifts.Add(g);
            _canvas.Children.Add(g.Shape);
        }

        Canvas BuildGift()
        {
            byte[] boxColors = new byte[] { 200, 40, 60, 160, 220, 40, 80 };
            Canvas gift = new Canvas();
            gift.Width = 12;
            gift.Height = 10;

            Rectangle box = new Rectangle
            {
                Width = 10, Height = 8,
                RadiusX = 1, RadiusY = 1,
                Fill = new SolidColorBrush(Color.FromRgb(
                    boxColors[_rng.Next(0, 3)],
                    boxColors[_rng.Next(3, 6)],
                    boxColors[_rng.Next(5, 7)]))
            };
            gift.Children.Add(ShapeAt(box, 1, 1));

            Rectangle vRibbon = new Rectangle
            {
                Width = 2, Height = 8,
                Fill = new SolidColorBrush(Color.FromRgb(250, 240, 200))
            };
            gift.Children.Add(ShapeAt(vRibbon, 5, 1));

            Rectangle hRibbon = new Rectangle
            {
                Width = 10, Height = 2,
                Fill = new SolidColorBrush(Color.FromRgb(250, 240, 200))
            };
            gift.Children.Add(ShapeAt(hRibbon, 1, 4));

            return gift;
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
