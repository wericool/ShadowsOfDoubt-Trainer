using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace SoDTrainer
{
    internal sealed class MainForm : Form
    {
        // ---------- палитра ----------
        static readonly Color Bg = Color.FromArgb(24, 26, 31);
        static readonly Color Panel = Color.FromArgb(33, 36, 43);
        static readonly Color Panel2 = Color.FromArgb(42, 46, 55);
        static readonly Color Accent = Color.FromArgb(230, 168, 60);
        static readonly Color Fg = Color.FromArgb(232, 232, 235);
        static readonly Color Muted = Color.FromArgb(140, 146, 158);
        static readonly Color Good = Color.FromArgb(110, 200, 120);
        static readonly Color Bad = Color.FromArgb(225, 95, 85);

        readonly GameLink g = new GameLink();
        readonly Timer fast = new Timer { Interval = 100 };
        readonly Timer slow = new Timer { Interval = 600 };
        readonly ToolTip tip = new ToolTip { AutoPopDelay = 12000 };

        Label statusLabel, killerLabel;
        Panel statusDot;
        RichTextBox caseBox, logBox;
        string lastReport = "";
        bool suppress;

        readonly Dictionary<string, Panel> pages = new Dictionary<string, Panel>();
        readonly Dictionary<string, Button> navButtons = new Dictionary<string, Button>();
        readonly List<Action> refreshers = new List<Action>();

        public MainForm()
        {
            Text = "Shadows of Doubt - Трейнер";
            Size = new Size(1000, 700);
            MinimumSize = new Size(940, 620);
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Bg;
            ForeColor = Fg;
            Font = new Font("Segoe UI", 10f);

            BuildLayout();
            ShowPage("case");

            fast.Tick += delegate { g.Tick(); };
            slow.Tick += delegate { SlowTick(); };
            fast.Start();
            slow.Start();
            FormClosed += delegate { g.Detach(); };
        }

        internal void CaptureShots(string dir)
        {
            TopMost = true;
            Show();
            for (int i = 0; i < 60; i++)
            {
                Application.DoEvents();
                System.Threading.Thread.Sleep(120);
            }
            foreach (var key in pages.Keys.ToList())
            {
                ShowPage(key);
                Application.DoEvents();
                System.Threading.Thread.Sleep(300);
                Application.DoEvents();
                using (var bmp = new Bitmap(Width, Height))
                {
                    using (var gr = Graphics.FromImage(bmp)) gr.CopyFromScreen(Left, Top, 0, 0, new Size(Width, Height));
                    bmp.Save(Path.Combine(dir, key + ".png"), System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }

        // =====================================================================
        //  Каркас
        // =====================================================================
        void BuildLayout()
        {
            var side = new Panel { Dock = DockStyle.Left, Width = 190, BackColor = Panel };
            var title = new Label
            {
                Text = "SHADOWS\nOF DOUBT",
                Font = new Font("Segoe UI Semibold", 15f),
                ForeColor = Accent,
                Dock = DockStyle.Top,
                Height = 78,
                Padding = new Padding(18, 18, 0, 0)
            };
            var sub = new Label
            {
                Text = "ТРЕЙНЕР",
                Font = new Font("Segoe UI", 9f),
                ForeColor = Muted,
                Dock = DockStyle.Top,
                Height = 24,
                Padding = new Padding(20, 0, 0, 0)
            };
            side.Controls.Add(MakeNav("log", "Журнал"));
            side.Controls.Add(MakeNav("cheats", "Читы"));
            side.Controls.Add(MakeNav("player", "Игрок"));
            side.Controls.Add(MakeNav("case", "Дело"));
            side.Controls.Add(sub);
            side.Controls.Add(title);

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 34, BackColor = Panel };
            statusDot = new Panel { Left = 14, Top = 12, Width = 10, Height = 10, BackColor = Bad };
            statusLabel = new Label { Left = 32, Top = 7, AutoSize = true, ForeColor = Muted };
            bottom.Controls.Add(statusDot);
            bottom.Controls.Add(statusLabel);

            Controls.Add(MakeCasePage());
            Controls.Add(MakePlayerPage());
            Controls.Add(MakeCheatsPage());
            Controls.Add(MakeLogPage());
            Controls.Add(bottom);
            Controls.Add(side);
        }

        Button MakeNav(string key, string text)
        {
            var b = new Button
            {
                Text = "   " + text,
                Dock = DockStyle.Top,
                Height = 46,
                FlatStyle = FlatStyle.Flat,
                TextAlign = ContentAlignment.MiddleLeft,
                BackColor = Panel,
                ForeColor = Fg,
                Font = new Font("Segoe UI", 11f),
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderSize = 0;
            b.FlatAppearance.MouseOverBackColor = Panel2;
            b.Click += delegate { ShowPage(key); };
            navButtons[key] = b;
            return b;
        }

        void ShowPage(string key)
        {
            foreach (var kv in pages) kv.Value.Visible = kv.Key == key;
            foreach (var kv in navButtons)
            {
                bool on = kv.Key == key;
                kv.Value.BackColor = on ? Panel2 : Panel;
                kv.Value.ForeColor = on ? Accent : Fg;
            }
        }

        Panel NewPage(string key)
        {
            var p = new Panel { Dock = DockStyle.Fill, BackColor = Bg, AutoScroll = true, Padding = new Padding(20, 16, 20, 16) };
            pages[key] = p;
            return p;
        }

        // =====================================================================
        //  Страница "Дело"
        // =====================================================================
        Panel MakeCasePage()
        {
            var p = NewPage("case");

            caseBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Panel,
                ForeColor = Fg,
                Font = new Font("Consolas", 11f),
                DetectUrls = false
            };

            var top = new Panel { Dock = DockStyle.Top, Height = 118 };
            var cap = new Label { Text = "ИМЯ УБИЙЦЫ", ForeColor = Muted, Left = 0, Top = 0, AutoSize = true, Font = new Font("Segoe UI", 9f) };
            killerLabel = new Label
            {
                Text = "-",
                ForeColor = Accent,
                Left = 0,
                Top = 20,
                AutoSize = true,
                Font = new Font("Segoe UI Semibold", 24f)
            };
            var copy = Btn("Копировать имя", 0, 78, 160);
            copy.Click += delegate
            {
                if (!string.IsNullOrEmpty(g.LastKillerName))
                {
                    try
                    {
                        Clipboard.SetText(g.LastKillerName);
                    }
                    catch
                    {
                    }
                }
            };
            top.Controls.Add(cap);
            top.Controls.Add(killerLabel);
            top.Controls.Add(copy);

            var spacer = new Panel { Dock = DockStyle.Top, Height = 8 };
            p.Controls.Add(caseBox);
            p.Controls.Add(spacer);
            p.Controls.Add(top);
            p.AutoScroll = false;
            return p;
        }

        void RenderReport(List<ReportLine> lines)
        {
            var sb = new StringBuilder();
            foreach (var l in lines) sb.Append((int)l.Kind).Append('|').Append(l.Key).Append('|').Append(l.Value).Append('\n');
            string sig = sb.ToString();
            if (sig == lastReport) return;
            lastReport = sig;

            caseBox.SuspendLayout();
            caseBox.Clear();
            foreach (var l in lines)
            {
                switch (l.Kind)
                {
                    case LineKind.Heading:
                        Append(l.Value + "\n", Accent, true);
                        break;
                    case LineKind.Pair:
                        Append(("  " + l.Key + ":").PadRight(18), Muted, false);
                        Append(l.Value + "\n", Fg, false);
                        break;
                    case LineKind.Text:
                        Append("  " + l.Value + "\n", Muted, false);
                        break;
                    default:
                        Append("\n", Fg, false);
                        break;
                }
            }
            caseBox.ResumeLayout();
        }

        void Append(string s, Color c, bool bold)
        {
            caseBox.SelectionStart = caseBox.TextLength;
            caseBox.SelectionLength = 0;
            caseBox.SelectionColor = c;
            caseBox.SelectionFont = new Font("Consolas", 11f, bold ? FontStyle.Bold : FontStyle.Regular);
            caseBox.AppendText(s);
        }

        // =====================================================================
        //  Страница "Игрок"
        // =====================================================================
        Panel MakePlayerPage()
        {
            var p = NewPage("player");
            int y = 0;
            p.Controls.Add(Header("РЕСУРСЫ", ref y));
            AddResource(p, ref y, "Деньги", "GameplayController", "money", new[] { 1000, 10000, 100000 });
            AddResource(p, ref y, "Отмычки", "GameplayController", "lockPicks", new[] { 5, 20, 100 });
            AddResource(p, ref y, "Социальный рейтинг", "GameplayController", "socialCredit", new[] { 10, 100, 1000 });

            y += 10;
            p.Controls.Add(Header("ПЕРСОНАЖ", ref y));
            var b1 = Btn("Полное здоровье", 0, y, 190);
            var b2 = Btn("Пополнить потребности", 200, y, 210);
            var b3 = Btn("Снять недуги", 420, y, 160);
            b1.Click += delegate { g.HealPlayer(); };
            b2.Click += delegate { g.FillNeeds(); };
            b3.Click += delegate { g.ClearAilments(); };
            tip.SetToolTip(b2, "Еда, вода, бодрость, энергия, гигиена и т.д. = максимум");
            tip.SetToolTip(b3, "Опьянение, болезнь, головная боль, кровотечение, травмы и т.д. = 0");
            p.Controls.Add(b1);
            p.Controls.Add(b2);
            p.Controls.Add(b3);
            y += 48;

            y = AddFreezeCheck(p, y, "god", "Бессмертие (здоровье и нервы всегда полные)",
                "Каждые 0,1 с восстанавливает здоровье и нервы.",
                () =>
                {
                    g.HealPlayer();
                });
            y = AddFreezeCheck(p, y, "illegal", "Без нелегальных действий",
                "Игра не считает ваши действия незаконными (никто не вызовет полицию за взлом, обыск и т.п.).",
                () =>
                {
                    ulong pl = g.PlayerObj;
                    if (pl == 0) return;
                    g.Il.SetBool(pl, "illegalActionActive", false);
                    g.Il.SetBool(pl, "illegalAreaActive", false);
                });
            y = AddFreezeCheck(p, y, "survival", "Без голода, жажды, усталости и болезней",
                "Отключает статусы выживания (голод, жажда, усталость, простуда, головная боль и т.д.).",
                () =>
                {
                    foreach (var f in new[]
                    {
                        "coldStatusEnabled", "smellyStatusEnabled", "headacheStatusEnabled", "injuryStatusEnabled",
                        "tiredStatusEnabled", "hungerStatusEnabled", "hydrationStatusEnabled", "numbStatusEnabled",
                        "bleedingStatusEnabled", "wetStatusEnabled", "sickStatusEnabled", "drunkStatusEnabled",
                        "poisonStatusEnabled", "blindedStatusEnabled"
                    })
                        g.SetBool("Game", f, false);
                });
            return p;
        }

        Control Header(string text, ref int y)
        {
            var l = new Label { Text = text, ForeColor = Accent, Left = 0, Top = y, AutoSize = true, Font = new Font("Segoe UI Semibold", 10f) };
            y += 30;
            return l;
        }

        Button Btn(string text, int x, int y, int w)
        {
            var b = new Button
            {
                Text = text,
                Left = x,
                Top = y,
                Width = w,
                Height = 36,
                FlatStyle = FlatStyle.Flat,
                BackColor = Panel2,
                ForeColor = Fg,
                Cursor = Cursors.Hand
            };
            b.FlatAppearance.BorderColor = Color.FromArgb(70, 75, 88);
            b.FlatAppearance.MouseOverBackColor = Color.FromArgb(60, 65, 78);
            return b;
        }

        TextBox Box(int x, int y, int w, string text)
        {
            return new TextBox
            {
                Left = x,
                Top = y,
                Width = w,
                Text = text,
                BackColor = Panel2,
                ForeColor = Fg,
                BorderStyle = BorderStyle.FixedSingle,
                Font = new Font("Segoe UI", 11f)
            };
        }

        CheckBox Check(string text, int x, int y, int w)
        {
            return new CheckBox { Text = text, Left = x, Top = y, Width = w, Height = 28, ForeColor = Fg, Cursor = Cursors.Hand };
        }

        void AddResource(Panel p, ref int y, string title, string cls, string field, int[] quick)
        {
            var name = new Label { Text = title, Left = 0, Top = y + 4, Width = 160, ForeColor = Fg };
            var cur = new Label { Text = "-", Left = 160, Top = y + 2, Width = 100, ForeColor = Accent, Font = new Font("Segoe UI Semibold", 12f) };
            var box = Box(265, y, 100, "1000");
            var set = Btn("Установить", 375, y - 3, 105);
            var add = Btn("+ Добавить", 487, y - 3, 105);
            var frz = Check("Заморозить", 602, y, 120);
            tip.SetToolTip(frz, "Значение не будет меняться, пока галочка стоит (удерживается на указанном числе).");
            set.Click += delegate
            {
                int v;
                if (ParseInt(box.Text, out v)) g.SetInt(cls, field, v);
            };
            add.Click += delegate
            {
                int v;
                if (ParseInt(box.Text, out v)) g.AddInt(cls, field, v);
            };
            string key = cls + "." + field;
            frz.CheckedChanged += delegate
            {
                if (suppress) return;
                if (frz.Checked)
                {
                    int v;
                    if (!ParseInt(box.Text, out v)) v = g.GetInt(cls, field);
                    box.Text = v.ToString();
                    g.Freeze(key, () => g.SetInt(cls, field, v));
                }
                else g.Unfreeze(key);
            };
            p.Controls.Add(name);
            p.Controls.Add(cur);
            p.Controls.Add(box);
            p.Controls.Add(set);
            p.Controls.Add(add);
            p.Controls.Add(frz);
            refreshers.Add(delegate { cur.Text = g.Game == 0 ? "-" : g.GetInt(cls, field).ToString("N0", CultureInfo.CurrentCulture); });
            y += 44;
            int qx = 265;
            foreach (int q in quick)
            {
                int amount = q;
                var qb = new Button
                {
                    Text = "+" + amount.ToString("N0"),
                    Left = qx,
                    Top = y - 4,
                    Width = 92,
                    Height = 26,
                    FlatStyle = FlatStyle.Flat,
                    BackColor = Panel,
                    ForeColor = Muted,
                    Cursor = Cursors.Hand
                };
                qb.FlatAppearance.BorderColor = Color.FromArgb(60, 65, 78);
                qb.Click += delegate { g.AddInt(cls, field, amount); };
                p.Controls.Add(qb);
                qx += 98;
            }
            y += 34;
        }

        int AddFreezeCheck(Panel p, int y, string key, string text, string hint, Action apply)
        {
            var cb = Check(text, 0, y, 640);
            tip.SetToolTip(cb, hint);
            cb.CheckedChanged += delegate
            {
                if (suppress) return;
                if (cb.Checked) g.Freeze(key, apply);
                else g.Unfreeze(key);
            };
            p.Controls.Add(cb);
            return y + 32;
        }

        static bool ParseInt(string s, out int v)
        {
            s = (s ?? "").Replace(" ", "").Replace(" ", "");
            long l;
            if (long.TryParse(s, NumberStyles.Integer, CultureInfo.CurrentCulture, out l) ||
                long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out l))
            {
                v = (int)Math.Max(int.MinValue, Math.Min(int.MaxValue, l));
                return true;
            }
            v = 0;
            return false;
        }

        static bool ParseFloat(string s, out float v)
        {
            s = (s ?? "").Trim();
            return float.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out v) ||
                   float.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out v);
        }

        // =====================================================================
        //  Страница "Читы"
        // =====================================================================
        sealed class Flag
        {
            public string Field, Label, Hint;
            public bool Experimental;

            public Flag(string f, string l, string h, bool exp = false)
            {
                Field = f;
                Label = l;
                Hint = h;
                Experimental = exp;
            }
        }

        Panel MakeCheatsPage()
        {
            var p = NewPage("cheats");
            int y = 0;
            p.Controls.Add(Header("ПЕРЕКЛЮЧАТЕЛИ", ref y));

            var flags = new[]
            {
                new Flag("invinciblePlayer", "Неуязвимость", "Игрок не получает урон."),
                new Flag("invisiblePlayer", "Невидимость для NPC", "NPC и полиция вас не замечают."),
                new Flag("inaudiblePlayer", "Бесшумность", "NPC вас не слышат."),
                new Flag("disableFallDamage", "Без урона от падения", "Падение с высоты безопасно."),
                new Flag("pauseAI", "Заморозить всех NPC", "Пауза ИИ: жители и полиция стоят на месте."),
                new Flag("disableTrespass", "Можно входить куда угодно", "Отключает нарушение границ (trespass)."),
                new Flag("alwaysRun", "Всегда бежать", "Постоянный бег без зажатия клавиши."),
                new Flag("keysToTheCity", "Ключи от всего города", "Все двери открываются как своими ключами."),
                new Flag("noReactOnAttack", "NPC не реагируют на нападение", "Жители не реагируют на атаки (отладочная опция игры)."),
                new Flag("allHospitalAccess", "Доступ в больницы", "Открывает больничные зоны."),
                new Flag("discoverAllRooms", "Открыть все комнаты на карте", "Отладочная опция игры.", true),
                new Flag("discoverAllEvidence", "Раскрыть все улики", "Отладочная опция игры. Может нарушить прогресс дела.", true),
                new Flag("giveAllUpgrades", "Все улучшения", "Отладочная опция игры.", true),
                new Flag("freeCam", "Свободная камера", "Отладочная опция игры.", true),
                new Flag("fastForward", "Ускорение времени", "Отладочная опция игры.", true)
            };

            int col = 0, startY = y;
            foreach (var f in flags)
            {
                var fl = f;
                var cb = Check(fl.Label + (fl.Experimental ? "  (экспер.)" : ""), col * 340, y, 330);
                tip.SetToolTip(cb, fl.Hint);
                cb.CheckedChanged += delegate
                {
                    if (suppress) return;
                    g.SetBool("Game", fl.Field, cb.Checked);
                };
                p.Controls.Add(cb);
                refreshers.Add(delegate
                {
                    bool has = g.Game != 0 && g.HasField("Game", fl.Field);
                    cb.Enabled = has;
                    if (has)
                    {
                        bool v = g.GetBool("Game", fl.Field);
                        if (cb.Checked != v) cb.Checked = v;
                    }
                });
                if (col == 1)
                {
                    col = 0;
                    y += 32;
                }
                else col = 1;
            }
            if (col == 1) y += 32;
            y += 10;

            p.Controls.Add(Header("ЗНАЧЕНИЯ", ref y));
            AddFloatRow(p, ref y, "Скорость движения", "Game", "movementSpeed", 1f, "Множитель скорости игрока (1 = обычная).");
            AddFloatRow(p, ref y, "Сила отмычки", "GameplayController", "currentLockpickStrength", 1f, "Чем больше, тем быстрее взлом (1 = обычная).");
            AddFloatRow(p, ref y, "Множитель наград за дела", "Game", "jobRewardMultiplier", 1.2f, "Умножает денежную награду за побочные дела.");

            // код-пароль
            var l = new Label { Text = "Фиксированный код замков", Left = 0, Top = y + 4, Width = 230, ForeColor = Fg };
            var tb = Box(235, y, 90, "1000");
            var ap = Btn("Применить", 335, y - 3, 110);
            var cbP = Check("Включить", 455, y, 120);
            tip.SetToolTip(cbP, "Все кодовые замки открываются указанным 4-значным кодом.");
            ap.Click += delegate
            {
                int v;
                if (ParseInt(tb.Text, out v)) g.SetInt("Game", "overriddenPasscode", v);
            };
            cbP.CheckedChanged += delegate
            {
                if (suppress) return;
                int v;
                if (ParseInt(tb.Text, out v)) g.SetInt("Game", "overriddenPasscode", v);
                g.SetBool("Game", "overridePasscodes", cbP.Checked);
            };
            p.Controls.Add(l);
            p.Controls.Add(tb);
            p.Controls.Add(ap);
            p.Controls.Add(cbP);
            refreshers.Add(delegate
            {
                if (g.Game == 0) return;
                bool v = g.GetBool("Game", "overridePasscodes");
                if (cbP.Checked != v) cbP.Checked = v;
            });
            y += 46;
            return p;
        }

        void AddFloatRow(Panel p, ref int y, string title, string cls, string field, float defVal, string hint)
        {
            var l = new Label { Text = title, Left = 0, Top = y + 4, Width = 230, ForeColor = Fg };
            var cur = new Label { Text = "-", Left = 235, Top = y + 4, Width = 70, ForeColor = Accent };
            var tb = Box(310, y, 80, defVal.ToString("0.##", CultureInfo.CurrentCulture));
            var ap = Btn("Применить", 400, y - 3, 110);
            var rs = Btn("Сброс", 520, y - 3, 90);
            tip.SetToolTip(l, hint);
            ap.Click += delegate
            {
                float v;
                if (ParseFloat(tb.Text, out v)) g.SetFloat(cls, field, v);
            };
            rs.Click += delegate
            {
                g.SetFloat(cls, field, defVal);
                tb.Text = defVal.ToString("0.##", CultureInfo.CurrentCulture);
            };
            p.Controls.Add(l);
            p.Controls.Add(cur);
            p.Controls.Add(tb);
            p.Controls.Add(ap);
            p.Controls.Add(rs);
            refreshers.Add(delegate
            {
                bool ok = g.Game != 0 && g.HasField(cls, field);
                cur.Text = ok ? g.GetFloat(cls, field).ToString("0.##", CultureInfo.CurrentCulture) : "-";
                ap.Enabled = rs.Enabled = ok;
            });
            y += 44;
        }

        // =====================================================================
        //  Страница "Журнал"
        // =====================================================================
        Panel MakeLogPage()
        {
            var p = NewPage("log");
            p.AutoScroll = false;
            logBox = new RichTextBox
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                BorderStyle = BorderStyle.None,
                BackColor = Panel,
                ForeColor = Muted,
                Font = new Font("Consolas", 10f)
            };
            var top = new Panel { Dock = DockStyle.Top, Height = 50 };
            var dump = Btn("Сохранить диагностику в файл", 0, 4, 260);
            dump.Click += delegate
            {
                try
                {
                    string path = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "diagnostic.txt");
                    File.WriteAllText(path, g.DiagnosticDump(), Encoding.UTF8);
                    g.Log("Диагностика сохранена: " + path);
                }
                catch (Exception e)
                {
                    g.Log("Не удалось сохранить диагностику: " + e.Message);
                }
            };
            top.Controls.Add(dump);
            p.Controls.Add(logBox);
            p.Controls.Add(top);
            return p;
        }

        // =====================================================================
        //  Обновление
        // =====================================================================
        void SlowTick()
        {
            try
            {
                string line;
                while (g.LogQueue.TryDequeue(out line)) logBox.AppendText(line + "\n");

                statusLabel.Text = g.Status ?? "";
                bool ready = g.Attached && g.Game != 0;
                statusDot.BackColor = ready ? Good : (g.Attached ? Accent : Bad);

                suppress = true;
                try
                {
                    foreach (var r in refreshers) r();
                }
                finally
                {
                    suppress = false;
                }

                var report = g.BuildReport();
                RenderReport(report);
                killerLabel.Text = string.IsNullOrEmpty(g.LastKillerName) ? "-" : g.LastKillerName;
            }
            catch (Exception e)
            {
                statusLabel.Text = "Ошибка обновления: " + e.Message;
            }
        }
    }
}
