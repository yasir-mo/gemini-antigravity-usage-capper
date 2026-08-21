// GeminiCapper - Google Antigravity & Gemini usage tracker and capper
// Copyright 2026 Yasir Mo (https://github.com/yasir-mo). Apache License 2.0.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using System.Web.Script.Serialization;

public class GeminiCapperForm : Form
{
    // Windows Credential Manager Interop
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("Advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

    [DllImport("Advapi32.dll", EntryPoint = "CredFree", SetLastError = true)]
    public static extern void CredFree(IntPtr cred);

    public static string ReadCredential(string target)
    {
        IntPtr credPtr;
        if (CredRead(target, 1, 0, out credPtr))
        {
            try
            {
                CREDENTIAL cred = (CREDENTIAL)Marshal.PtrToStructure(credPtr, typeof(CREDENTIAL));
                byte[] b = new byte[cred.CredentialBlobSize];
                Marshal.Copy(cred.CredentialBlob, b, 0, cred.CredentialBlobSize);
                return Encoding.UTF8.GetString(b);
            }
            finally
            {
                CredFree(credPtr);
            }
        }
        return null;
    }

    string toolDir;
    string configFile;
    long pausedUntilEpoch = 0;
    bool loading = true;
    bool shownBalloon = false;
    bool userInitialized = false;
    ArrayList limits = null;

    Label lblStatus;
    Label lblFetched;
    Label lblAllowedToday;
    Label lblPoints;
    NumericUpDown numThreshold;
    NumericUpDown numPointsPerDay;
    CheckBox chkPacing;
    Label[] rowName = new Label[2];
    ProgressBar[] rowBar = new ProgressBar[2];
    Label[] rowPct = new Label[2];
    System.Windows.Forms.Timer timer;
    NotifyIcon notifyIcon;
    ContextMenuStrip trayMenu;
    bool isExiting = false;

    [STAThread]
    public static void Main()
    {
        string dir = Path.GetDirectoryName(Application.ExecutablePath);
        try
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new GeminiCapperForm(dir));
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(Path.Combine(dir, "gui-error.log"), ex.ToString()); } catch { }
            MessageBox.Show("GeminiCapper failed to start: " + ex.Message, "GeminiCapper Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    public GeminiCapperForm(string dir)
    {
        toolDir = dir;
        configFile = Path.Combine(toolDir, "config.json");
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;

        Text = "GeminiCapper - Google Antigravity & Gemini Usage Capper";
        
        Icon appIcon = null;
        string iconPath = Path.Combine(toolDir, "assets\\icon.ico");
        if (File.Exists(iconPath))
        {
            try { appIcon = new Icon(iconPath); } catch { }
        }
        if (appIcon == null)
        {
            try { appIcon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        }
        if (appIcon == null)
        {
            appIcon = SystemIcons.Shield;
        }

        Icon = appIcon;
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        ClientSize = new Size(480, 422);
        Font = new Font("Segoe UI", 9f);

        IntPtr forceHandle = this.Handle;

        // ---- system tray icon & menu ----
        notifyIcon = new NotifyIcon();
        notifyIcon.Icon = appIcon;
        notifyIcon.Text = "GeminiCapper: Active";
        notifyIcon.Visible = true;

        trayMenu = new ContextMenuStrip();
        ToolStripMenuItem itemOpen = new ToolStripMenuItem("Open GeminiCapper", null, delegate { RestoreWindow(); });
        itemOpen.Font = new Font(itemOpen.Font, FontStyle.Bold);
        trayMenu.Items.Add(itemOpen);
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(new ToolStripMenuItem("Pause 30 min", null, delegate { SetPause(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1800); }));
        trayMenu.Items.Add(new ToolStripMenuItem("Pause 2 h", null, delegate { SetPause(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 7200); }));
        trayMenu.Items.Add(new ToolStripMenuItem("Pause until resumed", null, delegate { SetPause(-1); }));
        trayMenu.Items.Add(new ToolStripMenuItem("Resume", null, delegate { SetPause(0); }));
        trayMenu.Items.Add(new ToolStripSeparator());
        trayMenu.Items.Add(new ToolStripMenuItem("Exit", null, delegate { ExitApplication(); }));
        notifyIcon.ContextMenuStrip = trayMenu;

        notifyIcon.DoubleClick += delegate { RestoreWindow(); };
        notifyIcon.MouseClick += delegate(object sender, MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { RestoreWindow(); }
        };

        // ---- protection / pause ----
        GroupBox grpStatus = new GroupBox();
        grpStatus.Text = "Antigravity & Gemini Protection";
        grpStatus.SetBounds(12, 8, 456, 110);
        Controls.Add(grpStatus);

        lblStatus = new Label();
        lblStatus.SetBounds(12, 22, 430, 20);
        lblStatus.Font = new Font("Segoe UI", 9f, FontStyle.Bold);
        grpStatus.Controls.Add(lblStatus);

        Button btnPause30 = MakeButton(grpStatus, "Pause 30 min", 12, 50, 95);
        Button btnPause2h = MakeButton(grpStatus, "Pause 2 h", 115, 50, 95);
        Button btnPauseManual = MakeButton(grpStatus, "Pause until resumed", 218, 50, 136);
        Button btnResume = MakeButton(grpStatus, "Resume", 362, 50, 80);

        Label lblPauseNote = new Label();
        lblPauseNote.SetBounds(12, 84, 430, 18);
        lblPauseNote.Text = "While paused, rate limit and quota blocks are temporarily suspended.";
        lblPauseNote.ForeColor = Color.DimGray;
        grpStatus.Controls.Add(lblPauseNote);

        // ---- usage ----
        GroupBox grpUsage = new GroupBox();
        grpUsage.Text = "Live Model Quotas & Usage (/usage)";
        grpUsage.SetBounds(12, 126, 456, 112);
        Controls.Add(grpUsage);

        for (int i = 0; i < 2; i++)
        {
            int y = 24 + i * 28;
            rowName[i] = new Label();
            rowName[i].SetBounds(16, y, 160, 18);
            rowName[i].Visible = false;
            grpUsage.Controls.Add(rowName[i]);
            rowBar[i] = new ProgressBar();
            rowBar[i].SetBounds(180, y, 130, 18);
            rowBar[i].Minimum = 0;
            rowBar[i].Maximum = 100;
            rowBar[i].Visible = false;
            grpUsage.Controls.Add(rowBar[i]);
            rowPct[i] = new Label();
            rowPct[i].SetBounds(318, y, 126, 18);
            rowPct[i].Visible = false;
            grpUsage.Controls.Add(rowPct[i]);
        }

        Button btnRefresh = MakeButton(grpUsage, "Refresh", 368, 80, 76);
        btnRefresh.Height = 24;

        lblFetched = new Label();
        lblFetched.SetBounds(16, 84, 345, 16);
        lblFetched.ForeColor = Color.DimGray;
        grpUsage.Controls.Add(lblFetched);

        // ---- settings ----
        GroupBox grpSettings = new GroupBox();
        grpSettings.Text = "Settings (saved immediately)";
        grpSettings.SetBounds(12, 246, 456, 138);
        Controls.Add(grpSettings);

        Label lblThreshold = new Label();
        lblThreshold.Text = "Block when quota used reaches (%):";
        lblThreshold.SetBounds(12, 26, 220, 18);
        grpSettings.Controls.Add(lblThreshold);

        numThreshold = new NumericUpDown();
        numThreshold.SetBounds(240, 23, 60, 22);
        numThreshold.Minimum = 50;
        numThreshold.Maximum = 100;
        grpSettings.Controls.Add(numThreshold);

        chkPacing = new CheckBox();
        chkPacing.Text = "Pace daily/weekly quotas to prevent mid-cycle exhaustion";
        chkPacing.SetBounds(12, 56, 380, 20);
        grpSettings.Controls.Add(chkPacing);

        lblPoints = new Label();
        lblPoints.Text = "Allowed quota percent per day:";
        lblPoints.SetBounds(30, 84, 205, 18);
        grpSettings.Controls.Add(lblPoints);

        numPointsPerDay = new NumericUpDown();
        numPointsPerDay.SetBounds(240, 81, 60, 22);
        numPointsPerDay.Minimum = 1;
        numPointsPerDay.Maximum = 100;
        numPointsPerDay.DecimalPlaces = 1;
        numPointsPerDay.Increment = 0.5m;
        grpSettings.Controls.Add(numPointsPerDay);

        lblAllowedToday = new Label();
        lblAllowedToday.SetBounds(30, 110, 410, 18);
        lblAllowedToday.ForeColor = Color.DimGray;
        grpSettings.Controls.Add(lblAllowedToday);

        Label lblFooter = new Label();
        lblFooter.SetBounds(12, 394, 456, 18);
        lblFooter.Text = "Minimizing or closing hides to tray. Right-click tray icon to Exit.";
        lblFooter.ForeColor = Color.DimGray;
        Controls.Add(lblFooter);

        LoadConfig();

        btnPause30.Click += delegate { SetPause(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1800); };
        btnPause2h.Click += delegate { SetPause(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 7200); };
        btnPauseManual.Click += delegate { SetPause(-1); };
        btnResume.Click += delegate { SetPause(0); };
        btnRefresh.Click += delegate { RefreshUsage(); };
        numThreshold.ValueChanged += delegate { if (!loading) { SaveConfig(); UpdateAllowedToday(); } };
        numPointsPerDay.ValueChanged += delegate { if (!loading) { SaveConfig(); UpdateAllowedToday(); } };
        chkPacing.CheckedChanged += delegate
        {
            if (!loading) SaveConfig();
            numPointsPerDay.Enabled = chkPacing.Checked;
            lblPoints.Enabled = chkPacing.Checked;
            UpdateAllowedToday();
        };

        timer = new System.Windows.Forms.Timer();
        timer.Interval = 30000;
        timer.Tick += delegate { RefreshUsage(); UpdateStatusLabel(); };
        timer.Start();

        numPointsPerDay.Enabled = chkPacing.Checked;
        lblPoints.Enabled = chkPacing.Checked;
        loading = false;

        UpdateStatusLabel();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        userInitialized = true;
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        BringToFront();
        Activate();
        RefreshUsage();
    }

    void RestoreWindow()
    {
        Show();
        WindowState = FormWindowState.Normal;
        ShowInTaskbar = true;
        BringToFront();
        Activate();
    }

    void ExitApplication()
    {
        isExiting = true;
        if (notifyIcon != null)
        {
            notifyIcon.Visible = false;
            notifyIcon.Dispose();
        }
        Close();
        Application.Exit();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        if (userInitialized && WindowState == FormWindowState.Minimized)
        {
            Hide();
            ShowInTaskbar = false;
            ShowTrayBalloon();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!isExiting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            ShowInTaskbar = false;
            ShowTrayBalloon();
        }
        else
        {
            if (notifyIcon != null)
            {
                notifyIcon.Visible = false;
                notifyIcon.Dispose();
            }
            base.OnFormClosing(e);
        }
    }

    void ShowTrayBalloon()
    {
        if (!shownBalloon && notifyIcon != null)
        {
            notifyIcon.ShowBalloonTip(2500, "GeminiCapper Running in Background", "GeminiCapper is active in your system tray. Right-click the icon to pause or Exit.", ToolTipIcon.Info);
            shownBalloon = true;
        }
    }

    Button MakeButton(Control parent, string text, int x, int y, int w)
    {
        Button b = new Button();
        b.Text = text;
        b.SetBounds(x, y, w, 28);
        parent.Controls.Add(b);
        return b;
    }

    void LoadConfig()
    {
        double threshold = 90;
        bool pacingEnabled = false;
        double pointsPerDay = 14.3;
        pausedUntilEpoch = 0;
        try
        {
            if (File.Exists(configFile))
            {
                var j = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(File.ReadAllText(configFile));
                if (j.ContainsKey("threshold")) threshold = Convert.ToDouble(j["threshold"], CultureInfo.InvariantCulture);
                if (j.ContainsKey("pausedUntilEpoch")) pausedUntilEpoch = Convert.ToInt64(j["pausedUntilEpoch"], CultureInfo.InvariantCulture);
                if (j.ContainsKey("pacing") && j["pacing"] is Dictionary<string, object>)
                {
                    var p = (Dictionary<string, object>)j["pacing"];
                    if (p.ContainsKey("enabled")) pacingEnabled = Convert.ToBoolean(p["enabled"]);
                    if (p.ContainsKey("pointsPerDay")) pointsPerDay = Convert.ToDouble(p["pointsPerDay"], CultureInfo.InvariantCulture);
                }
            }
        }
        catch { }
        numThreshold.Value = (decimal)Math.Min(100, Math.Max(50, threshold));
        chkPacing.Checked = pacingEnabled;
        numPointsPerDay.Value = (decimal)Math.Min(100, Math.Max(1, pointsPerDay));
    }

    void SaveConfig()
    {
        var pacing = new Dictionary<string, object>();
        pacing["enabled"] = chkPacing.Checked;
        pacing["pointsPerDay"] = (double)numPointsPerDay.Value;
        var obj = new Dictionary<string, object>();
        obj["threshold"] = (double)numThreshold.Value;
        obj["pausedUntilEpoch"] = pausedUntilEpoch;
        obj["pacing"] = pacing;
        File.WriteAllText(configFile, new JavaScriptSerializer().Serialize(obj));
    }

    ArrayList FetchUsage()
    {
        ArrayList result = new ArrayList();
        string credJson = ReadCredential("gemini:antigravity");
        if (string.IsNullOrEmpty(credJson)) return result;

        try
        {
            var serializer = new JavaScriptSerializer();
            var authData = serializer.Deserialize<Dictionary<string, object>>(credJson);
            if (!authData.ContainsKey("token")) return result;
            var tokenObj = (Dictionary<string, object>)authData["token"];
            if (!tokenObj.ContainsKey("access_token")) return result;
            string token = tokenObj["access_token"].ToString();

            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("https://daily-cloudcode-pa.googleapis.com/v1internal:fetchAvailableModels");
            req.Method = "POST";
            req.ContentType = "application/json";
            req.Headers["Authorization"] = "Bearer " + token;
            req.UserAgent = "antigravity/1.0";
            req.Timeout = 8000;

            using (var streamWriter = new StreamWriter(req.GetRequestStream()))
            {
                streamWriter.Write("{}");
                streamWriter.Flush();
            }

            using (HttpWebResponse resp = (HttpWebResponse)req.GetResponse())
            using (var reader = new StreamReader(resp.GetResponseStream()))
            {
                string json = reader.ReadToEnd();
                var catalog = serializer.Deserialize<Dictionary<string, object>>(json);
                if (catalog.ContainsKey("models") && catalog["models"] is Dictionary<string, object>)
                {
                    var modelsMap = (Dictionary<string, object>)catalog["models"];
                    Dictionary<string, object> geminiQuota = null;
                    Dictionary<string, object> claudeQuota = null;

                    foreach (KeyValuePair<string, object> kvp in modelsMap)
                    {
                        string id = kvp.Key.ToLowerInvariant();
                        if (kvp.Value is Dictionary<string, object>)
                        {
                            var m = (Dictionary<string, object>)kvp.Value;
                            if (m.ContainsKey("quotaInfo") && m["quotaInfo"] is Dictionary<string, object>)
                            {
                                var qi = (Dictionary<string, object>)m["quotaInfo"];
                                if (qi.ContainsKey("remainingFraction"))
                                {
                                    if (id.Contains("gemini") && geminiQuota == null)
                                    {
                                        geminiQuota = qi;
                                    }
                                    else if (id.Contains("claude") && claudeQuota == null)
                                    {
                                        claudeQuota = qi;
                                    }
                                }
                            }
                        }
                    }

                    // 1. Gemini indicator (all Gemini models share usage)
                    result.Add(CreateQuotaEntry("Gemini", geminiQuota));

                    // 2. Claude indicator
                    result.Add(CreateQuotaEntry("Claude", claudeQuota));
                }
            }
        }
        catch { }

        return result;
    }

    Dictionary<string, object> CreateQuotaEntry(string name, Dictionary<string, object> qi)
    {
        double remainingPct = 100.0;
        string resetIn = "100% rem";

        if (qi != null)
        {
            if (qi.ContainsKey("remainingFraction"))
            {
                remainingPct = Convert.ToDouble(qi["remainingFraction"], CultureInfo.InvariantCulture) * 100.0;
            }
            if (qi.ContainsKey("resetTime"))
            {
                DateTime resetDt;
                if (DateTime.TryParse(qi["resetTime"].ToString(), out resetDt))
                {
                    TimeSpan diff = resetDt.ToUniversalTime() - DateTime.UtcNow;
                    if (diff.TotalSeconds > 0)
                    {
                        if (diff.TotalHours >= 1)
                            resetIn = string.Format("{0}h {1}m", (int)diff.TotalHours, diff.Minutes);
                        else
                            resetIn = string.Format("{0}m", diff.Minutes);
                    }
                }
            }
        }

        double usedPct = Math.Round(Math.Max(0, Math.Min(100, 100.0 - remainingPct)), 1);

        var entry = new Dictionary<string, object>();
        entry["name"] = name;
        entry["percent"] = usedPct;
        entry["remainingPercent"] = Math.Round(remainingPct, 1);
        entry["resetIn"] = resetIn;
        return entry;
    }

    void RefreshUsage()
    {
        lblFetched.Text = "Loading usage from AGY API...";
        ThreadPool.QueueUserWorkItem(delegate
        {
            try
            {
                ArrayList newLimits = FetchUsage();
                BeginInvoke((MethodInvoker)delegate
                {
                    limits = newLimits;
                    int i = 0;
                    if (limits != null && limits.Count > 0)
                    {
                        foreach (object o in limits)
                        {
                            if (i >= 2) break;
                            var limit = (Dictionary<string, object>)o;
                            string name = limit["name"].ToString();
                            double usedPct = Convert.ToDouble(limit["percent"], CultureInfo.InvariantCulture);
                            double remPct = Convert.ToDouble(limit["remainingPercent"], CultureInfo.InvariantCulture);
                            string resetIn = limit.ContainsKey("resetIn") ? limit["resetIn"].ToString() : "";

                            rowName[i].Text = name;
                            rowBar[i].Value = (int)Math.Min(100, Math.Max(0, usedPct));
                            rowPct[i].Text = string.Format("{0}% used ({1})", usedPct.ToString("0.#", CultureInfo.InvariantCulture), resetIn);
                            rowName[i].Visible = rowBar[i].Visible = rowPct[i].Visible = true;
                            i++;
                        }
                    }
                    for (; i < 2; i++) rowName[i].Visible = rowBar[i].Visible = rowPct[i].Visible = false;
                    lblFetched.Text = "Updated " + DateTime.Now.ToString("HH:mm:ss") + " (/usage synchronized)";
                    UpdateAllowedToday();
                });
            }
            catch (Exception ex)
            {
                BeginInvoke((MethodInvoker)delegate
                {
                    lblFetched.Text = "Could not load usage: " + ex.Message;
                    UpdateAllowedToday();
                });
            }
        });
    }

    void UpdateAllowedToday()
    {
        if (!chkPacing.Checked) { lblAllowedToday.Text = ""; return; }
        int dayNumber = ((int)DateTime.UtcNow.DayOfWeek == 0) ? 7 : (int)DateTime.UtcNow.DayOfWeek;
        double allowed = Math.Min((double)numThreshold.Value, (double)numPointsPerDay.Value * dayNumber);
        lblAllowedToday.Text = string.Format(CultureInfo.InvariantCulture,
            "Allowed so far (day {0} of 7): {1}% of quota.", dayNumber, Math.Round(allowed, 1));
    }

    void UpdateStatusLabel()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (pausedUntilEpoch == -1)
        {
            lblStatus.Text = "PAUSED until you press Resume";
            lblStatus.ForeColor = Color.Firebrick;
        }
        else if (pausedUntilEpoch > now)
        {
            string until = DateTimeOffset.FromUnixTimeSeconds(pausedUntilEpoch).ToLocalTime().ToString("HH:mm");
            lblStatus.Text = "PAUSED until " + until;
            lblStatus.ForeColor = Color.Firebrick;
        }
        else
        {
            if (pausedUntilEpoch != 0) { pausedUntilEpoch = 0; SaveConfig(); }
            lblStatus.Text = "Active: Antigravity & Gemini requests protected";
            lblStatus.ForeColor = Color.ForestGreen;
        }

        if (notifyIcon != null)
        {
            string statusText = (pausedUntilEpoch == -1) ? "PAUSED (manual)" : (pausedUntilEpoch > now) ? "PAUSED" : "Active";
            string text = "GeminiCapper: " + statusText;
            if (text.Length > 63) text = text.Substring(0, 63);
            notifyIcon.Text = text;
        }
    }

    void SetPause(long untilEpoch)
    {
        pausedUntilEpoch = untilEpoch;
        SaveConfig();
        UpdateStatusLabel();
    }
}
