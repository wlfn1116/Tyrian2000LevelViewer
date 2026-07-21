namespace T2A.Tyrian;

/// <summary>
/// tyrian%d.lvl container header: u16 lvlNum, then s32 lvlPos[lvlNum].
/// A loadable level section "f" (1-based) starts at lvlPos[(f-1)*2]
/// (odd entries are internal sub-offsets, unused by the loader).
/// See lvllib.c:JE_analyzeLevel and tyrian2.c:3104.
/// </summary>
public sealed class LevelContainer
{
    public readonly string Path;
    public readonly byte[] Raw;
    public readonly int LvlNum;
    public readonly int[] LvlPos; // length LvlNum+1, last entry = filesize

    public int SectionCount => LvlNum / 2;

    public LevelContainer(string path)
    {
        Path = path;
        Raw = File.ReadAllBytes(path);
        var r = new ByteReader(Raw);
        LvlNum = r.U16();
        LvlPos = new int[LvlNum + 1];
        for (int i = 0; i < LvlNum; i++)
            LvlPos[i] = r.S32();
        LvlPos[LvlNum] = Raw.Length;
    }

    /// <summary>Byte offset of section f (1-based).</summary>
    public int SectionOffset(int fileNum1Based) => LvlPos[(fileNum1Based - 1) * 2];
}
