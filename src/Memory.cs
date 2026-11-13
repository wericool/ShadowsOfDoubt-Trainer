using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SoDTrainer
{
    internal static unsafe class Native
    {
        public const uint PROCESS_VM_OPERATION = 0x0008;
        public const uint PROCESS_VM_READ = 0x0010;
        public const uint PROCESS_VM_WRITE = 0x0020;
        public const uint PROCESS_QUERY_INFORMATION = 0x0400;

        [StructLayout(LayoutKind.Sequential)]
        public struct MBI
        {
            public ulong BaseAddress;
            public ulong AllocationBase;
            public uint AllocationProtect;
            public ushort PartitionId;
            public ulong RegionSize;
            public uint State;
            public uint Protect;
            public uint Type;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

        [DllImport("kernel32.dll")]
        public static extern bool CloseHandle(IntPtr h);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool ReadProcessMemory(IntPtr h, ulong addr, void* buf, UIntPtr size, out UIntPtr read);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool WriteProcessMemory(IntPtr h, ulong addr, void* buf, UIntPtr size, out UIntPtr written);

        [DllImport("kernel32.dll")]
        public static extern UIntPtr VirtualQueryEx(IntPtr h, ulong addr, out MBI mbi, UIntPtr len);
    }

    internal struct Region
    {
        public ulong Base;
        public ulong Size;
        public uint Protect;
        public uint Type;

        public bool Writable
        {
            get { return (Protect & (0x04 | 0x08 | 0x40 | 0x80)) != 0; }
        }
    }

    /// <summary>Тонкая обёртка над ReadProcessMemory / WriteProcessMemory.</summary>
    internal sealed unsafe class ProcMem : IDisposable
    {
        public readonly Process Proc;
        public readonly IntPtr Handle;
        public ulong GameAssemblyBase;

        public ProcMem(Process p)
        {
            Proc = p;
            Handle = Native.OpenProcess(
                Native.PROCESS_VM_READ | Native.PROCESS_VM_WRITE | Native.PROCESS_VM_OPERATION |
                Native.PROCESS_QUERY_INFORMATION, false, p.Id);
            if (Handle == IntPtr.Zero)
                throw new Exception("Не удалось открыть процесс игры (код " + Marshal.GetLastWin32Error() +
                                    "). Попробуйте запустить трейнер от имени администратора.");
            try
            {
                foreach (ProcessModule mod in p.Modules)
                {
                    if (string.Equals(mod.ModuleName, "GameAssembly.dll", StringComparison.OrdinalIgnoreCase))
                    {
                        GameAssemblyBase = (ulong)mod.BaseAddress.ToInt64();
                        break;
                    }
                }
            }
            catch
            {
            }
        }

        public bool Alive
        {
            get
            {
                try
                {
                    return !Proc.HasExited;
                }
                catch
                {
                    return false;
                }
            }
        }

        public void Dispose()
        {
            if (Handle != IntPtr.Zero) Native.CloseHandle(Handle);
        }

        public static bool IsUserPtr(ulong p)
        {
            return p >= 0x10000UL && p < 0x7FFFFFFF0000UL;
        }

        public bool Read(ulong addr, byte[] buf, int n)
        {
            if (n <= 0) return true;
            fixed (byte* p = buf)
            {
                UIntPtr got;
                return Native.ReadProcessMemory(Handle, addr, p, (UIntPtr)(uint)n, out got) && (int)got == n;
            }
        }

        public byte[] ReadBytes(ulong addr, int n)
        {
            var b = new byte[n];
            return Read(addr, b, n) ? b : null;
        }

        public ulong U64(ulong addr)
        {
            ulong v;
            UIntPtr got;
            if (!Native.ReadProcessMemory(Handle, addr, &v, (UIntPtr)8, out got) || (int)got != 8) return 0;
            return v;
        }

        public int I32(ulong addr)
        {
            int v;
            UIntPtr got;
            if (!Native.ReadProcessMemory(Handle, addr, &v, (UIntPtr)4, out got) || (int)got != 4) return 0;
            return v;
        }

        public ushort U16(ulong addr)
        {
            ushort v;
            UIntPtr got;
            if (!Native.ReadProcessMemory(Handle, addr, &v, (UIntPtr)2, out got) || (int)got != 2) return 0;
            return v;
        }

        public byte U8(ulong addr)
        {
            byte v;
            UIntPtr got;
            if (!Native.ReadProcessMemory(Handle, addr, &v, (UIntPtr)1, out got) || (int)got != 1) return 0;
            return v;
        }

        public float F32(ulong addr)
        {
            float v;
            UIntPtr got;
            if (!Native.ReadProcessMemory(Handle, addr, &v, (UIntPtr)4, out got) || (int)got != 4) return 0;
            return v;
        }

        public double F64(ulong addr)
        {
            double v;
            UIntPtr got;
            if (!Native.ReadProcessMemory(Handle, addr, &v, (UIntPtr)8, out got) || (int)got != 8) return 0;
            return v;
        }

        public bool WriteBytes(ulong addr, byte[] data)
        {
            fixed (byte* p = data)
            {
                UIntPtr w;
                return Native.WriteProcessMemory(Handle, addr, p, (UIntPtr)(uint)data.Length, out w);
            }
        }

        public bool WriteI32(ulong addr, int v)
        {
            UIntPtr w;
            return Native.WriteProcessMemory(Handle, addr, &v, (UIntPtr)4, out w);
        }

        public bool WriteF32(ulong addr, float v)
        {
            UIntPtr w;
            return Native.WriteProcessMemory(Handle, addr, &v, (UIntPtr)4, out w);
        }

        public bool WriteU8(ulong addr, byte v)
        {
            UIntPtr w;
            return Native.WriteProcessMemory(Handle, addr, &v, (UIntPtr)1, out w);
        }

        public string Ascii(ulong addr, int max = 128)
        {
            if (!IsUserPtr(addr)) return null;
            var b = new byte[max];
            // читаем сколько получится (граница страницы может оборвать чтение)
            int n = max;
            while (n > 0 && !Read(addr, b, n)) n /= 2;
            if (n == 0) return null;
            int len = 0;
            while (len < n && b[len] != 0) len++;
            if (len == n && len == max) return null; // нет терминатора
            for (int i = 0; i < len; i++)
                if (b[i] < 0x20 || b[i] > 0x7E) return null;
            return Encoding.ASCII.GetString(b, 0, len);
        }

        /// <summary>Читает System.String (Il2CppString): длина по +0x10, символы UTF-16 по +0x14.</summary>
        public string Il2String(ulong strPtr)
        {
            if (!IsUserPtr(strPtr)) return null;
            int len = I32(strPtr + 0x10);
            if (len < 0 || len > 4096) return null;
            if (len == 0) return "";
            var b = ReadBytes(strPtr + 0x14, len * 2);
            return b == null ? null : Encoding.Unicode.GetString(b);
        }

        public IEnumerable<Region> Regions()
        {
            ulong addr = 0x10000;
            Native.MBI mbi;
            var sz = (UIntPtr)(uint)Marshal.SizeOf(typeof(Native.MBI));
            while (addr < 0x7FFFFFFF0000UL)
            {
                if (Native.VirtualQueryEx(Handle, addr, out mbi, sz) == UIntPtr.Zero) break;
                ulong size = mbi.RegionSize;
                if (size == 0) break;
                bool committed = mbi.State == 0x1000;
                bool guard = (mbi.Protect & 0x100) != 0;
                bool noaccess = (mbi.Protect & 0x01) != 0;
                if (committed && !guard && !noaccess && mbi.Protect != 0)
                    yield return new Region { Base = mbi.BaseAddress, Size = size, Protect = mbi.Protect, Type = mbi.Type };
                addr = mbi.BaseAddress + size;
            }
        }
    }
}
