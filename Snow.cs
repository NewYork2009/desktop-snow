// DesktopSnow — снег на рабочем столе Windows.
// Прозрачное окно в слое обоев (за иконками), клики мышью проходят сквозь.
// Сборка: build.bat (использует встроенный в Windows компилятор csc, ничего ставить не нужно).

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
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        const int GWL_EXSTYLE = -20;
        const int WS_EX_TRANSPARENT = 0x20;
        const int WS_EX_TOOLWINDOW = 0x80;
        const int WS_EX_NOACTIVATE = 0x08000000;
        const uint SWP_NOSIZE = 0x0001;
        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOACTIVATE = 0x0010;
        const uint SWP_SHOWWINDOW = 0x0040;
        const uint SWP_FRAMECHANGED = 0x0020;

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

        Canvas _canvas;
        DispatcherTimer _timer;
        Stopwatch _clock;
        readonly List<Flake> _flakes = new List<Flake>();
        double _t;
        double _wind;
        SWF.NotifyIcon _tray;
        SWF.MenuItem _miAutostart;
        SWF.MenuItem _miTopmost;
        readonly Random _rng = new Random();
        int _targetCount = 180;
        bool _topmostMode;
        IntPtr _hwnd = IntPtr.Zero;
        IntPtr _parentBefore = IntPtr.Zero;

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
                Log("hwnd ok: " + _hwnd);
                ApplyExtendedStyles();
                SendBehindDesktopIcons();
                SetupTray();
                EnsureAutostart();
                StartAnimation();
                Log("started, flakes target: " + _targetCount + ", topmost mode: " + _topmostMode);
                _tray.ShowBalloonTip(4000, "DesktopSnow",
                    "Снег запущен. Управление: значок снежинки возле часов.", SWF.ToolTipIcon.Info);
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

        // Готовим окно слоем обоев: Progman -> WorkerW, тогда снег оказывается ЗА иконками.
        void SendBehindDesktopIcons()
        {
            try
            {
                IntPtr progman = FindWindow("Progman", null);
                Log("progman: " + progman);
                if (progman == IntPtr.Zero) { EnableTopmostFallback("Progman not found"); return; }
                SendMessage(progman, 0x052C, IntPtr.Zero, IntPtr.Zero);
                System.Threading.Thread.Sleep(200);
                SendMessage(progman, 0x052C, IntPtr.Zero, IntPtr.Zero);

                IntPtr workerw = IntPtr.Zero;
                for (int attempt = 0; attempt < 2 && workerw == IntPtr.Zero; attempt++)
                {
                    EnumWindows(delegate(IntPtr top, IntPtr lp)
                    {
                        IntPtr defview = FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
                        if (defview != IntPtr.Zero)
                        {
                            workerw = FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
                        }
                        return true;
                    }, IntPtr.Zero);
                    if (workerw == IntPtr.Zero)
                    {
                        Log("workerw empty, retry " + attempt);
                        System.Threading.Thread.Sleep(300);
                    }
                }
                Log("workerw: " + workerw);

                if (workerw == IntPtr.Zero)
                {
                    EnableTopmostFallback("WorkerW not found");
                    return;
                }

                SetParent(_hwnd, workerw);
                SetWindowPos(_hwnd, IntPtr.Zero,
                    (int)SystemParameters.VirtualScreenLeft, (int)SystemParameters.VirtualScreenTop,
                    (int)SystemParameters.VirtualScreenWidth, (int)SystemParameters.VirtualScreenHeight,
                    SWP_NOACTIVATE | SWP_SHOWWINDOW);
                Log("parented to wallpaper layer");
            }
            catch (Exception ex)
            {
                Log("wallpaper mode error: " + ex.Message);
                EnableTopmostFallback("exception");
            }
        }

        void EnableTopmostFallback(string reason)
        {
            Log("fallback to topmost: " + reason);
            _topmostMode = true;
            Topmost = true;
            SyncTrayCheckState();
        }

        void OnDisplayChanged(object sender, EventArgs e)
        {
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;
            if (!_topmostMode) SendBehindDesktopIcons();
        }

        void SetupTray()
        {
            _miAutostart = new SWF.MenuItem("Включён вместе с ПК (автозапуск)", OnClickAutostart);
            _miTopmost = new SWF.MenuItem("Снег поверх всех окон (иначе за иконками)", OnClickTopmost);

            SWF.ContextMenu menu = new SWF.ContextMenu();
            menu.MenuItems.Add(_miTopmost);
            menu.MenuItems.Add(new SWF.MenuItem("Больше снега", delegate { _targetCount += 80; TrimFlakes(); }));
            menu.MenuItems.Add(new SWF.MenuItem("Меньше снега", delegate { _targetCount = Math.Max(20, _targetCount - 80); TrimFlakes(); }));
            menu.MenuItems.Add(new SWF.MenuItem("-"));
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
            _miTopmost.Checked = _topmostMode;
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

        void OnClickTopmost(object sender, EventArgs e)
        {
            _topmostMode = !_topmostMode;
            if (_topmostMode)
            {
                if (_parentBefore == IntPtr.Zero) _parentBefore = GetParentSafe(_hwnd);
                SetParent(_hwnd, IntPtr.Zero);
                Topmost = true;
            }
            else
            {
                Topmost = false;
                SendBehindDesktopIcons();
            }
            SyncTrayCheckState();
        }

        [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hWnd, uint flags);

        IntPtr GetParentSafe(IntPtr hwnd)
        {
            try { return GetAncestor(hwnd, 1); }
            catch { return IntPtr.Zero; }
        }

        void EnsureAutostart()
        {
            if (!IsAutostartEnabled())
            {
                AddAutostart();
            }
        }

        static string ExePath()
        {
            return Assembly.GetExecutingAssembly().Location;
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

        void Quit()
        {
            _tray.Visible = false;
            Application.Current.Shutdown();
        }

        void StartAnimation()
        {
            AddFlakes(_targetCount);
            _clock = Stopwatch.StartNew();
            _timer = new DispatcherTimer(DispatcherPriority.Render);
            _timer.Interval = TimeSpan.FromMilliseconds(16);
            _timer.Tick += Tick;
            _timer.Start();
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
            double height = SystemParameters.VirtualScreenHeight;

            for (int i = _flakes.Count - 1; i >= 0; i--)
            {
                Flake f = _flakes[i];
                f.Phase += dt * f.SwaySpeed;
                f.Y += f.Speed * dt;
                f.X = f.BaseX + Math.Sin(f.Phase) * f.SwayAmp + _wind;

                if (f.X < -20) f.X += width + 40;
                if (f.X > width + 20) f.X -= width + 40;

                if (f.Y > height + 10)
                {
                    f.Y = -10;
                    f.BaseX = _rng.NextDouble() * (width + 80);
                }
            }

            for (int i = _flakes.Count - 1; i >= 0; i--)
            {
                Flake f = _flakes[i];
                Canvas.SetLeft(f.Shape, f.X - f.R);
                Canvas.SetTop(f.Shape, f.Y - f.R);
            }
        }

        double W() { return SystemParameters.VirtualScreenWidth; }
        double H() { return SystemParameters.VirtualScreenHeight; }
    }

    public static class Program
    {
        [STAThread]
        public static void Main()
        {
            _logPath = FindLogPath();
            try
            {
                Log("— program start —");
                SWF.Application.EnableVisualStyles();
                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                SnowWindow window = new SnowWindow();
                app.Run(window);
                Log("— normal exit —");
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
