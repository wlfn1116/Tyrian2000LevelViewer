namespace T2LV.Util;

/// <summary>Writes 16-bit PCM WAVE files -- the one format everything can open.</summary>
public static class Wav
{
    /// <summary>
    /// Writes interleaved 16-bit samples. <paramref name="channels"/> is how many of them make
    /// one frame, so a stereo buffer is L,R,L,R and <paramref name="samples"/>.Length must be a
    /// multiple of it.
    /// </summary>
    public static void Write(string path, ReadOnlySpan<short> samples, int sampleRate, int channels)
    {
        int dataBytes = samples.Length * sizeof(short);
        int byteRate = sampleRate * channels * sizeof(short);

        using var f = File.Create(path);
        using var w = new BinaryWriter(f);

        w.Write(new[] { 'R', 'I', 'F', 'F' });
        w.Write(36 + dataBytes);              // everything after this field
        w.Write(new[] { 'W', 'A', 'V', 'E' });

        w.Write(new[] { 'f', 'm', 't', ' ' });
        w.Write(16);                          // PCM fmt chunk size
        w.Write((short)1);                    // format: PCM
        w.Write((short)channels);
        w.Write(sampleRate);
        w.Write(byteRate);
        w.Write((short)(channels * sizeof(short)));   // block align
        w.Write((short)16);                           // bits per sample

        w.Write(new[] { 'd', 'a', 't', 'a' });
        w.Write(dataBytes);
        foreach (short s in samples) w.Write(s);
    }
}
