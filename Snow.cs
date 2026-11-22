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
using System.Threading;
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
        [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(POINT point);
        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
        [DllImport("user32.dll")] static extern IntPtr GetDesktopWindow();
        [DllImport("user32.dll", CharSet = CharSet.Auto)] static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder name, int count);
        [DllImport("user32.dll")] static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint vk);
        [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left; public int Top; public int Right; public int Bottom; }

        const int WH_MOUSE_LL = 14;
        const int WS_CAPTION = 0x00C00000;
        const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
        const int DWMWA_CLOAKED = 14;
        const int WM_MOUSEMOVE = 0x0200;
        const int WM_LBUTTONDOWN = 0x0201;
        const int WM_LBUTTONUP = 0x0202;
        const int WM_HOTKEY = 0x0312;
        const int HOTKEY_ID = 1;
        const uint HOTKEY_MODS = 0x0002 | 0x0001;
        const uint HOTKEY_VK_S = 0x53;
        const uint GA_ROOT = 2;

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
        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_SHOWWINDOW = 0x0040;
        const uint SWP_FRAMECHANGED = 0x0020;
        static readonly IntPtr HWND_BOTTOM = new IntPtr(1);
        static readonly IntPtr HWND_TOPMOST = new IntPtr(-1);

        const string AppName = "DesktopSnow";
        const string RunKeyPath = "Software\\Microsoft\\Windows\\CurrentVersion\\Run";
        const string SettingsKeyPath = "Software\\DesktopSnow";

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

        enum Season { Winter = 0, Spring = 1, Summer = 2, Autumn = 3 }

        Canvas _canvas;
        DispatcherTimer _timer;
        Stopwatch _clock;
        readonly List<Flake> _flakes = new List<Flake>();
        double _t;
        double _wind;
        double _sparkleTimer = 0.4;
        double _decoTimer = 1.2;
        SnowBall _ballHeld;
        double _ballVx;
        double _ballVy;
        double _ballLastX;
        double _ballLastY;
        uint _ballLastT;
        readonly List<SnowBall> _balls = new List<SnowBall>();
        readonly List<Snowman> _snowmen = new List<Snowman>();
        Canvas _rabbit;
        double _rabbitX;
        double _rabbitDir = 1.0;
        double _rabbitTarget;
        int _rabbitState;
        double _rabbitStateT;
        double _rabbitPhase;
        TextBlock _countdownText;
        Rectangle _countdownBoard;
        TextBlock _nyBanner;
        double _nyBannerLife = -1.0;
        bool _nyDone;
        double _showTimer = -1.0;
        int _showLeft;
        readonly List<Rocket> _rockets = new List<Rocket>();
        readonly List<ShootingStar> _stars = new List<ShootingStar>();
        double _fireworkTimer = 22.0;
        double _starTimer = 30.0;
        Canvas _moon;
        Ellipse _moonGlow;
        int _bulbPhase = 0;
        readonly List<Ellipse> _treeBulbs = new List<Ellipse>();
        readonly List<Color> _treeBulbColors = new List<Color>();
        readonly List<Sparkle> _sparkles = new List<Sparkle>();
        readonly List<DriftDeco> _decos = new List<DriftDeco>();
        SWF.NotifyIcon _tray;
        SWF.MenuItem _miAutostart;
        SWF.MenuItem _miScreensaver;
        SWF.MenuItem _miIcons;
        Season _season = Season.Winter;
        Canvas _butterfly;
        ScaleTransform _bfWL;
        ScaleTransform _bfWR;
        double _bfX;
        double _bfBaseY;
        double _bfPhase;
        double _bfDir = 1.0;
        double _bfTimer = 6.0;
        double _bfFlightT;
        Canvas _bird;
        ScaleTransform _birdFlip;
        RotateTransform _birdWing;
        double _birdX;
        double _birdY;
        double _birdPhase;
        double _birdDir = 1.0;
        double _birdTimer = 10.0;
        double _birdDropT = 2.0;
        double _birdDropsLeft;
        readonly List<Feather> _feathers = new List<Feather>();
        bool _santaAutoEnabled = true;
        bool _grinchEnabled = true;
        bool _rabbitEnabled = true;
        bool _fireworksAuto = true;
        bool _countdownEnabled = true;
        bool _snowballsEnabled = true;
        bool _roofCapsEnabled = true;
        bool _sparklesEnabled = true;
        bool _iciclesEnabled = true;
        bool _birdEnabled = true;
        bool _butterflyEnabled = true;
        Window _settingsWin;
        IntPtr _settingsHwnd;
        System.Windows.Controls.Primitives.UniformGrid _ugA;
        System.Windows.Controls.Primitives.UniformGrid _ugT;
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
        bool _trainDowned;
        double _trainDownVy;
        double _trainDownSpin;
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
        readonly List<Canvas> _grinchStack = new List<Canvas>();
        double _grinchIdleTimer = 20.0;
        Window _roofWindow;
        Canvas _roofCanvas;
        IntPtr _roofHwnd = IntPtr.Zero;
        EnumWindowsProc _enumProc;
        readonly List<IntPtr> _enumList = new List<IntPtr>();
        readonly List<RECT> _enumRects = new List<RECT>();
        readonly List<Roof> _roofs = new List<Roof>();
        readonly HashSet<IntPtr> _roofSet = new HashSet<IntPtr>();
        bool _fullscreenPresent;
        readonly List<Icicle> _icicles = new List<Icicle>();
        readonly List<IcicleDrip> _icicleDrips = new List<IcicleDrip>();
        double _icicleTimer = 3.0;
        bool _grinchActive;
        bool _grinchDead;
        bool _grinchPending;
        int _grinchDir = 1;
        int _grinchSteals;
        double _grinchX;
        double _grinchDelay;
        double _grinchPhase;
        double _grinchSink;
        double _grinchDeadT;
        bool _grinchLanded;
        double _grinchSlide;
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
            public int LastMoveTick = -100;
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

        class Sparkle
        {
            public double X;
            public double Y;
            public double Life;
            public double MaxLife;
            public Ellipse Shape;
        }

        class DriftDeco
        {
            public double X;
            public double Y;
            public double W2;
            public double H2;
            public double EmbedOff;
            public double Life;
            public double MaxLife;
            public double BaseOp;
            public Roof Ref;
            public bool OnRoof;
            public FrameworkElement Shape;
        }

        class Rocket
        {
            public double X;
            public double Y;
            public double Vy;
            public double ExplodeY;
            public Ellipse Shape;
        }

        class ShootingStar
        {
            public double X;
            public double Y;
            public double Vx;
            public double Vy;
            public double Life;
            public double MaxLife;
            public Line Shape;
        }

        class SnowBall
        {
            public double X;
            public double Y;
            public double Vx;
            public double Vy;
            public double R;
            public bool Held;
            public bool Flying;
            public bool Standing;
            public int Level;
            public SnowBall Under;
            public Canvas Shape;
        }

        class Snowman
        {
            public double X;
            public Canvas Shape;
        }

        class Rabbit
        {
            public double X;
            public double Dir;
            public int State;
            public double Target;
            public double Phase;
            public double StateT;
            public Canvas Shape;
        }

        class Icicle
        {
            public Roof Roof;
            public int Seg;
            public double X;
            public double H;
            public double W;
            public double Phase;
            public Polygon Shape;
        }

        class IcicleDrip
        {
            public double X;
            public double Y;
            public double Life;
            public Ellipse Shape;
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
            public FrameworkElement Shape;
            public int Kind;
            public double Rot;
            public double RotSpeed;
            public RotateTransform Rtf;
            public Color Tint;
        }

        class Feather
        {
            public double X;
            public double Y;
            public double Vx;
            public double Vy;
            public double Life;
            public double Phase;
            public RotateTransform Rtf;
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
                InstallHotkey();
                SetupTray();
                EnsureAutostart();
                LoadSettings();
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
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_NOZORDER);
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
            _miScreensaver = new SWF.MenuItem("Эффекты поверх всех окон", OnClickScreensaver);
            _miIcons = new SWF.MenuItem("Эффекты за иконками (эксперимент)", OnClickBehindIcons);

            SWF.ContextMenu menu = new SWF.ContextMenu();
            menu.MenuItems.Add(_miScreensaver);
            menu.MenuItems.Add(_miIcons);
            menu.MenuItems.Add(new SWF.MenuItem("Вызвать Санту", delegate
            {
                if (!_trainActive) SpawnTrain();
            }));
            menu.MenuItems.Add(new SWF.MenuItem("Салют!", delegate { SpawnRocket(); SpawnRocket(); SpawnRocket(); }));
            menu.MenuItems.Add(new SWF.MenuItem("Больше эффектов", delegate { _targetCount += 80; TrimFlakes(); }));
            menu.MenuItems.Add(new SWF.MenuItem("Меньше эффектов", delegate { _targetCount = Math.Max(20, _targetCount - 80); TrimFlakes(); }));
            menu.MenuItems.Add(new SWF.MenuItem("Убрать все эффекты", delegate { ClearAllSnow(); }));
            menu.MenuItems.Add(new SWF.MenuItem("\u041d\u0430\u0441\u0442\u0440\u043e\u0439\u043a\u0438...", delegate
            {
                Dispatcher.BeginInvoke((Action)delegate { ShowSettingsWindow(); });
            }));
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
            _tray.Text = "DesktopSnow — эффекты на рабочем столе";
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

        // ---- SEASONS ----

        Color SeasonDriftTop()
        {
            if (_season == Season.Spring) return Color.FromRgb(255, 233, 245);
            if (_season == Season.Summer) return Color.FromArgb(150, 138, 188, 238);
            if (_season == Season.Autumn) return Color.FromArgb(0, 104, 66, 36);
            return Color.FromRgb(245, 250, 255);
        }

        Color SeasonDriftBottom()
        {
            if (_season == Season.Spring) return Color.FromRgb(244, 197, 226);
            if (_season == Season.Summer) return Color.FromArgb(110, 96, 148, 220);
            if (_season == Season.Autumn) return Color.FromArgb(0, 54, 32, 18);
            return Color.FromRgb(225, 235, 250);
        }

        Color SeasonSplashColor()
        {
            if (_season == Season.Autumn)
            {
                Color[] pal = new Color[]
                {
                    Color.FromRgb(196, 110, 50), Color.FromRgb(216, 158, 86),
                    Color.FromRgb(150, 96, 48), Color.FromRgb(178, 140, 58)
                };
                return pal[_rng.Next(pal.Length)];
            }
            if (_season == Season.Spring)
            {
                Color[] pal = new Color[]
                {
                    Color.FromRgb(255, 205, 228), Color.FromRgb(248, 180, 215),
                    Color.FromRgb(255, 233, 245)
                };
                return pal[_rng.Next(pal.Length)];
            }
            if (_season == Season.Summer)
            {
                return Color.FromArgb(160, 150, 190, 240);
            }
            return Color.FromRgb(250, 252, 255);
        }

        void ApplySeasonColors()
        {
            if (_drift != null)
            {
                _drift.Fill = new LinearGradientBrush(SeasonDriftTop(), SeasonDriftBottom(), 90.0);
                _drift.Opacity = (_season == Season.Summer) ? 0.8 : 0.95;
            }
            if (_roofCanvas != null)
            {
                for (int i = 0; i < _roofs.Count; i++)
                {
                    Roof r = _roofs[i];
                    if (r.Poly != null)
                    {
                        r.Poly.Fill = new SolidColorBrush(SeasonDriftTop());
                        r.Poly.Opacity = (_season == Season.Summer) ? 0.8 : 0.95;
                    }
                }
            }
        }

        void SaveSettings()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.CreateSubKey(SettingsKeyPath))
                {
                    key.SetValue("Season", ((int)_season).ToString());
                    key.SetValue("SantaAuto", _santaAutoEnabled ? "1" : "0");
                    key.SetValue("Grinch", _grinchEnabled ? "1" : "0");
                    key.SetValue("Rabbit", _rabbitEnabled ? "1" : "0");
                    key.SetValue("Fireworks", _fireworksAuto ? "1" : "0");
                    key.SetValue("Countdown", _countdownEnabled ? "1" : "0");
                    key.SetValue("Snowballs", _snowballsEnabled ? "1" : "0");
                    key.SetValue("RoofCaps", _roofCapsEnabled ? "1" : "0");
                    key.SetValue("Sparkles", _sparklesEnabled ? "1" : "0");
                    key.SetValue("Icicles", _iciclesEnabled ? "1" : "0");
                    key.SetValue("Bird", _birdEnabled ? "1" : "0");
                    key.SetValue("Butterfly", _butterflyEnabled ? "1" : "0");
                    key.SetValue("Density", ((int)_targetCount).ToString());
                }
            }
            catch (Exception ex)
            {
                Log("save settings failed: " + ex.Message);
            }
        }

        bool ReadFlag(RegistryKey key, string name, bool def)
        {
            object v = key.GetValue(name);
            if (v == null) return def;
            return v.ToString() == "1";
        }

        void LoadSettings()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(SettingsKeyPath, false))
                {
                    if (key == null) return;
                    object v = key.GetValue("Season");
                    if (v != null)
                    {
                        int n;
                        if (int.TryParse(v.ToString(), out n) && n >= 0 && n <= 3)
                        {
                            _season = (Season)n;
                            Log("season restored: " + n);
                        }
                    }
                    _santaAutoEnabled = ReadFlag(key, "SantaAuto", true);
                    _grinchEnabled = ReadFlag(key, "Grinch", true);
                    _rabbitEnabled = ReadFlag(key, "Rabbit", true);
                    _fireworksAuto = ReadFlag(key, "Fireworks", true);
                    _countdownEnabled = ReadFlag(key, "Countdown", true);
                    _snowballsEnabled = ReadFlag(key, "Snowballs", true);
                    _roofCapsEnabled = ReadFlag(key, "RoofCaps", true);
                    _sparklesEnabled = ReadFlag(key, "Sparkles", true);
                    _iciclesEnabled = ReadFlag(key, "Icicles", true);
                    _birdEnabled = ReadFlag(key, "Bird", true);
                    _butterflyEnabled = ReadFlag(key, "Butterfly", true);
                    object den = key.GetValue("Density");
                    if (den != null)
                    {
                        int dn;
                        if (int.TryParse(den.ToString(), out dn) && dn >= 20 && dn <= 800)
                        {
                            _targetCount = dn;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log("load settings failed: " + ex.Message);
            }
        }

        void ApplySeason(Season s)
        {
            _season = s;
            SaveSettings();
            RebuildSeasonScene();
            Log("season applied: " + s);
        }

        void RebuildSeasonScene()
        {
            ClearAllSnow();
            CleanupSeasonActors();
            CleanupWinterActors();
            if (_rabbit != null && _canvas != null)
            {
                _canvas.Children.Remove(_rabbit);
                _rabbit = null;
            }
            _rabbitState = 0;
            if (_canvas != null)
            {
                for (int i = _flakes.Count - 1; i >= 0; i--)
                {
                    _canvas.Children.Remove(_flakes[i].Shape);
                }
            }
            _flakes.Clear();
            ApplySeasonColors();
            if (_moon != null)
            {
                _moon.Visibility = (_season == Season.Winter) ? Visibility.Visible : Visibility.Collapsed;
            }
            AddFlakes(_targetCount);
        }

        void CleanupWinterActors()
        {
            if (_train != null)
            {
                if (_canvas != null) _canvas.Children.Remove(_train);
                _train = null;
            }
            _trainActive = false;
            _trainDowned = false;
            _trainTf = null;
            _trainRot = null;
            _legTfs.Clear();
            _headTfs.Clear();
            _armTf = null;
            _santaCheckTimer = 0.0;

            if (_grinch != null)
            {
                if (_canvas != null) _canvas.Children.Remove(_grinch);
                _grinch = null;
            }
            _grinchActive = false;
            _grinchDead = false;
            _grinchPending = false;
            _grinchLoot = null;
            _grinchRot = null;
            _grinchStack.Clear();

            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                SnowDrop d = _drops[i];
                if (d.Shape != null) RemoveShape(d.Shape);
                _drops.RemoveAt(i);
            }
            for (int i = _balls.Count - 1; i >= 0; i--)
            {
                SnowBall b = _balls[i];
                if (b.Shape != null) RemoveShape(b.Shape);
                for (int j = 0; j < _balls.Count; j++)
                {
                    if (_balls[j].Under == b) _balls[j].Under = null;
                }
                _balls.RemoveAt(i);
            }
            _ballHeld = null;
            _grabbed = null;
        }

        void CleanupSeasonActors()
        {
            if (_butterfly != null)
            {
                if (_canvas != null) _canvas.Children.Remove(_butterfly);
                _butterfly = null;
            }
            _bfWL = null;
            _bfWR = null;
            _bfTimer = 6.0 + _rng.NextDouble() * 10.0;
            _bfFlightT = 0.0;

            if (_bird != null)
            {
                if (_canvas != null) _canvas.Children.Remove(_bird);
                _bird = null;
            }
            _birdFlip = null;
            _birdWing = null;
            _birdTimer = 10.0 + _rng.NextDouble() * 20.0;

            if (_canvas != null)
            {
                for (int i = _feathers.Count - 1; i >= 0; i--)
                {
                    if (_feathers[i].Shape != null) _canvas.Children.Remove(_feathers[i].Shape);
                    _feathers.RemoveAt(i);
                }
            }
        }

        void SeasonTick(double dt)
        {
            UpdateButterfly(dt);
            UpdateBird(dt);
            UpdateFeathers(dt);
        }

        void BuildButterfly()
        {
            if (_canvas == null || _butterfly != null) return;
            _bfPhase = _rng.NextDouble() * Math.PI * 2.0;
            _bfDir = (_rng.NextDouble() < 0.5) ? 1.0 : -1.0;
            _bfBaseY = 110.0 + _rng.NextDouble() * (H() * 0.35);
            _bfX = _rng.NextDouble() * (W() - 120.0) + 60.0;
            _bfFlightT = 0.0;

            _butterfly = new Canvas();
            _bfWL = new ScaleTransform { ScaleX = 1.0, ScaleY = 1.0 };
            _bfWR = new ScaleTransform { ScaleX = 1.0, ScaleY = 1.0 };

            Ellipse body = new Ellipse { Width = 4.0, Height = 13.0 };
            body.Fill = new SolidColorBrush(Color.FromRgb(70, 45, 80));
            Canvas.SetLeft(body, 2.0);
            Canvas.SetTop(body, 1.5);
            _butterfly.Children.Add(body);

            Color[] wingColors = new Color[] { Color.FromRgb(196, 110, 255), Color.FromRgb(255, 140, 222) };
            for (int i = 0; i < 2; i++)
            {
                Ellipse wing = new Ellipse { Width = 12.5, Height = 16.0 };
                Color wc = wingColors[i];
                wing.Fill = new LinearGradientBrush(
                    Color.FromArgb(230, wc.R, wc.G, wc.B), Color.FromArgb(120, wc.R, wc.G, wc.B), 90.0);
                Canvas.SetLeft(wing, i == 0 ? -11.0 : 6.5);
                Canvas.SetTop(wing, 0.0);
                wing.RenderTransform = (i == 0) ? _bfWL : _bfWR;
                wing.RenderTransformOrigin = (i == 0) ? new Point(1.0, 0.5) : new Point(0.0, 0.5);
                _butterfly.Children.Add(wing);
            }

            System.Windows.Controls.Panel.SetZIndex(_butterfly, 60);
            Canvas.SetLeft(_butterfly, _bfX);
            Canvas.SetTop(_butterfly, _bfBaseY);
            _butterfly.Opacity = 0.0;
            _canvas.Children.Add(_butterfly);
            Log("butterfly appeared");
        }

        void UpdateButterfly(double dt)
        {
            if (_season != Season.Spring || !_butterflyEnabled)
            {
                if (_butterfly != null)
                {
                    if (_canvas != null) _canvas.Children.Remove(_butterfly);
                    _butterfly = null;
                    _bfWL = null;
                    _bfWR = null;
                    _bfTimer = 99999.0;
                }
                return;
            }
            if (_butterfly == null)
            {
                _bfTimer -= dt;
                if (_bfTimer <= 0.0) BuildButterfly();
                return;
            }
            _bfPhase += dt * 13.0;
            _bfFlightT += dt;
            double flap = 0.30 + Math.Abs(Math.Sin(_bfPhase)) * 0.75;
            if (_bfWL != null) _bfWL.ScaleX = flap;
            if (_bfWR != null) _bfWR.ScaleX = flap;
            _bfX += _bfDir * 46.0 * dt;
            if (_bfX < 20.0) { _bfX = 20.0; _bfDir = 1.0; }
            if (_bfX > W() - 20.0) { _bfX = W() - 20.0; _bfDir = -1.0; }
            if (_rng.NextDouble() < 0.006) _bfDir *= -1.0;
            double by = _bfBaseY + Math.Sin(_bfPhase * 0.25) * 60.0 + Math.Sin(_bfPhase * 0.93) * 18.0;
            _butterfly.Opacity = Math.Min(1.0, _butterfly.Opacity + dt * 0.6);
            Canvas.SetLeft(_butterfly, _bfX);
            Canvas.SetTop(_butterfly, by - 8.0);
            if (_bfFlightT > 40.0)
            {
                _butterfly.Opacity = Math.Max(0.0, _butterfly.Opacity - dt * 0.25);
                if (_butterfly.Opacity <= 0.0)
                {
                    _canvas.Children.Remove(_butterfly);
                    _butterfly = null;
                    _bfWL = null;
                    _bfWR = null;
                    _bfTimer = 25.0 + _rng.NextDouble() * 40.0;
                }
            }
        }

        void BuildBird()
        {
            if (_canvas == null || _bird != null) return;
            _birdPhase = _rng.NextDouble() * Math.PI * 2.0;
            _birdY = 90.0 + _rng.NextDouble() * (H() * 0.30);
            _birdX = (_birdDir > 0) ? -40.0 : W() + 40.0;
            _birdDropsLeft = (_season == Season.Summer) ? 2.0 : 1.0;
            _birdDropT = 1.4 + _rng.NextDouble() * 1.6;

            _bird = new Canvas();
            _birdFlip = new ScaleTransform { ScaleX = (_birdDir > 0) ? 1.0 : -1.0, ScaleY = 1.0 };
            _bird.RenderTransform = _birdFlip;
            _bird.RenderTransformOrigin = new Point(0.5, 0.5);
            _birdWing = new RotateTransform { Angle = 0.0 };

            Ellipse body = new Ellipse { Width = 18.0, Height = 11.0 };
            body.Fill = new SolidColorBrush(Color.FromRgb(72, 58, 52));
            Canvas.SetLeft(body, 0.0);
            Canvas.SetTop(body, 4.0);
            _bird.Children.Add(body);

            Ellipse head = new Ellipse { Width = 7.0, Height = 7.0 };
            head.Fill = new SolidColorBrush(Color.FromRgb(72, 58, 52));
            Canvas.SetLeft(head, 14.0);
            Canvas.SetTop(head, 1.0);
            _bird.Children.Add(head);

            Polygon beak = new Polygon();
            beak.Points = new PointCollection { new Point(0, 1.5), new Point(6, 3.5), new Point(0, 5.5) };
            beak.Fill = new SolidColorBrush(Color.FromRgb(230, 172, 66));
            Canvas.SetLeft(beak, 19.0);
            Canvas.SetTop(beak, 2.2);
            _bird.Children.Add(beak);

            Polygon tail = new Polygon();
            tail.Points = new PointCollection { new Point(0, 1), new Point(9, 0), new Point(9, 6), new Point(0, 4) };
            tail.Fill = new SolidColorBrush(Color.FromRgb(56, 45, 40));
            Canvas.SetLeft(tail, -8.0);
            Canvas.SetTop(tail, 4.5);
            _bird.Children.Add(tail);

            Polygon wing = new Polygon();
            wing.Points = new PointCollection { new Point(0, 0), new Point(14, 2.5), new Point(5, 8) };
            wing.Fill = new SolidColorBrush(Color.FromRgb(96, 78, 68));
            Canvas.SetLeft(wing, 3.5);
            Canvas.SetTop(wing, 3.5);
            wing.RenderTransform = _birdWing;
            wing.RenderTransformOrigin = new Point(0.1, 0.15);
            _bird.Children.Add(wing);

            System.Windows.Controls.Panel.SetZIndex(_bird, 60);
            Canvas.SetLeft(_bird, _birdX - 15.0);
            Canvas.SetTop(_bird, _birdY);
            _bird.Opacity = 0.0;
            _canvas.Children.Add(_bird);
            Log("bird appeared");
        }

        void UpdateBird(double dt)
        {
            if ((_season != Season.Summer && _season != Season.Autumn) || !_birdEnabled)
            {
                if (_bird != null)
                {
                    if (_canvas != null) _canvas.Children.Remove(_bird);
                    _bird = null;
                    _birdFlip = null;
                    _birdWing = null;
                    _birdTimer = 99999.0;
                }
                return;
            }
            if (_bird == null)
            {
                _birdTimer -= dt;
                if (_birdTimer <= 0.0)
                {
                    _birdDir = (_rng.NextDouble() < 0.5) ? 1.0 : -1.0;
                    BuildBird();
                }
                return;
            }
            double speed = (_season == Season.Autumn) ? 200.0 : 135.0;
            _birdX += _birdDir * speed * dt;
            _birdPhase += dt * 15.0;
            double bob = Math.Sin(_birdPhase * 0.25) * 12.0;
            if (_birdWing != null) _birdWing.Angle = Math.Sin(_birdPhase) * 38.0;
            if (_birdFlip != null) _birdFlip.ScaleX = (_birdDir > 0) ? 1.0 : -1.0;
            _bird.Opacity = Math.Min(1.0, _bird.Opacity + dt * 1.5);
            Canvas.SetLeft(_bird, _birdX - 15.0);
            Canvas.SetTop(_bird, _birdY + bob - 6.0);

            bool past = (_birdDir > 0) ? (_birdX > W() * 0.25 + 100.0) : (_birdX < W() * 0.75 - 100.0);
            _birdDropT -= dt;
            if (past && _birdDropT <= 0.0 && _birdDropsLeft > 0)
            {
                _birdDropsLeft -= 1.0;
                _birdDropT = 2.5 + _rng.NextDouble() * 2.5;
                SpawnFeather(_birdX + 12.0 * _birdDir, _birdY + bob + 10.0);
            }

            bool offside = (_birdDir > 0) ? (_birdX > W() + 60.0) : (_birdX < -60.0);
            if (offside)
            {
                if (_canvas != null) _canvas.Children.Remove(_bird);
                _bird = null;
                _birdFlip = null;
                _birdWing = null;
                _birdTimer = 35.0 + _rng.NextDouble() * 50.0;
            }
        }

        void SpawnFeather(double x, double y)
        {
            if (_canvas == null) return;
            Feather f = new Feather();
            f.X = x;
            f.Y = y;
            f.Vx = -25.0 + _rng.NextDouble() * 50.0;
            f.Vy = 20.0 + _rng.NextDouble() * 25.0;
            f.Life = 3.5 + _rng.NextDouble() * 2.5;
            f.Phase = _rng.NextDouble() * Math.PI * 2.0;
            Ellipse shape = new Ellipse { Width = 4.5, Height = 11.0 };
            shape.Fill = new LinearGradientBrush(Color.FromRgb(250, 248, 240), Color.FromRgb(225, 218, 200), 90.0);
            RotateTransform rtf = new RotateTransform(0.0);
            shape.RenderTransform = rtf;
            shape.RenderTransformOrigin = new Point(0.5, 0.5);
            shape.Opacity = 0.95;
            f.Shape = shape;
            f.Rtf = rtf;
            System.Windows.Controls.Panel.SetZIndex(shape, 61);
            Canvas.SetLeft(shape, f.X);
            Canvas.SetTop(shape, f.Y);
            _feathers.Add(f);
            _canvas.Children.Add(shape);
        }

        void UpdateFeathers(double dt)
        {
            for (int i = _feathers.Count - 1; i >= 0; i--)
            {
                Feather f = _feathers[i];
                f.Life -= dt;
                f.Phase += dt * 3.2;
                f.X += (f.Vx + Math.Sin(f.Phase) * 32.0 + _wind * 0.6) * dt;
                f.Vy = Math.Min(60.0, f.Vy + 12.0 * dt);
                f.Y += f.Vy * dt;
                if (f.Rtf != null) f.Rtf.Angle = Math.Sin(f.Phase) * 36.0;
                if (f.Shape != null)
                {
                    f.Shape.Opacity = Math.Max(0.0, Math.Min(1.0, f.Life * 1.2));
                    Canvas.SetLeft(f.Shape, f.X);
                    Canvas.SetTop(f.Shape, f.Y);
                }
                if (f.Life <= 0.0 || (_groundY > 0 && f.Y >= _groundY - 2.0))
                {
                    if (f.Shape != null) _canvas.Children.Remove(f.Shape);
                    _feathers.RemoveAt(i);
                }
            }
        }

        void AddPetal(bool randomY)
        {
            Flake f = new Flake();
            f.Kind = 1;
            f.R = 4.0;
            f.Speed = 12 + _rng.NextDouble() * 30;
            f.SwayAmp = 20 + _rng.NextDouble() * 40;
            f.Phase = _rng.NextDouble() * Math.PI * 2;
            f.SwaySpeed = 1.2 + _rng.NextDouble() * 1.6;
            f.Opacity = 0.7 + _rng.NextDouble() * 0.3;
            f.BaseX = _rng.NextDouble() * (W() + 80);
            f.X = f.BaseX;
            f.Y = randomY ? _rng.NextDouble() * (H() + 80) : -10 - _rng.NextDouble() * 40;
            f.RotSpeed = -90 + _rng.NextDouble() * 180;

            Color[] tints = new Color[]
            {
                Color.FromRgb(255, 183, 213),
                Color.FromRgb(255, 214, 232),
                Color.FromRgb(250, 240, 245),
                Color.FromRgb(255, 168, 200)
            };
            f.Tint = tints[_rng.Next(tints.Length)];

            Ellipse shape = new Ellipse { Width = 9.0, Height = 5.5 };
            shape.Fill = new LinearGradientBrush(Color.FromRgb(255, 236, 244), f.Tint, 90.0);
            shape.Opacity = f.Opacity;
            RotateTransform rt = new RotateTransform(_rng.NextDouble() * 360.0);
            shape.RenderTransform = rt;
            shape.RenderTransformOrigin = new Point(0.5, 0.5);
            f.Shape = shape;
            f.Rtf = rt;
            f.Rot = rt.Angle;

            _flakes.Add(f);
            _canvas.Children.Add(shape);
        }

        void AddLeaf(bool randomY)
        {
            Flake f = new Flake();
            f.Kind = 2;
            f.R = 5.0;
            f.Speed = 25 + _rng.NextDouble() * 40;
            f.SwayAmp = 25 + _rng.NextDouble() * 55;
            f.Phase = _rng.NextDouble() * Math.PI * 2;
            f.SwaySpeed = 0.8 + _rng.NextDouble() * 1.4;
            f.Opacity = 0.85;
            f.BaseX = _rng.NextDouble() * (W() + 80);
            f.X = f.BaseX;
            f.Y = randomY ? _rng.NextDouble() * (H() + 80) : -12 - _rng.NextDouble() * 40;
            f.RotSpeed = -140 + _rng.NextDouble() * 280;

            Color[] tints = new Color[]
            {
                Color.FromRgb(200, 90, 40),
                Color.FromRgb(224, 130, 50),
                Color.FromRgb(240, 180, 60),
                Color.FromRgb(150, 70, 35),
                Color.FromRgb(190, 110, 45)
            };
            f.Tint = tints[_rng.Next(tints.Length)];
            Color light = Color.FromRgb(
                (byte)Math.Min(255, f.Tint.R + 45),
                (byte)Math.Min(255, f.Tint.G + 45),
                (byte)Math.Min(255, f.Tint.B + 45));

            Ellipse shape = new Ellipse { Width = 12.5, Height = 7.2 };
            shape.Fill = new LinearGradientBrush(light, f.Tint, 45.0);
            shape.Stroke = new SolidColorBrush(Color.FromArgb(120, 90, 50, 25));
            shape.StrokeThickness = 0.6;
            shape.Opacity = f.Opacity;
            RotateTransform rt = new RotateTransform(_rng.NextDouble() * 360.0);
            shape.RenderTransform = rt;
            shape.RenderTransformOrigin = new Point(0.5, 0.5);
            f.Shape = shape;
            f.Rtf = rt;
            f.Rot = rt.Angle;

            _flakes.Add(f);
            _canvas.Children.Add(shape);
        }

        void AddRaindrop(bool randomY)
        {
            Flake f = new Flake();
            f.Kind = 3;
            f.R = 2.0;
            f.Speed = 460 + _rng.NextDouble() * 260;
            f.SwayAmp = 4 + _rng.NextDouble() * 8;
            f.Phase = _rng.NextDouble() * Math.PI * 2;
            f.SwaySpeed = 1.4 + _rng.NextDouble() * 1.2;
            f.Opacity = 0.5 + _rng.NextDouble() * 0.4;
            f.BaseX = _rng.NextDouble() * (W() + 80);
            f.X = f.BaseX;
            f.Y = randomY ? _rng.NextDouble() * (H() + 80) : -20 - _rng.NextDouble() * 100;

            Line shape = new Line();
            double len = 9 + _rng.NextDouble() * 7;
            double dx = 1.6 + _rng.NextDouble() * 1.4;
            shape.X1 = 0;
            shape.Y1 = 0;
            shape.X2 = dx;
            shape.Y2 = len;
            shape.Stroke = new SolidColorBrush(Color.FromArgb(175, 150, 195, 240));
            shape.StrokeThickness = 1.4;
            shape.StrokeEndLineCap = PenLineCap.Round;
            shape.Opacity = f.Opacity;
            f.Shape = shape;
            f.Rtf = null;

            _flakes.Add(f);
            _canvas.Children.Add(shape);
        }

        void ShowSettingsWindow()
        {
            try
            {
                Window win = new Window();
                win.Title = "DesktopSnow";
                win.Width = 480;
                win.Height = 580;
                win.ResizeMode = ResizeMode.NoResize;
                win.WindowStyle = WindowStyle.None;
                win.AllowsTransparency = true;
                win.WindowStartupLocation = WindowStartupLocation.CenterScreen;
                win.Topmost = true;
                win.Background = Brushes.Transparent;
                if (_settingsWin != null)
                {
                    try { _settingsWin.Activate(); } catch (Exception) { }
                    return;
                }
                _settingsWin = win;
                win.Loaded += delegate
                {
                    try { _settingsHwnd = new WindowInteropHelper(win).Handle; } catch (Exception) { }
                };
                win.PreviewKeyDown += delegate(object pks, System.Windows.Input.KeyEventArgs pke)
                {
                    if (pke.Key == System.Windows.Input.Key.Escape)
                    {
                        try { win.Close(); } catch (Exception) { }
                    }
                };

                Border shell = new Border();
                shell.CornerRadius = new CornerRadius(16.0);
                shell.BorderThickness = new Thickness(1.0);
                shell.BorderBrush = new SolidColorBrush(Color.FromRgb(108, 70, 200));
                LinearGradientBrush shellBg = new LinearGradientBrush();
                shellBg.StartPoint = new Point(0, 0);
                shellBg.EndPoint = new Point(0, 1);
                shellBg.GradientStops.Add(new GradientStop(Color.FromRgb(36, 27, 51), 0.0));
                shellBg.GradientStops.Add(new GradientStop(Color.FromRgb(20, 16, 29), 1.0));
                shell.Background = shellBg;
                win.Content = shell;

                Grid root = new Grid();
                root.Margin = new Thickness(16);
                shell.Child = root;
                for (int i = 0; i < 5; i++)
                {
                    RowDefinition rd = new RowDefinition();
                    rd.Height = new GridLength(1, GridUnitType.Star);
                    root.RowDefinitions.Add(rd);
                }
                root.RowDefinitions[0].Height = new GridLength(1, GridUnitType.Auto);
                root.RowDefinitions[1].Height = new GridLength(1, GridUnitType.Auto);
                root.RowDefinitions[2].Height = new GridLength(1, GridUnitType.Auto);
                root.RowDefinitions[4].Height = new GridLength(1, GridUnitType.Auto);

                Border header = new Border();
                header.Background = new SolidColorBrush(Color.FromRgb(30, 27, 43));
                header.CornerRadius = new CornerRadius(10.0);
                header.Padding = new Thickness(12, 6, 6, 6);
                header.MouseLeftButtonDown += delegate
                {
                    try { win.DragMove(); } catch (Exception) { }
                };
                Grid.SetRow(header, 0);
                root.Children.Add(header);

                Grid hg = new Grid();
                header.Child = hg;
                ColumnDefinition hc1 = new ColumnDefinition();
                hc1.Width = new GridLength(1, GridUnitType.Star);
                ColumnDefinition hc2 = new ColumnDefinition();
                hc2.Width = new GridLength(1, GridUnitType.Auto);
                hg.ColumnDefinitions.Add(hc1);
                hg.ColumnDefinitions.Add(hc2);

                StackPanel hdrBox = new StackPanel();
                hdrBox.Orientation = Orientation.Horizontal;
                hdrBox.VerticalAlignment = VerticalAlignment.Center;
                Grid.SetColumn(hdrBox, 0);

                TextBlock flake = new TextBlock();
                flake.Text = "\u2744 ";
                flake.FontSize = 15;
                flake.Foreground = new SolidColorBrush(Color.FromRgb(150, 100, 255));
                flake.VerticalAlignment = VerticalAlignment.Center;
                hdrBox.Children.Add(flake);

                TextBlock hdr = new TextBlock();
                hdr.Text = "\u041d\u0430\u0441\u0442\u0440\u043e\u0439\u043a\u0438 DesktopSnow";
                hdr.FontSize = 15;
                hdr.FontWeight = FontWeights.Bold;
                hdr.Foreground = new SolidColorBrush(Color.FromRgb(214, 190, 255));
                hdr.VerticalAlignment = VerticalAlignment.Center;
                hdrBox.Children.Add(hdr);
                hg.Children.Add(hdrBox);

                Border close = new Border();
                close.Width = 26;
                close.Height = 26;
                close.CornerRadius = new CornerRadius(13.0);
                close.Background = new SolidColorBrush(Color.FromRgb(44, 36, 64));
                close.Cursor = System.Windows.Input.Cursors.Hand;
                TextBlock closeX = new TextBlock();
                closeX.Text = "\u00d7";
                closeX.FontSize = 15;
                closeX.Foreground = Brushes.White;
                closeX.HorizontalAlignment = HorizontalAlignment.Center;
                closeX.VerticalAlignment = VerticalAlignment.Center;
                close.Child = closeX;
                close.MouseEnter += delegate
                {
                    close.Background = new SolidColorBrush(Color.FromRgb(200, 70, 90));
                };
                close.MouseLeave += delegate
                {
                    close.Background = new SolidColorBrush(Color.FromRgb(44, 36, 64));
                };
                close.MouseLeftButtonUp += delegate
                {
                    try { win.Close(); } catch (Exception) { }
                };
                Grid.SetColumn(close, 1);
                hg.Children.Add(close);

                StackPanel secSeason = new StackPanel();
                Grid.SetRow(secSeason, 1);
                root.Children.Add(secSeason);
                secSeason.Children.Add(MakeSectionLabel("\u0421\u0415\u0417\u041e\u041d"));

                System.Windows.Controls.Primitives.UniformGrid ugS = new System.Windows.Controls.Primitives.UniformGrid();
                ugS.Rows = 2;
                ugS.Columns = 2;
                secSeason.Children.Add(ugS);

                Season[] seasons = new Season[] { Season.Winter, Season.Spring, Season.Summer, Season.Autumn };
                string[] names = new string[] { "\u0421\u043d\u0435\u0433", "\u041b\u0435\u043f\u0435\u0441\u0442\u043a\u0438 \u0438 \u0431\u0430\u0431\u043e\u0447\u043a\u0438", "\u0414\u043e\u0436\u0434\u044c", "\u0421\u0443\u0445\u0438\u0435 \u043b\u0438\u0441\u0442\u044c\u044f" };
                string[] descs = new string[] { "\u0421\u043d\u0435\u0433, \u0414\u0435\u0434 \u041c\u043e\u0440\u043e\u0437 \u0438 \u0413\u0440\u0438\u043d\u0447", "\u041b\u0435\u043f\u0435\u0441\u0442\u043a\u0438 \u0438 \u0431\u0430\u0431\u043e\u0447\u043a\u0430", "\u0414\u043e\u0436\u0434\u044c \u0438 \u043f\u0442\u0438\u0446\u0430", "\u041b\u0438\u0441\u0442\u044c\u044f \u0438 \u0432\u0435\u0442\u0435\u0440" };
                List<Border> seasonBtns = new List<Border>();
                for (int i = 0; i < seasons.Length; i++)
                {
                    int idx = i;
                    Season picked = seasons[idx];
                    string name = names[idx];
                    string desc = descs[idx];

                    Border btn = new Border();
                    btn.CornerRadius = new CornerRadius(12.0);
                    btn.Margin = new Thickness(6);
                    btn.Height = 58;
                    btn.Cursor = System.Windows.Input.Cursors.Hand;
                    btn.Background = (seasons[idx] == _season) ? SeasonBrush(true) : SeasonBrush(false);
                    btn.BorderThickness = new Thickness(1.5);
                    btn.BorderBrush = (seasons[idx] == _season) ? new SolidColorBrush(Color.FromRgb(230, 214, 255)) : Brushes.Transparent;

                    StackPanel sp = new StackPanel();
                    sp.VerticalAlignment = VerticalAlignment.Center;
                    sp.HorizontalAlignment = HorizontalAlignment.Center;

                    TextBlock big = new TextBlock();
                    big.Text = name;
                    big.FontSize = 15;
                    big.FontWeight = FontWeights.Bold;
                    big.Foreground = Brushes.White;
                    big.HorizontalAlignment = HorizontalAlignment.Center;
                    sp.Children.Add(big);

                    TextBlock small = new TextBlock();
                    small.Text = desc;
                    small.FontSize = 10.5;
                    small.Foreground = new SolidColorBrush(Color.FromRgb(214, 202, 238));
                    small.HorizontalAlignment = HorizontalAlignment.Center;
                    sp.Children.Add(small);

                    btn.Child = sp;
                    btn.MouseEnter += delegate
                    {
                        btn.Background = SeasonBrushHover(seasons[idx] == _season);
                    };
                    btn.MouseLeave += delegate
                    {
                        btn.Background = (seasons[idx] == _season) ? SeasonBrush(true) : SeasonBrush(false);
                    };
                    btn.MouseLeftButtonUp += delegate
                    {
                        try { ApplySeason(picked); }
                        catch (Exception ex) { Log("apply season failed: " + ex.Message); }
                        for (int k = 0; k < seasonBtns.Count; k++)
                        {
                            seasonBtns[k].Background = (seasons[k] == _season) ? SeasonBrush(true) : SeasonBrush(false);
                            seasonBtns[k].BorderBrush = (seasons[k] == _season) ? new SolidColorBrush(Color.FromRgb(230, 214, 255)) : Brushes.Transparent;
                        }
                        FillSeasonControls();
                    };
                    seasonBtns.Add(btn);
                    ugS.Children.Add(btn);
                }

                StackPanel secAct = new StackPanel();
                Grid.SetRow(secAct, 2);
                root.Children.Add(secAct);
                secAct.Children.Add(MakeSectionLabel("\u0414\u0415\u0419\u0421\u0422\u0412\u0418\u042f"));

                System.Windows.Controls.Primitives.UniformGrid ugA = new System.Windows.Controls.Primitives.UniformGrid();
                ugA.Columns = 3;
                secAct.Children.Add(ugA);

                StackPanel secTog = new StackPanel();
                Grid.SetRow(secTog, 3);
                root.Children.Add(secTog);
                secTog.Children.Add(MakeSectionLabel("\u041f\u0415\u0420\u0421\u041e\u041d\u0410\u0416\u0418 \u0418 \u042d\u0424\u0424\u0415\u041a\u0422\u042b"));

                System.Windows.Controls.Primitives.UniformGrid ugT = new System.Windows.Controls.Primitives.UniformGrid();
                ugT.Columns = 2;
                secTog.Children.Add(ugT);

                _ugA = ugA;
                _ugT = ugT;
                FillSeasonControls();

                StackPanel secDen = new StackPanel();
                Grid.SetRow(secDen, 4);
                root.Children.Add(secDen);
                secDen.Children.Add(MakeSectionLabel("\u041f\u041b\u041e\u0422\u041d\u041e\u0421\u0422\u042c \u042d\u0424\u0424\u0415\u041a\u0422\u041e\u0412"));

                TextBlock denLabel = new TextBlock();
                denLabel.Text = "\u041f\u043b\u043e\u0442\u043d\u043e\u0441\u0442\u044c: " + _targetCount;
                denLabel.FontSize = 12;
                denLabel.Foreground = Brushes.White;
                denLabel.Margin = new Thickness(6, 0, 0, 4);
                secDen.Children.Add(denLabel);

                Slider sl = new Slider();
                sl.Minimum = 20;
                sl.Maximum = 620;
                sl.Value = _targetCount;
                sl.Margin = new Thickness(6, 0, 6, 6);
                sl.ValueChanged += delegate(object ss, System.Windows.RoutedPropertyChangedEventArgs<double> ee)
                {
                    _targetCount = (int)ee.NewValue;
                    denLabel.Text = "\u041f\u043b\u043e\u0442\u043d\u043e\u0441\u0442\u044c: " + _targetCount;
                    TrimFlakes();
                };
                secDen.Children.Add(sl);

                win.Closed += delegate
                {
                    SaveSettings();
                    _settingsWin = null;
                    _settingsHwnd = IntPtr.Zero;
                    if (_mode == SnowMode.DesktopOnly && !_attachedBehindIcons) PlaceAtWindowStackBottom();
                };
                win.Show();
            }
            catch (Exception ex)
            {
                Log("settings window failed: " + ex.Message);
            }
        }

        void FillSeasonControls()
        {
            if (_ugA == null || _ugT == null) return;
            System.Windows.Controls.Primitives.UniformGrid ugA = _ugA;
            System.Windows.Controls.Primitives.UniformGrid ugT = _ugT;
            ugA.Children.Clear();
            ugT.Children.Clear();
            if (_season == Season.Winter)
            {
                ugA.Children.Add(MakeActionButton("\u0412\u044b\u0437\u0432\u0430\u0442\u044c \u0421\u0430\u043d\u0442\u0443", delegate
                {
                    if (!_trainActive) SpawnTrain();
                }));
                ugA.Children.Add(MakeActionButton("\u0412\u044b\u0437\u0432\u0430\u0442\u044c \u0413\u0440\u0438\u043d\u0447\u0430", delegate
                {
                    if (!_grinchActive) SpawnGrinch();
                }));
                ugA.Children.Add(MakeActionButton("\u041f\u043e\u0437\u0432\u0430\u0442\u044c \u0437\u0430\u0439\u0447\u0438\u043a\u0430", delegate
                {
                    if (_rabbit == null) SpawnRabbit(_rng.NextDouble() * (W() - 100.0) + 50.0);
                }));
            }
            ugA.Children.Add(MakeActionButton("\u0421\u0430\u043b\u044e\u0442!", delegate
            {
                SpawnRocket();
                SpawnRocket();
                SpawnRocket();
            }));
            ugA.Children.Add(MakeActionButton("\u0423\u0431\u0440\u0430\u0442\u044c \u0432\u0441\u0435 \u044d\u0444\u0444\u0435\u043a\u0442\u044b", delegate
            {
                ClearAllSnow();
            }));
            if (_season == Season.Winter)
            {
                ugT.Children.Add(MakeToggle("\u0414\u0435\u0434 \u041c\u043e\u0440\u043e\u0437 \u0441\u0430\u043c (\u0430\u0432\u0442\u043e)", _santaAutoEnabled, delegate(bool on)
                {
                    _santaAutoEnabled = on;
                    SaveSettings();
                }));
                ugT.Children.Add(MakeToggle("\u0413\u0440\u0438\u043d\u0447", _grinchEnabled, delegate(bool on)
                {
                    _grinchEnabled = on;
                    SaveSettings();
                }));
                ugT.Children.Add(MakeToggle("\u0417\u0430\u0439\u0447\u0438\u043a", _rabbitEnabled, delegate(bool on)
                {
                    _rabbitEnabled = on;
                    SaveSettings();
                }));
                ugT.Children.Add(MakeToggle("\u0424\u0435\u0439\u0435\u0440\u0432\u0435\u0440\u043a\u0438 \u0438 \u0437\u0432\u0451\u0437\u0434\u044b (\u0430\u0432\u0442\u043e)", _fireworksAuto, delegate(bool on)
                {
                    _fireworksAuto = on;
                    SaveSettings();
                }));
                ugT.Children.Add(MakeToggle("\u0421\u043d\u0435\u0436\u043a\u0438", _snowballsEnabled, delegate(bool on)
                {
                    _snowballsEnabled = on;
                    SaveSettings();
                }));
                ugT.Children.Add(MakeToggle("\u0421\u043e\u0441\u0443\u043b\u044c\u043a\u0438", _iciclesEnabled, delegate(bool on)
                {
                    _iciclesEnabled = on;
                    SaveSettings();
                }));
            }
            if (_season == Season.Spring)
            {
                ugT.Children.Add(MakeToggle("\u0411\u0430\u0431\u043e\u0447\u043a\u0430", _butterflyEnabled, delegate(bool on)
                {
                    _butterflyEnabled = on;
                    SaveSettings();
                }));
            }
            if (_season == Season.Summer || _season == Season.Autumn)
            {
                ugT.Children.Add(MakeToggle("\u041f\u0442\u0438\u0446\u0430", _birdEnabled, delegate(bool on)
                {
                    _birdEnabled = on;
                    SaveSettings();
                }));
            }
            if (_season != Season.Summer)
            {
                ugT.Children.Add(MakeToggle("\u0428\u0430\u043f\u043a\u0438 \u043d\u0430 \u043e\u043a\u043d\u0430\u0445", _roofCapsEnabled, delegate(bool on)
                {
                    _roofCapsEnabled = on;
                    SaveSettings();
                }));
            }
            ugT.Children.Add(MakeToggle("\u0411\u043b\u0451\u0441\u0442\u043a\u0438", _sparklesEnabled, delegate(bool on)
            {
                _sparklesEnabled = on;
                SaveSettings();
            }));
            ugT.Children.Add(MakeToggle("\u041e\u0442\u0441\u0447\u0451\u0442 \u0434\u043e \u041d\u043e\u0432\u043e\u0433\u043e \u0433\u043e\u0434\u0430", _countdownEnabled, delegate(bool on)
            {
                _countdownEnabled = on;
                if (!on) UpdateCountdown();
                SaveSettings();
            }));
            ugT.Children.Add(MakeToggle("\u0412\u043c\u0435\u0441\u0442\u0435 \u0441 \u041f\u041a (\u0430\u0432\u0442\u043e\u0437\u0430\u043f\u0443\u0441\u043a)", IsAutostartEnabled(), delegate(bool on)
            {
                if (on) AddAutostart();
                else RemoveAutostart();
                SyncTrayCheckState();
            }));
        }

        Brush SeasonBrush(bool active)
        {
            LinearGradientBrush lg = new LinearGradientBrush();
            lg.StartPoint = new Point(0, 0);
            lg.EndPoint = new Point(0, 1);
            if (active)
            {
                lg.GradientStops.Add(new GradientStop(Color.FromRgb(173, 123, 255), 0.0));
                lg.GradientStops.Add(new GradientStop(Color.FromRgb(126, 77, 222), 1.0));
            }
            else
            {
                lg.GradientStops.Add(new GradientStop(Color.FromRgb(112, 76, 198), 0.0));
                lg.GradientStops.Add(new GradientStop(Color.FromRgb(88, 55, 160), 1.0));
            }
            return lg;
        }

        Brush SeasonBrushHover(bool active)
        {
            if (active) return SeasonBrush(true);
            LinearGradientBrush lg = new LinearGradientBrush();
            lg.StartPoint = new Point(0, 0);
            lg.EndPoint = new Point(0, 1);
            lg.GradientStops.Add(new GradientStop(Color.FromRgb(132, 92, 225), 0.0));
            lg.GradientStops.Add(new GradientStop(Color.FromRgb(104, 66, 190), 1.0));
            return lg;
        }

        Brush ActionBrush()
        {
            LinearGradientBrush lg = new LinearGradientBrush();
            lg.StartPoint = new Point(0, 0);
            lg.EndPoint = new Point(0, 1);
            lg.GradientStops.Add(new GradientStop(Color.FromRgb(104, 66, 196), 0.0));
            lg.GradientStops.Add(new GradientStop(Color.FromRgb(76, 48, 146), 1.0));
            return lg;
        }

        TextBlock MakeSectionLabel(string caption)
        {
            TextBlock tb = new TextBlock();
            tb.Text = caption;
            tb.FontSize = 11;
            tb.FontWeight = FontWeights.Bold;
            tb.Foreground = new SolidColorBrush(Color.FromRgb(150, 120, 210));
            tb.Margin = new Thickness(6, 14, 0, 6);
            return tb;
        }

        Border MakeActionButton(string caption, Action onClick)
        {
            Border b = new Border();
            b.Background = ActionBrush();
            b.CornerRadius = new CornerRadius(10.0);
            b.Margin = new Thickness(5);
            b.Height = 34;
            b.Cursor = System.Windows.Input.Cursors.Hand;

            TextBlock tb = new TextBlock();
            tb.Text = caption;
            tb.FontSize = 12;
            tb.FontWeight = FontWeights.Bold;
            tb.Foreground = Brushes.White;
            tb.HorizontalAlignment = HorizontalAlignment.Center;
            tb.VerticalAlignment = VerticalAlignment.Center;
            b.Child = tb;

            b.MouseEnter += delegate
            {
                b.Background = new SolidColorBrush(Color.FromRgb(128, 86, 222));
            };
            b.MouseLeave += delegate
            {
                b.Background = ActionBrush();
            };
            b.MouseLeftButtonUp += delegate
            {
                try { onClick(); }
                catch (Exception ex) { Log("action failed: " + ex.Message); }
            };
            return b;
        }

        CheckBox MakeToggle(string caption, bool isChecked, Action<bool> on)
        {
            CheckBox cb = new CheckBox();
            cb.Content = caption;
            cb.IsChecked = isChecked;
            cb.Foreground = Brushes.White;
            cb.FontSize = 12;
            cb.Margin = new Thickness(6, 5, 6, 5);
            cb.Cursor = System.Windows.Input.Cursors.Hand;
            cb.Checked += delegate { on(true); };
            cb.Unchecked += delegate { on(false); };
            return cb;
        }

        void Quit()
        {
            if (_hwnd != IntPtr.Zero) UnregisterHotKey(_hwnd, HOTKEY_ID);
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

        void InstallHotkey()
        {
            try
            {
                HwndSource src = HwndSource.FromHwnd(_hwnd);
                if (src != null) src.AddHook(WndProcHook);
                RegisterHotKey(_hwnd, HOTKEY_ID, HOTKEY_MODS, HOTKEY_VK_S);
                Log("hotkey registered: Ctrl+Alt+S");
            }
            catch (Exception ex)
            {
                Log("hotkey failed: " + ex.Message);
            }
        }

        IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
            {
                Dispatcher.BeginInvoke((Action)delegate
                {
                    ClearAllSnow();
                    Log("hotkey: cleared all snow");
                });
                handled = true;
            }
            return IntPtr.Zero;
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
                    bool onDesk = ClickOnDesktop(info.pt);
                    if (onDesk && _grinchActive && !_grinchDead && _grinch != null)
                    {
                        double gl = Canvas.GetLeft(_grinch), gt = Canvas.GetTop(_grinch);
                        if (mx >= gl - 8 && mx <= gl + 72 && my >= gt - 8 && my <= gt + 80)
                        {
                            Dispatcher.BeginInvoke((Action)delegate { GrinchDie(); });
                            return (IntPtr)1;
                        }
                    }
                    SnowDrop hit = FindClickableDrop(mx, my, onDesk);
                    if (hit != null)
                    {
                        Dispatcher.BeginInvoke((Action)delegate { PopDrop(hit); });
                        return (IntPtr)1;
                    }
                    if (_grabbed == null)
                    {
                        if (onDesk)
                        {
                            SnowBall pick = FindBall(mx, my);
                            if (pick != null)
                            {
                                Dispatcher.BeginInvoke((Action)delegate { GrabBall(pick, mx, my); });
                                return (IntPtr)1;
                            }
                        }
                        SnowDrop tree = FindGrabbableTree(mx, my, onDesk);
                        if (tree != null)
                        {
                            Dispatcher.BeginInvoke((Action)delegate { GrabTree(tree, mx, my); });
                            return (IntPtr)1;
                        }
                        if (onDesk && _snowballsEnabled && _ballHeld == null && InSnowZone(mx, my))
                        {
                            Dispatcher.BeginInvoke((Action)delegate { StartBall(mx, my); });
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
                else if (msg == WM_MOUSEMOVE && _ballHeld != null)
                {
                    double mx2 = info.pt.X - SystemParameters.VirtualScreenLeft;
                    double my2 = info.pt.Y - SystemParameters.VirtualScreenTop;
                    uint t2 = info.time;
                    Dispatcher.BeginInvoke((Action)delegate { MoveBall(mx2, my2, t2); });
                }
                else if (msg == WM_LBUTTONUP && _ballHeld != null)
                {
                    Dispatcher.BeginInvoke((Action)delegate { ReleaseBall(); });
                }
            }
            return CallNextHookEx(_hook, nCode, wParam, lParam);
        }

        bool ClickOnDesktop(POINT pt)
        {
            try
            {
                IntPtr h = WindowFromPoint(pt);
                if (h == IntPtr.Zero) return true;
                h = GetAncestor(h, GA_ROOT);
                if (h == IntPtr.Zero || h == GetDesktopWindow()) return true;
                System.Text.StringBuilder sb = new System.Text.StringBuilder(64);
                GetClassName(h, sb, 64);
                string cls = sb.ToString();
                return cls == "Progman" || cls == "WorkerW";
            }
            catch (Exception)
            {
                return true;
            }
        }

        SnowDrop FindClickableDrop(double mx, double my, bool onDesk)
        {
            for (int i = 0; i < _drops.Count; i++)
            {
                SnowDrop d = _drops[i];
                if (d.Popped || d.IsTree || !d.Stuck) continue;
                if (!onDesk && d.RoofHwnd == IntPtr.Zero) continue;
                double l = Canvas.GetLeft(d.Shape), t = Canvas.GetTop(d.Shape);
                if (mx >= l - 8 && mx <= l + d.Shape.Width + 8 && my >= t - 8 && my <= t + d.Shape.Height + 8)
                {
                    return d;
                }
            }
            return null;
        }

        SnowDrop FindGrabbableTree(double mx, double my, bool onDesk)
        {
            for (int i = 0; i < _drops.Count; i++)
            {
                SnowDrop d = _drops[i];
                if (!d.IsTree || d.Popped || d.Held || !d.Stuck) continue;
                if (!onDesk && d.RoofHwnd == IntPtr.Zero) continue;
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

        bool InSnowZone(double x, double y)
        {
            if (_season != Season.Winter) return false;
            if (_depths == null || _groundY <= 0.0) return false;
            if (y >= _groundY) return false;
            return y >= _groundY - _depths[DriftIndexFor(x)] - 6.0;
        }

        void ScrapeSnow(double x, double amount)
        {
            if (_depths == null) return;
            int idx = DriftIndexFor(x);
            for (int d = -2; d <= 2; d++)
            {
                int j = idx + d;
                if (j < 0 || j >= _depths.Length) continue;
                _depths[j] -= amount / (1 + d * d);
                if (_depths[j] < 0.0) _depths[j] = 0.0;
            }
        }

        SnowBall FindBall(double mx, double my)
        {
            for (int i = _balls.Count - 1; i >= 0; i--)
            {
                SnowBall b = _balls[i];
                if (b.Held || b.Shape == null) continue;
                if (Math.Abs(mx - (b.X + b.R)) < b.R + 8.0 &&
                    Math.Abs(my - (b.Y + b.R)) < b.R + 8.0) return b;
            }
            return null;
        }

        Canvas BuildBallShape(double r)
        {
            Canvas c = new Canvas { Width = r * 2.0, Height = r * 2.0 };
            Ellipse body = new Ellipse
            {
                Width = r * 2.0,
                Height = r * 2.0,
                Fill = new SolidColorBrush(Color.FromRgb(248, 250, 255)),
                Stroke = new SolidColorBrush(Color.FromRgb(224, 232, 246)),
                StrokeThickness = 1.0
            };
            Canvas.SetLeft(body, 0.0);
            Canvas.SetTop(body, 0.0);
            c.Children.Add(body);
            Ellipse hi = new Ellipse
            {
                Width = r * 0.7,
                Height = r * 0.55,
                Fill = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255))
            };
            Canvas.SetLeft(hi, r * 0.25);
            Canvas.SetTop(hi, r * 0.2);
            c.Children.Add(hi);
            c.RenderTransformOrigin = new Point(0.0, 0.0);
            c.RenderTransform = new ScaleTransform(1.0, 1.0);
            return c;
        }

        void GrabBall(SnowBall b, double mx, double my)
        {
            if (b == null || b.Shape == null) return;
            _ballHeld = b;
            b.Held = true;
            b.Standing = false;
            b.Flying = false;
            b.Under = null;
            b.Level = 0;
            b.Vx = 0.0;
            b.Vy = 0.0;
            b.X = mx - b.R;
            b.Y = my - b.R;
            Canvas.SetLeft(b.Shape, b.X);
            Canvas.SetTop(b.Shape, b.Y);
            System.Windows.Controls.Panel.SetZIndex(b.Shape, 85);
            _ballVx = 0.0;
            _ballVy = 0.0;
            _ballLastX = mx;
            _ballLastY = my;
            _ballLastT = 0;
            SnowSplash(mx, my, 8);
            Log("snowball grabbed");
        }

        void StartBall(double mx, double my)
        {
            if (_canvas == null || _depths == null || _groundY <= 0.0) return;
            foreach (SnowBall b in _balls)
            {
                if (b.Held) return;
            }
            double r = 9.0;
            SnowBall nb = new SnowBall();
            nb.R = r;
            nb.Held = true;
            nb.Standing = false;
            nb.Flying = false;
            nb.Vx = 0.0;
            nb.Vy = 0.0;
            nb.Level = 0;
            nb.Under = null;
            nb.Shape = BuildBallShape(r);
            nb.X = mx - r;
            nb.Y = my - r;
            Canvas.SetLeft(nb.Shape, nb.X);
            Canvas.SetTop(nb.Shape, nb.Y);
            System.Windows.Controls.Panel.SetZIndex(nb.Shape, 85);
            _balls.Add(nb);
            _canvas.Children.Add(nb.Shape);
            _ballHeld = nb;
            _ballVx = 0.0;
            _ballVy = 0.0;
            _ballLastX = mx;
            _ballLastY = my;
            _ballLastT = 0;
            SnowSplash(mx, my, 8);
            Log("snowball started");
        }

        void MoveBall(double mx, double my, uint tstamp)
        {
            SnowBall b = _ballHeld;
            if (b == null || !b.Held) return;
            double dx = mx - _ballLastX;
            bool inDrift = my < _groundY &&
                InSnowZone(b.X + b.R, b.Y + b.R * 2.0);
            if (b.R < 30.0 && inDrift)
            {
                double gr = Math.Min(1.4, Math.Abs(dx) * 0.03);
                if (gr > 0.02)
                {
                    b.R += gr;
                    ScaleTransform st = b.Shape.RenderTransform as ScaleTransform;
                    if (st != null)
                    {
                        st.ScaleX = b.R / 9.0;
                        st.ScaleY = b.R / 9.0;
                    }
                }
            }
            if (inDrift)
            {
                ScrapeSnow(b.X + b.R, 0.6 + Math.Abs(dx) * 0.25);
                if (_driftTick % 10 == 0)
                {
                    SnowSplash(b.X + b.R + _rng.NextDouble() * 20.0 - 10.0,
                        b.Y + b.R * 2.0, 2);
                }
            }
            b.X = mx - b.R;
            b.Y = my - b.R;
            Canvas.SetLeft(b.Shape, b.X);
            Canvas.SetTop(b.Shape, b.Y);
            if (tstamp > _ballLastT)
            {
                double sdt = (tstamp - _ballLastT) / 1000.0;
                if (sdt > 0.004 && sdt < 0.4)
                {
                    _ballVx = _ballVx * 0.6 + (dx / sdt) * 0.4;
                    _ballVy = _ballVy * 0.6 + ((my - _ballLastY) / sdt) * 0.4;
                }
                _ballLastT = tstamp;
            }
            _ballLastX = mx;
            _ballLastY = my;
        }

        void ReleaseBall()
        {
            SnowBall b = _ballHeld;
            if (b == null) return;
            _ballHeld = null;
            b.Held = false;
            double sp = Math.Sqrt(_ballVx * _ballVx + _ballVy * _ballVy);
            if (sp > 250.0)
            {
                b.Flying = true;
                double max = 1400.0;
                b.Vx = Math.Max(-max, Math.Min(max, _ballVx));
                b.Vy = Math.Max(-max, Math.Min(max, _ballVy * 0.8));
                System.Windows.Controls.Panel.SetZIndex(b.Shape, 70);
                Log("snowball thrown, v=" + Math.Round(sp));
            }
            else
            {
                SettleBall(b);
            }
        }

        void SettleBall(SnowBall b)
        {
            if (_depths == null || _groundY <= 0.0)
            {
                _canvas.Children.Remove(b.Shape);
                _balls.Remove(b);
                return;
            }
            double cx = b.X + b.R;
            SnowBall support = null;
            for (int i = 0; i < _balls.Count; i++)
            {
                SnowBall s2 = _balls[i];
                if (s2 == b || !s2.Standing) continue;
                double d = Math.Abs((s2.X + s2.R) - cx);
                if (d >= Math.Max(s2.R * 0.8, b.R * 0.8)) continue;
                if (support == null || s2.Level > support.Level ||
                    (s2.Level == support.Level && d < Math.Abs((support.X + support.R) - cx)))
                {
                    support = s2;
                }
            }
            if (support != null && support.Level < 2)
            {
                b.Level = support.Level + 1;
                b.Under = support;
                b.X = support.X + support.R - b.R;
                b.Y = support.Y - b.R * 2.0 + b.R * 0.25;
                if (b.Level == 2 && support.Under != null)
                {
                    SnowBall baseB = support.Under;
                    double sr = support.R;
                    double hr = b.R;
                    double scx = baseB.X + baseB.R;
                    _balls.Remove(baseB);
                    _balls.Remove(support);
                    _balls.Remove(b);
                    _canvas.Children.Remove(baseB.Shape);
                    _canvas.Children.Remove(support.Shape);
                    _canvas.Children.Remove(b.Shape);
                    BuildSnowman(scx, baseB.R, sr, hr);
                    SnowSplash(scx, _groundY - _depths[DriftIndexFor(scx)], 10);
                    Log("snowman built");
                    return;
                }
            }
            else
            {
                b.Level = 0;
                b.Under = null;
                double landY = _groundY - _depths[DriftIndexFor(cx)];
                b.Y = landY - b.R * 2.0 + b.R * 0.25;
            }
            b.Standing = true;
            System.Windows.Controls.Panel.SetZIndex(b.Shape, 52);
            Canvas.SetLeft(b.Shape, b.X);
            Canvas.SetTop(b.Shape, b.Y);
            SnowSplash(b.X + b.R, b.Y + b.R * 2.0, 5);
        }

        void UpdateBalls(double dt)
        {
            if (_canvas == null) return;
            for (int i = _balls.Count - 1; i >= 0; i--)
            {
                SnowBall b = _balls[i];
                if (b.Held || b.Standing || !b.Flying) continue;
                b.Vy += 750.0 * dt;
                b.X += b.Vx * dt;
                b.Y += b.Vy * dt;
                Canvas.SetLeft(b.Shape, b.X);
                Canvas.SetTop(b.Shape, b.Y);
                double bcx = b.X + b.R;
                double bcy = b.Y + b.R;

                if (_trainActive && !_trainDowned && _train != null)
                {
                    double tx = _trainX + ((_trainDir > 0) ? 120.0 : 50.0);
                    if (Math.Abs(bcx - tx) < 150.0 && Math.Abs(bcy - _trainY) < 70.0)
                    {
                        _canvas.Children.Remove(b.Shape);
                        _balls.RemoveAt(i);
                        SnowSplash(bcx, bcy, 14);
                        KnockDownTrain(bcx, bcy);
                        continue;
                    }
                }

                if (_grinchActive && !_grinchDead && _grinch != null)
                {
                    double gl = Canvas.GetLeft(_grinch);
                    double gt = Canvas.GetTop(_grinch);
                    if (bcx >= gl - 6.0 && bcx <= gl + 74.0 && bcy >= gt - 6.0 && bcy <= gt + 80.0)
                    {
                        _canvas.Children.Remove(b.Shape);
                        _balls.RemoveAt(i);
                        SnowSplash(bcx, bcy, 12);
                        GrinchDie();
                        continue;
                    }
                }

                if (_depths == null || _groundY <= 0.0) continue;
                double landY = _groundY - _depths[DriftIndexFor(bcx)];
                if (b.Y + b.R * 2.0 >= landY - 2.0)
                {
                    _canvas.Children.Remove(b.Shape);
                    _balls.RemoveAt(i);
                    SnowSplash(bcx, landY, 12);
                    if (b.R > 12.0) DepositSnow(bcx, 2.0);
                    continue;
                }
                if (b.X < -80.0 || b.X > W() + 80.0 || b.Y > H() + 80.0)
                {
                    _canvas.Children.Remove(b.Shape);
                    _balls.RemoveAt(i);
                }
            }
        }

        void BuildSnowman(double cx, double r0, double r1, double r2)
        {
            if (_canvas == null || _depths == null || _groundY <= 0.0) return;
            double maxR = Math.Max(r0, Math.Max(r1, r2));
            double width = maxR * 2.0 + 60.0;
            double x0 = width / 2.0;
            double cy0 = r0 * 2.0;
            double cy1 = cy0 - (r0 + r1) * 0.75;
            double cy2 = cy1 - (r1 + r2) * 0.75;
            double topY = cy2 - r2;
            double height = cy0 + r0 - 6.0;
            Canvas man = new Canvas { Width = width, Height = height };
            Brush fur = new SolidColorBrush(Color.FromRgb(249, 251, 255));
            Brush line = new SolidColorBrush(Color.FromRgb(224, 232, 246));
            Ellipse baseBall = new Ellipse { Width = r0 * 2.0, Height = r0 * 2.0, Fill = fur, Stroke = line, StrokeThickness = 1.0 };
            Canvas.SetLeft(baseBall, x0 - r0);
            Canvas.SetTop(baseBall, cy0 - r0);
            man.Children.Add(baseBall);
            Ellipse midBall = new Ellipse { Width = r1 * 2.0, Height = r1 * 2.0, Fill = fur, Stroke = line, StrokeThickness = 1.0 };
            Canvas.SetLeft(midBall, x0 - r1);
            Canvas.SetTop(midBall, cy1 - r1);
            man.Children.Add(midBall);
            Ellipse head = new Ellipse { Width = r2 * 2.0, Height = r2 * 2.0, Fill = fur, Stroke = line, StrokeThickness = 1.0 };
            Canvas.SetLeft(head, x0 - r2);
            Canvas.SetTop(head, cy2 - r2);
            man.Children.Add(head);
            Brush dot = new SolidColorBrush(Color.FromRgb(70, 65, 60));
            for (int i = 0; i < 3; i++)
            {
                Ellipse btn = new Ellipse { Width = 4.0, Height = 4.0, Fill = dot };
                Canvas.SetLeft(btn, x0 - 2.0);
                Canvas.SetTop(btn, cy1 - r1 * 0.3 + i * r1 * 0.25);
                man.Children.Add(btn);
            }
            Ellipse eyeL = new Ellipse { Width = 3.5, Height = 3.5, Fill = dot };
            Canvas.SetLeft(eyeL, x0 - 6.5);
            Canvas.SetTop(eyeL, cy2 - r2 * 0.35);
            man.Children.Add(eyeL);
            Ellipse eyeR = new Ellipse { Width = 3.5, Height = 3.5, Fill = dot };
            Canvas.SetLeft(eyeR, x0 + 3.0);
            Canvas.SetTop(eyeR, cy2 - r2 * 0.35);
            man.Children.Add(eyeR);
            Polygon carrot = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromRgb(245, 140, 40)),
                Points = new PointCollection
                {
                    new Point(x0 - 1.0, cy2 - 1.0),
                    new Point(x0 + 13.0, cy2 + 3.0),
                    new Point(x0 - 1.0, cy2 + 5.0)
                }
            };
            man.Children.Add(carrot);
            Brush stick = new SolidColorBrush(Color.FromRgb(110, 80, 50));
            Line armL = new Line { X1 = x0 - r1 * 0.7, Y1 = cy1 - 2.0, X2 = x0 - r1 * 1.7, Y2 = cy1 - r1 * 0.8, Stroke = stick, StrokeThickness = 3.0, StrokeEndLineCap = PenLineCap.Round };
            man.Children.Add(armL);
            Line armR = new Line { X1 = x0 + r1 * 0.7, Y1 = cy1 - 2.0, X2 = x0 + r1 * 1.7, Y2 = cy1 - r1 * 0.8, Stroke = stick, StrokeThickness = 3.0, StrokeEndLineCap = PenLineCap.Round };
            man.Children.Add(armR);
            Polygon bucket = new Polygon
            {
                Fill = new SolidColorBrush(Color.FromRgb(200, 50, 50)),
                Points = new PointCollection
                {
                    new Point(x0 - r2 * 0.6, topY + 3.0),
                    new Point(x0 + r2 * 0.6, topY + 3.0),
                    new Point(x0 + r2 * 0.42, topY - r2 * 0.75),
                    new Point(x0 - r2 * 0.42, topY - r2 * 0.75)
                }
            };
            man.Children.Add(bucket);
            System.Windows.Controls.Panel.SetZIndex(man, 52);
            Canvas.SetLeft(man, cx - width / 2.0);
            Canvas.SetTop(man, _groundY - _depths[DriftIndexFor(cx)] - height + 4.0);
            _canvas.Children.Add(man);
            Snowman sm = new Snowman();
            sm.X = cx;
            sm.Shape = man;
            _snowmen.Add(sm);
            if (_snowmen.Count > 2)
            {
                Snowman old = _snowmen[0];
                _snowmen.RemoveAt(0);
                SnowSplash(old.X, Canvas.GetTop(old.Shape) + old.Shape.Height, 8);
                _canvas.Children.Remove(old.Shape);
            }
        }


        Canvas BuildRabbit()
        {
            Canvas c = new Canvas { Width = 46.0, Height = 46.0 };
            Brush fur = new SolidColorBrush(Color.FromRgb(242, 240, 236));
            Brush pink = new SolidColorBrush(Color.FromRgb(245, 190, 195));
            Ellipse earL = new Ellipse { Width = 7.0, Height = 20.0, Fill = fur, RenderTransformOrigin = new Point(0.5, 1.0), RenderTransform = new RotateTransform(-12.0) };
            Canvas.SetLeft(earL, 18.0);
            Canvas.SetTop(earL, 0.0);
            c.Children.Add(earL);
            Ellipse earLi = new Ellipse { Width = 3.5, Height = 13.0, Fill = pink, RenderTransformOrigin = new Point(0.5, 1.0), RenderTransform = new RotateTransform(-12.0) };
            Canvas.SetLeft(earLi, 19.75);
            Canvas.SetTop(earLi, 4.0);
            c.Children.Add(earLi);
            Ellipse earR = new Ellipse { Width = 7.0, Height = 20.0, Fill = fur, RenderTransformOrigin = new Point(0.5, 1.0), RenderTransform = new RotateTransform(8.0) };
            Canvas.SetLeft(earR, 26.0);
            Canvas.SetTop(earR, 1.0);
            c.Children.Add(earR);
            Ellipse earRi = new Ellipse { Width = 3.5, Height = 13.0, Fill = pink, RenderTransformOrigin = new Point(0.5, 1.0), RenderTransform = new RotateTransform(8.0) };
            Canvas.SetLeft(earRi, 27.75);
            Canvas.SetTop(earRi, 5.0);
            c.Children.Add(earRi);
            Ellipse tail = new Ellipse { Width = 9.0, Height = 9.0, Fill = new SolidColorBrush(Color.FromRgb(252, 252, 250)) };
            Canvas.SetLeft(tail, 1.0);
            Canvas.SetTop(tail, 27.0);
            c.Children.Add(tail);
            Ellipse body = new Ellipse { Width = 30.0, Height = 21.0, Fill = fur };
            Canvas.SetLeft(body, 6.0);
            Canvas.SetTop(body, 22.0);
            c.Children.Add(body);
            Ellipse head = new Ellipse { Width = 19.0, Height = 17.0, Fill = fur };
            Canvas.SetLeft(head, 22.0);
            Canvas.SetTop(head, 10.0);
            c.Children.Add(head);
            Ellipse eye = new Ellipse { Width = 3.0, Height = 3.0, Fill = new SolidColorBrush(Color.FromRgb(60, 50, 45)) };
            Canvas.SetLeft(eye, 33.0);
            Canvas.SetTop(eye, 15.0);
            c.Children.Add(eye);
            Ellipse nose = new Ellipse { Width = 4.0, Height = 3.0, Fill = pink };
            Canvas.SetLeft(nose, 38.0);
            Canvas.SetTop(nose, 19.0);
            c.Children.Add(nose);
            return c;
        }

        void SetRabbitFacing(double moveDir)
        {
            if (_rabbit == null) return;
            _rabbit.RenderTransformOrigin = new Point(0.5, 1.0);
            _rabbit.RenderTransform = new ScaleTransform((moveDir < 0.0) ? -1.0 : 1.0, 1.0);
        }

        void SpawnRabbit(double x)
        {
            if (_rabbit != null || _canvas == null) return;
            double dir = (x < W() * 0.5) ? 1.0 : -1.0;
            _rabbit = BuildRabbit();
            _rabbitDir = dir;
            _rabbitX = (dir > 0.0) ? -60.0 : W() + 50.0;
            _rabbitTarget = Math.Max(100.0, Math.Min(W() - 100.0, x));
            _rabbitState = 0;
            _rabbitStateT = 0.0;
            _rabbitPhase = 0.0;
            SetRabbitFacing(dir);
            System.Windows.Controls.Panel.SetZIndex(_rabbit, 62);
            Canvas.SetLeft(_rabbit, _rabbitX);
            Canvas.SetTop(_rabbit, H() - 46.0);
            _canvas.Children.Add(_rabbit);
            Log("rabbit came to dig a gift");
        }

        void RabbitTick(double dt)
        {
            if (_rabbit == null || _canvas == null) return;
            if (_depths == null || _groundY <= 0.0) return;
            double landY = _groundY - _depths[DriftIndexFor(_rabbitX + 23.0)];
            if (_rabbitState == 0)
            {
                _rabbitPhase += dt * 9.0;
                double hop = Math.Abs(Math.Sin(_rabbitPhase)) * 13.0;
                _rabbitX += _rabbitDir * 130.0 * dt;
                Canvas.SetLeft(_rabbit, _rabbitX);
                Canvas.SetTop(_rabbit, landY - 46.0 - hop);
                if (Math.Abs(_rabbitX - _rabbitTarget) < 10.0)
                {
                    _rabbitState = 1;
                    _rabbitStateT = 0.0;
                }
            }
            else if (_rabbitState == 1)
            {
                _rabbitStateT += dt;
                _rabbitX += Math.Sin(_rabbitStateT * 14.0) * 12.0 * dt;
                Canvas.SetLeft(_rabbit, _rabbitX);
                Canvas.SetTop(_rabbit, landY - 44.0);
                if (_driftTick % 8 == 0) SnowSplash(_rabbitX + _rabbitDir * 15.0, landY, 2);
                if (_rabbitStateT >= 2.2)
                {
                    SpawnDrop(_rabbitX + 34.0, landY - 36.0, _rabbitDir * 120.0, -170.0, false);
                    _rabbitState = 2;
                    SetRabbitFacing(-_rabbitDir);
                }
            }
            else
            {
                _rabbitPhase += dt * 9.0;
                double hop = Math.Abs(Math.Sin(_rabbitPhase)) * 13.0;
                _rabbitX += -_rabbitDir * 130.0 * dt;
                Canvas.SetLeft(_rabbit, _rabbitX);
                Canvas.SetTop(_rabbit, landY - 46.0 - hop);
                if ((_rabbitDir > 0.0 && _rabbitX < -70.0) || (_rabbitDir < 0.0 && _rabbitX > W() + 60.0))
                {
                    _canvas.Children.Remove(_rabbit);
                    _rabbit = null;
                    Log("rabbit done");
                }
            }
        }

        void BuildCountdown()
        {
            if (_canvas == null) return;
            _countdownBoard = new Rectangle
            {
                Width = 330,
                Height = 44,
                RadiusX = 9,
                RadiusY = 9,
                Fill = new SolidColorBrush(Color.FromArgb(150, 30, 45, 65)),
                Stroke = new SolidColorBrush(Color.FromArgb(102, 255, 255, 255)),
                StrokeThickness = 1.0
            };
            System.Windows.Controls.Panel.SetZIndex(_countdownBoard, 56);
            _canvas.Children.Add(_countdownBoard);
            _countdownText = new TextBlock
            {
                Text = "",
                Foreground = new SolidColorBrush(Color.FromRgb(240, 246, 255)),
                FontFamily = new FontFamily("Segoe UI"),
                FontSize = 17.0,
                FontWeight = FontWeights.Bold,
                TextAlignment = System.Windows.TextAlignment.Center,
                Width = 330.0
            };
            System.Windows.Controls.Panel.SetZIndex(_countdownText, 57);
            _canvas.Children.Add(_countdownText);
            _countdownBoard.Visibility = Visibility.Collapsed;
            _countdownText.Visibility = Visibility.Collapsed;
            UpdateCountdown();
        }

        void UpdateCountdown()
        {
            if (_countdownBoard == null || _countdownText == null) return;
            if (!_countdownEnabled || _season != Season.Winter)
            {
                _countdownBoard.Visibility = Visibility.Collapsed;
                _countdownText.Visibility = Visibility.Collapsed;
                return;
            }
            if (_groundY <= 0.0) return;
            DateTime n = DateTime.Now;
            if (n.Month == 1 && n.Day == 1)
            {
                _countdownBoard.Visibility = Visibility.Collapsed;
                _countdownText.Visibility = Visibility.Collapsed;
                return;
            }
            DateTime ny = new DateTime(n.Year + 1, 1, 1);
            TimeSpan sp = ny - n;
            _countdownText.Text = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "До Нового года: {0} д {1:00}:{2:00}:{3:00}",
                (int)sp.TotalDays, sp.Hours, sp.Minutes, sp.Seconds);
            double bx = W() - 18.0 - 330.0;
            Canvas.SetLeft(_countdownBoard, bx);
            Canvas.SetTop(_countdownBoard, _groundY - 96.0);
            Canvas.SetLeft(_countdownText, bx);
            Canvas.SetTop(_countdownText, _groundY - 88.0);
            _countdownBoard.Visibility = Visibility.Visible;
            _countdownText.Visibility = Visibility.Visible;
        }

        void ShowNewYearCard()
        {
            if (_canvas == null) return;
            _nyBanner = new TextBlock
            {
                Text = "С Новым годом!",
                FontSize = 54,
                FontWeight = FontWeights.Bold,
                Foreground = new SolidColorBrush(Color.FromRgb(225, 240, 255)),
                TextAlignment = System.Windows.TextAlignment.Center,
                Width = 460.0,
                Opacity = 0.0
            };
            _nyBanner.RenderTransformOrigin = new Point(0.5, 0.5);
            _nyBanner.RenderTransform = new RotateTransform(-4.0);
            _nyBanner.Effect = new System.Windows.Media.Effects.DropShadowEffect
            {
                BlurRadius = 18.0,
                ShadowDepth = 0.0,
                Opacity = 0.9
            };
            System.Windows.Controls.Panel.SetZIndex(_nyBanner, 83);
            Canvas.SetLeft(_nyBanner, W() / 2.0 - 230.0);
            Canvas.SetTop(_nyBanner, H() * 0.32);
            _canvas.Children.Add(_nyBanner);
            _nyBannerLife = 9.0;
            _showLeft = 6;
            _showTimer = 0.2;
            Log("new year greeting shown");
        }

        void KnockDownTrain(double cx, double cy)
        {
            if (_train == null || _trainDowned) return;
            _trainDowned = true;
            _trainDownVy = -260.0;
            _trainDownSpin = -_trainDir * (260.0 + _rng.NextDouble() * 220.0);
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
            BuildMoon();
            if (_moon != null)
            {
                _moon.Visibility = (_season == Season.Winter) ? Visibility.Visible : Visibility.Collapsed;
            }
            BuildCountdown();
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
            _drift.Fill = new LinearGradientBrush(SeasonDriftTop(), SeasonDriftBottom(), 90.0);
            _drift.Opacity = (_season == Season.Summer) ? 0.8 : 0.95;
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
            if (_season == Season.Spring) AddPetal(randomY);
            else if (_season == Season.Summer) AddRaindrop(randomY);
            else if (_season == Season.Autumn) AddLeaf(randomY);
            else AddSnowflake(randomY);
        }

        void AddSnowflake(bool randomY)
        {
            Flake f = new Flake();
            f.Kind = 0;
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
            if (_season == Season.Autumn)
            {
                _wind = _wind * 2.0 + Math.Sin(_t / 3.7) * 16.0;
            }


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


            _sparkleTimer -= dt;
            if (_sparkleTimer <= 0.0)
            {
                _sparkleTimer = 0.08 + _rng.NextDouble() * 0.16;
                if (_sparklesEnabled && _depths != null && _groundY > 0 && _sparkles.Count < 40)
                {
                    for (int tries = 0; tries < 3; tries++)
                    {
                        double sx = _rng.NextDouble() * W();
                        int si = DriftIndexFor(sx);
                        if (_depths[si] > 2.0)
                        {
                            SpawnSparkle(sx, _groundY - _depths[si]);
                            break;
                        }
                    }
                }
                if (_sparklesEnabled && _roofs.Count > 0 && _sparkles.Count < 60 && _rng.NextDouble() < 0.6)
                {
                    Roof rr = _roofs[_rng.Next(_roofs.Count)];
                    if (rr.Depths != null && rr.Depths.Length > 4)
                    {
                        for (int tries = 0; tries < 4; tries++)
                        {
                            int idx2 = _rng.Next(rr.Depths.Length);
                            if (rr.Depths[idx2] > 2.0)
                            {
                                SpawnSparkle(rr.L + idx2 * _driftSegment + 8.0, rr.T - rr.Depths[idx2]);
                                break;
                            }
                        }
                    }
                }
            }
            _decoTimer -= dt;
            if (_decoTimer <= 0.0)
            {
                _decoTimer = 0.08 + _rng.NextDouble() * 0.12;
                if (_season == Season.Autumn && _sparklesEnabled && _decos.Count < 1500)
                {
                    for (int dk = 0; dk < 12; dk++)
                    {
                        if (_decos.Count >= 1500) break;
                        SpawnDriftDeco();
                    }
                }
            }
            UpdateDecos(dt);
            UpdateSparkles(dt);
            UpdateIcicles(dt);

            _bulbPhase++;
            if (_bulbPhase % 33 == 0) ToggleTreeLights();

            if (_season == Season.Winter && _fireworksAuto)
            {
                _fireworkTimer -= dt;
                if (_fireworkTimer <= 0.0)
                {
                    _fireworkTimer = 45.0 + _rng.NextDouble() * 45.0;
                    if (_rng.NextDouble() < 0.7) SpawnRocket();
                }
                _starTimer -= dt;
                if (_starTimer <= 0.0)
                {
                    _starTimer = 40.0 + _rng.NextDouble() * 55.0;
                    SpawnShootingStar();
                }
            }
            UpdateRockets(dt);
            UpdateStars(dt);
            UpdateConfetti(dt);
            UpdateBalls(dt);
            RabbitTick(dt);
            if (_moonGlow != null) _moonGlow.Opacity = 0.55 + Math.Sin(_t * 0.6) * 0.25;
            if (_showLeft > 0)
            {
                _showTimer -= dt;
                if (_showTimer <= 0.0)
                {
                    SpawnRocket();
                    _showLeft--;
                    _showTimer = 0.55;
                }
            }
            if (_nyBanner != null)
            {
                _nyBannerLife -= dt;
                if (_nyBannerLife <= 0.0)
                {
                    _canvas.Children.Remove(_nyBanner);
                    _nyBanner = null;
                }
                else
                {
                    _nyBanner.Opacity = Math.Max(0.0, Math.Min(1.0,
                        Math.Min((9.0 - _nyBannerLife) / 0.8, _nyBannerLife / 2.5)));
                }
            }

            _driftTick++;
            if (!_nyDone && _driftTick % 60 == 10)
            {
                DateTime n1 = DateTime.Now;
                if (n1.Month == 1 && n1.Day == 1 && n1.Hour < 6)
                {
                    _nyDone = true;
                    ShowNewYearCard();
                }
            }
            if (_driftTick % 60 == 0) UpdateCountdown();
            if (_driftTick % 6 == 0) UpdateDriftShape();
            RefreshRoofs();
            if (_roofHwnd != IntPtr.Zero && _driftTick % 30 == 0)
            {
                ApplyRoofStyles(_roofHwnd);
                if (_settingsHwnd != IntPtr.Zero)
                {
                    SetWindowPos(_settingsHwnd, HWND_TOPMOST, 0, 0, 0, 0,
                        SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                }
            }

            if (_mode == SnowMode.DesktopOnly && _driftTick % 10 == 0 && !_attachedBehindIcons)
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
                if (f.Rtf != null)
                {
                    f.Rot += f.RotSpeed * dt;
                    f.Rtf.Angle = f.Rot;
                }

                bool roofLanded = false;
                if (f.Kind != 3)
                {
                    for (int ri = 0; ri < _roofs.Count; ri++)
                    {
                        Roof r = _roofs[ri];
                        if (r.Depths == null) continue;
                        if (f.X >= r.L && f.X <= r.R && oldY <= r.T && f.Y >= r.T)
                        {
                            if (IsRoofOccluded(r, f.X)) continue;
                            DepositRoof(r, f.X, f.R * 5.0);
                            if (_season == Season.Autumn && _decos.Count < 1500)
                            {
                                int li = (int)((f.X - r.L) / _driftSegment);
                                if (li < 0) li = 0;
                                if (li >= r.Depths.Length) li = r.Depths.Length - 1;
                                SpawnLeafDecoAt(f.X, r.T - r.Depths[li] + _rng.NextDouble() * 2.5, true, r, f.Tint, r.T - r.Depths[li]);
                            }
                            roofLanded = true;
                            break;
                        }
                    }
                }

                if (!roofLanded && f.Y >= _groundY - _depths[DriftIndexFor(f.X)])
                {
                    if (f.Kind == 3)
                    {
                        int wet = DriftIndexFor(f.X);
                        if (_depths[wet] > 1.0 && _rng.NextDouble() < 0.10)
                        {
                            SpawnSparkle(f.X, _groundY - _depths[wet]);
                        }
                    }
                    else
                    {
                        DepositSnow(f.X, f.R * 5.0);
                        if (_season == Season.Autumn && _decos.Count < 1500)
                        {
                            SpawnLeafDecoAt(f.X, _groundY - _depths[DriftIndexFor(f.X)] - 1.0 + _rng.NextDouble() * 2.5, false, null, f.Tint, _groundY - _depths[DriftIndexFor(f.X)]);
                        }
                    }
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
                if (f.Kind == 3)
                {
                    Canvas.SetLeft(f.Shape, f.X);
                    Canvas.SetTop(f.Shape, f.Y);
                }
                else
                {
                    Canvas.SetLeft(f.Shape, f.X - f.R);
                    Canvas.SetTop(f.Shape, f.Y - f.R);
                }
            }

            SantaTick(dt);
            GrinchTick(dt);
            SeasonTick(dt);
        }

        void SantaTick(double dt)
        {
            bool activeTrain = _trainActive || _trainDowned;
            if (_season != Season.Winter && !activeTrain) return;
            if (_season == Season.Winter && _santaAutoEnabled)
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
            }

            if (_trainDowned && _train != null)
            {
                _trainDownVy += 1000.0 * dt;
                _trainX += _trainDir * 140.0 * dt;
                _trainY += _trainDownVy * dt;
                Canvas.SetLeft(_train, _trainDir > 0 ? _trainX : _trainX - 170);
                Canvas.SetTop(_train, _trainY);
                if (_trainRot != null) _trainRot.Angle += _trainDownSpin * dt;
                if (_trainY > H() + 300.0)
                {
                    _canvas.Children.Remove(_train);
                    _train = null;
                    _trainActive = false;
                    _trainDowned = false;
                    _trainRot = null;
                    Log("santa fell off screen");
                }
            }
            else if (_trainActive)
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

        }

        void SpawnTrain()
        {
            _trainDir = (_rng.NextDouble() < 0.5) ? 1 : -1;
            _legTfs.Clear();
            _headTfs.Clear();
            _armTf = null;
            _trailTimer = 0.0;
            if (_grinchEnabled && !_grinchActive && _rng.NextDouble() < 0.4)
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
            _trainDowned = false;
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

            Color[] bulbColors = new Color[]
            {
                Color.FromRgb(255, 60, 60), Color.FromRgb(255, 200, 40),
                Color.FromRgb(80, 200, 255), Color.FromRgb(100, 235, 100),
                Color.FromRgb(230, 80, 200)
            };
            double[] bx = new double[] { 17.0, 27.0, 21.0, 25.0 };
            double[] by = new double[] { 24.0, 33.0, 44.0, 12.0 };
            for (int i = 0; i < 4; i++)
            {
                Ellipse bulb = new Ellipse { Width = 3.5, Height = 3.5 };
                Color bc = bulbColors[_rng.Next(bulbColors.Length)];
                bulb.Fill = new SolidColorBrush(bc);
                tree.Children.Add(ShapeAt(bulb, bx[i], by[i]));
                _treeBulbs.Add(bulb);
                _treeBulbColors.Add(bc);
            }

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
                if (w >= W() - 40.0 && hh >= H() - 100.0) _fullscreenPresent = true;
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

        bool IsRoofOccluded(Roof roof, double x)
        {
            for (int k = 0; k < _enumList.Count && k < roof.Z; k++)
            {
                RECT e = _enumRects[k];
                if (x < e.Left - 2.0 || x > e.Right + 2.0) continue;
                double wtop = e.Top - SystemParameters.VirtualScreenTop;
                double wbot = e.Bottom - SystemParameters.VirtualScreenTop;
                if (wtop <= roof.T + 2.0 && wbot > roof.T) return true;
            }
            return false;
        }

        Geometry ComputeDropClip(Roof roof)
        {
            Rect area = new Rect(roof.L - 24.0, roof.T - 90.0, (roof.R - roof.L) + 48.0, (roof.B - roof.T) + 140.0);
            Geometry clip = new RectangleGeometry(area);
            for (int k = 0; k < _enumList.Count && k < roof.Z; k++)
            {
                RECT e = _enumRects[k];
                Rect win = new Rect(
                    e.Left - SystemParameters.VirtualScreenLeft,
                    e.Top - SystemParameters.VirtualScreenTop,
                    e.Right - e.Left,
                    e.Bottom - e.Top);
                if (!win.IntersectsWith(area)) continue;
                win.Inflate(2.0, 2.0);
                clip = new CombinedGeometry(GeometryCombineMode.Exclude, clip, new RectangleGeometry(win));
            }
            return clip;
        }

        Geometry ComputeDropClipAt(Roof roof, double left, double top)
        {
            Geometry g = ComputeDropClip(roof);
            if (g != null) g.Transform = new TranslateTransform(-left, -top);
            return g;
        }

        Geometry ComputeRoofClip(Roof roof)
        {
            Rect band = new Rect(roof.L + 1.0, roof.T - 100.0, Math.Max(4.0, (roof.R - roof.L) - 2.0), 148.0);
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

        Polygon BuildIcicle(double w, double h)
        {
            Polygon p = new Polygon();
            p.Points = new PointCollection
            {
                new Point(w * 0.15, 0.0), new Point(w * 0.85, 0.0),
                new Point(w * 0.72, h * 0.35), new Point(w * 0.58, h * 0.7),
                new Point(w * 0.5, h), new Point(w * 0.42, h * 0.7),
                new Point(w * 0.28, h * 0.35)
            };
            p.Fill = new LinearGradientBrush(
                Color.FromRgb(234, 244, 255), Color.FromArgb(170, 175, 210, 240), 90.0);
            p.Stroke = new SolidColorBrush(Color.FromArgb(120, 150, 185, 225));
            p.StrokeThickness = 1.0;
            p.Opacity = 0.9;
            return p;
        }

        void EnsureIcicles(Roof r)
        {
            if (_season != Season.Winter || !_iciclesEnabled) return;
            if (_roofCanvas == null || r == null || r.Depths == null || r.Depths.Length < 6) return;
            int have = 0;
            List<int> taken = new List<int>();
            for (int i = 0; i < _icicles.Count; i++)
            {
                if (_icicles[i].Roof == r)
                {
                    have++;
                    taken.Add(_icicles[i].Seg);
                }
            }
            if (have >= 5) return;
            for (int tries = 0; tries < 8; tries++)
            {
                int seg = _rng.Next(1, r.Depths.Length - 1);
                if (taken.Contains(seg)) continue;
                if (r.Depths[seg] < 3.0) continue;
                double w = 3.5 + _rng.NextDouble() * 3.5;
                double h = 9.0 + r.Depths[seg] * 0.9 + _rng.NextDouble() * 8.0;
                double x = r.L + seg * _driftSegment + 3.0 + _rng.NextDouble() * (_driftSegment - 8.0);
                if (x + w > r.R - 2.0) x = r.R - 2.0 - w;
                double y = r.T - r.Depths[seg] + 1.0;
                if (IsRoofOccluded(r, x)) continue;
                Polygon p = BuildIcicle(w, h);
                Canvas.SetLeft(p, x);
                Canvas.SetTop(p, y);
                System.Windows.Controls.Panel.SetZIndex(p, 46);
                Geometry iclip = ComputeRoofClip(r);
                if (iclip != null) iclip.Transform = new TranslateTransform(-x, -y);
                p.Clip = iclip;
                _roofCanvas.Children.Add(p);
                Icicle ic = new Icicle();
                ic.Roof = r;
                ic.Seg = seg;
                ic.X = x;
                ic.W = w;
                ic.H = h;
                ic.Phase = _rng.NextDouble() * 6.0;
                ic.Shape = p;
                _icicles.Add(ic);
                return;
            }
        }

        void DripIcicle(Icicle ic)
        {
            if (_roofCanvas == null || _icicleDrips.Count > 12) return;
            IcicleDrip d = new IcicleDrip();
            d.X = ic.X + ic.W * 0.5;
            d.Y = Canvas.GetTop(ic.Shape) + ic.H - 1.0;
            d.Life = 0.6;
            d.Shape = new Ellipse
            {
                Width = 2.5,
                Height = 4.5,
                Fill = new SolidColorBrush(Color.FromArgb(210, 198, 226, 250))
            };
            System.Windows.Controls.Panel.SetZIndex(d.Shape, 46);
            Canvas.SetLeft(d.Shape, d.X);
            Canvas.SetTop(d.Shape, d.Y);
            _icicleDrips.Add(d);
            _roofCanvas.Children.Add(d.Shape);
        }

        void UpdateIcicles(double dt)
        {
            if (_roofCanvas == null) return;
            _icicleTimer -= dt;
            if (_icicleTimer <= 0.0)
            {
                _icicleTimer = 2.0 + _rng.NextDouble() * 2.0;
                if (!_fullscreenPresent)
                {
                    for (int i = _roofs.Count - 1; i >= 0; i--) EnsureIcicles(_roofs[i]);
                }
            }
            for (int i = _icicles.Count - 1; i >= 0; i--)
            {
                Icicle ic = _icicles[i];
                Roof r2 = ic.Roof;
                bool bad = r2 == null || _roofs.IndexOf(r2) < 0 ||
                    r2.Depths == null || r2.Depths.Length <= ic.Seg;
                if (!bad && r2.Depths[ic.Seg] < 1.5) bad = true;
                if (!bad)
                {
                    bool hidden = IsRoofOccluded(r2, ic.X);
                    if (ic.Shape != null)
                    {
                        ic.Shape.Visibility = hidden ? Visibility.Collapsed : Visibility.Visible;
                        ic.Shape.Opacity = 0.8 + Math.Sin(ic.Phase * 2.2) * 0.2;
                    }
                    if (!hidden)
                    {
                        ic.Phase += dt;
                        if (_rng.NextDouble() < dt * 0.25) DripIcicle(ic);
                    }
                }
                if (bad)
                {
                    if (ic.Shape != null) _roofCanvas.Children.Remove(ic.Shape);
                    _icicles.RemoveAt(i);
                }
            }
            for (int i = _icicleDrips.Count - 1; i >= 0; i--)
            {
                IcicleDrip d = _icicleDrips[i];
                d.Life -= dt;
                d.Y += 90.0 * dt;
                if (d.Life <= 0.0 || d.Y > H())
                {
                    _roofCanvas.Children.Remove(d.Shape);
                    _icicleDrips.RemoveAt(i);
                    continue;
                }
                d.Shape.Opacity = Math.Max(0.0, d.Life / 0.6);
                Canvas.SetTop(d.Shape, d.Y);
            }
        }

        void RefreshRoofs()
        {
            if (_roofCanvas == null || _enumProc == null) return;
            _enumList.Clear();
            _enumRects.Clear();
            _fullscreenPresent = false;
            EnumWindows(_enumProc, IntPtr.Zero);

            _roofSet.Clear();
            if (!_fullscreenPresent && _season != Season.Summer && _roofCapsEnabled)
            {
                for (int k = 0; k < _enumList.Count; k++)
                {
                    RECT e = _enumRects[k];
                    double w = e.Right - e.Left;
                    double hh = e.Bottom - e.Top;
                    if (w >= 100.0 && hh >= 80.0 && !(w >= W() - 40.0 && hh >= H() - 100.0)
                        && _enumList[k] != _settingsHwnd)
                    {
                        _roofSet.Add(_enumList[k]);
                    }
                }
            }

            for (int i = _roofs.Count - 1; i >= 0; i--)
            {
                Roof r = _roofs[i];
                int idx = IndexOfHwnd(r.Hwnd);
                if (idx < 0 || !_roofSet.Contains(r.Hwnd))
                {
                    ReleaseRoofSnow(r, false);
                    MoveRoofDropsOff(r);
                    for (int d3 = _decos.Count - 1; d3 >= 0; d3--)
                    {
                        if (_decos[d3].OnRoof && _decos[d3].Ref == r) KillDeco(_decos[d3], d3);
                    }
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
                    bool quiet = (_driftTick - r.LastMoveTick) < 20;
                    r.LastMoveTick = _driftTick;
                    ReleaseRoofSnow(r, quiet);
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

            RoofMeltAndSmooth(_driftTick % 2 == 0);
            UpdateRoofShapes();
        }

        void ReleaseRoofSnow(Roof roof, bool quiet)
        {
            if (roof.Depths == null || _canvas == null) return;
            for (int i = _decos.Count - 1; i >= 0; i--)
            {
                if (_decos[i].OnRoof && _decos[i].Ref == roof) KillDeco(_decos[i], i);
            }
            if (quiet)
            {
                for (int i = 0; i < roof.Depths.Length; i++) roof.Depths[i] = 0.0;
                if (roof.Poly != null) roof.Poly.Points = new PointCollection();
                return;
            }
            int spawned = 0;
            for (int i = 0; i < roof.Depths.Length && spawned < 200; i++)
            {
                int n = 1 + (int)(roof.Depths[i] / 2.5);
                for (int k = 0; k < n && spawned < 200; k++)
                {
                    Confetto c = new Confetto();
                    c.X = roof.L + i * _driftSegment + _rng.NextDouble() * _driftSegment;
                    c.Y = roof.T - _rng.NextDouble() * Math.Max(1.0, roof.Depths[i]);
                    c.Vx = -45.0 + _rng.NextDouble() * 90.0;
                    c.Vy = 25.0 + _rng.NextDouble() * 70.0;
                    c.Life = 0.5 + _rng.NextDouble() * 0.5;
                    Ellipse puff = new Ellipse
                    {
                        Width = 3.0 + _rng.NextDouble() * 3.5,
                        Height = 3.0 + _rng.NextDouble() * 3.5,
                        Fill = new SolidColorBrush(SeasonSplashColor())
                    };
                    c.Shape = puff;
                    System.Windows.Controls.Panel.SetZIndex(puff, 90);
                    Canvas.SetLeft(puff, c.X);
                    Canvas.SetTop(puff, c.Y);
                    _confetti.Add(c);
                    _canvas.Children.Add(puff);
                    spawned++;
                }
                roof.Depths[i] = 0.0;
            }
            if (roof.Poly != null) roof.Poly.Points = new PointCollection();
        }

        void RoofPuff(double x, double y, int count, Roof roof)
        {
            if (_canvas == null) return;
            for (int i = 0; i < count; i++)
            {
                Confetto c = new Confetto();
                double side = (_rng.NextDouble() < 0.5) ? -1.0 : 1.0;
                double px = x + side * _rng.NextDouble() * 10.0;
                if (roof != null && IsRoofOccluded(roof, px)) continue;
                c.X = px;
                c.Y = y - 2.0 - _rng.NextDouble() * 6.0;
                c.Vx = side * (40.0 + _rng.NextDouble() * 110.0);
                c.Vy = -40.0 - _rng.NextDouble() * 80.0;
                c.Life = 0.5 + _rng.NextDouble() * 0.4;
                Ellipse puff = new Ellipse
                {
                    Width = 2.5 + _rng.NextDouble() * 2.5,
                    Height = 2.5 + _rng.NextDouble() * 2.5,
                    Fill = new SolidColorBrush(SeasonSplashColor())
                };
                c.Shape = puff;
                System.Windows.Controls.Panel.SetZIndex(puff, 80);
                Canvas.SetLeft(puff, c.X);
                Canvas.SetTop(puff, c.Y);
                _confetti.Add(c);
                _canvas.Children.Add(puff);
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

        void RoofMeltAndSmooth(bool melt)
        {
            double dt = 0.16;
            for (int ri = 0; ri < _roofs.Count; ri++)
            {
                double[] dep = _roofs[ri].Depths;
                if (dep == null) continue;
                if (melt)
                {
                    for (int i = 0; i < dep.Length; i++)
                    {
                        double excess = dep[i] - DriftSoftCap;
                        if (excess > 0.0) dep[i] -= (excess * 0.3) * dt;
                        if (dep[i] > _maxDrift) dep[i] = _maxDrift;
                        if (dep[i] < 0) dep[i] = 0;
                    }
                }
                for (int pass = 0; pass < 2; pass++)
                {
                    double[] tmp = new double[dep.Length];
                    for (int i = 0; i < dep.Length; i++) tmp[i] = dep[i];
                    for (int i = 1; i < dep.Length - 1; i++)
                    {
                        dep[i] = (tmp[i - 1] + 2.0 * tmp[i] + tmp[i + 1]) * 0.25;
                    }
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
                double total = 0.0;
                for (int i = 0; i < r.Depths.Length; i++) total += r.Depths[i];
                if (total < 0.5)
                {
                    if (r.Poly != null && r.Poly.Points.Count > 0)
                    {
                        r.Poly.Points = new PointCollection();
                        r.Poly.Clip = null;
                    }
                    continue;
                }
                if (r.Poly == null)
                {
                    r.Poly = new Polygon();
                    r.Poly.Fill = new SolidColorBrush(SeasonDriftTop());
                    r.Poly.Opacity = (_season == Season.Summer) ? 0.8 : 0.95;
                    System.Windows.Controls.Panel.SetZIndex(r.Poly, 50);
                    _roofCanvas.Children.Add(r.Poly);
                }
                PointCollection pts = new PointCollection();
                int n = r.Depths.Length;
                int first = -1, last = -1;
                for (int i = 0; i < n; i++)
                {
                    if (r.Depths[i] > 0.35)
                    {
                        last = i;
                        if (first < 0) first = i;
                    }
                }
                if (first < 0)
                {
                    if (r.Poly.Points.Count > 0) r.Poly.Points = new PointCollection();
                    continue;
                }
                int a = Math.Max(0, first - 2);
                int b = Math.Min(n - 1, last + 2);
                pts.Add(new Point(r.L + a * _driftSegment, r.T + 1));
                for (int i = a; i <= b; i++)
                {
                    double t = Math.Min(1.0, Math.Min(i - a, b - i) / 3.0);
                    double taper = t * t * (3.0 - 2.0 * t);
                    pts.Add(new Point(r.L + i * _driftSegment, r.T - r.Depths[i] * taper));
                }
                pts.Add(new Point(r.L + b * _driftSegment, r.T + 1));
                pts.Add(new Point(r.L + b * _driftSegment, r.T + 2));
                pts.Add(new Point(r.L + a * _driftSegment, r.T + 2));
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
                dr.Shape.Clip = (owner != null) ? ComputeDropClipAt(owner, dr.X, dr.Y) : null;
            }
        }

        void DepositRoof(Roof roof, double x, double amount)
        {
            if (roof.Depths == null) return;
            int idx = (int)((x - roof.L) / _driftSegment);
            if (idx < 0) idx = 0;
            if (idx >= roof.Depths.Length) idx = roof.Depths.Length - 1;
            for (int d = -3; d <= 3; d++)
            {
                int j = idx + d;
                if (j < 0 || j >= roof.Depths.Length) continue;
                roof.Depths[j] += amount * 1.3 / (1 + d * d);
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
                if (IsRoofOccluded(r, cx)) continue;
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
            d.Sink = d.IsTree ? 4.0 : 3.0;
            d.Y = roof.T - h + Math.Min(d.Sink, 2.0);
            d.Host = _roofCanvas;
            Canvas prev = d.Shape.Parent as Canvas;
            if (prev != null && prev != _roofCanvas) prev.Children.Remove(d.Shape);
            _roofCanvas.Children.Add(d.Shape);
            System.Windows.Controls.Panel.SetZIndex(d.Shape, 45);
            d.Shape.Clip = ComputeDropClipAt(roof, d.X, d.Y);
            RoofPuff(d.X + d.Shape.Width / 2.0, roof.T, 8, roof);
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
                    Fill = new SolidColorBrush(SeasonSplashColor())
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
            _grinchDeadT = 0.0;
            _grinchLanded = false;
            _grinchSlide = 0.0;
            _grinchSink = 0.0;
            _grinchSteals = 0;
            _grinchStack.Clear();
            _grinchStealTimer = 1.0;
            _grinchPhase = 0.0;
            Log("grinch appeared");
        }

        void AttachGrinchGift()
        {
            if (_grinch == null || _grinchStack.Count >= 6) return;
            Canvas gift = BuildGift();
            gift.RenderTransformOrigin = new Point(0.5, 0.5);
            gift.RenderTransform = new RotateTransform(-12.0 + _rng.NextDouble() * 24.0);
            int n = _grinchStack.Count;
            Canvas.SetLeft(gift, 40.0 - _rng.NextDouble() * 8.0 - n * 1.5);
            Canvas.SetTop(gift, 40.0 - (n + 1) * 9.0 + _rng.NextDouble() * 4.0);
            _grinch.Children.Add(gift);
            _grinchStack.Add(gift);
        }

        void GrinchDie()
        {
            if (_grinch == null || _grinchDead || !_grinchActive) return;
            _grinchDead = true;
            _grinchDeadT = 0.0;
            _grinchLanded = false;
            _grinchSlide = 0.0;
            if (_grinchLoot != null) _grinchLoot.Visibility = Visibility.Collapsed;
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
                _grinchDeadT += dt;
                double tilt;
                if (_grinchDeadT < 0.22)
                {
                    tilt = -30.0 * (_grinchDeadT / 0.22);
                    _grinchX -= _grinchDir * 150.0 * dt;
                }
                else if (_grinchDeadT < 0.52)
                {
                    double t = (_grinchDeadT - 0.22) / 0.30;
                    tilt = -30.0 + t * 128.0;
                    if (tilt > 60.0 && !_grinchLanded)
                    {
                        _grinchLanded = true;
                        System.Windows.Controls.Panel.SetZIndex(_grinch, 45);
                        SnowSplash(_grinchX + w / 2.0, landY, 16);
                        _grinchSlide = _grinchDir * 95.0;
                        int sp = Math.Min(_grinchStack.Count, 4);
                        if (sp == 0 && _grinchSteals > 0) sp = 1;
                        for (int n = 0; n < sp; n++)
                        {
                            SpawnDrop(_grinchX + _grinchDir * 40.0 - n * 8.0 * _grinchDir,
                                landY - 30.0 - n * 8.0,
                                -_grinchDir * (70.0 + _rng.NextDouble() * 60.0) + _wind * 0.5,
                                -160.0 - _rng.NextDouble() * 80.0, false);
                        }
                        _grinchSteals = 0;
                    }
                    if (tilt > 98.0) tilt = 98.0;
                }
                else
                {
                    double e = Math.Exp(-(_grinchDeadT - 0.52) * 3.5);
                    tilt = 88.0 + 10.0 * e;
                    _grinchX += _grinchSlide * dt;
                    _grinchSlide -= _grinchSlide * 4.0 * dt;
                    _grinchSink += 7.5 * dt;
                    if (_grinchSink >= h)
                    {
                        double gx = _grinchX + 32.0;
                        SnowSplash(_grinchX + w / 2.0, landY, 8);
                        _canvas.Children.Remove(_grinch);
                        _grinch = null;
                        _grinchActive = false;
                        _grinchStack.Clear();
                        Log("grinch buried");
                        if (_rabbitEnabled) SpawnRabbit(gx);
                        return;
                    }
                }
                if (_grinchRot != null) _grinchRot.Angle = tilt;
                Canvas.SetLeft(_grinch, _grinchX);
                Canvas.SetTop(_grinch, landY - h + _grinchSink);
                if (_grinchLanded && _driftTick % 10 == 0) SnowSplash(_grinchX + w / 2.0, landY, 3);
                return;
            }

            _grinchPhase += dt * 8.0;
            _grinchX += _grinchDir * 95.0 * dt;
            double bob = Math.Sin(_grinchPhase * 2.0) * 1.5;
            if (_grinchRot != null) _grinchRot.Angle = Math.Sin(_grinchPhase * 2.0) * 4.0;
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
                        AttachGrinchGift();
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
                _grinchStack.Clear();
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

        void SpawnSparkle(double x, double y)
        {
            if (_canvas == null) return;
            Sparkle s = new Sparkle();
            s.X = x + (_rng.NextDouble() - 0.5) * 10.0;
            s.Y = y - _rng.NextDouble() * 2.0;
            s.MaxLife = 0.8 + _rng.NextDouble() * 1.0;
            s.Life = s.MaxLife;
            bool warm = _rng.NextDouble() < 0.4;
            byte v = (byte)(238 + _rng.Next(18));
            Ellipse e = new Ellipse
            {
                Width = 2.4 + _rng.NextDouble() * 1.8,
                Height = 2.4 + _rng.NextDouble() * 1.8,
                Fill = new SolidColorBrush(warm ? Color.FromRgb(255, 240, 170) : Color.FromRgb(v, v, 255))
            };
            e.Opacity = 1.0;
            s.Shape = e;
            System.Windows.Controls.Panel.SetZIndex(e, 55);
            Canvas.SetLeft(e, s.X);
            Canvas.SetTop(e, s.Y);
            _sparkles.Add(s);
            _canvas.Children.Add(e);
        }

        void SpawnDriftDeco()
        {
            if (_canvas == null || _roofCanvas == null) return;
            bool onRoof = _roofs.Count > 0 && _rng.NextDouble() < 0.55;
            if (onRoof)
            {
                Roof rr = _roofs[_rng.Next(_roofs.Count)];
                if (rr.Depths == null || rr.Depths.Length < 4) return;
                int idx = _rng.Next(rr.Depths.Length);
                if (rr.Depths[idx] < 2.5) return;
                double x = rr.L + idx * _driftSegment + _rng.NextDouble() * _driftSegment;
                double y = rr.T - rr.Depths[idx] + _rng.NextDouble() * 4.0 - 1.5;
                SpawnLeafDecoAt(x, y, true, rr, PickLeafTint(), rr.T - rr.Depths[idx]);
            }
            else
            {
                if (_depths == null || _groundY <= 0.0) return;
                double x = _rng.NextDouble() * W();
                int si = DriftIndexFor(x);
                if (_depths[si] < 2.5) return;
                x += _rng.NextDouble() * _driftSegment - _driftSegment * 0.5;
                double y = _groundY - _depths[si] + _rng.NextDouble() * 4.0 - 1.5;
                SpawnLeafDecoAt(x, y, false, null, PickLeafTint(), _groundY - _depths[si]);
            }
        }

        Color PickLeafTint()
        {
            Color[] pal = new Color[] {
                Color.FromRgb(190, 58, 36),
                Color.FromRgb(222, 128, 48),
                Color.FromRgb(232, 188, 84),
                Color.FromRgb(166, 102, 44),
                Color.FromRgb(104, 118, 48),
                Color.FromRgb(206, 150, 66),
                Color.FromRgb(128, 72, 36),
                Color.FromRgb(216, 108, 58)
            };
            return pal[_rng.Next(pal.Length)];
        }

        void SpawnLeafDecoAt(double x, double y, bool onRoof, Roof refRoof, Color tint, double surfY)
        {
            if (_canvas == null || _roofCanvas == null) return;
            if (_decos.Count >= 1500) return;
            DriftDeco d = new DriftDeco();
            double w = 11.0 + _rng.NextDouble() * 5.0;
            double h = w * (0.55 + _rng.NextDouble() * 0.12);
            d.W2 = w * 0.5;
            d.H2 = h * 0.5;
            d.EmbedOff = y - surfY;
            if (onRoof && refRoof != null)
            {
                if (x < refRoof.L + 3.0) x = refRoof.L + 3.0 + _rng.NextDouble() * 4.0;
                if (x > refRoof.R - 3.0 - w) x = refRoof.R - 3.0 - w - _rng.NextDouble() * 4.0;
                if (x < refRoof.L || x > refRoof.R) return;
            }
            else
            {
                if (x < 2.0) x = 2.0 + _rng.NextDouble() * 4.0;
                if (x > W() - 2.0 - w) x = W() - 2.0 - w - _rng.NextDouble() * 4.0;
            }
            Ellipse leaf = new Ellipse { Width = w, Height = h };
            Color light = Color.FromRgb(
                (byte)Math.Min(255, tint.R + 45),
                (byte)Math.Min(255, tint.G + 45),
                (byte)Math.Min(255, tint.B + 45));
            leaf.Fill = new LinearGradientBrush(light, tint, 45.0);
            leaf.Stroke = new SolidColorBrush(Color.FromArgb(120, 90, 50, 25));
            leaf.StrokeThickness = 0.6;
            leaf.RenderTransform = new RotateTransform(_rng.NextDouble() * 360.0);
            leaf.Opacity = 0.7 + _rng.NextDouble() * 0.3;
            d.BaseOp = leaf.Opacity;
            d.Shape = leaf;
            d.X = x;
            d.Y = y;
            d.Ref = refRoof;
            d.OnRoof = onRoof;
            d.MaxLife = 90.0 + _rng.NextDouble() * 60.0;
            d.Life = d.MaxLife;
            System.Windows.Controls.Panel.SetZIndex(leaf, 56);
            Canvas.SetLeft(leaf, x - w * 0.5);
            Canvas.SetTop(leaf, y - h * 0.5);
            if (onRoof)
            {
                if (refRoof != null)
                {
                    Geometry rclip = ComputeRoofClip(refRoof);
                    if (rclip != null) rclip.Transform = new TranslateTransform(w * 0.5 - x, h * 0.5 - y);
                    leaf.Clip = rclip;
                }
                _roofCanvas.Children.Add(leaf);
            }
            else
            {
                _canvas.Children.Add(leaf);
            }
            _decos.Add(d);
        }

        void UpdateDecos(double dt)
        {
            for (int i = _decos.Count - 1; i >= 0; i--)
            {
                DriftDeco d = _decos[i];
                if (_season != Season.Autumn && d.Life > 2.0) d.Life = 2.0;
                if ((_driftTick + i) % 30 == 0 && !ReanchorDeco(d))
                {
                    KillDeco(d, i);
                    continue;
                }
                bool gone = false;
                d.Life -= dt;
                if (d.Life <= 0.0 || DepthAtDeco(d) < 1.5)
                {
                    gone = true;
                }
                else
                {
                    double t = 1.0;
                    if (d.Life > d.MaxLife - 0.4) t = (d.MaxLife - d.Life) / 0.4;
                    if (d.Life < 2.0) t = Math.Min(t, d.Life / 2.0);
                    d.Shape.Opacity = d.BaseOp * Math.Max(0.0, Math.Min(1.0, t));
                }
                if (gone)
                {
                    KillDeco(d, i);
                    continue;
                }
            }
        }

        bool ReanchorDeco(DriftDeco d)
        {
            if (d.OnRoof)
            {
                if (d.Ref == null || d.Ref.Depths == null) return false;
                int idx = (int)((d.X - d.Ref.L) / _driftSegment);
                if (idx < 0 || idx >= d.Ref.Depths.Length) return false;
                if (d.X < d.Ref.L || d.X > d.Ref.R) return false;
                d.Y = d.Ref.T - d.Ref.Depths[idx] + d.EmbedOff;
            }
            else
            {
                if (_depths == null || _groundY <= 0.0) return false;
                d.Y = _groundY - _depths[DriftIndexFor(d.X)] + d.EmbedOff;
            }
            if (d.Y > H()) return false;
            Canvas.SetTop(d.Shape, d.Y - d.H2);
            if (d.OnRoof && d.Ref != null)
            {
                Geometry g = ComputeRoofClip(d.Ref);
                if (g != null) g.Transform = new TranslateTransform(d.W2 - d.X, d.H2 - d.Y);
                d.Shape.Clip = g;
            }
            return true;
        }

        double DepthAtDeco(DriftDeco d)
        {
            if (d.OnRoof)
            {
                if (d.Ref == null || d.Ref.Depths == null) return 0.0;
                int idx = (int)((d.X - d.Ref.L) / _driftSegment);
                if (idx < 0 || idx >= d.Ref.Depths.Length) return 0.0;
                return d.Ref.Depths[idx];
            }
            if (_depths == null || _groundY <= 0.0) return 0.0;
            return _depths[DriftIndexFor(d.X)];
        }

        void KillDeco(DriftDeco d, int i)
        {
            Canvas host = d.OnRoof ? _roofCanvas : _canvas;
            if (host != null && d.Shape != null) host.Children.Remove(d.Shape);
            _decos.RemoveAt(i);
        }

        void ClearDecos()
        {
            for (int i = _decos.Count - 1; i >= 0; i--) KillDeco(_decos[i], i);
        }

        void BuildMoon()
        {
            if (_canvas == null) return;
            _moon = new Canvas();
            _moonGlow = new Ellipse { Width = 200, Height = 200 };
            RadialGradientBrush glow = new RadialGradientBrush();
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(80, 230, 238, 255), 0.0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, 230, 238, 255), 1.0));
            _moonGlow.Fill = glow;
            Canvas.SetLeft(_moonGlow, -58);
            Canvas.SetTop(_moonGlow, -58);
            _moon.Children.Add(_moonGlow);
            Ellipse disc = new Ellipse
            {
                Width = 84,
                Height = 84,
                Fill = new SolidColorBrush(Color.FromRgb(246, 247, 252))
            };
            disc.Opacity = 0.92;
            _moon.Children.Add(disc);
            Brush crater = new SolidColorBrush(Color.FromRgb(216, 221, 235));
            double[][] craters = new double[][]
            {
                new double[] { 26, 20, 13 },
                new double[] { 46, 40, 9 },
                new double[] { 20, 48, 7 },
                new double[] { 44, 14, 5 }
            };
            for (int i = 0; i < craters.Length; i++)
            {
                Ellipse e = new Ellipse
                {
                    Width = craters[i][2],
                    Height = craters[i][2],
                    Fill = crater,
                    Opacity = 0.7
                };
                Canvas.SetLeft(e, craters[i][0]);
                Canvas.SetTop(e, craters[i][1]);
                _moon.Children.Add(e);
            }
            Canvas.SetLeft(_moon, W() - 148.0);
            Canvas.SetTop(_moon, 34.0);
            System.Windows.Controls.Panel.SetZIndex(_moon, -4);
            _canvas.Children.Add(_moon);
        }

        void StarTrail(double x, double y, Color col)
        {
            if (_canvas == null || _sparkles.Count >= 90) return;
            Sparkle s = new Sparkle();
            s.X = x + (_rng.NextDouble() - 0.5) * 7.0;
            s.Y = y + (_rng.NextDouble() - 0.5) * 7.0;
            s.MaxLife = 0.5 + _rng.NextDouble() * 0.4;
            s.Life = s.MaxLife;
            Ellipse e = new Ellipse
            {
                Width = 1.8 + _rng.NextDouble() * 1.5,
                Height = 1.8 + _rng.NextDouble() * 1.5,
                Fill = new SolidColorBrush(col)
            };
            e.Opacity = 1.0;
            s.Shape = e;
            System.Windows.Controls.Panel.SetZIndex(e, 79);
            Canvas.SetLeft(e, s.X);
            Canvas.SetTop(e, s.Y);
            _sparkles.Add(s);
            _canvas.Children.Add(e);
        }

        void SpawnShootingStar()
        {
            if (_canvas == null) return;
            bool dirRight = _rng.NextDouble() < 0.5;
            ShootingStar s = new ShootingStar();
            s.X = dirRight ? -50.0 : W() + 50.0;
            s.Y = 30.0 + _rng.NextDouble() * (H() * 0.30);
            s.Vx = (dirRight ? 1.0 : -1.0) * (500.0 + _rng.NextDouble() * 260.0);
            s.Vy = 130.0 + _rng.NextDouble() * 110.0;
            s.MaxLife = 1.4 + _rng.NextDouble() * 0.5;
            s.Life = s.MaxLife;
            Line ln = new Line();
            double len = 46.0 + _rng.NextDouble() * 26.0;
            double vlen = Math.Sqrt(s.Vx * s.Vx + s.Vy * s.Vy);
            ln.X1 = 0;
            ln.Y1 = 0;
            ln.X2 = -(s.Vx / vlen) * len;
            ln.Y2 = -(s.Vy / vlen) * len;
            LinearGradientBrush tail = new LinearGradientBrush();
            tail.StartPoint = new Point(0.5, 0.5);
            tail.EndPoint = new Point(0.5, 0.5);
            tail.StartPoint = new Point(0, 0);
            tail.EndPoint = new Point(1, 1);
            tail.GradientStops.Add(new GradientStop(Color.FromArgb(240, 255, 244, 170), 0.0));
            tail.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 225, 120), 1.0));
            ln.Stroke = tail;
            ln.StrokeThickness = 2.6;
            s.Shape = ln;
            System.Windows.Controls.Panel.SetZIndex(ln, 79);
            Canvas.SetLeft(ln, s.X);
            Canvas.SetTop(ln, s.Y);
            _stars.Add(s);
            _canvas.Children.Add(ln);
        }

        void UpdateStars(double dt)
        {
            for (int i = _stars.Count - 1; i >= 0; i--)
            {
                ShootingStar s = _stars[i];
                s.Life -= dt;
                s.X += s.Vx * dt;
                s.Y += s.Vy * dt;
                Canvas.SetLeft(s.Shape, s.X);
                Canvas.SetTop(s.Shape, s.Y);
                StarTrail(s.X, s.Y, Color.FromRgb(255, 226, 130));
                StarTrail(s.X + (_rng.NextDouble() - 0.5) * 18.0, s.Y - _rng.NextDouble() * 10.0, Color.FromRgb(255, 244, 180));
                if (_rng.NextDouble() < 0.5)
                    StarTrail(s.X + (_rng.NextDouble() - 0.5) * 24.0, s.Y - _rng.NextDouble() * 14.0, Color.FromRgb(255, 200, 90));
                if (s.Life <= 0.0 || s.X < -80.0 || s.X > W() + 80.0 || s.Y > H())
                {
                    RemoveShape(s.Shape);
                    _stars.RemoveAt(i);
                }
            }
        }

        void SpawnRocket()
        {
            if (_canvas == null) return;
            Rocket r = new Rocket();
            r.X = 60.0 + _rng.NextDouble() * Math.Max(80.0, W() - 120.0);
            r.Y = _groundY - 4.0;
            r.Vy = -(650.0 + _rng.NextDouble() * 250.0);
            r.ExplodeY = H() * (0.16 + _rng.NextDouble() * 0.25);
            r.Shape = new Ellipse
            {
                Width = 4.0,
                Height = 7.0,
                Fill = new SolidColorBrush(Color.FromRgb(255, 220, 140))
            };
            System.Windows.Controls.Panel.SetZIndex(r.Shape, 80);
            Canvas.SetLeft(r.Shape, r.X);
            Canvas.SetTop(r.Shape, r.Y);
            _rockets.Add(r);
            _canvas.Children.Add(r.Shape);
        }

        void UpdateRockets(double dt)
        {
            for (int i = _rockets.Count - 1; i >= 0; i--)
            {
                Rocket r = _rockets[i];
                r.Vy += 300.0 * dt;
                r.Y += r.Vy * dt;
                Canvas.SetLeft(r.Shape, r.X);
                Canvas.SetTop(r.Shape, r.Y);
                StarTrail(r.X + (_rng.NextDouble() - 0.5) * 5.0, r.Y + 6.0, Color.FromRgb(255, 215, 120));
                if (r.Y <= r.ExplodeY || r.Vy > -120.0)
                {
                    FireworkBurst(r.X, r.Y);
                    RemoveShape(r.Shape);
                    _rockets.RemoveAt(i);
                }
            }
        }

        void FireworkBurst(double cx, double cy)
        {
            if (_canvas == null) return;
            Color[] palette = new Color[]
            {
                Color.FromRgb(235, 60, 55), Color.FromRgb(250, 200, 45),
                Color.FromRgb(60, 130, 245), Color.FromRgb(55, 200, 85),
                Color.FromRgb(245, 120, 40), Color.FromRgb(225, 70, 200),
                Color.FromRgb(90, 220, 235)
            };
            Confetto flash = new Confetto();
            flash.X = cx - 55.0;
            flash.Y = cy - 55.0;
            flash.Vx = 0.0;
            flash.Vy = 0.0;
            flash.Life = 0.22;
            Ellipse fl = new Ellipse { Width = 110, Height = 110 };
            RadialGradientBrush rg = new RadialGradientBrush();
            rg.GradientStops.Add(new GradientStop(Color.FromArgb(150, 255, 248, 220), 0.0));
            rg.GradientStops.Add(new GradientStop(Color.FromArgb(0, 255, 248, 220), 1.0));
            fl.Fill = rg;
            flash.Shape = fl;
            System.Windows.Controls.Panel.SetZIndex(fl, 82);
            Canvas.SetLeft(fl, flash.X);
            Canvas.SetTop(fl, flash.Y);
            _confetti.Add(flash);
            _canvas.Children.Add(fl);
            for (int i = 0; i < 54; i++)
            {
                double ang = (i / 54.0) * Math.PI * 2.0;
                double sp = 100.0 + _rng.NextDouble() * 220.0;
                Confetto c = new Confetto();
                c.X = cx;
                c.Y = cy;
                c.Vx = Math.Cos(ang) * sp;
                c.Vy = Math.Sin(ang) * sp * 0.9;
                c.Life = 0.9 + _rng.NextDouble() * 0.8;
                Color col = (_rng.NextDouble() < 0.2)
                    ? Color.FromRgb(255, 255, 255)
                    : palette[_rng.Next(palette.Length)];
                c.Shape = new Ellipse
                {
                    Width = 2.4 + _rng.NextDouble() * 1.8,
                    Height = 2.4 + _rng.NextDouble() * 1.8,
                    Fill = new SolidColorBrush(col)
                };
                System.Windows.Controls.Panel.SetZIndex(c.Shape, 81);
                Canvas.SetLeft(c.Shape, c.X);
                Canvas.SetTop(c.Shape, c.Y);
                _confetti.Add(c);
                _canvas.Children.Add(c.Shape);
            }
        }

        void UpdateSparkles(double dt)
        {
            for (int i = _sparkles.Count - 1; i >= 0; i--)
            {
                Sparkle s = _sparkles[i];
                s.Life -= dt;
                if (s.Life <= 0.0)
                {
                    RemoveShape(s.Shape);
                    _sparkles.RemoveAt(i);
                    continue;
                }
                s.Shape.Opacity = Math.Min(1.0, 1.4 * (s.Life / s.MaxLife));
            }
        }

        void ToggleTreeLights()
        {
            bool on = (_bulbPhase / 33) % 2 == 0;
            for (int i = _treeBulbs.Count - 1; i >= 0; i--)
            {
                Ellipse b = _treeBulbs[i];
                Canvas pc = b.Parent as Canvas;
                if (pc == null || pc.Parent == null)
                {
                    _treeBulbs.RemoveAt(i);
                    _treeBulbColors.RemoveAt(i);
                    continue;
                }
                b.Fill = on
                    ? new SolidColorBrush(_treeBulbColors[i])
                    : new SolidColorBrush(Color.FromRgb(70, 72, 66));
            }
        }

        void ClearAllSnow()
        {
            ClearDecos();
            if (_depths != null)
            {
                for (int i = 0; i < _depths.Length; i++) _depths[i] = 0.0;
                UpdateDriftShape();
            }
            for (int i = _roofs.Count - 1; i >= 0; i--)
            {
                Roof r = _roofs[i];
                if (r.Poly != null) r.Poly.Points = new PointCollection();
                r.Depths = new double[r.Depths == null ? 2 : r.Depths.Length];
                MoveRoofDropsOff(r);
            }
            for (int i = _drops.Count - 1; i >= 0; i--)
            {
                SnowDrop d = _drops[i];
                if (d.Held || !d.Stuck) continue;
                RemoveShape(d.Shape);
                _drops.RemoveAt(i);
            }
            for (int i = _balls.Count - 1; i >= 0; i--)
            {
                SnowBall b = _balls[i];
                if (b.Held || b.Shape == null) continue;
                SnowSplash(b.X + b.R, b.Y + b.R * 2.0, 4);
                _canvas.Children.Remove(b.Shape);
                for (int j = 0; j < _balls.Count; j++)
                {
                    if (_balls[j].Under == b) _balls[j].Under = null;
                }
                _balls.RemoveAt(i);
            }
            for (int i = _snowmen.Count - 1; i >= 0; i--)
            {
                Snowman s = _snowmen[i];
                if (s.Shape != null)
                {
                    SnowSplash(s.X, Canvas.GetTop(s.Shape) + s.Shape.Height, 8);
                    _canvas.Children.Remove(s.Shape);
                }
                _snowmen.RemoveAt(i);
            }
            for (int i = _icicles.Count - 1; i >= 0; i--)
            {
                Icicle ic = _icicles[i];
                if (ic.Shape != null && _roofCanvas != null) _roofCanvas.Children.Remove(ic.Shape);
                _icicles.RemoveAt(i);
            }
            if (_roofCanvas != null)
            {
                for (int i = _icicleDrips.Count - 1; i >= 0; i--)
                {
                    _roofCanvas.Children.Remove(_icicleDrips[i].Shape);
                }
            }
            _icicleDrips.Clear();
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

        static Mutex _singleMutex;

        [STAThread]
        public static void Main()
        {
            _logPath = FindLogPath();
            bool createdNew = false;
            try
            {
                _singleMutex = new Mutex(true, "DesktopSnow.SingleInstance", out createdNew);
            }
            catch (Exception) { createdNew = true; }
            if (!createdNew)
            {
                Log("second instance blocked");
                SWF.MessageBox.Show(
                    "DesktopSnow \u0443\u0436\u0435 \u0437\u0430\u043f\u0443\u0449\u0435\u043d (\u0437\u043d\u0430\u0447\u043e\u043a \u0440\u044f\u0434\u043e\u043c \u0441 \u0447\u0430\u0441\u0430\u043c\u0438). \u0417\u0430\u043a\u0440\u043e\u0439 \u0441\u0442\u0430\u0440\u044b\u0439 \u0447\u0435\u0440\u0435\u0437 \u0442\u0440\u0435\u0439 \u2192 \u0412\u044b\u0445\u043e\u0434, \u0435\u0441\u043b\u0438 \u043d\u0443\u0436\u043d\u043e \u043f\u0435\u0440\u0435\u0437\u0430\u043f\u0443\u0441\u0442\u0438\u0442\u044c.",
                    "DesktopSnow", SWF.MessageBoxButtons.OK, SWF.MessageBoxIcon.Information);
                return;
            }
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
