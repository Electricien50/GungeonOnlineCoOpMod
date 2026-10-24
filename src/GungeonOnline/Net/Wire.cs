using System;
using System.Text;

namespace GungeonOnline.Net
{
    /// <summary>Little-endian byte writer over a reusable buffer. No allocations per write.</summary>
    public sealed class WireWriter
    {
        public byte[] Buffer;
        public int Length;

        public WireWriter(int capacity = 1500) { Buffer = new byte[capacity]; }

        public void Reset() => Length = 0;

        void Ensure(int n)
        {
            if (Length + n <= Buffer.Length) return;
            Array.Resize(ref Buffer, Math.Max(Buffer.Length * 2, Length + n));
        }

        public void U8(byte v) { Ensure(1); Buffer[Length++] = v; }
        public void Bool(bool v) => U8(v ? (byte)1 : (byte)0);
        public void U16(ushort v) { Ensure(2); Buffer[Length++] = (byte)v; Buffer[Length++] = (byte)(v >> 8); }
        public void I16(short v) => U16((ushort)v);
        public void U32(uint v) { Ensure(4); for (int i = 0; i < 4; i++) Buffer[Length++] = (byte)(v >> (8 * i)); }
        public void I32(int v) => U32((uint)v);
        public void U64(ulong v) { Ensure(8); for (int i = 0; i < 8; i++) Buffer[Length++] = (byte)(v >> (8 * i)); }
        public unsafe void F32(float v) => U32(*(uint*)&v);

        /// <summary>Unsigned varint (1 to 5 bytes).</summary>
        public void VarU(uint v)
        {
            while (v >= 0x80) { U8((byte)(v | 0x80)); v >>= 7; }
            U8((byte)v);
        }

        public void VarI(int v) => VarU((uint)((v << 1) ^ (v >> 31)));

        public void Bytes(byte[] src, int offset, int count)
        {
            Ensure(count);
            System.Buffer.BlockCopy(src, offset, Buffer, Length, count);
            Length += count;
        }

        public void Str(string s)
        {
            var b = Encoding.UTF8.GetBytes(s ?? "");
            VarU((uint)b.Length);
            Bytes(b, 0, b.Length);
        }

        public byte[] ToArray()
        {
            var a = new byte[Length];
            System.Buffer.BlockCopy(Buffer, 0, a, 0, Length);
            return a;
        }
    }

    /// <summary>Bounds-checked little-endian reader. Throws <see cref="WireException"/> on truncated input.</summary>
    public struct WireReader
    {
        public readonly byte[] Buffer;
        public int Pos;
        public readonly int End;

        public WireReader(byte[] buf, int offset, int count) { Buffer = buf; Pos = offset; End = offset + count; }

        public int Remaining => End - Pos;

        void Need(int n) { if (Pos + n > End) throw new WireException("truncated"); }

        public byte U8() { Need(1); return Buffer[Pos++]; }
        public bool Bool() => U8() != 0;
        public ushort U16() { Need(2); ushort v = (ushort)(Buffer[Pos] | Buffer[Pos + 1] << 8); Pos += 2; return v; }
        public short I16() => (short)U16();
        public uint U32() { Need(4); uint v = 0; for (int i = 0; i < 4; i++) v |= (uint)Buffer[Pos + i] << (8 * i); Pos += 4; return v; }
        public int I32() => (int)U32();
        public ulong U64() { Need(8); ulong v = 0; for (int i = 0; i < 8; i++) v |= (ulong)Buffer[Pos + i] << (8 * i); Pos += 8; return v; }
        public unsafe float F32() { uint u = U32(); return *(float*)&u; }

        public uint VarU()
        {
            uint v = 0;
            for (int shift = 0; shift < 35; shift += 7)
            {
                byte b = U8();
                v |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return v;
            }
            throw new WireException("varint too long");
        }

        public int VarI() { uint u = VarU(); return (int)(u >> 1) ^ -(int)(u & 1); }

        public byte[] Bytes(int n)
        {
            Need(n);
            var a = new byte[n];
            System.Buffer.BlockCopy(Buffer, Pos, a, 0, n);
            Pos += n;
            return a;
        }

        /// <summary>A string with a larger cap (8 MB), for bulk data such as save state.</summary>
        public string LongStr()
        {
            int n = (int)VarU();
            if (n < 0 || n > 8 * 1024 * 1024) throw new WireException("string too long");
            Need(n);
            var s = Encoding.UTF8.GetString(Buffer, Pos, n);
            Pos += n;
            return s;
        }

        public string Str()
        {
            int n = (int)VarU();
            if (n > 4096) throw new WireException("string too long");
            Need(n);
            var s = Encoding.UTF8.GetString(Buffer, Pos, n);
            Pos += n;
            return s;
        }
    }

    public sealed class WireException : Exception
    {
        public WireException(string m) : base(m) { }
    }

    /// <summary>Wrapping 16-bit sequence comparison.</summary>
    public static class Seq
    {
        public static bool Greater(ushort a, ushort b) => (a > b && a - b <= 32768) || (a < b && b - a > 32768);
        public static int Diff(ushort a, ushort b) => (short)(a - b);
    }
}
