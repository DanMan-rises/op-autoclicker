// OP Auto Clicker – humanizovaný autoclicker: klikání v náhodných dávkách s pauzami a přirozený pohyb kamery.
// Build: build.bat (používá csc z .NET Frameworku, který je součástí Windows).
using System;
using System.Diagnostics;
using System.Drawing;
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
}

struct Range
{
    public readonly double Min, Max;
    public Range(double a, double b) { Min = Math.Min(a, b); Max = Math.Max(a, b); }
}

sealed class Settings
{
    public readonly Range BurstClicks, Cps, Pause, CameraPause;
    public readonly bool Right, Camera;
    public readonly int CameraStrength;
    public readonly string WindowFilter;

    public Settings(Range burstClicks, Range cps, Range pause, bool right,
                    bool camera, int cameraStrength, Range cameraPause, string windowFilter)
    {
        BurstClicks = burstClicks;
        Cps = cps;
        Pause = pause;
        Right = right;
        Camera = camera;
        CameraStrength = cameraStrength;
        CameraPause = cameraPause;
        WindowFilter = windowFilter.Trim();
    }
}

sealed class Clicker
{
    public volatile Settings Settings;
    public volatile bool Running;
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

    bool Active(Settings s)
    {
        if (!Running) return false;
        return s.WindowFilter.Length == 0 ||
               Native.ForegroundTitle().IndexOf(s.WindowFilter, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    void Loop()
    {
        Native.timeBeginPeriod(1);
        var sw = Stopwatch.StartNew();
        bool down = false, downRight = false;
        double nextDown = 0, upAt = 0, burstCps = 1;
        int clicksLeft = 0;

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

                if (!Active(s))
                {
                    if (down) { Native.Mouse(downRight ? Native.RIGHTUP : Native.LEFTUP, 0, 0); down = false; }
                    nextDown = now + rng.Next(40, 120);
                    clicksLeft = 0;
                    lastCam = now;
                    Thread.Sleep(5);
                    continue;
                }

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

                    // Tempo v dávce pomalu bloudí, jednotlivé intervaly kolem něj kolísají.
                    burstCps = Clamp(burstCps + Gauss(0, (s.Cps.Max - s.Cps.Min) * 0.1 + 0.05), s.Cps.Min, s.Cps.Max);
                    double gap = Clamp(Gauss(1000 / burstCps, 1000 / burstCps * 0.18),
                                       1000 / (s.Cps.Max * 1.25), 1000 / (s.Cps.Min * 0.75));
                    if (rng.NextDouble() < 0.03) gap += rng.Next(80, 300); // občasné zaváhání uvnitř dávky

                    // Délka stisku nezávisí na tempu (člověk drží tlačítko ~50–120 ms), jen se musí vejít.
                    upAt = now + Clamp(Gauss(75, 22), 15, Math.Max(15, Math.Min(160, gap * 0.6)));
                    nextDown = now + (clicksLeft > 0 ? gap : Math.Max(gap, PauseSeconds(s.Pause) * 1000));
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
    const uint VK_F6 = 0x75;

    readonly NumericUpDown burstMin = Num(1, 50, 0, 1, 2), burstMax = Num(1, 50, 0, 1, 6);
    readonly NumericUpDown cpsMin = Num(0.1m, 30, 1, 0.1m, 3), cpsMax = Num(0.1m, 30, 1, 0.1m, 10);
    readonly NumericUpDown pauseMin = Num(0, 120, 1, 0.5m, 1), pauseMax = Num(0, 120, 1, 0.5m, 6);
    readonly NumericUpDown camPauseMin = Num(0, 60, 1, 0.5m, 0.5m), camPauseMax = Num(0, 60, 1, 0.5m, 4);
    readonly ComboBox button = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    readonly CheckBox camera = new CheckBox { Text = "Pohyb kamery", AutoSize = true };
    readonly TrackBar strength = new TrackBar { Minimum = 1, Maximum = 20, Value = 5, Width = 160, TickStyle = TickStyle.None };
    readonly TextBox window = new TextBox { Text = "Minecraft", Width = 160 };
    readonly Button toggle = new Button { Text = "Start (F6)", Width = 160, Height = 32 };
    readonly Label status = new Label { Text = "Zastaveno", AutoSize = true, ForeColor = Color.DarkRed };
    readonly Clicker clicker;

    static NumericUpDown Num(decimal min, decimal max, int decimals, decimal step, decimal value)
    {
        return new NumericUpDown { Minimum = min, Maximum = max, DecimalPlaces = decimals, Increment = step, Value = value, Width = 60 };
    }

    public MainForm()
    {
        Text = "OP Auto Clicker";
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        TopMost = true;

        button.Items.AddRange(new object[] { "Levé", "Pravé" });
        button.SelectedIndex = 0;

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Padding = new Padding(10) };
        AddRow(grid, "Kliků v dávce", Pair(burstMin, burstMax));
        AddRow(grid, "CPS v dávce", Pair(cpsMin, cpsMax));
        AddRow(grid, "Pauza mezi dávkami (s)", Pair(pauseMin, pauseMax));
        AddRow(grid, "Tlačítko", button);
        AddRow(grid, "", camera);
        AddRow(grid, "Síla kamery", strength);
        AddRow(grid, "Pauza kamery (s)", Pair(camPauseMin, camPauseMax));
        AddRow(grid, "Jen v okně", window);
        AddRow(grid, "", toggle);
        AddRow(grid, "Stav", status);
        Controls.Add(grid);

        var tip = new ToolTip();
        tip.SetToolTip(pauseMin, "Pauza 0–0 = klikání bez přestávek. Jeden klik za ~5 s: dávka 1–1, pauza 4–6.");
        tip.SetToolTip(window, "Kliká jen když je v popředí okno, jehož název obsahuje tento text. Prázdné = všude.");
        tip.SetToolTip(strength, "Rozsah pohybu kamery (cca 6–120 px).");

        clicker = new Clicker(ReadSettings());
        EventHandler changed = delegate { clicker.Settings = ReadSettings(); };
        foreach (var n in new[] { burstMin, burstMax, cpsMin, cpsMax, pauseMin, pauseMax, camPauseMin, camPauseMax })
            n.ValueChanged += changed;
        button.SelectedIndexChanged += changed;
        camera.CheckedChanged += changed;
        strength.ValueChanged += changed;
        window.TextChanged += changed;
        toggle.Click += delegate { Toggle(); };
        clicker.Start();
    }

    static Control Pair(NumericUpDown a, NumericUpDown b)
    {
        var p = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(0) };
        p.Controls.Add(a);
        p.Controls.Add(new Label { Text = "–", AutoSize = true, Margin = new Padding(3, 6, 3, 3) });
        p.Controls.Add(b);
        return p;
    }

    static void AddRow(TableLayoutPanel grid, string label, Control c)
    {
        grid.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(3, 8, 10, 3) });
        grid.Controls.Add(c);
    }

    static Range R(NumericUpDown a, NumericUpDown b) { return new Range((double)a.Value, (double)b.Value); }

    Settings ReadSettings()
    {
        return new Settings(R(burstMin, burstMax), R(cpsMin, cpsMax), R(pauseMin, pauseMax), button.SelectedIndex == 1,
                            camera.Checked, strength.Value, R(camPauseMin, camPauseMax), window.Text);
    }

    void Toggle()
    {
        clicker.Running = !clicker.Running;
        toggle.Text = clicker.Running ? "Stop (F6)" : "Start (F6)";
        status.Text = clicker.Running ? "Běží" : "Zastaveno";
        status.ForeColor = clicker.Running ? Color.DarkGreen : Color.DarkRed;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        if (!Native.RegisterHotKey(Handle, HotkeyId, 0, VK_F6))
            MessageBox.Show("F6 nejde zaregistrovat (používá ji jiný program). Ovládej tlačítkem.", Text);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && m.WParam.ToInt32() == HotkeyId) Toggle(); // WM_HOTKEY
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
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
