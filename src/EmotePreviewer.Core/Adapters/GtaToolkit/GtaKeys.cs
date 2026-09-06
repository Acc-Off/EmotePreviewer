using System.Security.Cryptography;
using RageLib.GTA5.Cryptography;
using RageLib.GTA5.Cryptography.Helpers;

namespace EmotePreviewer.Core.Adapters.GtaToolkit;

/// <summary>
/// Loads the RPF decryption keys from user-provided key files and installs them into gta-toolkit's
/// <see cref="GTA5Constants"/>. Nothing is bundled and nothing is written back to disk.
/// </summary>
/// <remarks>
/// The current Legacy GTA5.exe only carries the AES key and the hash LUT in plain form, so gta-toolkit's
/// <c>GTA5Constants.Generate</c> (SHA1 search over the exe) cannot recover the NG keys / tables. The user creates
/// the four files once with the separate EmotePreviewerKeyTool; the format is the RageLib one read by <see cref="CryptoIO"/>.
/// </remarks>
public static class GtaKeys
{
    public const string AesKeyFile = "gtav_aes_key.dat";                    // 32 bytes
    public const string NgKeyFile = "gtav_ng_key.dat";                      // 101 × 272 bytes
    public const string NgDecryptTablesFile = "gtav_ng_decrypt_tables.dat"; // 17 × 16 × 256 × u32
    public const string HashLutFile = "gtav_hash_lut.dat";                  // 256 bytes
    public static readonly string[] RequiredFiles = { AesKeyFile, NgKeyFile, NgDecryptTablesFile, HashLutFile };

    /// <summary>Environment variable naming the key folder (second in the search order).</summary>
    public const string KeyFolderVariable = "EMOTEPREVIEWER_KEYS";

    /// <summary>Default key folder: %LOCALAPPDATA%\EmotePreviewer\keys.</summary>
    public static string DefaultKeyFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EmotePreviewer", "keys");

    public static bool IsInstalled => GTA5Constants.PC_AES_KEY != null && GTA5Constants.PC_NG_KEYS != null && GTA5Constants.PC_NG_DECRYPT_TABLES != null && GTA5Constants.PC_LUT != null;

    /// <summary>
    /// Resolves the key folder (<paramref name="explicitFolder"/> → <see cref="KeyFolderVariable"/> → <see cref="DefaultKeyFolder"/>)
    /// and installs the keys. Throws <see cref="KeyMaterialMissingException"/> with setup instructions when no complete folder exists.
    /// </summary>
    /// <returns>The folder the keys were loaded from.</returns>
    public static string Install(string? explicitFolder = null)
    {
        var candidates = new List<(string source, string? folder)>
        {
            ("--keys", explicitFolder),
            ("environment variable " + KeyFolderVariable, Environment.GetEnvironmentVariable(KeyFolderVariable)),
            ("default folder", DefaultKeyFolder),
        };
        var tried = new List<string>();
        foreach (var (source, folder) in candidates)
        {
            if (string.IsNullOrWhiteSpace(folder)) continue;
            var missing = RequiredFiles.Where(f => !File.Exists(Path.Combine(folder, f))).ToList();
            if (missing.Count == 0)
            {
                InstallFromFolder(folder);
                return folder;
            }
            tried.Add($"{folder} ({source}): missing {string.Join(", ", missing)}");
        }
        throw new KeyMaterialMissingException(
            "GTA V key files not found. The current GTA5.exe does not contain the NG keys in plain form, so they must be " +
            "supplied as files. Create them once from your own GTA V install with EmotePreviewerKeyTool (separate repository) and put " +
            $"{string.Join(", ", RequiredFiles)} into {DefaultKeyFolder} (or pass --keys <folder> / set {KeyFolderVariable}).\n" +
            "Looked in:\n  " + string.Join("\n  ", tried));
    }

    /// <summary>Loads the four key files from <paramref name="folder"/>, verifies them against the known SHA1 hashes, and installs them.</summary>
    public static void InstallFromFolder(string folder)
    {
        var aes = File.ReadAllBytes(Path.Combine(folder, AesKeyFile));
        var ngKeys = CryptoIO.ReadNgKeys(Path.Combine(folder, NgKeyFile));
        var tables = CryptoIO.ReadNgTables(Path.Combine(folder, NgDecryptTablesFile));
        var lut = File.ReadAllBytes(Path.Combine(folder, HashLutFile));

        Verify(aes, GTA5HashConstants.PC_AES_KEY_HASH, AesKeyFile);
        Verify(lut, GTA5HashConstants.PC_LUT_HASH, HashLutFile);
        for (int i = 0; i < ngKeys.Length; i++) Verify(ngKeys[i], GTA5HashConstants.PC_NG_KEY_HASHES[i], $"{NgKeyFile} key {i}");
        var tableBytes = new byte[0x400];
        for (int i = 0; i < 17; i++)
            for (int j = 0; j < 16; j++)
            {
                Buffer.BlockCopy(tables[i][j], 0, tableBytes, 0, 0x400);
                Verify(tableBytes, GTA5HashConstants.PC_NG_DECRYPT_TABLE_HASHES[i * 16 + j], $"{NgDecryptTablesFile} table {i}/{j}");
            }

        GTA5Constants.PC_AES_KEY = aes;
        GTA5Constants.PC_NG_KEYS = ngKeys;
        GTA5Constants.PC_NG_DECRYPT_TABLES = tables;
        GTA5Constants.PC_LUT = lut;
    }

    static void Verify(byte[] data, byte[] expectedSha1, string what)
    {
        if (!SHA1.HashData(data).AsSpan().SequenceEqual(expectedSha1))
            throw new KeyMaterialMissingException($"{what} does not match the expected SHA1 hash; the key file is corrupt or from a different game version");
    }

    public sealed class KeyMaterialMissingException : Exception
    {
        public KeyMaterialMissingException(string message) : base(message) { }
    }
}
