using System;
using System.Collections.Generic;
using System.Text;

namespace SoDTrainer
{
    internal sealed class ClassInfo
    {
        public ulong Ptr;
        public string Name;
        public string Ns;
        public string Image;
    }

    internal sealed class FieldDef
    {
        public string Name;
        public int Offset;
        public int TypeCode;
        public bool IsStatic;
        public ulong Owner;
        public string OwnerName;
    }

    /// <summary>
    /// Разбор внутренних структур IL2CPP прямо из памяти процесса (без инжекта и без загрузчиков модов).
    /// Классы ищутся по именам из global-metadata, поля - по именам из FieldInfo, поэтому смещения
    /// не зашиты в код и переживают обновления игры, пока разработчики не переименовали поля.
    /// </summary>
    internal sealed unsafe class Il2
    {
        // Смещения внутри Il2CppClass (Unity 2021.3 / metadata v29-31)
        public int OffName = 0x10, OffNs = 0x18, OffParent = 0x58, OffSelf = 0x78, OffFields = 0x80,
            OffStatic = 0xB8, OffFieldCount = 0x120;

        readonly ProcMem m;
        public Action<string> Log = delegate { };

        public ulong MetaBase;
        public ulong StrLo, StrHi;
        byte[] strTable;

        public readonly Dictionary<string, List<ClassInfo>> ByName = new Dictionary<string, List<ClassInfo>>();
        readonly Dictionary<ulong, Dictionary<string, FieldDef>> fieldCache = new Dictionary<ulong, Dictionary<string, FieldDef>>();
        readonly Dictionary<ulong, string> classNameCache = new Dictionary<ulong, string>();

        public Il2(ProcMem mem)
        {
            m = mem;
        }

        public bool HasMetadata
        {
            get { return MetaBase != 0; }
        }

        // ---------- поиск global-metadata в памяти ----------
        public bool FindMetadata()
        {
            const int Chunk = 4 << 20;
            var buf = new byte[Chunk + 64];
            foreach (var r in m.Regions())
            {
                if (r.Size < (4UL << 20)) continue;
                for (ulong off = 0; off < r.Size; off += Chunk)
                {
                    int n = (int)Math.Min((ulong)Chunk + 64, r.Size - off);
                    if (!m.Read(r.Base + off, buf, n)) continue;
                    fixed (byte* p = buf)
                    {
                        for (int i = 0; i + 16 <= n; i += 8)
                        {
                            if (*(uint*)(p + i) != 0xFAB11BAFu) continue;
                            int ver = *(int*)(p + i + 4);
                            if (ver < 24 || ver > 40) continue;
                            ulong b = r.Base + off + (ulong)i;
                            if (TryMeta(b)) return true;
                        }
                    }
                }
            }
            return false;
        }

        bool TryMeta(ulong b)
        {
            int strOff = m.I32(b + 8 + 2 * 8), strSize = m.I32(b + 8 + 2 * 8 + 4);
            if (strOff <= 0 || strOff > 0x10000000 || strSize < 100000 || strSize > 0x10000000) return false;
            var tbl = m.ReadBytes(b + (ulong)strOff, strSize);
            if (tbl == null) return false;
            if (IndexOf(tbl, Encoding.ASCII.GetBytes("<Module>\0"), 0) < 0) return false;
            MetaBase = b;
            StrLo = b + (ulong)strOff;
            StrHi = StrLo + (ulong)strSize;
            strTable = tbl;
            return true;
        }

        static int IndexOf(byte[] hay, byte[] needle, int start)
        {
            int last = hay.Length - needle.Length;
            for (int i = start; i <= last; i++)
            {
                if (hay[i] != needle[0]) continue;
                int j = 1;
                while (j < needle.Length && hay[i + j] == needle[j]) j++;
                if (j == needle.Length) return i;
            }
            return -1;
        }

        // ---------- сканирование Il2CppClass ----------
        public bool Missing(IEnumerable<string> names)
        {
            foreach (var n in names)
                if (!ByName.ContainsKey(n)) return true;
            return false;
        }

        /// <summary>Ищет в куче структуры Il2CppClass для заданных имён классов.</summary>
        public int Scan(IEnumerable<string> names)
        {
            if (!HasMetadata && !FindMetadata()) throw new Exception("global-metadata не найден в памяти игры");

            var targets = new Dictionary<ulong, string>();
            foreach (var n in names)
            {
                int at = IndexOf(strTable, Encoding.ASCII.GetBytes("\0" + n + "\0"), 0);
                if (at >= 0) targets[StrLo + (ulong)at + 1] = n;
            }
            if (targets.Count == 0) return 0;
            ulong lo = ulong.MaxValue, hi = 0;
            foreach (var k in targets.Keys)
            {
                if (k < lo) lo = k;
                if (k > hi) hi = k;
            }

            int found = 0;
            const int Chunk = 8 << 20;
            var buf = new byte[Chunk];
            var cands = new List<KeyValuePair<ulong, string>>();
            foreach (var r in m.Regions())
            {
                if (r.Type != 0x20000 || !r.Writable) continue; // MEM_PRIVATE + запись: куча il2cpp
                for (ulong off = 0; off < r.Size; off += Chunk)
                {
                    int n = (int)Math.Min((ulong)Chunk, r.Size - off);
                    if (!m.Read(r.Base + off, buf, n)) continue;
                    ulong baseAddr = r.Base + off;
                    fixed (byte* p = buf)
                    {
                        ulong* q = (ulong*)p;
                        int cnt = n / 8;
                        for (int i = 0; i < cnt; i++)
                        {
                            ulong v = q[i];
                            if (v < lo || v > hi) continue;
                            string nm;
                            if (!targets.TryGetValue(v, out nm)) continue;
                            ulong at = baseAddr + (ulong)i * 8;
                            if (at < (ulong)OffName) continue;
                            cands.Add(new KeyValuePair<ulong, string>(at - (ulong)OffName, nm));
                        }
                    }
                }
            }

            foreach (var c in cands)
            {
                ulong k = c.Key;
                if (m.U64(k + (ulong)OffSelf) != k) continue;
                var info = ReadClass(k);
                if (info == null || info.Name != c.Value) continue;
                if (info.Image == null || !info.Image.StartsWith("Assembly-CSharp")) continue;
                List<ClassInfo> list;
                if (!ByName.TryGetValue(info.Name, out list)) ByName[info.Name] = list = new List<ClassInfo>();
                bool dup = false;
                foreach (var e in list)
                    if (e.Ptr == k) dup = true;
                if (dup) continue;
                list.Add(info);
                found++;
            }
            return found;
        }

        public ClassInfo ReadClass(ulong k)
        {
            if (!ProcMem.IsUserPtr(k)) return null;
            string name = m.Ascii(m.U64(k + (ulong)OffName), 160);
            if (string.IsNullOrEmpty(name)) return null;
            string ns = m.Ascii(m.U64(k + (ulong)OffNs), 160) ?? "";
            ulong img = m.U64(k);
            string imgName = ProcMem.IsUserPtr(img) ? m.Ascii(m.U64(img), 128) : null;
            return new ClassInfo { Ptr = k, Name = name, Ns = ns, Image = imgName };
        }

        public string ClassNameOf(ulong klass)
        {
            string s;
            if (classNameCache.TryGetValue(klass, out s)) return s;
            var ci = ReadClass(klass);
            s = ci == null ? "?" : ci.Name;
            classNameCache[klass] = s;
            return s;
        }

        // ---------- поля ----------
        Dictionary<string, FieldDef> OwnFields(ulong klass)
        {
            Dictionary<string, FieldDef> d;
            if (fieldCache.TryGetValue(klass, out d)) return d;
            d = new Dictionary<string, FieldDef>();
            ulong arr = m.U64(klass + (ulong)OffFields);
            int count = m.U16(klass + (ulong)OffFieldCount);
            if (!ProcMem.IsUserPtr(arr) || count <= 0 || count > 4000)
            {
                // класс ещё не инициализирован - не кешируем пустоту
                return d;
            }
            int stride = 0x20;
            if (count >= 2)
            {
                if (m.U64(arr + 0x20 + 0x10) != klass && m.U64(arr + 0x28 + 0x10) == klass) stride = 0x28;
            }
            string owner = ClassNameOf(klass);
            for (int i = 0; i < count; i++)
            {
                ulong f = arr + (ulong)(i * stride);
                string nm = m.Ascii(m.U64(f), 128);
                if (nm == null) continue;
                ulong type = m.U64(f + 8);
                int off = m.I32(f + 0x18);
                uint bits = (uint)m.I32(type + 8);
                var def = new FieldDef
                {
                    Name = nm,
                    Offset = off,
                    TypeCode = (int)((bits >> 16) & 0xFF),
                    IsStatic = (bits & 0x10) != 0,
                    Owner = klass,
                    OwnerName = owner
                };
                d[nm] = def;
            }
            fieldCache[klass] = d;
            return d;
        }

        public IEnumerable<FieldDef> AllFields(ulong klass)
        {
            int guard = 0;
            while (ProcMem.IsUserPtr(klass) && guard++ < 16)
            {
                if (ClassNameOf(klass) == "Object") yield break;
                var own = OwnFields(klass);
                foreach (var f in own.Values) yield return f;
                klass = m.U64(klass + (ulong)OffParent);
            }
        }

        public FieldDef Field(ulong klass, string name)
        {
            int guard = 0;
            while (ProcMem.IsUserPtr(klass) && guard++ < 16)
            {
                FieldDef f;
                if (OwnFields(klass).TryGetValue(name, out f)) return f;
                klass = m.U64(klass + (ulong)OffParent);
            }
            return null;
        }

        public ClassInfo Class(string name)
        {
            List<ClassInfo> l;
            if (!ByName.TryGetValue(name, out l) || l.Count == 0) return null;
            ClassInfo best = null;
            foreach (var c in l)
            {
                if (c.Ns.Length == 0 && Field(c.Ptr, "_instance") != null) return c;
                if (best == null || c.Ns.Length == 0) best = c;
            }
            return best;
        }

        /// <summary>Значение статического поля _instance (синглтон), либо 0.</summary>
        public ulong Singleton(string cls)
        {
            var c = Class(cls);
            if (c == null) return 0;
            var f = Field(c.Ptr, "_instance");
            if (f == null || !f.IsStatic) return 0;
            ulong sf = m.U64(c.Ptr + (ulong)OffStatic);
            if (!ProcMem.IsUserPtr(sf)) return 0;
            ulong obj = m.U64(sf + (ulong)f.Offset);
            if (!ProcMem.IsUserPtr(obj)) return 0;
            return obj;
        }

        // ---------- доступ к полям объекта ----------
        public FieldDef FieldOf(ulong obj, string name)
        {
            if (!ProcMem.IsUserPtr(obj)) return null;
            ulong k = m.U64(obj);
            if (!ProcMem.IsUserPtr(k)) return null;
            var f = Field(k, name);
            return (f == null || f.IsStatic) ? null : f;
        }

        public ulong Addr(ulong obj, string name)
        {
            var f = FieldOf(obj, name);
            return f == null ? 0 : obj + (ulong)f.Offset;
        }

        public ulong Ptr(ulong obj, string name)
        {
            ulong a = Addr(obj, name);
            if (a == 0) return 0;
            ulong v = m.U64(a);
            return ProcMem.IsUserPtr(v) ? v : 0;
        }

        public ulong Chain(ulong obj, params string[] path)
        {
            foreach (var p in path)
            {
                obj = Ptr(obj, p);
                if (obj == 0) return 0;
            }
            return obj;
        }

        public int Int(ulong obj, string name)
        {
            ulong a = Addr(obj, name);
            return a == 0 ? 0 : m.I32(a);
        }

        public float Float(ulong obj, string name)
        {
            ulong a = Addr(obj, name);
            return a == 0 ? 0 : m.F32(a);
        }

        public bool Bool(ulong obj, string name)
        {
            ulong a = Addr(obj, name);
            return a != 0 && m.U8(a) != 0;
        }

        public string Str(ulong obj, string name)
        {
            ulong p = Ptr(obj, name);
            return p == 0 ? null : m.Il2String(p);
        }

        public bool SetInt(ulong obj, string name, int v)
        {
            ulong a = Addr(obj, name);
            return a != 0 && m.WriteI32(a, v);
        }

        public bool SetFloat(ulong obj, string name, float v)
        {
            ulong a = Addr(obj, name);
            return a != 0 && m.WriteF32(a, v);
        }

        public bool SetBool(ulong obj, string name, bool v)
        {
            ulong a = Addr(obj, name);
            return a != 0 && m.WriteU8(a, (byte)(v ? 1 : 0));
        }

        /// <summary>Элементы System.Collections.Generic.List&lt;T&gt; (ссылочный T).</summary>
        public List<ulong> ListItems(ulong list)
        {
            var res = new List<ulong>();
            if (!ProcMem.IsUserPtr(list)) return res;
            ulong items = m.U64(list + 0x10);
            int size = m.I32(list + 0x18);
            if (!ProcMem.IsUserPtr(items) || size <= 0 || size > 100000) return res;
            var b = m.ReadBytes(items + 0x20, size * 8);
            if (b == null) return res;
            for (int i = 0; i < size; i++)
            {
                ulong v = BitConverter.ToUInt64(b, i * 8);
                if (ProcMem.IsUserPtr(v)) res.Add(v);
            }
            return res;
        }

        // ---------- диагностика ----------
        public string DumpObject(ulong obj)
        {
            var sb = new StringBuilder();
            if (!ProcMem.IsUserPtr(obj)) return "(null)";
            ulong k = m.U64(obj);
            sb.AppendLine(string.Format("object 0x{0:X} class {1}", obj, ClassNameOf(k)));
            foreach (var f in AllFields(k))
            {
                if (f.IsStatic) continue;
                ulong a = obj + (ulong)f.Offset;
                string val;
                switch (f.TypeCode)
                {
                    case 2: val = (m.U8(a) != 0).ToString(); break;
                    case 8: val = m.I32(a).ToString(); break;
                    case 12: val = m.F32(a).ToString("0.###"); break;
                    case 14: val = "\"" + m.Il2String(m.U64(a)) + "\""; break;
                    case 18:
                    case 21:
                        {
                            ulong p = m.U64(a);
                            val = p == 0 ? "null" : "-> " + ClassNameOf(m.U64(p));
                            break;
                        }
                    default: val = "0x" + m.U64(a).ToString("X"); break;
                }
                sb.AppendLine(string.Format("  +0x{0:X3} {1,-32} t{2,-2} {3}   [{4}]", f.Offset, f.Name, f.TypeCode, val, f.OwnerName));
            }
            return sb.ToString();
        }
    }
}
