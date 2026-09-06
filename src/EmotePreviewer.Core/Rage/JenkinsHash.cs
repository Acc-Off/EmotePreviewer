namespace EmotePreviewer.Core.Rage;

/// <summary>Jenkins one-at-a-time hash as used by RAGE for names (clip names, archive entry names, bone names).</summary>
public static class JenkinsHash
{
    /// <summary>Hashes the string as-is (bytes are the UTF-8 code units; RAGE names are ASCII).</summary>
    public static uint Hash(ReadOnlySpan<char> s)
    {
        uint h = 0;
        foreach (var c in s)
        {
            h += (byte)c;
            h += h << 10;
            h ^= h >> 6;
        }
        h += h << 3;
        h ^= h >> 11;
        h += h << 15;
        return h;
    }

    /// <summary>Hashes the lower-cased string (archive entry names are stored/looked up lower-case).</summary>
    public static uint HashLower(string s) => Hash(s.ToLowerInvariant());
}
