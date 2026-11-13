using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace SoDTrainer
{
    internal enum LineKind { Heading, Pair, Text, Gap }

    internal struct ReportLine
    {
        public LineKind Kind;
        public string Key;
        public string Value;

        public ReportLine(LineKind k, string key, string val)
        {
            Kind = k;
            Key = key;
            Value = val;
        }
    }

    /// <summary>Связь с процессом игры: подключение, поиск синглтонов, чтение/запись значений, "заморозка" значений.</summary>
    internal sealed class GameLink
    {
        public const string ProcessName = "Shadows of Doubt";
        static readonly string[] EntryClasses = { "Game", "GameplayController", "Player", "MurderController", "CasePanelController" };

        ProcMem mem;
        Il2 il;
        int pid;
        DateTime lastScan = DateTime.MinValue;
        volatile bool scanning;

        public readonly ConcurrentQueue<string> LogQueue = new ConcurrentQueue<string>();
        readonly Dictionary<string, Action> freezes = new Dictionary<string, Action>();

        public bool Attached
        {
            get { return mem != null && mem.Alive; }
        }

        public bool Scanning
        {
            get { return scanning; }
        }

        public ProcMem Mem
        {
            get { return mem; }
        }

        public Il2 Il
        {
            get { return il; }
        }

        public void Log(string s)
        {
            LogQueue.Enqueue(DateTime.Now.ToString("HH:mm:ss") + "  " + s);
        }

        // ---------- подключение ----------
        public void Detach()
        {
            if (mem != null)
            {
                mem.Dispose();
                Log("Игра закрыта - отключаюсь.");
            }
            mem = null;
            il = null;
            pid = 0;
        }

        public string Status { get; private set; }

        /// <summary>Вызывать часто (каждые ~100 мс): подключение, фоновый скан, применение заморозок.</summary>
        public void Tick()
        {
            try
            {
                if (mem != null && !mem.Alive) Detach();
                if (mem == null)
                {
                    Process p = null;
                    try
                    {
                        p = Process.GetProcessesByName(ProcessName).FirstOrDefault(x => !x.HasExited);
                    }
                    catch
                    {
                    }
                    if (p == null)
                    {
                        Status = "Игра не запущена. Запустите Shadows of Doubt.";
                        return;
                    }
                    try
                    {
                        mem = new ProcMem(p);
                    }
                    catch (Exception e)
                    {
                        Status = e.Message;
                        return;
                    }
                    pid = p.Id;
                    il = new Il2(mem);
                    il.Log = Log;
                    lastScan = DateTime.MinValue;
                    Log("Подключено к игре (PID " + pid + ").");
                }

                if (!scanning && il.Missing(EntryClasses) && (DateTime.Now - lastScan).TotalSeconds > 8)
                {
                    lastScan = DateTime.Now;
                    scanning = true;
                    var il2 = il;
                    Task.Run(() =>
                    {
                        try
                        {
                            var sw = Stopwatch.StartNew();
                            int n = il2.Scan(EntryClasses);
                            Log("Поиск классов игры: найдено " + n + " за " + sw.ElapsedMilliseconds + " мс.");
                            var miss = EntryClasses.Where(c => !il2.ByName.ContainsKey(c)).ToArray();
                            if (miss.Length > 0)
                                Log("Пока не найдены: " + string.Join(", ", miss) + " (появятся после загрузки сохранения).");
                        }
                        catch (Exception e)
                        {
                            Log("Ошибка поиска: " + e.Message);
                        }
                        finally
                        {
                            scanning = false;
                        }
                    });
                }

                if (scanning) Status = "Сканирую память игры...";
                else if (Game == 0) Status = "Подключено. Загрузите сохранение (игра на главном меню).";
                else Status = "Подключено к игре. Всё работает.";

                if (Game != 0)
                {
                    foreach (var a in freezes.Values.ToArray())
                    {
                        try
                        {
                            a();
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Status = "Ошибка: " + e.Message;
            }
        }

        // ---------- синглтоны ----------
        ulong Inst(string cls)
        {
            if (!Attached || il == null) return 0;
            ulong o = il.Singleton(cls);
            if (o == 0) return 0;
            var c = il.Class(cls);
            return (c != null && mem.U64(o) == c.Ptr) ? o : 0;
        }

        public ulong Game { get { return Inst("Game"); } }
        public ulong Gpc { get { return Inst("GameplayController"); } }
        public ulong PlayerObj { get { return Inst("Player"); } }
        public ulong Murder { get { return Inst("MurderController"); } }
        public ulong CasePanel { get { return Inst("CasePanelController"); } }

        public ulong ObjOf(string cls)
        {
            return Inst(cls);
        }

        // ---------- значения ----------
        public bool HasField(string cls, string field)
        {
            ulong o = Inst(cls);
            return o != 0 && il.FieldOf(o, field) != null;
        }

        public bool GetBool(string cls, string field)
        {
            ulong o = Inst(cls);
            return o != 0 && il.Bool(o, field);
        }

        public void SetBool(string cls, string field, bool v)
        {
            ulong o = Inst(cls);
            if (o != 0) il.SetBool(o, field, v);
        }

        public int GetInt(string cls, string field)
        {
            ulong o = Inst(cls);
            return o == 0 ? 0 : il.Int(o, field);
        }

        public void SetInt(string cls, string field, int v)
        {
            ulong o = Inst(cls);
            if (o != 0) il.SetInt(o, field, v);
        }

        public float GetFloat(string cls, string field)
        {
            ulong o = Inst(cls);
            return o == 0 ? 0 : il.Float(o, field);
        }

        public void SetFloat(string cls, string field, float v)
        {
            ulong o = Inst(cls);
            if (o != 0) il.SetFloat(o, field, v);
        }

        public void AddInt(string cls, string field, int delta)
        {
            long v = (long)GetInt(cls, field) + delta;
            if (v > int.MaxValue) v = int.MaxValue;
            if (v < int.MinValue) v = int.MinValue;
            SetInt(cls, field, (int)v);
        }

        // ---------- заморозка значений ----------
        public bool IsFrozen(string key)
        {
            return freezes.ContainsKey(key);
        }

        public void Freeze(string key, Action apply)
        {
            freezes[key] = apply;
        }

        public void Unfreeze(string key)
        {
            freezes.Remove(key);
        }

        // ---------- действия игрока ----------
        public void HealPlayer()
        {
            ulong p = PlayerObj;
            if (p == 0) return;
            float max = il.Float(p, "maximumHealth");
            if (max <= 0) max = 1;
            il.SetFloat(p, "currentHealth", max);
            float nerve = il.Float(p, "maxNerve");
            if (nerve > 0) il.SetFloat(p, "currentNerve", nerve);
        }

        public void FillNeeds()
        {
            ulong p = PlayerObj;
            if (p == 0) return;
            foreach (var f in new[] { "nourishment", "hydration", "alertness", "energy", "hygiene", "bladder", "breath", "excitement" })
                il.SetFloat(p, f, 1f);
        }

        public void ClearAilments()
        {
            ulong p = PlayerObj;
            if (p == 0) return;
            foreach (var f in new[] { "drunk", "sick", "headache", "wet", "brokenLeg", "bruised", "blackEye", "numb", "poisoned", "bleeding" })
                il.SetFloat(p, f, 0f);
        }

        // ---------- отчёт по делу ----------
        string EvName(ulong obj)
        {
            ulong ev = il.Ptr(obj, "evidenceEntry");
            return ev == 0 ? null : il.Str(ev, "name");
        }

        string PlaceName(ulong loc)
        {
            if (loc == 0) return null;
            string n = EvName(loc);
            if (!string.IsNullOrEmpty(n)) return n;
            ulong b = il.Ptr(loc, "building");
            string bn = b == 0 ? null : EvName(b);
            return bn;
        }

        string Describe(ulong human)
        {
            string n = il.Str(human, "citizenName");
            return string.IsNullOrEmpty(n) ? "(неизвестно)" : n;
        }

        string WhereNow(ulong human)
        {
            if (il.Bool(human, "isDead")) return "мёртв";
            ulong bld = il.Ptr(human, "currentBuilding");
            ulong loc = il.Ptr(human, "currentGameLocation");
            ulong room = il.Ptr(human, "currentRoom");
            var parts = new List<string>();
            string ln = PlaceName(loc);
            if (!string.IsNullOrEmpty(ln)) parts.Add(ln);
            string bn = bld == 0 ? null : EvName(bld);
            if (!string.IsNullOrEmpty(bn) && (ln == null || ln.IndexOf(bn, StringComparison.Ordinal) < 0)) parts.Add(bn);
            if (parts.Count == 0)
            {
                if (loc != 0 && il.Bool(loc, "isOutside")) parts.Add("на улице");
                else parts.Add("неизвестно");
            }
            string rn = room == 0 ? null : il.Str(room, "name");
            if (!string.IsNullOrEmpty(rn) && rn != "controller" && rn != "null") parts.Add("комната: " + rn);
            string s = string.Join(", ", parts);
            if (il.Bool(human, "isHome")) s += "  [дома]";
            else if (il.Bool(human, "isAtWork")) s += "  [на работе]";
            return s;
        }

        void AddHuman(List<ReportLine> r, ulong h, bool detailed)
        {
            r.Add(new ReportLine(LineKind.Pair, "Имя", Describe(h)));
            if (!detailed) return;
            string home = PlaceName(il.Ptr(h, "home"));
            r.Add(new ReportLine(LineKind.Pair, "Дом", string.IsNullOrEmpty(home) ? "-" : home));
            ulong job = il.Ptr(h, "job");
            if (job != 0)
            {
                string jn = il.Str(job, "name");
                ulong emp = il.Ptr(job, "employer");
                string en = emp == 0 ? null : il.Str(emp, "name");
                string ea = emp == 0 ? null : PlaceName(il.Ptr(emp, "address"));
                string work = (jn ?? "?") + (en != null ? " - " + en : "") + (ea != null && ea != en ? " (" + ea + ")" : "");
                r.Add(new ReportLine(LineKind.Pair, "Работа", work));
            }
            ulong den = il.Ptr(h, "den");
            if (den != 0)
            {
                string dn = PlaceName(den);
                if (!string.IsNullOrEmpty(dn)) r.Add(new ReportLine(LineKind.Pair, "Логово", dn));
            }
            r.Add(new ReportLine(LineKind.Pair, "Сейчас", WhereNow(h)));
        }

        public string LastKillerName { get; private set; }

        public List<ReportLine> BuildReport()
        {
            var r = new List<ReportLine>();
            if (!Attached || il == null || Game == 0)
            {
                r.Add(new ReportLine(LineKind.Text, null, "Игра не готова. Запустите игру и загрузите сохранение."));
                return r;
            }

            LastKillerName = null;
            ulong mc = Murder;
            var murders = new List<ulong>();
            if (mc != 0) murders = il.ListItems(il.Ptr(mc, "activeMurders"));

            var shownKillers = new HashSet<ulong>();
            int idx = 0;
            foreach (var mu in murders)
            {
                ulong killer = il.Ptr(mu, "murderer");
                ulong victim = il.Ptr(mu, "victim");
                if (killer == 0) continue;
                shownKillers.Add(killer);
                idx++;
                r.Add(new ReportLine(LineKind.Heading, null, murders.Count > 1 ? "УБИЙЦА #" + idx : "УБИЙЦА"));
                AddHuman(r, killer, true);
                if (LastKillerName == null) LastKillerName = Describe(killer);
                string preset = il.Str(mu, "presetStr");
                string mo = il.Str(mu, "moStr");
                string weapon = il.Str(mu, "weaponStr");
                string card = il.Str(mu, "callingCardStr");
                if (!string.IsNullOrEmpty(preset)) r.Add(new ReportLine(LineKind.Pair, "Тип", preset + (string.IsNullOrEmpty(mo) ? "" : " / " + mo)));
                if (!string.IsNullOrEmpty(weapon)) r.Add(new ReportLine(LineKind.Pair, "Оружие", weapon));
                if (!string.IsNullOrEmpty(card)) r.Add(new ReportLine(LineKind.Pair, "Визитка", card));
                if (victim != 0)
                {
                    r.Add(new ReportLine(LineKind.Gap, null, null));
                    r.Add(new ReportLine(LineKind.Heading, null, "ЖЕРТВА"));
                    AddHuman(r, victim, true);
                    string site = PlaceName(il.Ptr(mu, "location"));
                    if (!string.IsNullOrEmpty(site)) r.Add(new ReportLine(LineKind.Pair, "Место убийства", site));
                }
                r.Add(new ReportLine(LineKind.Gap, null, null));
            }

            ulong cur = mc == 0 ? 0 : il.Ptr(mc, "currentMurderer");
            if (cur != 0 && !shownKillers.Contains(cur))
            {
                r.Add(new ReportLine(LineKind.Heading, null, "УБИЙЦА (текущий)"));
                AddHuman(r, cur, true);
                if (LastKillerName == null) LastKillerName = Describe(cur);
                ulong victim = il.Ptr(mc, "currentVictim");
                if (victim != 0)
                {
                    r.Add(new ReportLine(LineKind.Gap, null, null));
                    r.Add(new ReportLine(LineKind.Heading, null, "ЖЕРТВА"));
                    AddHuman(r, victim, true);
                }
                r.Add(new ReportLine(LineKind.Gap, null, null));
            }

            if (murders.Count == 0 && cur == 0)
            {
                r.Add(new ReportLine(LineKind.Heading, null, "УБИЙЦА"));
                r.Add(new ReportLine(LineKind.Text, null, "Сейчас нет активного дела об убийстве (или убийца ещё не выбран игрой)."));
                r.Add(new ReportLine(LineKind.Gap, null, null));
            }

            ulong cp = CasePanel;
            ulong ac = cp == 0 ? 0 : il.Ptr(cp, "activeCase");
            ulong job2 = ac == 0 ? 0 : il.Ptr(ac, "job");
            if (job2 != 0)
            {
                string cn = il.Str(ac, "name");
                r.Add(new ReportLine(LineKind.Heading, null, "ОТКРЫТОЕ ПОБОЧНОЕ ДЕЛО"));
                if (!string.IsNullOrEmpty(cn)) r.Add(new ReportLine(LineKind.Pair, "Дело", cn));
                ulong purp = il.Ptr(job2, "purp");
                ulong poster = il.Ptr(job2, "poster");
                if (purp != 0)
                {
                    r.Add(new ReportLine(LineKind.Text, null, "Виновный:"));
                    AddHuman(r, purp, true);
                }
                if (poster != 0)
                {
                    r.Add(new ReportLine(LineKind.Text, null, "Заказчик:"));
                    AddHuman(r, poster, true);
                }
            }
            return r;
        }

        // ---------- диагностика ----------
        public string DiagnosticDump()
        {
            var sb = new StringBuilder();
            sb.AppendLine("Trainer diagnostic " + DateTime.Now);
            if (!Attached) return sb + "не подключено";
            sb.AppendLine("GameAssembly base 0x" + mem.GameAssemblyBase.ToString("X") + ", metadata 0x" + il.MetaBase.ToString("X"));
            foreach (var n in EntryClasses)
            {
                ulong o = il.Singleton(n);
                sb.AppendLine("== " + n + " instance 0x" + o.ToString("X"));
                if (o != 0) sb.AppendLine(il.DumpObject(o));
            }
            return sb.ToString();
        }
    }
}
