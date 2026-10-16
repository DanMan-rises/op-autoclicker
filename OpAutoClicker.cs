// OP Auto Clicker – humanizovaný autoclicker: klikání v náhodných dávkách s pauzami a přirozený pohyb kamery.
// Build: build.bat (používá csc z .NET Frameworku, který je součástí Windows).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

static class Native
{
    [StructLayout(LayoutKind.Sequential)]
    struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public MOUSEINPUT mi; }

    [StructLayout(LayoutKind.Sequential)]
    struct CURSORINFO { public int cbSize, flags; public IntPtr hCursor; public int x, y; }

    [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
    [DllImport("winmm.dll")] public static extern uint timeBeginPeriod(uint ms);
    [DllImport("winmm.dll")] public static extern uint timeEndPeriod(uint ms);

    public const uint MOVE = 0x1, LEFTDOWN = 0x2, LEFTUP = 0x4, RIGHTDOWN = 0x8, RIGHTUP = 0x10;

    // Relativní pohyb přes SendInput – Minecraft čte raw input, SetCursorPos by kameru nepohnul.
    public static void Mouse(uint flags, int dx, int dy)
    {
        var input = new INPUT[1];
        input[0].type = 0; // INPUT_MOUSE
        input[0].mi.dx = dx;
        input[0].mi.dy = dy;
        input[0].mi.dwFlags = flags;
        SendInput(1, input, Marshal.SizeOf(typeof(INPUT)));
    }

    public static string ForegroundTitle()
    {
        var sb = new StringBuilder(256);
        GetWindowText(GetForegroundWindow(), sb, sb.Capacity);
        return sb.ToString();
    }

    // Ve hře je kurzor schovaný; viditelný = otevřené menu/inventář.
    public static bool CursorVisible()
    {
        var ci = new CURSORINFO();
        ci.cbSize = Marshal.SizeOf(typeof(CURSORINFO));
        return GetCursorInfo(ref ci) && (ci.flags & 1) != 0;
    }

    // Funguje globálně (i když má fokus hra) a pro klávesy i tlačítka myši.
    public static bool KeyDown(int vk) { return (GetAsyncKeyState(vk) & 0x8000) != 0; }
}

struct Range
{
    public readonly double Min, Max;
    public Range(double a, double b) { Min = Math.Min(a, b); Max = Math.Max(a, b); }
}

// Snímek nastavení předávaný vláknu clickeru; po předání se už nemění.
sealed class Settings
{
    public Range BurstClicks, Cps, Pause, CameraPause;
    public bool Right, HoldButton, Camera, HoldTrigger;
    public int CameraStrength, TriggerVk, LimitClicks;
    public double LimitMinutes;
    public string WindowFilter;
}

sealed class Clicker
{
    public volatile Settings Settings;
    public volatile bool Running;
    public Action LimitReached; // volá se z vlákna clickeru
    volatile bool exit;
    readonly Random rng = new Random();
    Thread thread;

    public Clicker(Settings s) { Settings = s; }

    public void Start() { thread = new Thread(Loop) { IsBackground = true, Priority = ThreadPriority.AboveNormal }; thread.Start(); }
    public void Stop() { exit = true; if (thread != null) thread.Join(500); }

    double Gauss(double mean, double sd)
    {
        double u1 = 1.0 - rng.NextDouble(), u2 = rng.NextDouble();
        return mean + sd * Math.Sqrt(-2 * Math.Log(u1)) * Math.Cos(2 * Math.PI * u2);
    }

    double Uniform(Range r) { return r.Min + rng.NextDouble() * (r.Max - r.Min); }

    // Kratší pauzy jsou častější než dlouhé (log-uniformní rozdělení), v sekundách.
    double PauseSeconds(Range r)
    {
        if (r.Max <= 0) return 0;
        double lo = Math.Max(r.Min, Math.Min(0.05, r.Max));
        return Math.Exp(Math.Log(lo) + rng.NextDouble() * (Math.Log(r.Max) - Math.Log(lo)));
    }

    static double Clamp(double v, double lo, double hi) { return v < lo ? lo : v > hi ? hi : v; }

    static bool Active(Settings s, bool running)
    {
        if (!running) return false;
        if (s.HoldTrigger && !Native.KeyDown(s.TriggerVk)) return false;
        return s.WindowFilter.Length == 0 ||
               Native.ForegroundTitle().IndexOf(s.WindowFilter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    void Loop()
    {
        Native.timeBeginPeriod(1);
        var sw = Stopwatch.StartNew();
        bool down = false, downRight = false, wasRunning = false;
        double nextDown = 0, upAt = 0, burstCps = 1, runStart = 0;
        int clicksLeft = 0, clicks = 0;

        // Kamera: bod jede pružinou k cíli; cíle přicházejí v sériích pohybů, mezi sériemi je klid.
        double posX = 0, posY = 0, velX = 0, velY = 0, tgtX = 0, tgtY = 0;
        double sentX = 0, sentY = 0, camNext = 0, lastCam = 0, K = 25, D = 8;
        int movesLeft = 0;

        try
        {
            while (!exit)
            {
                Settings s = Settings;
                double now = sw.Elapsed.TotalMilliseconds;
                bool running = Running;

                // Limity se počítají od posledního spuštění; klik se před zastavením dokončí.
                if (running && !wasRunning) { runStart = now; clicks = 0; }
                wasRunning = running;
                if (running && ((s.LimitMinutes > 0 && now - runStart >= s.LimitMinutes * 60000) ||
                                (s.LimitClicks > 0 && !s.HoldButton && !down && clicks >= s.LimitClicks)))
                {
                    Running = running = false;
                    var h = LimitReached;
                    if (h != null) h();
                }

                if (!Active(s, running))
                {
                    if (down) { Native.Mouse(downRight ? Native.RIGHTUP : Native.LEFTUP, 0, 0); down = false; }
                    nextDown = now + rng.Next(40, 120);
                    clicksLeft = 0;
                    lastCam = now;
                    Thread.Sleep(5);
                    continue;
                }

                if (s.HoldButton)
                {
                    // Držení: tlačítko zůstává dole, dokud je klikání aktivní.
                    if (down && downRight != s.Right) { Native.Mouse(downRight ? Native.RIGHTUP : Native.LEFTUP, 0, 0); down = false; }
                    if (!down)
                    {
                        downRight = s.Right;
                        Native.Mouse(downRight ? Native.RIGHTDOWN : Native.LEFTDOWN, 0, 0);
                        down = true;
                        upAt = 0; // po přepnutí zpět na klikání se hned pustí
                    }
                    clicksLeft = 0;
                }
                else
                {
                    if (down && now >= upAt)
                    {
                        Native.Mouse(downRight ? Native.RIGHTUP : Native.LEFTUP, 0, 0);
                        down = false;
                    }

                    if (!down && now >= nextDown)
                    {
                        if (clicksLeft <= 0)
                        {
                            // Nová dávka: vlastní počet kliků i vlastní tempo.
                            clicksLeft = rng.Next((int)s.BurstClicks.Min, (int)s.BurstClicks.Max + 1);
                            burstCps = Uniform(s.Cps);
                        }

                        downRight = s.Right;
                        Native.Mouse(downRight ? Native.RIGHTDOWN : Native.LEFTDOWN, 0, 0);
                        down = true;
                        clicksLeft--;
                        clicks++;

                        // Tempo v dávce pomalu bloudí, jednotlivé intervaly kolem něj kolísají.
                        burstCps = Clamp(burstCps + Gauss(0, (s.Cps.Max - s.Cps.Min) * 0.1 + 0.05), s.Cps.Min, s.Cps.Max);
                        double gap = Clamp(Gauss(1000 / burstCps, 1000 / burstCps * 0.18),
                                           1000 / (s.Cps.Max * 1.25), 1000 / (s.Cps.Min * 0.75));
                        if (rng.NextDouble() < 0.03) gap += rng.Next(80, 300); // občasné zaváhání uvnitř dávky

                        // Délka stisku nezávisí na tempu (člověk drží tlačítko ~50–120 ms), jen se musí vejít.
                        upAt = now + Clamp(Gauss(75, 22), 15, Math.Max(15, Math.Min(160, gap * 0.6)));
                        nextDown = now + (clicksLeft > 0 ? gap : Math.Max(gap, PauseSeconds(s.Pause) * 1000));
                    }
                }

                if (s.Camera && now - lastCam >= 8)
                {
                    double dt = Math.Min((now - lastCam) / 1000, 0.05);
                    lastCam = now;
                    double radius = s.CameraStrength * 6;

                    if (now >= camNext)
                    {
                        if (movesLeft <= 0) movesLeft = rng.Next(1, 5);
                        if (rng.NextDouble() < 0.35)
                        {
                            // drobná korekce kolem aktuálního místa
                            tgtX = posX + Gauss(0, radius * 0.12);
                            tgtY = posY + Gauss(0, radius * 0.06);
                        }
                        else
                        {
                            // rozhlédnutí – vždy kolem výchozího směru, aby pohled neutekl
                            tgtX = Gauss(0, radius * 0.5);
                            tgtY = Gauss(0, radius * 0.22);
                        }
                        tgtX = Clamp(tgtX, -radius * 1.5, radius * 1.5);
                        tgtY = Clamp(tgtY, -radius * 0.7, radius * 0.7);

                        // Každý pohyb má jinou rychlost a jiné tlumení (někdy přejede a vrátí se).
                        K = 8 + rng.NextDouble() * 40;
                        D = 2 * Math.Sqrt(K) * (0.65 + rng.NextDouble() * 0.35);
                        movesLeft--;
                        camNext = now + (movesLeft > 0 ? rng.Next(150, 700) : 300 + PauseSeconds(s.CameraPause) * 1000);
                    }

                    velX += ((tgtX - posX) * K - velX * D) * dt;
                    velY += ((tgtY - posY) * K - velY * D) * dt;
                    posX += velX * dt;
                    posY += velY * dt;

                    // Posílá se jen celočíselný rozdíl; zbytek se přenese do dalšího kroku.
                    // V menu se pohyb zahodí, aby po zavření inventáře kamera neskočila.
                    int dx = (int)Math.Round(posX - sentX), dy = (int)Math.Round(posY - sentY);
                    if (dx != 0 || dy != 0)
                    {
                        if (!Native.CursorVisible()) Native.Mouse(Native.MOVE, dx, dy);
                        sentX += dx;
                        sentY += dy;
                    }
                }

                Thread.Sleep(1);
            }
        }
        finally
        {
            if (down) Native.Mouse(downRight ? Native.RIGHTUP : Native.LEFTUP, 0, 0);
            Native.timeEndPeriod(1);
        }
    }
}

sealed class MainForm : Form
{
    const int HotkeyId = 1;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_NOREPEAT = 0x4000;
    static readonly string IniPath = Path.ChangeExtension(Application.ExecutablePath, ".ini");

    readonly NumericUpDown burstMin = Num(1, 50, 0, 1, 2), burstMax = Num(1, 50, 0, 1, 6);
    readonly NumericUpDown cpsMin = Num(0.1m, 30, 1, 0.1m, 3), cpsMax = Num(0.1m, 30, 1, 0.1m, 10);
    readonly NumericUpDown pauseMin = Num(0, 120, 1, 0.5m, 1), pauseMax = Num(0, 120, 1, 0.5m, 6);
    readonly NumericUpDown camPauseMin = Num(0, 60, 1, 0.5m, 0.5m), camPauseMax = Num(0, 60, 1, 0.5m, 4);
    readonly NumericUpDown limitMinutes = Num(0, 1440, 1, 1, 0), limitClicks = Num(0, 1000000, 0, 10, 0);
    readonly ComboBox button = Combo("Levé", "Pravé");
    readonly ComboBox action = Combo("Klikat", "Držet tlačítko");
    readonly ComboBox mode = Combo("Přepínat klávesou", "Jen při držení klávesy");
    readonly CheckBox camera = new CheckBox { Text = "Pohyb kamery", AutoSize = true };
    readonly TrackBar strength = new TrackBar { Minimum = 1, Maximum = 20, Value = 5, Width = 160, TickStyle = TickStyle.None };
    readonly TextBox window = new TextBox { Text = "Minecraft", Width = 160 };
    readonly Button hotkeyButton = new Button { Width = 160 }, triggerButton = new Button { Width = 160 };
    readonly Button toggle = new Button { Width = 160, Height = 32 };
    readonly Label status = new Label { AutoSize = true };
    readonly NotifyIcon tray = new NotifyIcon();
    readonly Icon iconOn = Dot(Color.LimeGreen), iconOff = Dot(Color.Gray);
    readonly Clicker clicker;

    Keys hotkey = Keys.F6, trigger = Keys.XButton1;
    Button capturing; // tlačítko, které právě čeká na stisk nové klávesy
    bool limitHit;

    static NumericUpDown Num(decimal min, decimal max, int decimals, decimal step, decimal value)
    {
        return new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = step, Value = value, Width = 60 };
    }

    static ComboBox Combo(params string[] items)
    {
        var c = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 160 };
        c.Items.AddRange(items);
        c.SelectedIndex = 0;
        return c;
    }

    // Barevná tečka pro ikonu v liště. Ikon je pár na celý běh programu, handle se neuvolňuje.
    static Icon Dot(Color color)
    {
        using (var bmp = new Bitmap(16, 16))
        {
            using (var g = Graphics.FromImage(bmp))
            using (var brush = new SolidBrush(color))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.FillEllipse(brush, 1, 1, 14, 14);
            }
            return Icon.FromHandle(bmp.GetHicon());
        }
    }

    public MainForm()
    {
        Text = "OP Auto Clicker";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        TopMost = true;
        limitClicks.Width = 70;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(10) };
        AddRow(grid, "Kliků v dávce", Pair(burstMin, burstMax));
        AddRow(grid, "CPS v dávce", Pair(cpsMin, cpsMax));
        AddRow(grid, "Pauza mezi dávkami (s)", Pair(pauseMin, pauseMax));
        AddRow(grid, "Tlačítko", button);
        AddRow(grid, "Akce", action);
        AddRow(grid, "", camera);
        AddRow(grid, "Síla kamery", strength);
        AddRow(grid, "Pauza kamery (s)", Pair(camPauseMin, camPauseMax));
        AddRow(grid, "Jen v okně", window);
        AddRow(grid, "Spouštění", mode);
        AddRow(grid, "Klávesa Start/Stop", hotkeyButton);
        AddRow(grid, "Klávesa pro držení", triggerButton);
        AddRow(grid, "Zastavit po", Flow(limitMinutes, Caption("min"), limitClicks, Caption("kliků")));
        AddRow(grid, "", toggle);
        AddRow(grid, "Stav", status);
        Controls.Add(grid);

        var tip = new ToolTip();
        tip.SetToolTip(pauseMin, "Pauza 0–0 = klikání bez přestávek. Jeden klik za ~5 s: dávka 1–1, pauza 4–6.");
        tip.SetToolTip(window, "Kliká jen když je v popředí okno, jehož název obsahuje tento text. Prázdné = všude.");
        tip.SetToolTip(strength, "Rozsah pohybu kamery (cca 6–120 px).");
        tip.SetToolTip(action, "Držet tlačítko = tlačítko zůstane stisknuté, dokud klikání běží (kopání, AFK farmy).");
        tip.SetToolTip(mode, "Jen při držení: klávesa Start/Stop klikání odjistí, kliká se jen při držení klávesy pro držení.");
        tip.SetToolTip(hotkeyButton, "Klikni a stiskni novou klávesu (i s Ctrl/Shift/Alt). Esc = zrušit.");
        tip.SetToolTip(triggerButton, "Klikni a stiskni klávesu nebo kolečko / boční tlačítko myši. Esc = zrušit.");
        tip.SetToolTip(limitMinutes, "Automatické zastavení po zadaném čase od spuštění. 0 = bez limitu.");
        tip.SetToolTip(limitClicks, "Automatické zastavení po zadaném počtu kliků. 0 = bez limitu.");

        LoadIni();

        clicker = new Clicker(ReadSettings());
        clicker.LimitReached = delegate
        {
            try { BeginInvoke(new Action(delegate { limitHit = true; UpdateUi(); })); }
            catch (InvalidOperationException) { } // okno se právě zavírá
        };

        EventHandler changed = delegate { clicker.Settings = ReadSettings(); UpdateUi(); };
        foreach (var n in new[] { burstMin, burstMax, cpsMin, cpsMax, pauseMin, pauseMax, camPauseMin, camPauseMax, limitMinutes, limitClicks })
            n.ValueChanged += changed;
        foreach (var c in new[] { button, action, mode })
            c.SelectedIndexChanged += changed;
        camera.CheckedChanged += changed;
        strength.ValueChanged += changed;
        window.TextChanged += changed;
        toggle.Click += delegate { Toggle(); };
        hotkeyButton.Click += delegate { StartCapture(hotkeyButton); };
        triggerButton.Click += delegate { StartCapture(triggerButton); };
        triggerButton.MouseDown += (sender, e) =>
        {
            Keys k = MouseKey(e.Button);
            if (capturing == triggerButton && k != Keys.None) FinishCapture(k);
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Start / Stop", null, delegate { Toggle(); });
        menu.Items.Add("Zobrazit", null, delegate { RestoreFromTray(); });
        menu.Items.Add("Konec", null, delegate { Close(); });
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += delegate { RestoreFromTray(); };
        tray.Visible = true;

        UpdateUi();
        clicker.Start();
    }

    static Control Flow(params Control[] items)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        p.Controls.AddRange(items);
        return p;
    }

    static Control Pair(NumericUpDown a, NumericUpDown b) { return Flow(a, Caption("–"), b); }

    static Label Caption(string text) { return new Label { Text = text, AutoSize = true, Margin = new Padding(3, 6, 3, 3) }; }

    static void AddRow(TableLayoutPanel grid, string label, Control c)
    {
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 10, 3) });
        grid.Controls.Add(c);
    }

    static Range R(NumericUpDown a, NumericUpDown b) { return new Range((double)a.Value, (double)b.Value); }

    Settings ReadSettings()
    {
        return new Settings
        {
            BurstClicks = R(burstMin, burstMax),
            Cps = R(cpsMin, cpsMax),
            Pause = R(pauseMin, pauseMax),
            Right = button.SelectedIndex == 1,
            HoldButton = action.SelectedIndex == 1,
            Camera = camera.Checked,
            CameraStrength = strength.Value,
            CameraPause = R(camPauseMin, camPauseMax),
            WindowFilter = window.Text.Trim(),
            HoldTrigger = mode.SelectedIndex == 1,
            TriggerVk = (int)trigger,
            LimitMinutes = (double)limitMinutes.Value,
            LimitClicks = (int)limitClicks.Value,
        };
    }

    void Toggle()
    {
        clicker.Running = !clicker.Running;
        limitHit = false;
        UpdateUi();
    }

    void UpdateUi()
    {
        bool hold = mode.SelectedIndex == 1, holdButton = action.SelectedIndex == 1, on = clicker.Running;

        if (capturing != hotkeyButton) hotkeyButton.Text = KeyName(hotkey);
        if (capturing != triggerButton) triggerButton.Text = KeyName(trigger);
        triggerButton.Enabled = hold;
        foreach (var n in new[] { burstMin, burstMax, cpsMin, cpsMax, pauseMin, pauseMax, limitClicks })
            n.Enabled = !holdButton; // při držení tlačítka se nekliká

        toggle.Text = (on ? "Stop (" : "Start (") + KeyName(hotkey) + ")";
        status.Text = on ? (hold ? "Připraveno – drž " + KeyName(trigger) : "Běží")
                         : (limitHit ? "Zastaveno (limit)" : "Zastaveno");
        status.ForeColor = on ? Color.DarkGreen : Color.DarkRed;

        string tip = Text + " – " + status.Text;
        tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip; // NotifyIcon víc nedovolí
        tray.Icon = on ? iconOn : iconOff;
    }

    // ---- Volba kláves ----

    static string KeyName(Keys k)
    {
        switch (k)
        {
            case Keys.MButton: return "Kolečko myši";
            case Keys.XButton1: return "Myš 4 (boční)";
            case Keys.XButton2: return "Myš 5 (boční)";
        }
        return new KeysConverter().ConvertToString(k);
    }

    // Levé a pravé tlačítko nejdou – ty klikání samo posílá.
    static Keys MouseKey(MouseButtons b)
    {
        switch (b)
        {
            case MouseButtons.Middle: return Keys.MButton;
            case MouseButtons.XButton1: return Keys.XButton1;
            case MouseButtons.XButton2: return Keys.XButton2;
        }
        return Keys.None;
    }

    void StartCapture(Button b)
    {
        if (capturing != null) { FinishCapture(Keys.None); return; }
        capturing = b;
        b.Text = b == triggerButton ? "Stiskni klávesu / myš…" : "Stiskni klávesu…";
        if (b == hotkeyButton) Native.UnregisterHotKey(Handle, HotkeyId); // aby stisk nepřepnul klikání
    }

    // Keys.None = zrušeno, zůstává původní klávesa.
    void FinishCapture(Keys key)
    {
        Button b = capturing;
        capturing = null;
        if (b == hotkeyButton)
        {
            if (key != Keys.None) hotkey = key;
            RegisterHotkey();
        }
        else if (key != Keys.None)
        {
            trigger = key;
            clicker.Settings = ReadSettings();
        }
        UpdateUi();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (capturing == null) return base.ProcessCmdKey(ref msg, keyData);

        Keys code = keyData & Keys.KeyCode;
        if (code == Keys.Escape) FinishCapture(Keys.None);
        else if (capturing == triggerButton) FinishCapture(code); // drží se jen jedna klávesa, i samotný Shift apod.
        else if (code != Keys.ControlKey && code != Keys.ShiftKey && code != Keys.Menu) FinishCapture(keyData); // u zkratky se čeká na hlavní klávesu
        return true;
    }

    protected override void OnDeactivate(EventArgs e)
    {
        if (capturing != null) FinishCapture(Keys.None);
        base.OnDeactivate(e);
    }

    void RegisterHotkey()
    {
        Native.UnregisterHotKey(Handle, HotkeyId);
        uint mods = MOD_NOREPEAT; // držená klávesa nepřepíná dokola
        if ((hotkey & Keys.Control) != 0) mods |= MOD_CONTROL;
        if ((hotkey & Keys.Shift) != 0) mods |= MOD_SHIFT;
        if ((hotkey & Keys.Alt) != 0) mods |= MOD_ALT;
        if (!Native.RegisterHotKey(Handle, HotkeyId, mods, (uint)(hotkey & Keys.KeyCode)))
            MessageBox.Show(KeyName(hotkey) + " nejde zaregistrovat (používá ji jiný program). Vyber jinou klávesu nebo ovládej tlačítkem.", Text);
    }

    // ---- Uložení nastavení (OpAutoClicker.ini vedle exe) ----

    Dictionary<string, NumericUpDown> Numbers()
    {
        return new Dictionary<string, NumericUpDown>
        {
            { "BurstMin", burstMin }, { "BurstMax", burstMax }, { "CpsMin", cpsMin }, { "CpsMax", cpsMax },
            { "PauseMin", pauseMin }, { "PauseMax", pauseMax }, { "CameraPauseMin", camPauseMin }, { "CameraPauseMax", camPauseMax },
            { "LimitMinutes", limitMinutes }, { "LimitClicks", limitClicks },
        };
    }

    void LoadIni()
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var line in File.ReadAllLines(IniPath))
            {
                int i = line.IndexOf('=');
                if (i > 0) d[line.Substring(0, i).Trim()] = line.Substring(i + 1);
            }
        }
        catch (IOException) { return; } // první spuštění – soubor ještě není
        catch (UnauthorizedAccessException) { return; }

        string s;
        int v;
        foreach (var p in Numbers())
        {
            decimal n;
            if (d.TryGetValue(p.Key, out s) && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out n))
                p.Value.Value = Math.Max(p.Value.Minimum, Math.Min(p.Value.Maximum, n));
        }
        foreach (var c in new[] { new KeyValuePair<string, ComboBox>("Button", button),
                                  new KeyValuePair<string, ComboBox>("Action", action),
                                  new KeyValuePair<string, ComboBox>("Mode", mode) })
            if (d.TryGetValue(c.Key, out s) && int.TryParse(s, out v) && v >= 0 && v < c.Value.Items.Count)
                c.Value.SelectedIndex = v;
        if (d.TryGetValue("Camera", out s)) camera.Checked = s.Trim() == "1";
        if (d.TryGetValue("Strength", out s) && int.TryParse(s, out v))
            strength.Value = Math.Max(strength.Minimum, Math.Min(strength.Maximum, v));
        if (d.TryGetValue("Window", out s)) window.Text = s;
        if (d.TryGetValue("Hotkey", out s) && int.TryParse(s, out v) && ((Keys)v & Keys.KeyCode) != Keys.None) hotkey = (Keys)v;
        if (d.TryGetValue("Trigger", out s) && int.TryParse(s, out v) && ((Keys)v & Keys.KeyCode) != Keys.None) trigger = (Keys)v & Keys.KeyCode;
    }

    void SaveIni()
    {
        var lines = new List<string>();
        foreach (var p in Numbers())
            lines.Add(p.Key + "=" + p.Value.Value.ToString(CultureInfo.InvariantCulture));
        lines.Add("Button=" + button.SelectedIndex);
        lines.Add("Action=" + action.SelectedIndex);
        lines.Add("Mode=" + mode.SelectedIndex);
        lines.Add("Camera=" + (camera.Checked ? 1 : 0));
        lines.Add("Strength=" + strength.Value);
        lines.Add("Window=" + window.Text);
        lines.Add("Hotkey=" + (int)hotkey);
        lines.Add("Trigger=" + (int)trigger);
        try { File.WriteAllLines(IniPath, lines); }
        catch (IOException) { } // nastavení se jen neuloží, program kvůli tomu nespadne
        catch (UnauthorizedAccessException) { }
    }

    // ---- Systémová lišta ----

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (WindowState == FormWindowState.Minimized) Hide(); // minimalizace = schovat do lišty
    }

    void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterHotkey();
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyId) Toggle(); // WM_HOTKEY
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        SaveIni();
        tray.Visible = false; // jinak v liště zůstane „mrtvá“ ikona
        tray.Dispose();
        Native.UnregisterHotKey(Handle, HotkeyId);
        clicker.Stop();
        base.OnFormClosed(e);
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.Run(new MainForm());
    }
}
