using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;

namespace SoDTrainer
{
    internal static class Program
    {
        [STAThread]
        static int Main(string[] args)
        {
            if (args.Length > 1 && args[0] == "--shots")
            {
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                var f = new MainForm();
                f.CaptureShots(args[1]);
                return 0;
            }
            if (args.Length > 0 && args[0] == "--selftest")
                return SelfTest(args.Length > 1 ? args[1] : "selftest.txt");
            if (args.Length > 0 && args[0] == "--probe")
                return Probe(args.Length > 1 ? args[1] : "probe.txt");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
            return 0;
        }

        static int SelfTest(string outFile)
        {
            var sb = new StringBuilder();
            var g = new GameLink();
            for (int i = 0; i < 400 && !(g.Attached && g.Game != 0 && !g.Scanning); i++)
            {
                g.Tick();
                System.Threading.Thread.Sleep(50);
            }
            string l;
            while (g.LogQueue.TryDequeue(out l)) sb.AppendLine(l);
            sb.AppendLine("Status: " + g.Status);
            foreach (var r in g.BuildReport())
                sb.AppendLine(string.Format("[{0}] {1} {2}", r.Kind, r.Key, r.Value));
            sb.AppendLine("killer = " + g.LastKillerName);
            sb.AppendLine("money=" + g.GetInt("GameplayController", "money") + " picks=" + g.GetInt("GameplayController", "lockPicks") + " social=" + g.GetInt("GameplayController", "socialCredit"));
            int before = g.GetInt("GameplayController", "lockPicks");
            g.AddInt("GameplayController", "lockPicks", 1);
            int mid = g.GetInt("GameplayController", "lockPicks");
            g.SetInt("GameplayController", "lockPicks", before);
            sb.AppendLine("write test lockPicks: " + before + " -> " + mid + " -> " + g.GetInt("GameplayController", "lockPicks"));
            bool inv = g.GetBool("Game", "invisiblePlayer");
            g.SetBool("Game", "invisiblePlayer", !inv);
            bool inv2 = g.GetBool("Game", "invisiblePlayer");
            g.SetBool("Game", "invisiblePlayer", inv);
            sb.AppendLine("flag test invisiblePlayer: " + inv + " -> " + inv2 + " -> " + g.GetBool("Game", "invisiblePlayer"));
            sb.AppendLine("movementSpeed=" + g.GetFloat("Game", "movementSpeed") + " lockpickStrength=" + g.GetFloat("GameplayController", "currentLockpickStrength"));
            sb.AppendLine("health=" + g.Il.Float(g.PlayerObj, "currentHealth") + "/" + g.Il.Float(g.PlayerObj, "maximumHealth"));
            File.WriteAllText(outFile, sb.ToString(), Encoding.UTF8);
            return 0;
        }

        static int Probe(string outFile)
        {
            var sb = new StringBuilder();
            try
            {
                var p = Process.GetProcessesByName("Shadows of Doubt").FirstOrDefault();
                if (p == null) { File.WriteAllText(outFile, "game not running"); return 1; }
                var mem = new ProcMem(p);
                var il = new Il2(mem);
                var sw = Stopwatch.StartNew();
                sb.AppendLine("GameAssembly base: 0x" + mem.GameAssemblyBase.ToString("X"));
                bool ok = il.FindMetadata();
                sb.AppendLine("metadata: " + ok + " base 0x" + il.MetaBase.ToString("X") + " (" + sw.ElapsedMilliseconds + " ms)");
                string[] names = { "Game", "GameplayController", "Player", "MurderController", "CasePanelController", "Human", "NewAddress" };
                sw.Restart();
                int n = il.Scan(names);
                sb.AppendLine("scan found " + n + " classes in " + sw.ElapsedMilliseconds + " ms");
                foreach (var kv in il.ByName)
                    foreach (var c in kv.Value)
                        sb.AppendLine(string.Format("  class {0} ns='{1}' ptr=0x{2:X} img={3}", c.Name, c.Ns, c.Ptr, c.Image));
                foreach (var nm in new[] { "Game", "GameplayController" })
                {
                    var ci = il.Class(nm);
                    sb.AppendLine("== class " + nm + " static_fields=0x" + mem.U64(ci.Ptr + 0xB8).ToString("X") + " fieldsPtr=0x" + mem.U64(ci.Ptr + 0x80).ToString("X") + " count=" + mem.U16(ci.Ptr + 0x120));
                    foreach (var f in il.AllFields(ci.Ptr))
                        if (f.IsStatic || f.Name == "_instance") sb.AppendLine("   field " + f.Name + " off=0x" + f.Offset.ToString("X") + " t" + f.TypeCode + " static=" + f.IsStatic + " owner=" + f.OwnerName);
                }
                foreach (var nm in new[] { "Game", "GameplayController", "Player", "MurderController", "CasePanelController" })
                {
                    ulong inst = il.Singleton(nm);
                    sb.AppendLine(nm + " instance: 0x" + inst.ToString("X"));
                    if (inst != 0) sb.AppendLine(il.DumpObject(inst));
                }
                ulong mc = il.Singleton("MurderController");
                ulong killer = il.Ptr(mc, "currentMurderer");
                sb.AppendLine("##### killer name: " + il.Str(killer, "citizenName"));
                foreach (var path in new[] { "home", "residence", "den", "currentGameLocation", "currentBuilding", "currentRoom" })
                {
                    ulong o = il.Ptr(killer, path);
                    sb.AppendLine("##### killer." + path + ":");
                    sb.AppendLine(il.DumpObject(o));
                }
                ulong home = il.Ptr(killer, "home");
                foreach (var path in new[] { "district", "building" })
                {
                    sb.AppendLine("##### killer.home." + path + ":");
                    sb.AppendLine(il.DumpObject(il.Ptr(home, path)));
                }
                foreach (var path in new[] { "home.evidenceEntry", "home.building.evidenceEntry", "evidenceEntry", "job", "job.employer", "job.employer.address", "home.floor", "home.building.street", "residence.address" })
                {
                    ulong o = il.Chain(killer, path.Split('.'));
                    sb.AppendLine("##### killer." + path + ":");
                    sb.AppendLine(il.DumpObject(o));
                }
                var murders = il.ListItems(il.Ptr(mc, "activeMurders"));
                sb.AppendLine("##### activeMurders: " + murders.Count);
                foreach (var mu in murders) sb.AppendLine(il.DumpObject(mu));
            }
            catch (Exception e)
            {
                sb.AppendLine(e.ToString());
            }
            File.WriteAllText(outFile, sb.ToString());
            return 0;
        }
    }
}
