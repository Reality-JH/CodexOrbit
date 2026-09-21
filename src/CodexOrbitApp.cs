using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

[assembly: System.Reflection.AssemblyTitle("CodexOrbit")]
[assembly: System.Reflection.AssemblyDescription("CodexOrbit tray console · orbit-core engine")]
[assembly: System.Reflection.AssemblyProduct("CodexOrbit")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.0.0")]
[assembly: System.Reflection.AssemblyCopyright("Reality-JH · non-commercial")]

// CodexOrbit — Windows-native tray console for the orbit-core engine (ccodex-rotate core).
// Runs the serve process windowless, shows a status card on click,
// and restores the Codex config on exit.

// Bilingual strings: follows OS UI language, --lang=en|zh or the tray
// 语言/Language submenu overrides; choice persists in CodexOrbit.lang.
static class L10n
{
    public static bool Zh =
        System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
    public static string T(string en, string zh) { return Zh ? zh : en; }

    static readonly string LangFile =
        Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location),
            "CodexOrbit.lang");

    public static void LoadSaved()
    {
        try { if (File.Exists(LangFile)) Zh = File.ReadAllText(LangFile).Trim().StartsWith("zh"); } catch { }
    }

    public static void Set(bool zh)
    {
        Zh = zh;
        try { File.WriteAllText(LangFile, zh ? "zh" : "en"); } catch { }
    }
}

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        if (args != null)
        {
            L10n.LoadSaved();
            int li = Array.IndexOf(args, "--lang");
            if (li >= 0 && li + 1 < args.Length)
                L10n.Zh = args[li + 1].StartsWith("zh", StringComparison.OrdinalIgnoreCase);
            int si = Array.IndexOf(args, "--shot");
            if (si >= 0 && si + 1 < args.Length) { Shot.Save(args[si + 1]); return; }
            int ci = Array.IndexOf(args, "--cshot");
            if (ci >= 0 && ci + 1 < args.Length) { Shot.SaveConsole(args[ci + 1]); return; }
        }
        bool created;
        using (new Mutex(true, "CodexOrbit.Tray", out created))
        {
            if (!created) return;
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool preview = args != null && Array.IndexOf(args, "--preview") >= 0;
            Application.Run(new OrbitApp(preview));
        }
    }
}

class OrbitApp : ApplicationContext
{
    internal const string BaseUrl = "http://127.0.0.1:17850";
    internal static readonly string Dir =
        Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location);
    internal static readonly string Exe = Path.Combine(Dir, "orbit-core.exe");
    internal static readonly string Cfg = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ccodex-rotate", "config.json");

    readonly NotifyIcon tray = new NotifyIcon();
    readonly StatusCard card = new StatusCard();
    readonly System.Threading.Timer poller;
    Process child;
    volatile bool alive;
    volatile int errSpike;
    volatile bool spikeNotified;
    volatile bool downNotified;
    DateTime spikeFixUntil = DateTime.MinValue;
    DateTime lastCollectNudge = DateTime.MinValue;
    string tip = "CodexOrbit";

    public OrbitApp(bool preview)
    {
        OrbitAppHolder.App = this;
        Log.Write("app", "start");
        // Create the card's handle on the UI thread up front — otherwise a
        // background BeginInvoke creates it on the wrong thread and every
        // later Show()/Invoke dies with "窗口句柄已经存在".
        var forceHandle = card.Handle;
        // spawn off the UI thread — WMI orphan cleanup can block
        ThreadPool.QueueUserWorkItem(delegate { EnsureRunning(); });
        if (preview)
            new Thread(new ThreadStart(delegate { Thread.Sleep(1500); card.PreviewCenter(); })) { IsBackground = true }.Start();

        tray.Icon = Icons.Make(IconState.Starting);
        tray.Text = "CodexOrbit";
        tray.Visible = true;
        tray.MouseClick += delegate(object s, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) ShowCard();
            else if (e.Button == MouseButtons.Right) ShowMenu();
        };
        tray.MouseDoubleClick += delegate { ShowConsole(); };
        tray.BalloonTipClicked += delegate
        {
            if (DateTime.Now < spikeFixUntil) { spikeFixUntil = DateTime.MinValue; ApplySpikeFix(); }
            else ShowCard(); // click bubble → card, never browser
        };

        poller = new System.Threading.Timer(delegate { Poll(); }, null, 1200, 4000);
    }

    bool deadWarned;

    void EnsureRunning()
    {
        if (Http.Get(BaseUrl + "/healthz", 1200) != null) { alive = true; deadWarned = false; return; }
        if (!File.Exists(Exe))
        {
            Log.Write("svc", "orbit-core.exe missing");
            SetTip(L10n.T("CodexOrbit: orbit-core.exe missing", "CodexOrbit: 缺少 orbit-core.exe"), IconState.Dead);
            if (!deadWarned)
            {
                deadWarned = true;
                Balloon(L10n.T("orbit-core.exe missing", "缺少 orbit-core.exe"),
                    L10n.T("Re-extract the full release zip - orbit-core.exe must sit next to CodexOrbit.exe", "发布包没解压全 - orbit-core.exe 要和 CodexOrbit.exe 放在一起"));
            }
            return;
        }
        // a dead serve leaves its mihomo child holding 17890/17891 — clear it first
        Proc.KillOrphanKernels();
        try
        {
            var psi = new ProcessStartInfo(Exe, "serve --config \"" + Cfg + "\"");
            psi.WorkingDirectory = Dir;
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            child = Process.Start(psi);
            Log.Write("svc", "spawned orbit-core pid " + child.Id);
        }
        catch (Exception ex)
        {
            Log.Write("svc", "spawn failed: " + ex.Message);
            SetTip(L10n.T("CodexOrbit: start failed: ", "CodexOrbit: 启动失败: ") + ex.Message, IconState.Dead);
            if (!deadWarned)
            {
                deadWarned = true;
                Balloon(L10n.T("orbit-core failed to start", "orbit-core 启动失败"), ex.Message);
            }
        }
    }

    void ShowCard()
    {
        card.RefreshData(lastStatus);
        card.ShowNearTray();
        card.Activate();
    }

    ConsoleForm console;

    internal void ShowConsole()
    {
        if (console != null && !console.IsDisposed)
        {
            console.Show(); console.WindowState = FormWindowState.Normal;
            console.Activate(); console.Reload();
            return;
        }
        console = new ConsoleForm();
        console.Show();
    }

    internal void RestartBg()
    {
        ThreadPool.QueueUserWorkItem(delegate { RestartService(); });
    }

    internal static void PostJson(string url, string body)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST"; req.Timeout = 10000; req.Proxy = null;
            req.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            req.ContentLength = bytes.Length;
            using (var st = req.GetRequestStream()) st.Write(bytes, 0, bytes.Length);
            using (req.GetResponse()) { }
        }
        catch { }
    }

    ContextMenuStrip menu;

    void ShowMenu()
    {
        if (menu != null) menu.Dispose();
        menu = new ContextMenuStrip();
        menu.Renderer = new DarkMenu();
        menu.BackColor = Color.FromArgb(24, 24, 28);
        menu.ForeColor = Color.FromArgb(240, 240, 245);
        menu.Font = new Font("Microsoft YaHei UI", 10f);
        menu.ShowImageMargin = false;

        string topName = lastStatus != null && lastStatus.Node != "" ? StatusCard.StripFlagsText(lastStatus.Node) : L10n.T("running", "运行中");
        bool topAuto = lastStatus != null && lastStatus.Node == "AUTO";
        var st = new ToolStripLabel(alive
            ? "  " + (topAuto ? L10n.T("auto-routing", "自动选路") : topName + L10n.T(" · pinned", " · 已固定"))
            : L10n.T("  offline", "  离线"));
        st.ForeColor = Color.FromArgb(150, 150, 160);
        menu.Items.Add(st);
        AddMenu(L10n.T("Console", "控制台"), delegate { ShowConsole(); });
        menu.Items.Add(new ToolStripSeparator());

        var pin = new ToolStripMenuItem(L10n.T("Pin node", "固定节点"));
        ThreadPool.QueueUserWorkItem(delegate
        {
            var nodes = Http.Get(BaseUrl + "/api/nodes", 2500);
            if (nodes == null) return;
            try
            {
                var j = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(nodes);
                string current = j.ContainsKey("current") ? Convert.ToString(j["current"]) : "";
                var arr = j.ContainsKey("nodes") ? j["nodes"] as System.Collections.ArrayList : null;
                if (arr == null) return;
                if (arr.Count == 0)
                {
                    SafeAdd(pin, new ToolStripMenuItem(L10n.T("(no nodes - add a source first)", "（无节点 · 先添加订阅）")) { Enabled = false });
                    return;
                }
                foreach (var it in arr)
                {
                    var d = it as System.Collections.Generic.Dictionary<string, object>;
                    if (d == null) continue;
                    string name = Convert.ToString(d["name"]);
                    string stt = Convert.ToString(d["state"]);
                    long delay = 0; object dv;
                    if (d.TryGetValue("delay", out dv) && dv != null) long.TryParse(Convert.ToString(dv), out delay);
                    bool cur = name == current;
                    var item = new ToolStripMenuItem((cur ? "✓ " : stt == "ok" ? "● " : "○ ") + name
                        + (delay > 0 ? "  " + delay + "ms" : ""));
                    string n = name;
                    item.Click += delegate { PinNode(n); };
                    SafeAdd(pin, item);
                }
            }
            catch { }
        });
        menu.Items.Add(pin);

        // collected turn-states (never display the state value itself)
        var cred = new ToolStripMenuItem(L10n.T("292 credential pool", "292 凭据池"));
        var snap0 = lastStatus;
        if (snap0 != null && snap0.States.Count > 0)
        {
            foreach (var sd in snap0.States)
            {
                object mv, lv, nv, hv;
                sd.TryGetValue("model", out mv); sd.TryGetValue("length", out lv);
                sd.TryGetValue("node", out nv); sd.TryGetValue("hits", out hv);
                cred.DropDownItems.Add(new ToolStripMenuItem(string.Format("{0} · {1} · ×{2} · {3}",
                    mv, lv, hv, StatusCard.StripFlagsText(Convert.ToString(nv)))) { Enabled = false });
            }
        }
        else cred.DropDownItems.Add(new ToolStripMenuItem(lastStatus != null && lastStatus.Collecting
            ? L10n.T("(collecting - hold on)", "（采集中 · 稍候）")
            : L10n.T("(none - auto-collect is scheduled)", "（暂无 · 到点自动采）")) { Enabled = false });
        menu.Items.Add(cred);

        var add = new ToolStripMenuItem(L10n.T("Add source", "添加来源"));
        var addSub = new ToolStripMenuItem(L10n.T("Subscription URL…", "订阅链接…"));
        addSub.Click += delegate { AddSource("sub"); };
        var addNode = new ToolStripMenuItem(L10n.T("Node share links…", "节点分享链接…"));
        addNode.Click += delegate { AddSource("node"); };
        var clr = new ToolStripMenuItem(L10n.T("Clear all sources…", "清空全部来源…"));
        clr.Click += delegate { ClearSources(); };
        add.DropDownItems.Add(addSub); add.DropDownItems.Add(addNode);
        add.DropDownItems.Add(new ToolStripSeparator()); add.DropDownItems.Add(clr);
        menu.Items.Add(add);

        bool streak = lastStatus != null && lastStatus.FailStreak >= 3;
        bool isAuto = lastStatus != null && lastStatus.Node == "AUTO";
        AddMenu(L10n.T("Switch node", "切换节点") + (streak && isAuto ? L10n.T("  ⚠ failing", "  ⚠ 连续失败") : ""), delegate { Post("/api/rotate"); });
        AddMenu(L10n.T("Collect 292 now", "立即采集 292"), delegate { Post("/api/collect"); Balloon(L10n.T("Collecting 292", "正在采集 292"), L10n.T("runs in background", "后台执行中…")); });
        var autoItem = AddMenu(L10n.T("Auto select", "恢复自动") + (streak && !isAuto ? L10n.T("  ⚠ failing", "  ⚠ 连续失败") : ""), delegate { Post("/api/reset"); mem.ForgetPin(); MarkAuto(); Log.Write("pin", "cleared -> auto"); });
        // already on AUTO — nothing to reset to
        if (isAuto) autoItem.Enabled = false;
        menu.Items.Add(new ToolStripSeparator());
        AddMenu(L10n.T("Restart service", "重启服务"), delegate { Balloon(L10n.T("Restarting", "正在重启"), L10n.T("service restarts in background", "服务后台重启中…")); Log.Write("action", "restart"); ThreadPool.QueueUserWorkItem(delegate { RestartService(); }); });

        var lang = new ToolStripMenuItem("语言 / Language");
        var lz = new ToolStripMenuItem((L10n.Zh ? "✓ " : "") + "中文");
        lz.Click += delegate { SwitchLang(true); };
        var le = new ToolStripMenuItem((L10n.Zh ? "" : "✓ ") + "English");
        le.Click += delegate { SwitchLang(false); };
        lang.DropDownItems.Add(lz); lang.DropDownItems.Add(le);
        menu.Items.Add(lang);

        menu.Items.Add(new ToolStripSeparator());
        AddMenu(L10n.T("Open log", "打开日志"), delegate { try { Process.Start("notepad.exe", Log.LogPath); } catch { } });
        menu.Items.Add(new ToolStripSeparator());
        AddMenu(L10n.T("Exit & restore Codex", "退出并还原 Codex"), delegate
        {
            if (MessageBox.Show(L10n.T("Stop the service and restore your Codex config?", "停止服务并还原 Codex 配置？"),
                "CodexOrbit", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes) Quit();
        });
        menu.Show(Cursor.Position);
    }

    void SafeAdd(ToolStripMenuItem parent, ToolStripItem item)
    {
        // marshal via the card (its handle is pre-created on the UI thread)
        if (card.InvokeRequired) { card.BeginInvoke(new MethodInvoker(delegate { SafeAdd(parent, item); })); return; }
        if (parent.IsDisposed || parent.DropDownItems.Count >= 24) return;
        parent.DropDownItems.Add(item);
    }

    void PinNode(string name)
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(BaseUrl + "/api/pin");
                req.Method = "POST"; req.Timeout = 10000; req.Proxy = null;
                req.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes("{\"name\":\"" + name.Replace("\"", "") + "\"}");
                req.ContentLength = bytes.Length;
                using (var st = req.GetRequestStream()) st.Write(bytes, 0, bytes.Length);
                using (req.GetResponse()) { }
                mem.Pinned = name; mem.Save();
                Log.Write("pin", name);
                Balloon(L10n.T("Pinned", "已固定"), StatusCard.StripFlagsText(name));
            }
            catch { Balloon(L10n.T("Pin failed", "固定失败"), StatusCard.StripFlagsText(name)); }
        });
    }

    // Small dark multiline input; returns text or null on cancel.
    static string AskLines(string title, string hint)
    {
        var f = new Form
        {
            Text = title, Width = 430, Height = 210,
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.CenterScreen, TopMost = true,
            BackColor = Color.FromArgb(23, 23, 28), ForeColor = Color.FromArgb(235, 235, 240)
        };
        var tb = new TextBox
        {
            Multiline = true, Left = 10, Top = 10, Width = 394, Height = 112,
            BackColor = Color.FromArgb(34, 34, 42), ForeColor = Color.White,
            BorderStyle = BorderStyle.FixedSingle, Text = hint
        };
        tb.GotFocus += delegate { if (tb.Text == hint) tb.Text = ""; };
        var ok = new Button { Text = "OK", Left = 230, Top = 130, Width = 80, DialogResult = DialogResult.OK, FlatStyle = FlatStyle.Flat };
        var no = new Button { Text = "Cancel", Left = 324, Top = 130, Width = 80, DialogResult = DialogResult.Cancel, FlatStyle = FlatStyle.Flat };
        ok.ForeColor = no.ForeColor = Color.FromArgb(235, 235, 240);
        ok.BackColor = no.BackColor = Color.FromArgb(34, 34, 42);
        f.Controls.AddRange(new Control[] { tb, ok, no });
        f.AcceptButton = ok; f.CancelButton = no;
        var r = f.ShowDialog();
        var t = tb.Text.Trim();
        return r == DialogResult.OK && t != "" && t != hint ? t : null;
    }

    internal void AddSource(string kind)
    {
        var text = AskLines(
            kind == "sub" ? L10n.T("Add subscription", "添加订阅") : L10n.T("Add nodes", "添加节点"),
            kind == "sub"
                ? L10n.T("Paste subscription URL(s), one per line", "粘贴订阅地址，每行一个\n例如 https://example.com/sub?token=xxx")
                : L10n.T("Paste share link(s), one per line\nvless:// ss:// trojan:// hysteria2:// tuic://", "粘贴节点分享链接，每行一个\n支持 vless:// ss:// trojan:// hysteria2:// tuic://"));
        if (text == null) return;
        var lines = new System.Collections.Generic.List<string>();
        foreach (var ln in text.Replace("\r", "").Split('\n'))
            if (ln.Trim() != "") lines.Add(ln.Trim());
        if (lines.Count == 0) return;
        string body = new JavaScriptSerializer().Serialize(
            new System.Collections.Generic.Dictionary<string, object> { { "kind", kind }, { "lines", lines.ToArray() } });
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                var req = (HttpWebRequest)WebRequest.Create(BaseUrl + "/api/sources/add");
                req.Method = "POST"; req.Timeout = 30000; req.Proxy = null;
                req.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(body);
                req.ContentLength = bytes.Length;
                using (var st = req.GetRequestStream()) st.Write(bytes, 0, bytes.Length);
                using (req.GetResponse()) { }
                Log.Write("action", "sources/add " + kind + " x" + lines.Count);
                Balloon(L10n.T("Source added", "已添加来源"), lines.Count + " " + L10n.T("line(s); nodes refresh in a moment", "行，节点稍后刷新"));
            }
            catch (Exception ex)
            {
                Log.Write("action", "sources/add failed: " + ex.Message);
                Balloon(L10n.T("Add failed", "添加失败"), L10n.T("Check the link format / service state", "检查链接格式或服务状态"));
            }
        });
    }

    internal void ClearSources()
    {
        if (MessageBox.Show(L10n.T("Remove ALL subscription sources?", "确定清空全部订阅来源？"),
            "CodexOrbit", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes) return;
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                Http.Post(BaseUrl + "/api/sources/clear");
                Log.Write("action", "sources/clear");
                Balloon(L10n.T("Sources cleared", "来源已清空"), "");
            }
            catch { }
        });
    }

    void RestartService()
    {
        try { if (child != null && !child.HasExited) child.Kill(); } catch { }
        foreach (var p in Process.GetProcessesByName("orbit-core"))
            try { p.Kill(); } catch { }
        Proc.KillOrphanKernels();
        Thread.Sleep(800);
        EnsureRunning();
    }

    ToolStripMenuItem AddMenu(string text, EventHandler fn)
    {
        var it = new ToolStripMenuItem(text);
        it.Click += fn;
        menu.Items.Add(it);
        return it;
    }

    void SwitchLang(bool zh)
    {
        L10n.Set(zh);
        card.ApplyLang();
        Balloon("CodexOrbit", zh ? "语言已切换：中文" : "Language: English");
    }

    void Post(string path)
    {
        Log.Write("action", "POST " + path);
        ThreadPool.QueueUserWorkItem(delegate { Http.Post(BaseUrl + path); });
    }

    StatusSnapshot lastStatus;
    bool prevAliveInit;
    bool prevAlive;
    string prevNode = "";
    DateTime lastSwitchBalloon = DateTime.MinValue;
    internal readonly Memory mem = Memory.Load();
    bool memTried;
    int prevStateCount = -1;

    int pollBusy;

    internal void MarkAuto() { if (lastStatus != null) lastStatus.Node = "AUTO"; }

    void ApplySpikeFix()
    {
        var s = lastStatus;
        bool auto = s != null && s.Node == "AUTO";
        string path = auto ? "/api/rotate" : "/api/reset";
        Log.Write("guide", "user accepted -> " + path + " + collect");
        ThreadPool.QueueUserWorkItem(delegate { Http.Post(BaseUrl + path); });
        ThreadPool.QueueUserWorkItem(delegate { Http.Post(BaseUrl + "/api/collect"); }); // refresh the credential too
        if (!auto) { mem.ForgetPin(); MarkAuto(); }
        Balloon(L10n.T("Done", "已处理"),
            auto ? L10n.T("Switching to the next healthy node", "正在切换到下一个健康节点")
                 : L10n.T("Auto-routing restored", "已恢复自动选路"));
    }

    void Poll()
    {
        if (Interlocked.Exchange(ref pollBusy, 1) != 0) return; // overlap guard
        try { PollCore(); } finally { pollBusy = 0; }
    }

    void PollCore()
    {
        var snap = StatusSnapshot.Fetch();
        lastStatus = snap;
        alive = snap != null && snap.Alive;
        errSpike = snap != null && snap.FailStreak >= 3 ? 1 : 0;

        var state = !alive ? IconState.Dead : (errSpike > 0 ? IconState.Warn : IconState.Ok);
        string node = snap != null ? snap.Node : "";
        string tipNode = node == "AUTO" ? L10n.T("auto-routing", "自动选路") : StatusCard.StripFlagsText(node);
        SetTip("CodexOrbit" + (alive && node != "" ? "  ·  " + tipNode : (alive ? "  ·  " + L10n.T("running", "运行中") : L10n.T("  (offline)", "  (离线)"))), state);

        if (prevAliveInit)
        {
            if (alive && !prevAlive) { Log.Write("svc", "up · " + node); Balloon(L10n.T("Service recovered", "服务恢复"), node == "" ? L10n.T("Back online", "已上线") : L10n.T("Current node ", "当前节点 ") + node); }
            if (!alive && prevAlive) { Log.Write("svc", "down"); Balloon(L10n.T("Service offline", "服务掉线"), L10n.T("Auto-restarting", "正在自动重启")); }
            if (alive && prevAlive && node != prevNode && prevNode != "")
            {
                Log.Write("node", prevNode + " -> " + node);
                if ((DateTime.Now - lastSwitchBalloon).TotalMinutes >= 5)
                {
                    Balloon(L10n.T("Node switched", "节点已切换"), prevNode + "  →  " + node);
                    lastSwitchBalloon = DateTime.Now;
                }
            }
        }
        // requests failing in a row? push a one-click fix to the user instead of
        // waiting for them to notice - pinned gets "restore auto", AUTO gets "switch"
        int streak = snap != null ? snap.FailStreak : 0;
        if (streak >= 3 && !spikeNotified)
        {
            spikeNotified = true;
            spikeFixUntil = DateTime.Now.AddSeconds(45);
            Log.Write("guide", streak + " consecutive failures - prompting " + (snap.Node == "AUTO" ? "switch" : "auto"));
            Balloon(L10n.T("Codex requests failing ×" + streak, "Codex 请求连续失败 ×" + streak),
                snap.Node == "AUTO"
                    ? L10n.T("Click to switch to the next healthy node", "点我切换到下一个健康节点")
                    : L10n.T("Click to restore auto-routing (recommended)", "点我恢复自动选路（推荐）"));
        }
        if (streak == 0) spikeNotified = false;
        // the failing credential can be the culprit, not just the node -
        // refresh the 292 too instead of waiting out its TTL
        if (streak >= 3 && snap.States.Count > 0 && !snap.Collecting &&
            (DateTime.Now - lastCollectNudge).TotalSeconds >= 150)
        {
            lastCollectNudge = DateTime.Now;
            Log.Write("guide", "fail streak - refreshing 292 too");
            ThreadPool.QueueUserWorkItem(delegate { Http.Post(BaseUrl + "/api/collect"); });
        }
        // upstream silently serving a different model than requested (e.g. astra -> luna):
        // no client fix exists - rotating nodes won't help - so warn once per episode
        if (snap != null && snap.WantModel != "" && !downNotified)
        {
            downNotified = true;
            Log.Write("guide", "downgrade " + snap.WantModel + " -> " + snap.GotModel);
            Balloon(L10n.T("Upstream downgraded your model", "上游偷偷换了模型"),
                string.Format(L10n.T("Asked for {0}, got {1} - rotation won't fix this; it clears on its own later",
                    "请求 {0}，实际给的 {1} · 换节点没用，等配额恢复即回"), snap.WantModel, snap.GotModel));
        }
        if (snap != null && snap.WantModel == "") downNotified = false;

        prevAliveInit = true; prevAlive = alive; if (node != "") prevNode = node;

        if (snap != null)
        {
            // 292 pool changes -> log + refresh memory
            if (snap.States.Count != prevStateCount)
            {
                if (prevStateCount >= 0) Log.Write("292", "pool " + prevStateCount + " -> " + snap.States.Count);
                prevStateCount = snap.States.Count;
            }
            if (snap.States.Count > 0)
            {
                var sd = snap.States[0];
                object nv, mv;
                sd.TryGetValue("node", out nv); sd.TryGetValue("model", out mv);
                string sn = Convert.ToString(nv), sm = Convert.ToString(mv);
                if (sn != mem.LastStateNode || sm != mem.LastModel || snap.States.Count != mem.LastStateCount)
                {
                    mem.LastStateNode = sn; mem.LastModel = sm;
                    mem.LastStateCount = snap.States.Count;
                    mem.LastCollectAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
                    mem.Save();
                }
            }
            // pool watchdog: engine retries a failed collect on a 300s schedule - we
            // close that gap. empty pool + idle + auth known -> nudge collect ourselves,
            // so nobody ever has to press the button
            if (snap.States.Count == 0 && !snap.Collecting && snap.AuthReady &&
                (DateTime.Now - lastCollectNudge).TotalSeconds >= 150)
            {
                lastCollectNudge = DateTime.Now;
                Log.Write("292", "pool empty & idle - nudging collect");
                ThreadPool.QueueUserWorkItem(delegate { Http.Post(BaseUrl + "/api/collect"); });
            }
            // memory restore - once, on the first live poll after launch
            if (!memTried)
            {
                memTried = true;
                if (snap.States.Count == 0 && mem.LastStateNode != "")
                {
                    Log.Write("memory", "pool empty on launch; last good " + mem.LastModel + " @ " + mem.LastStateNode + " (" + mem.LastCollectAt + ") - kicking collect");
                    ThreadPool.QueueUserWorkItem(delegate { Http.Post(BaseUrl + "/api/collect"); });
                }
                if (mem.Pinned != "" && snap.Node == "AUTO")
                {
                    Log.Write("memory", "restoring pin: " + mem.Pinned);
                    var pn = mem.Pinned;
                    ThreadPool.QueueUserWorkItem(delegate { PostJson(BaseUrl + "/api/pin", "{\"name\":\"" + pn.Replace("\"", "") + "\"}"); });
                }
            }
        }

        if (card.Visible) card.RefreshData(lastStatus);

        // self-heal: if the serve process vanished, relaunch
        if (!alive && (child == null || child.HasExited) && File.Exists(Exe))
        {
            var probe = Http.Get(BaseUrl + "/healthz", 800);
            if (probe == null && (child == null || child.HasExited))
            {
                EnsureRunning();
            }
        }
    }

    internal void Balloon(string title, string body)
    {
        try { tray.ShowBalloonTip(2200, title, body, ToolTipIcon.None); } catch { }
    }

    void SetTip(string text, IconState state)
    {
        tip = text;
        try
        {
            tray.Text = text.Length > 63 ? text.Substring(0, 63) : text;
            tray.Icon = Icons.Make(state);
        }
        catch { }
    }

    internal void Quit()
    {
        Log.Write("app", "quit");
        tray.Visible = false;
        try { if (child != null && !child.HasExited) child.Kill(); } catch { }
        foreach (var p in Process.GetProcessesByName("orbit-core"))
            try { p.Kill(); } catch { }
        Proc.KillOrphanKernels();
        try
        {
            var psi = new ProcessStartInfo(Exe, "restore --config \"" + Cfg + "\"");
            psi.CreateNoWindow = true;
            psi.UseShellExecute = false;
            var p = Process.Start(psi);
            if (p != null) p.WaitForExit(8000);
        }
        catch { }
        Application.Exit();
    }
}

enum IconState { Starting, Ok, Warn, Dead }

static class Icons
{
    static readonly Color Violet = Color.FromArgb(139, 124, 246);
    static readonly Color Amber = Color.FromArgb(245, 180, 80);
    static readonly Color Grey = Color.FromArgb(105, 105, 115);
    static readonly Color Red = Color.FromArgb(235, 95, 95);

    public static Icon Make(IconState s)
    {
        Color c = s == IconState.Ok ? Violet : s == IconState.Warn ? Amber : s == IconState.Dead ? Red : Grey;
        const int n = 32;
        var bmp = new Bitmap(n, n);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            // orbit ring
            using (var pen = new Pen(c, 2.6f))
                g.DrawEllipse(pen, 4, 4, n - 8, n - 8);
            // satellite dot on the ring (upper-right)
            float ang = -40f * (float)Math.PI / 180f;
            float cx = n / 2f + (n / 2f - 4) * (float)Math.Cos(ang);
            float cy = n / 2f + (n / 2f - 4) * (float)Math.Sin(ang);
            using (var b = new SolidBrush(c))
            {
                g.FillEllipse(b, cx - 3.4f, cy - 3.4f, 6.8f, 6.8f);
                g.FillEllipse(b, n / 2f - 3, n / 2f - 3, 6, 6); // core
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}

// Persistent memory: last pin + last good 292 producer, survives restarts.
class Memory
{
    public string Pinned { get; set; }
    public string LastStateNode { get; set; }
    public string LastModel { get; set; }
    public string LastCollectAt { get; set; }
    public int LastStateCount { get; set; }

    static readonly string FilePath = Path.Combine(OrbitApp.Dir, "CodexOrbit.memory.json");

    public static Memory Load()
    {
        try
        {
            var m = new JavaScriptSerializer().Deserialize<Memory>(File.ReadAllText(FilePath));
            return m ?? new Memory();
        }
        catch { return new Memory(); }
    }

    public void Save()
    {
        try { File.WriteAllText(FilePath, new JavaScriptSerializer().Serialize(this)); } catch { }
    }

    public void ForgetPin() { Pinned = ""; Save(); }
}

// Append-only local log, capped at ~1MB.
static class Log
{
    static readonly string FilePath = Path.Combine(OrbitApp.Dir, "CodexOrbit.log");
    static readonly object Gate = new object();
    public static string LogPath { get { return FilePath; } }

    public static void Write(string tag, string msg)
    {
        try
        {
            lock (Gate)
            {
                var fi = new FileInfo(FilePath);
                if (fi.Exists && fi.Length > 1048576)
                {
                    var txt = File.ReadAllText(FilePath);
                    File.WriteAllText(FilePath, txt.Substring(txt.Length / 2));
                }
                File.AppendAllText(FilePath,
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + tag + "] " + msg + "\r\n");
            }
        }
        catch { }
    }
}

class StatusSnapshot
{
    public bool Alive;
    public string Node = "";
    public long Ok, Errors, Alive2, Reachable;
    public bool Collecting, LastCollectOk, AuthReady;
    public long CollectTried, CollectTotal;
    public string NextCollect = "";
    public long LastMillis;
    public int FailStreak;
    public string WantModel = "", GotModel = "";

    public long[] LatencyHistory = new long[0];
    public System.Collections.ArrayList Recent = new System.Collections.ArrayList();
    public System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>> States =
        new System.Collections.Generic.List<System.Collections.Generic.Dictionary<string, object>>();

    public static StatusSnapshot Fetch()
    {
        string s = Http.Get(OrbitApp.BaseUrl + "/api/status", 2500);
        if (s == null) return null;
        try
        {
            var j = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(s);
            var o = new StatusSnapshot();
            o.Alive = true;
            o.Node = S(j, "node");
            o.Ok = L(j, "ok"); o.Errors = L(j, "errors");
            o.Alive2 = L(j, "alive"); o.Reachable = L(j, "reachable");
            o.Collecting = B(j, "collecting"); o.LastCollectOk = B(j, "last_collect_ok");
            o.CollectTried = L(j, "collect_tried"); o.CollectTotal = L(j, "collect_total");
            o.AuthReady = B(j, "auth_ready");
            o.NextCollect = S(j, "next_collect");
            object recent;
            if (j.TryGetValue("recent", out recent))
            {
                var arr = recent as System.Collections.ArrayList;
                if (arr != null && arr.Count > 0)
                {
                    var hist = new System.Collections.Generic.List<long>();
                    foreach (var it in arr)
                    {
                        var r = it as System.Collections.Generic.Dictionary<string, object>;
                        if (r == null) continue;
                        o.Recent.Add(r);
                        if (r.ContainsKey("millis"))
                            hist.Add(Convert.ToInt64(r["millis"]));
                        if (hist.Count >= 24) break;
                    }
                    hist.Reverse(); // oldest -> newest
                    o.LatencyHistory = hist.ToArray();
                    if (hist.Count > 0) o.LastMillis = hist[hist.Count - 1];
                }
                // consecutive failures from the newest record back (node-path failures only:
                // 403 blocked / 429 rate-limited / 5xx upstream) - 401 is an auth problem, not the node
                foreach (var it in o.Recent)
                {
                    var r = it as System.Collections.Generic.Dictionary<string, object>;
                    object sv; long st = 0;
                    if (r != null && r.TryGetValue("status", out sv)) long.TryParse(Convert.ToString(sv), out st);
                    if (st == 403 || st == 429 || st >= 500) o.FailStreak++; else break;
                }
                // silent downgrade: newest responses call whose served model != requested
                foreach (var it in o.Recent)
                {
                    var r = it as System.Collections.Generic.Dictionary<string, object>;
                    if (r == null) continue;
                    object pv, sv2, rmv, smv2; long st2 = 0;
                    r.TryGetValue("path", out pv); r.TryGetValue("status", out sv2);
                    r.TryGetValue("model", out rmv); r.TryGetValue("served_model", out smv2);
                    long.TryParse(Convert.ToString(sv2), out st2);
                    string p = Convert.ToString(pv), w = Convert.ToString(rmv), g = Convert.ToString(smv2);
                    if (st2 != 200 || g == null || g == "") continue;
                    if (p != null && p.EndsWith("responses") && w != null && w != "" && g != w)
                    { o.WantModel = w; o.GotModel = g; }
                    break;
                }
            }
            object states;
            if (j.TryGetValue("states", out states))
            {
                var sa = states as System.Collections.ArrayList;
                if (sa != null)
                    foreach (var it in sa)
                    {
                        var d = it as System.Collections.Generic.Dictionary<string, object>;
                        if (d != null) o.States.Add(d);
                    }
            }
            return o;
        }
        catch { return new StatusSnapshot { Alive = true }; }
    }

    // Demo snapshot + node list for doc screenshots - real node/subscription
    // names must never leak into published images.
    public static StatusSnapshot Demo()
    {
        var o = new StatusSnapshot();
        o.Alive = true; o.Node = "AUTO";
        o.Ok = 13; o.Errors = 1; o.Alive2 = 6; o.Reachable = 4;
        o.LastCollectOk = true; o.NextCollect = "2026-01-01T12:57:00+08:00";
        o.LastMillis = 28300;
        o.LatencyHistory = new long[] { 39669, 13126, 44739, 872, 28300, 37400 };
        o.States.Add(D("model", "gpt-6-astra", "length", 292, "node", "Node-A", "hits", 25));
        o.Recent.Add(D("time", "2026-01-01T12:38:33+08:00", "method", "POST", "path", "/backend-api/codex/responses", "status", 200, "node", "Node-A", "injected", true, "attempts", 1, "ttft", 8700, "millis", 28300));
        o.Recent.Add(D("time", "2026-01-01T12:38:00+08:00", "method", "POST", "path", "/backend-api/codex/responses", "status", 200, "node", "Node-A", "injected", true, "attempts", 1, "ttft", 2100, "millis", 37400));
        o.Recent.Add(D("time", "2026-01-01T12:37:20+08:00", "method", "GET", "path", "/backend-api/codex/models", "status", 200, "node", "Node-A", "injected", false, "attempts", 1, "ttft", 500, "millis", 540));
        o.Recent.Add(D("time", "2026-01-01T12:35:10+08:00", "method", "POST", "path", "/backend-api/codex/responses", "status", 502, "node", "Node-B", "injected", false, "attempts", 2, "ttft", 0, "millis", 4300));
        return o;
    }

    public const string DemoNodesJson =
        "{\"current\":\"Node-A\",\"nodes\":[" +
        "{\"name\":\"Node-A\",\"state\":\"ok\",\"type\":\"Tuic\",\"delay\":38}," +
        "{\"name\":\"Node-B\",\"state\":\"ok\",\"type\":\"Vless\",\"delay\":52}," +
        "{\"name\":\"Node-C\",\"state\":\"ok\",\"type\":\"Hysteria2\",\"delay\":71}," +
        "{\"name\":\"Node-D\",\"state\":\"ok\",\"type\":\"Tuic\",\"delay\":44}," +
        "{\"name\":\"Node-E\",\"state\":\"reachable\",\"type\":\"Vless\",\"delay\":0}," +
        "{\"name\":\"Node-F\",\"state\":\"unknown\",\"type\":\"SS\",\"delay\":0}]}";

    static System.Collections.Generic.Dictionary<string, object> D(params object[] kv)
    {
        var d = new System.Collections.Generic.Dictionary<string, object>();
        for (int i = 0; i + 1 < kv.Length; i += 2) d[Convert.ToString(kv[i])] = kv[i + 1];
        return d;
    }

    static string S(System.Collections.Generic.Dictionary<string, object> j, string k)
    {
        object v; return j.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : "";
    }
    static long L(System.Collections.Generic.Dictionary<string, object> j, string k)
    {
        object v; if (!j.TryGetValue(k, out v) || v == null) return 0;
        long n; return long.TryParse(Convert.ToString(v), out n) ? n : 0;
    }
    static bool B(System.Collections.Generic.Dictionary<string, object> j, string k)
    {
        object v; if (!j.TryGetValue(k, out v) || v == null) return false;
        bool b; return bool.TryParse(Convert.ToString(v), out b) && b;
    }
}

// Borderless rounded status card shown above the tray.
class StatusCard : Form
{
    static readonly Color Bg = Color.FromArgb(23, 23, 28);
    static readonly Color BgSoft = Color.FromArgb(34, 34, 42);
    static readonly Color Txt = Color.FromArgb(235, 235, 240);
    static readonly Color Dim = Color.FromArgb(150, 150, 162);
    static readonly Color Accent = Color.FromArgb(139, 124, 246);
    static readonly Color Good = Color.FromArgb(80, 210, 140);
    static readonly Color Bad = Color.FromArgb(235, 95, 95);
    static readonly Color Warn = Color.FromArgb(245, 180, 80);

    Label nodeLbl, statsLbl, stateLbl, exitLbl;
    DotLabel dot;
    Sparkline spark;
    Button btnPanel, btnSwitch, btnCollect, btnAuto;
    StatusSnapshot last;
    public bool NoAutoHide;

    public StatusCard()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = true;
        Width = 292; Height = 246;
        BackColor = Bg;
        Deactivate += delegate { if (!NoAutoHide) Hide(); };

        var head = new Label { Text = "CodexOrbit", ForeColor = Dim, AutoSize = true, Left = 16, Top = 14,
            Font = new Font("Segoe UI", 9f, FontStyle.Bold) };
        dot = new DotLabel { Left = 268, Top = 12, Width = 10, Height = 10 };

        nodeLbl = new Label { Text = "—", ForeColor = Txt, Left = 14, Top = 38, Width = 264, Height = 30,
            Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold) };
        statsLbl = new Label { Text = "", ForeColor = Dim, Left = 16, Top = 74, Width = 262, Height = 18,
            Font = new Font("Segoe UI", 8.6f) };
        stateLbl = new Label { Text = "", ForeColor = Dim, Left = 16, Top = 94, Width = 262, Height = 18,
            Font = new Font("Segoe UI", 8.6f) };

        spark = new Sparkline { Left = 16, Top = 118, Width = 260, Height = 34 };

        btnPanel = MkBtn("", 16, delegate { ShowStates(); });
        btnSwitch = MkBtn("", 82, delegate { Act("/api/rotate"); });
        btnCollect = MkBtn("", 148, delegate { Act("/api/collect"); OrbitAppHolder.App.Balloon(L10n.T("Collecting 292", "正在采集 292"), ""); });
        btnAuto = MkBtn("", 214, delegate { Act("/api/reset"); OrbitAppHolder.App.mem.ForgetPin(); OrbitAppHolder.App.MarkAuto(); Log.Write("pin", "cleared -> auto"); });
        foreach (var b in new[] { btnPanel, btnSwitch, btnCollect, btnAuto }) { b.Top = 164; Controls.Add(b); }

        exitLbl = new Label { ForeColor = Dim, AutoSize = true,
            Top = 216, Font = new Font("Segoe UI", 8.4f), Cursor = Cursors.Hand };
        exitLbl.MouseEnter += delegate { exitLbl.ForeColor = Bad; };
        exitLbl.MouseLeave += delegate { exitLbl.ForeColor = Dim; };
        exitLbl.Click += delegate { Hide(); QuitHost(); };

        Controls.AddRange(new Control[] { head, dot, nodeLbl, statsLbl, stateLbl, spark, exitLbl });
        ApplyLang();
    }

    // Re-apply static strings after a language switch (menu rebuilds itself).
    public void ApplyLang()
    {
        if (InvokeRequired) { BeginInvoke(new MethodInvoker(ApplyLang)); return; }
        btnPanel.Text = L10n.T("States", "凭据");
        btnSwitch.Text = L10n.T("Switch", "切换");
        btnCollect.Text = L10n.T("Collect", "采集");
        btnAuto.Text = L10n.T("Auto", "自动");
        exitLbl.Text = L10n.T("Exit · restore Codex", "退出 · 还原 Codex");
        exitLbl.Left = Math.Max(8, (Width - exitLbl.PreferredWidth) / 2);
        if (last != null) RefreshData(last);
        spark.Invalidate();
    }

    void QuitHost()
    {
        // find the app context via tray icon owner — simplest: static hook
        OrbitAppHolder.App.Quit();
    }

    Button MkBtn(string text, int x, EventHandler fn)
    {
        var b = new Button
        {
            Text = text, Left = x, Top = 126, Width = 62, Height = 30,
            FlatStyle = FlatStyle.Flat, BackColor = BgSoft, ForeColor = Txt,
            Font = new Font("Segoe UI", 8.6f), Cursor = Cursors.Hand,
            TabStop = false
        };
        b.FlatAppearance.BorderSize = 0;
        b.FlatAppearance.MouseOverBackColor = Color.FromArgb(46, 46, 56);
        b.FlatAppearance.MouseDownBackColor = Accent;
        b.Click += fn;
        return b;
    }

    // 凭据 button → popup listing collected turn-states (values never shown).
    void ShowStates()
    {
        var m = new ContextMenuStrip();
        m.Renderer = new DarkMenu();
        m.BackColor = Color.FromArgb(24, 24, 28);
        m.ForeColor = Color.FromArgb(232, 232, 236);
        m.ShowImageMargin = false;
        if (last != null && last.States.Count > 0)
            foreach (var sd in last.States)
            {
                object mv, lv, nv, hv;
                sd.TryGetValue("model", out mv); sd.TryGetValue("length", out lv);
                sd.TryGetValue("node", out nv); sd.TryGetValue("hits", out hv);
                m.Items.Add(new ToolStripMenuItem(string.Format("{0} · {1} · ×{2} · {3}",
                    mv, lv, hv, StripFlags(Convert.ToString(nv)))) { Enabled = false });
            }
        else
            m.Items.Add(new ToolStripMenuItem(L10n.T("(none collected)", "（尚未采到）")) { Enabled = false });
        m.Show(btnPanel, new Point(0, btnPanel.Height));
    }

    void Act(string path)
    {
        ThreadPool.QueueUserWorkItem(delegate { Http.Post(OrbitApp.BaseUrl + path); });
    }

    public void ShowNearTray()
    {
        if (Visible) { Hide(); return; } // click toggles
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Right - Width - 12, wa.Bottom - Height - 12);
        Show();
        // tray apps aren't granted foreground rights — force it or the card
        // shows un-activated and Deactivate instantly hides it again
        SetForegroundWindow(Handle);
        Activate();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetForegroundWindow(IntPtr hWnd);

    // --preview: show the card centered so it can be screenshotted for docs.
    public void PreviewCenter()
    {
        if (InvokeRequired) { BeginInvoke(new MethodInvoker(PreviewCenter)); return; }
        var wa = Screen.PrimaryScreen.WorkingArea;
        Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Top + (wa.Height - Height) / 2);
        NoAutoHide = true;
        TopMost = true;
        Show();
        SetForegroundWindow(Handle);
        Activate();
    }

    public void RefreshData(StatusSnapshot s)
    {
        if (s == null)
        {
            if (IsHandleCreated) BeginInvoke(new MethodInvoker(delegate { if (Visible) { nodeLbl.Text = L10n.T("offline", "离线"); dot.State = Bad; } }));
            return;
        }
        if (InvokeRequired) { BeginInvoke(new MethodInvoker(delegate { RefreshData(s); })); return; }
        last = s;
        nodeLbl.Text = string.IsNullOrEmpty(s.Node) ? L10n.T("(no node)", "(无节点)")
            : s.Node == "AUTO" ? L10n.T("AUTO · auto-routing", "AUTO · 自动选路")
            : StripFlags(s.Node) + L10n.T(" · pinned", " · 已固定");
        long total = s.Ok + s.Errors;
        string rate = total > 0 ? Math.Round(100.0 * s.Ok / total) + "%" : "—";
        statsLbl.Text = string.Format(L10n.T("{0} ok · {1} err · {2} · last {3}s", "{0} 成功 · {1} 失败 · {2} · 上次 {3}s"),
            s.Ok, s.Errors, rate, Math.Round(s.LastMillis / 1000.0, 1));
        if (s.FailStreak >= 3)
            statsLbl.Text += L10n.T("  ⚠ failing ×" + s.FailStreak, "  ⚠ 连失败 ×" + s.FailStreak);
        if (s.WantModel != "")
            statsLbl.Text += string.Format(L10n.T("  ⚠ downgraded: {0}", "  ⚠ 降级:{0}"), s.GotModel);
        string next = s.NextCollect.Length >= 16 ? s.NextCollect.Substring(11, 5) : "—";
        string pool = s.Collecting
            ? L10n.T("collecting", "采集中") + (s.CollectTotal > 0 ? " " + s.CollectTried + "/" + s.CollectTotal : "")
            : s.LastCollectOk ? L10n.T("ok", "正常") : L10n.T("pending", "待采");
        stateLbl.Text = string.Format(L10n.T("292 pool: {0} · {1} stored · next {2}", "292 池: {0} · 在库 {1} 条 · 下次 {2}"),
            pool, s.States.Count, next);
        dot.State = !s.Alive ? Bad : (s.Collecting ? Warn : Good);
        dot.Invalidate();
        spark.Points = s.LatencyHistory;
        spark.Invalidate();
        btnAuto.Enabled = s.Node != "AUTO";
        bool warn = s.FailStreak >= 3;
        btnAuto.BackColor = warn && s.Node != "AUTO" ? Accent : BgSoft;
        btnSwitch.BackColor = warn && s.Node == "AUTO" ? Accent : BgSoft;
    }

    // GDI+ has no glyphs for regional-indicator flag emoji — drop them.
    public static string StripFlagsText(string s) { return StripFlags(s); }
    static string StripFlags(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            // regional-indicator flags arrive as surrogate pair: high 0xD83C + low 0xDDE6..0xDDFF
            if (c == 0xD83C && i + 1 < s.Length && s[i + 1] >= 0xDDE6 && s[i + 1] <= 0xDDFF) continue;
            if (c >= 0xDDE6 && c <= 0xDDFF) continue; // stray low half
            if (char.IsSurrogate(c) || c == 0xFE0F) continue; // VS16 + leftover emoji pairs
            if (char.GetUnicodeCategory(c) == System.Globalization.UnicodeCategory.OtherSymbol) continue;
            sb.Append(c);
        }
        return sb.ToString();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var rc = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Rounded(rc, 14))
        using (var pen = new Pen(Color.FromArgb(52, 52, 62)))
            g.DrawPath(pen, path);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        using (var path = Rounded(new Rectangle(0, 0, Width, Height), 14))
            Region = new Region(path);
    }

    static GraphicsPath Rounded(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    class DotLabel : Label
    {
        public Color State = Good;
        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(State))
                e.Graphics.FillEllipse(b, 0, 0, Width - 1, Height - 1);
        }
    }

    // Mini latency sparkline over the last N requests.
    class Sparkline : Control
    {
        public long[] Points = new long[0];
        public Sparkline() { SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true); BackColor = Color.FromArgb(33, 33, 41); }
        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var b = new SolidBrush(BackColor)) g.FillRectangle(b, ClientRectangle);
            var pts = Points;
            if (pts == null || pts.Length < 2)
            {
                using (var f = new Font("Segoe UI", 7.5f))
                using (var tb = new SolidBrush(Color.FromArgb(140, 140, 155)))
                    g.DrawString(L10n.T("latency history", "延迟趋势"), f, tb, 6, Height / 2 - 7);
                return;
            }
            long max = 1;
            foreach (var v in pts) if (v > max) max = v;
            float n = pts.Length, w = Width - 12, h = Height - 10;
            var poly = new PointF[pts.Length];
            for (int i = 0; i < pts.Length; i++)
                poly[i] = new PointF(6 + w * i / (n - 1), 5 + h - h * pts[i] / max);
            // soft area fill + line
            var area = new GraphicsPath();
            area.AddLines(poly);
            area.AddLine(poly[poly.Length - 1].X, Height - 5, poly[0].X, Height - 5);
            area.CloseFigure();
            using (var fill = new SolidBrush(Color.FromArgb(38, Accent)))
                g.FillPath(fill, area);
            using (var pen = new Pen(Accent, 1.6f))
                g.DrawLines(pen, poly);
            using (var b = new SolidBrush(Accent))
                g.FillEllipse(b, poly[poly.Length - 1].X - 2.2f, poly[poly.Length - 1].Y - 2.2f, 4.4f, 4.4f);
        }
    }
}

// Lets the card reach the app context without a global.
static class OrbitAppHolder { public static OrbitApp App; }

// Native console window — full replacement for the web panel.
class ConsoleForm : Form
{
    static readonly Color Bg = Color.FromArgb(23, 23, 28);
    static readonly Color BgSoft = Color.FromArgb(34, 34, 42);
    static readonly Color Txt = Color.FromArgb(235, 235, 240);
    static readonly Color Dim = Color.FromArgb(150, 150, 162);
    static readonly Color Accent = Color.FromArgb(139, 124, 246);

    Label head, stats, state, nl2;
    ListBox nodes, creds, reqs;
    Button autoBtn, switchBtn;
    System.Windows.Forms.Timer refreshTimer;
    internal bool NoFetch; // shot mode: demo data only, never hit the live API
    System.Collections.ArrayList nodeRaw;

    public ConsoleForm()
    {
        Text = "CodexOrbit";
        ClientSize = new Size(404, 470);
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Bg; ForeColor = Txt;
        Font = new Font("Microsoft YaHei UI", 9f);
        Icon = Icon.FromHandle(Icons.Make(IconState.Ok).Handle);

        head = new Label { Text = "CodexOrbit", Left = 16, Top = 12, AutoSize = true,
            ForeColor = Txt, Font = new Font("Microsoft YaHei UI", 12f, FontStyle.Bold) };
        stats = new Label { Left = 16, Top = 40, Width = 372, Height = 18, ForeColor = Dim };
        state = new Label { Left = 16, Top = 58, Width = 372, Height = 18, ForeColor = Dim };

        nl2 = new Label { Text = L10n.T("Node pool · click to pin", "节点池 · 单击固定"), Left = 16, Top = 86, AutoSize = true, ForeColor = Dim };
        nodes = new ListBox { Left = 16, Top = 106, Width = 372, Height = 128, HorizontalScrollbar = true,
            BackColor = BgSoft, ForeColor = Txt, BorderStyle = BorderStyle.None, IntegralHeight = false };
        nodes.DoubleClick += delegate { PinSelected(); };
        nodes.Click += delegate { PinSelected(); };

        var cl = new Label { Text = L10n.T("292 credential pool", "292 凭据池"), Left = 16, Top = 242, AutoSize = true, ForeColor = Dim };
        creds = new ListBox { Left = 16, Top = 260, Width = 372, Height = 50, HorizontalScrollbar = true,
            BackColor = BgSoft, ForeColor = Txt, BorderStyle = BorderStyle.None, IntegralHeight = false };

        var rl = new Label { Text = L10n.T("Request log · first-byte / total", "请求记录 · 首字/总耗时"), Left = 16, Top = 318, AutoSize = true, ForeColor = Dim };
        reqs = new ListBox { Left = 16, Top = 336, Width = 372, Height = 56, HorizontalScrollbar = true,
            BackColor = BgSoft, ForeColor = Txt, BorderStyle = BorderStyle.None, IntegralHeight = false };

        int x = 16;
        foreach (var b in new string[] { "Switch", "Collect", "Auto", "Restart", "+Sub", "+Node" })
        {
            var btn = new Button { Left = x, Top = 404, Width = 60, Height = 28,
                FlatStyle = FlatStyle.Flat, BackColor = BgSoft, ForeColor = Txt, Cursor = Cursors.Hand };
            btn.FlatAppearance.BorderSize = 0;
            btn.FlatAppearance.MouseOverBackColor = Color.FromArgb(46, 46, 56);
            Controls.Add(btn);
            switch (b)
            {
                case "Switch": btn.Text = L10n.T("Switch", "切换"); switchBtn = btn; btn.Click += delegate { Act("/api/rotate"); }; break;
                case "Collect": btn.Text = L10n.T("Collect", "采集"); btn.Click += delegate { Act("/api/collect"); OrbitAppHolder.App.Balloon(L10n.T("Collecting 292", "正在采集 292"), ""); }; break;
                case "Auto": btn.Text = L10n.T("Auto", "自动"); autoBtn = btn; btn.Click += delegate { Act("/api/reset"); OrbitAppHolder.App.mem.ForgetPin(); OrbitAppHolder.App.MarkAuto(); Log.Write("pin", "cleared -> auto"); }; break;
                case "Restart": btn.Text = L10n.T("Restart", "重启"); btn.Click += delegate { OrbitAppHolder.App.Balloon(L10n.T("Restarting", "正在重启"), ""); Log.Write("action", "restart"); OrbitAppHolder.App.RestartBg(); Close(); }; break;
                case "+Sub": btn.Text = L10n.T("+Sub", "+订阅"); btn.Click += delegate { OrbitAppHolder.App.AddSource("sub"); RefreshSoon(); }; break;
                case "+Node": btn.Text = L10n.T("+Node", "+节点"); btn.Click += delegate { OrbitAppHolder.App.AddSource("node"); RefreshSoon(); }; break;
            }
            x += 63;
        }

        var hint = new Label { Text = L10n.T("Close = back to tray", "关闭 = 回到托盘"), Left = 16, Top = 444, AutoSize = true, ForeColor = Dim,
            Font = new Font("Microsoft YaHei UI", 8f) };

        Controls.AddRange(new Control[] { head, stats, state, nl2, nodes, cl, creds, rl, reqs, hint });

        refreshTimer = new System.Windows.Forms.Timer { Interval = 4000 };
        refreshTimer.Tick += delegate { if (Visible && !NoFetch) Reload(); };
        refreshTimer.Start();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        refreshTimer.Stop(); refreshTimer.Dispose();
        base.OnFormClosed(e);
    }

    protected override void OnShown(EventArgs e) { base.OnShown(e); if (!NoFetch) Reload(); }

    void RefreshSoon() { new Thread(new ThreadStart(delegate { Thread.Sleep(1500); Reload(); })) { IsBackground = true }.Start(); }

    void Act(string path)
    {
        ThreadPool.QueueUserWorkItem(delegate { Http.Post(OrbitApp.BaseUrl + path); });
        RefreshSoon();
    }

    void PinSelected()
    {
        if (nodes.SelectedIndex < 0 || nodeRaw == null || nodes.SelectedIndex >= nodeRaw.Count) return;
        var d = nodeRaw[nodes.SelectedIndex] as System.Collections.Generic.Dictionary<string, object>;
        if (d == null) return;
        string name = Convert.ToString(d["name"]);
        ThreadPool.QueueUserWorkItem(delegate { OrbitApp.PostJson(OrbitApp.BaseUrl + "/api/pin",
            "{\"name\":\"" + name.Replace("\"", "") + "\"}"); });
        RefreshSoon();
    }

    public void Reload()
    {
        ThreadPool.QueueUserWorkItem(delegate
        {
            var snap = StatusSnapshot.Fetch();
            string nodesJson = Http.Get(OrbitApp.BaseUrl + "/api/nodes", 3000);
            if (InvokeRequired) { BeginInvoke(new MethodInvoker(delegate { ApplyData(snap, nodesJson); })); return; }
            ApplyData(snap, nodesJson);
        });
    }

    internal void ApplyData(StatusSnapshot s, string nodesJson)
    {
        bool warn = s != null && s.FailStreak >= 3;
        if (autoBtn != null) { autoBtn.Enabled = s == null || s.Node != "AUTO"; autoBtn.BackColor = warn && s.Node != "AUTO" ? Accent : BgSoft; }
        if (switchBtn != null) switchBtn.BackColor = warn && s.Node == "AUTO" ? Accent : BgSoft;
        if (s != null)
        {
            long total = s.Ok + s.Errors;
            string rate = total > 0 ? Math.Round(100.0 * s.Ok / total) + "%" : "—";
            head.Text = s.Node == "AUTO" ? "CodexOrbit  ·  " + L10n.T("auto-routing", "自动选路")
                : string.IsNullOrEmpty(s.Node) ? "CodexOrbit"
                : "CodexOrbit  ·  " + StatusCard.StripFlagsText(s.Node) + L10n.T(" (pinned)", "（已固定）");
            stats.Text = string.Format(L10n.T("{0} ok · {1} err · {2} · last {3}s", "{0} 成功 · {1} 失败 · {2} · 上次 {3}s"),
                s.Ok, s.Errors, rate, Math.Round(s.LastMillis / 1000.0, 1));
            if (warn) stats.Text += s.Node == "AUTO" ? L10n.T("  ⚠ failing - hit Switch", "  ⚠ 连失败 · 点「切换」") : L10n.T("  ⚠ failing - hit Auto", "  ⚠ 连失败 · 点「自动」");
            if (s.WantModel != "")
                stats.Text += string.Format(L10n.T("  ⚠ asked {0}, served {1}", "  ⚠ 求 {0} 实得 {1}"), s.WantModel, s.GotModel);
            string next = s.NextCollect.Length >= 16 ? s.NextCollect.Substring(11, 5) : "—";
            string pool = s.Collecting
                ? L10n.T("collecting", "采集中") + (s.CollectTotal > 0 ? " " + s.CollectTried + "/" + s.CollectTotal : "")
                : s.LastCollectOk ? L10n.T("ok", "正常") : L10n.T("pending", "待采");
            state.Text = string.Format(L10n.T(
                "292 pool: {0} · {1} stored · next {2}",
                "292 池: {0} · 在库 {1} 条 · 下次 {2}"),
                pool, s.States.Count, next);
            creds.Items.Clear();
            if (s.States.Count == 0)
                creds.Items.Add(s.Collecting
                    ? L10n.T("(collecting now - it'll fill itself)", "（采集中 · 稍候自动补上）")
                    : L10n.T("(none yet - auto-collect is scheduled; or press Collect)", "（暂无 · 到点自动采 · 或点「采集」立即补）"));
            foreach (var sd in s.States)
            {
                object mv, lv, nv, hv;
                sd.TryGetValue("model", out mv); sd.TryGetValue("length", out lv);
                sd.TryGetValue("node", out nv); sd.TryGetValue("hits", out hv);
                creds.Items.Add(string.Format("{0} · {1} · ×{2} · {3}", mv, lv, hv, StatusCard.StripFlagsText(Convert.ToString(nv))));
            }
            reqs.Items.Clear();
            if (s.Recent.Count == 0) reqs.Items.Add(L10n.T("(no requests yet)", "（还没有请求）"));
            foreach (var it in s.Recent)
            {
                var r = it as System.Collections.Generic.Dictionary<string, object>;
                if (r == null) continue;
                string tm = Convert.ToString(r["time"]);
                tm = tm != null && tm.Length >= 19 ? tm.Substring(11, 8) : "--:--:--";
                string path = Convert.ToString(r["path"]);
                int slash = path == null ? -1 : path.LastIndexOf('/');
                if (slash >= 0) path = path.Substring(slash + 1);
                long ms = 0, tt = 0; int att = 1;
                object tmp;
                if (r.TryGetValue("millis", out tmp)) long.TryParse(Convert.ToString(tmp), out ms);
                if (r.TryGetValue("ttft", out tmp)) long.TryParse(Convert.ToString(tmp), out tt);
                if (r.TryGetValue("attempts", out tmp)) int.TryParse(Convert.ToString(tmp), out att);
                string dur = ms >= 1000 ? Math.Round(ms / 1000.0, 1) + "s" : ms + "ms";
                string row = string.Format("{0} {1} {2} · {3}", tm, r["method"], path, r["status"]);
                if (tt > 0) row += string.Format(L10n.T(" · first {0}", " · 首字 {0}"), tt >= 1000 ? Math.Round(tt / 1000.0, 1) + "s" : tt + "ms");
                row += string.Format(L10n.T(" · total {0}", " · 总 {0}"), dur);
                if (att > 1) row += " ×" + att;
                object inj; if (r.TryGetValue("injected", out inj) && Convert.ToString(inj) == "True") row += " ·292";
                object sm, rm;
                r.TryGetValue("served_model", out sm); r.TryGetValue("model", out rm);
                string smv = Convert.ToString(sm), rmv = Convert.ToString(rm);
                if (smv != "" && smv != rmv) row += L10n.T(" · got:", " · 实际:") + smv;
                object nd; if (r.TryGetValue("node", out nd)) row += " · " + StatusCard.StripFlagsText(Convert.ToString(nd));
                reqs.Items.Add(row);
            }
        }
        else
        {
            head.Text = "CodexOrbit  ·  " + L10n.T("offline", "离线");
            stats.Text = state.Text = "";
            reqs.Items.Clear();
        }
        nodes.Items.Clear();
        nodeRaw = null;
        if (nodesJson == null) return;
        try
        {
            var j = new JavaScriptSerializer().Deserialize<System.Collections.Generic.Dictionary<string, object>>(nodesJson);
            string current = j.ContainsKey("current") ? Convert.ToString(j["current"]) : "";
            var arr = j.ContainsKey("nodes") ? j["nodes"] as System.Collections.ArrayList : null;
            if (arr == null) return;
            if (arr.Count == 0) nodes.Items.Add(L10n.T("(empty - add a subscription with +Sub)", "（空 · 先点 +订阅 添加订阅链接）"));
            if (s != null && s.Node == "AUTO" && current != "" && current != "AUTO")
                head.Text = "CodexOrbit  ·  " + L10n.T("auto", "自动") + " · " + StatusCard.StripFlagsText(current);
            nodeRaw = arr;
            int okCount = 0;
            foreach (var it in arr)
            {
                var d = it as System.Collections.Generic.Dictionary<string, object>;
                if (d == null) continue;
                string name = Convert.ToString(d["name"]);
                string stt = Convert.ToString(d["state"]);
                if (stt == "ok") okCount++;
                object tpv, dvv; d.TryGetValue("type", out tpv); d.TryGetValue("delay", out dvv);
                long delay = 0; long.TryParse(Convert.ToString(dvv), out delay);
                nodes.Items.Add(string.Format("{0} {1}   {2}   {3}",
                    name == current ? "✓" : stt == "ok" ? "●" : "○",
                    StatusCard.StripFlagsText(name), tpv,
                    delay > 0 ? delay + "ms" : ""));
            }
            nl2.Text = string.Format(L10n.T(
                "Node pool · click to pin · {0}/{1} healthy · {2} upstream-ready",
                "节点池 · 单击固定 · 健康 {0}/{1} · 可达上游 {2}"),
                okCount, arr.Count, s != null ? s.Reachable : 0);
        }
        catch { }
    }
}

// --shot <out.png>: render the status card headlessly for docs/marketing.
static class Shot
{
    public static void Save(string path)
    {
        // DrawToBitmap misses owner-drawn children on a never-shown form, so we
        // show the card at a fixed corner, let it render, and grab the region.
        var card = new StatusCard();
        card.NoAutoHide = true;
        card.StartPosition = FormStartPosition.Manual;
        card.Location = new Point(60, 60);
        card.RefreshData(StatusSnapshot.Demo());
        card.Show();
        Application.DoEvents();
        Thread.Sleep(900); // let GDI+ finish
        var bmp = new Bitmap(card.Width, card.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(card.Location.X, card.Location.Y, 0, 0, bmp.Size);
        card.Close();
        // punch out rounded corners to transparent
        var outp = new Bitmap(card.Width, card.Height);
        using (var g = Graphics.FromImage(outp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var p = Rounded(new Rectangle(0, 0, card.Width, card.Height), 14))
            {
                g.SetClip(p);
                g.DrawImage(bmp, 0, 0);
            }
        }
        outp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    public static void SaveConsole(string path)
    {
        var f = new ConsoleForm();
        f.NoFetch = true;
        f.StartPosition = FormStartPosition.Manual;
        f.Location = new Point(60, 60);
        f.Show();
        Application.DoEvents();
        f.ApplyData(StatusSnapshot.Demo(), StatusSnapshot.DemoNodesJson);
        Thread.Sleep(900); // let GDI+ settle
        Application.DoEvents();
        var bmp = new Bitmap(f.Width, f.Height);
        using (var g = Graphics.FromImage(bmp))
            g.CopyFromScreen(f.Location.X, f.Location.Y, 0, 0, bmp.Size);
        f.Close();
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    static GraphicsPath Rounded(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

static class Http
{
    public static string Get(string url, int timeoutMs)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Timeout = timeoutMs;
            req.Proxy = null;
            using (var resp = (HttpWebResponse)req.GetResponse())
            using (var sr = new StreamReader(resp.GetResponseStream()))
                return sr.ReadToEnd();
        }
        catch { return null; }
    }

    public static void Post(string url)
    {
        try
        {
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.Method = "POST";
            req.Timeout = 15000;
            req.Proxy = null;
            req.ContentLength = 0;
            using (req.GetResponse()) { }
        }
        catch { }
    }
}

// Dark owner-drawn context menu (rounded hover, no system chrome).
class DarkMenu : ToolStripProfessionalRenderer
{
    // renderer defaults draw near-black text — force readable colors explicitly
    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Color.FromArgb(240, 240, 245) : Color.FromArgb(125, 125, 138);
        base.OnRenderItemText(e);
    }
    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        if (!e.Item.Selected || !e.Item.Enabled) { base.OnRenderMenuItemBackground(e); return; }
        var rc = new Rectangle(2, 0, e.Item.Width - 4, e.Item.Height);
        using (var b = new SolidBrush(Color.FromArgb(48, 48, 58)))
        using (var gp = Round(rc, 5))
            e.Graphics.FillPath(b, gp);
    }
    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        int y = e.Item.Height / 2;
        using (var p = new Pen(Color.FromArgb(52, 52, 60)))
            e.Graphics.DrawLine(p, 10, y, e.Item.Width - 10, y);
    }
    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using (var b = new SolidBrush(Color.FromArgb(24, 24, 28)))
            e.Graphics.FillRectangle(b, e.AffectedBounds);
    }
    static GraphicsPath Round(Rectangle r, int rad)
    {
        var p = new GraphicsPath();
        int d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }
}

static class Proc
{
    public static void KillOrphanKernels()
    {
        try
        {
            var mos = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, CommandLine FROM Win32_Process WHERE Name='verge-mihomo.exe'");
            foreach (var mo in mos.Get())
            {
                string cl = Convert.ToString(mo["CommandLine"]);
                if (cl != null && cl.IndexOf("ccodex-rotate", StringComparison.OrdinalIgnoreCase) >= 0)
                    try { Process.GetProcessById(Convert.ToInt32(mo["ProcessId"])).Kill(); } catch { }
            }
        }
        catch { }
    }
}
