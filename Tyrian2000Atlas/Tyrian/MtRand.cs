namespace T2LV.Tyrian;

/// <summary>
/// Exact port of the engine's Mersenne Twister (mtrand.c) with snapshot support,
/// so the playback simulator is deterministic and scrub/rewind can restore state.
/// </summary>
public sealed class MtRand
{
    private const int N = 624, M = 397;
    private const uint MatrixA = 0x9908b0dfu, UpperMask = 0x80000000u, LowerMask = 0x7fffffffu;

    private readonly uint[] _x = new uint[N];
    private int _p0, _p1, _pm;   // indices into _x (the C code uses pointers)

    public MtRand(uint seed = 5489u) => Seed(seed);

    public void Seed(uint s)
    {
        _x[0] = s;
        for (int i = 1; i < N; i++)
            _x[i] = (uint)(1812433253u * (_x[i - 1] ^ (_x[i - 1] >> 30)) + (uint)i);
        _p0 = 0; _p1 = 1; _pm = M;
    }

    public uint Next()
    {
        uint y = _x[_p0] = _x[_pm] ^ (((_x[_p0] & UpperMask) | (_x[_p1] & LowerMask)) >> 1)
            ^ ((uint)(-(int)(_x[_p1] & 1)) & MatrixA);
        _pm++;
        _p0 = _p1; _p1++;
        if (_pm == N) _pm = 0;
        if (_p1 == N) _p1 = 0;
        y ^= y >> 11;
        y ^= (y << 7) & 0x9d2c5680u;
        y ^= (y << 15) & 0xefc60000u;
        y ^= y >> 18;
        return y;
    }

    public (uint[] X, int P0, int P1, int Pm) Snapshot() => ((uint[])_x.Clone(), _p0, _p1, _pm);

    public void Restore((uint[] X, int P0, int P1, int Pm) s)
    {
        Array.Copy(s.X, _x, N);
        _p0 = s.P0; _p1 = s.P1; _pm = s.Pm;
    }
}
