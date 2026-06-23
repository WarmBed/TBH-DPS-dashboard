using System;

namespace TbhDpsMeter
{
    /// <summary>Pure (Unity-free) RIFF/WAVE decoder → interleaved float samples in [-1,1].
    /// Kept separate from <see cref="BoxSound"/> so the bit-depth/format handling can be unit-tested
    /// off the game (BoxSound only wraps the result in a Unity AudioClip). Returns null + an error
    /// string for anything it can't decode, so the caller can surface it instead of playing silence.</summary>
    public static class WavDecoder
    {
        /// <summary>Decode a WAV byte buffer. Returns interleaved float samples, or null on failure
        /// (with <paramref name="error"/> set to a short human-readable reason).</summary>
        public static float[] Decode(byte[] b, out int channels, out int rate, out string error)
        {
            channels = 1; rate = 44100; error = null;
            int bits = 16, format = 1;
            int dataPos = -1, dataLen = 0;

            if (b == null || b.Length < 44) { error = "file too small to be a WAV"; return null; }
            if (b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F') { error = "not a RIFF/WAV file"; return null; }

            int p = 12;
            while (p + 8 <= b.Length)
            {
                string id = "" + (char)b[p] + (char)b[p + 1] + (char)b[p + 2] + (char)b[p + 3];
                int sz = BitConverter.ToInt32(b, p + 4);
                if (id == "fmt ")
                {
                    format = BitConverter.ToInt16(b, p + 8);
                    channels = BitConverter.ToInt16(b, p + 10);
                    rate = BitConverter.ToInt32(b, p + 12);
                    bits = BitConverter.ToInt16(b, p + 22);
                    // WAVE_FORMAT_EXTENSIBLE (0xFFFE, read as -2): the real PCM/float tag lives in the
                    // SubFormat GUID's first 2 bytes (offset 24 of the fmt body), not wFormatTag.
                    if (format == -2 && sz >= 40 && p + 34 <= b.Length)
                        format = BitConverter.ToInt16(b, p + 32);
                }
                else if (id == "data") { dataPos = p + 8; dataLen = sz; break; }
                if (sz < 0) break;
                p += 8 + sz + (sz & 1);
            }
            if (dataPos < 0 || channels < 1) { error = "no audio data chunk"; return null; }
            if (dataPos + dataLen > b.Length) dataLen = b.Length - dataPos;

            // Only uncompressed PCM (1) and IEEE float (3) are decodable; reject MP3/ADPCM/etc.
            // rather than reading their bytes as garbage PCM.
            if (format != 1 && format != 3) { error = "unsupported WAV codec (format=" + (format & 0xFFFF) + ") — export 16-bit PCM"; return null; }
            if (bits != 8 && bits != 16 && bits != 24 && bits != 32) { error = "unsupported bit depth (" + bits + ")"; return null; }

            int bytesPer = bits / 8;
            if (bytesPer < 1) { error = "unsupported bit depth (" + bits + ")"; return null; }
            int total = dataLen / bytesPer;
            var f = new float[total];
            for (int i = 0; i < total; i++)
            {
                int o = dataPos + i * bytesPer;
                float s;
                if (format == 3 && bits == 32) s = BitConverter.ToSingle(b, o);
                else if (bits == 24)
                {
                    int v = b[o] | (b[o + 1] << 8) | (b[o + 2] << 16);
                    if ((v & 0x800000) != 0) v |= unchecked((int)0xFF000000);   // sign-extend 24->32
                    s = v / 8388608f;
                }
                else if (bits == 16) s = BitConverter.ToInt16(b, o) / 32768f;
                else if (format == 1 && bits == 32) s = BitConverter.ToInt32(b, o) / 2147483648f;
                else if (bits == 8) s = (b[o] - 128) / 128f;
                else s = 0f;
                f[i] = s;
            }
            return f;
        }
    }
}
