using System.Security.Cryptography;
using System.Text;

namespace SingleSignOn.Services;

/// <summary>
/// Reproduces — byte-for-byte — the two password representations the destination
/// "one_leave" app stores, so the portal can reset a password WITHOUT calling or
/// modifying that app. Both are derived from the same plaintext:
///
///  • <see cref="HashIdentityV2"/>  → one_leave.dbo.AspNetUsers.PasswordHash
///       ASP.NET Identity v2 (MVC5): Base64( 0x00 + salt(16) + subkey(32) ),
///       subkey = PBKDF2(pw, salt, 1000 iters, HMAC-SHA1, 32 bytes).
///  • <see cref="EncryptOneSecurity"/> → one_leave.dbo.log_users.user_password
///       legacy "OneSecurity": Base64( TripleDES-ECB/PKCS7( UTF8(pw), key = MD5("GBHPOS") ) ).
///
/// NOTE: MD5 / TripleDES / SHA-1 are obsolete, but are MANDATORY here for binary
/// interop with the existing app — do not "upgrade" them. Kept isolated to this file.
/// </summary>
#pragma warning disable SYSLIB0021 // legacy crypto types required for interop
#pragma warning disable CA5350, CA5351, CA5358 // weak algorithms required for interop
public static class LegacyPasswordCrypto
{
    private const string OneSecurityKey = "GBHPOS";

    // ---- ASP.NET Identity v2 (AspNetUsers.PasswordHash) -----------------------------

    private const int SaltSize = 16, SubkeySize = 32, Pbkdf2Iterations = 1000;

    /// <summary>Produce an Identity v2 password hash the destination app will accept.</summary>
    public static string HashIdentityV2(string password)
    {
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] subkey = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA1, SubkeySize);

        var output = new byte[1 + SaltSize + SubkeySize]; // 49 bytes
        output[0] = 0x00;                                 // version marker (Identity v2)
        Buffer.BlockCopy(salt, 0, output, 1, SaltSize);
        Buffer.BlockCopy(subkey, 0, output, 1 + SaltSize, SubkeySize);
        return Convert.ToBase64String(output);
    }

    /// <summary>Verify a plaintext against an Identity v2 hash (used only for self-testing).</summary>
    public static bool VerifyIdentityV2(string hashedPassword, string password)
    {
        byte[] decoded;
        try { decoded = Convert.FromBase64String(hashedPassword); }
        catch { return false; }

        if (decoded.Length != 1 + SaltSize + SubkeySize || decoded[0] != 0x00) return false;

        var salt = new byte[SaltSize];
        Buffer.BlockCopy(decoded, 1, salt, 0, SaltSize);
        var stored = new byte[SubkeySize];
        Buffer.BlockCopy(decoded, 1 + SaltSize, stored, 0, SubkeySize);

        byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Pbkdf2Iterations, HashAlgorithmName.SHA1, SubkeySize);
        return CryptographicOperations.FixedTimeEquals(actual, stored);
    }

    // ---- Legacy "OneSecurity" 3DES (log_users.user_password) ------------------------

    /// <summary>Encrypt a plaintext exactly like the legacy OneSecurity.Encryption.</summary>
    public static string EncryptOneSecurity(string password)
    {
        byte[] input = Encoding.UTF8.GetBytes(password);
        using var enc = OneSecurityTransform(encrypt: true);
        return Convert.ToBase64String(enc.TransformFinalBlock(input, 0, input.Length));
    }

    /// <summary>Decrypt an OneSecurity ciphertext — used to cross-check against real data.</summary>
    public static string DecryptOneSecurity(string cipherBase64)
    {
        byte[] cipher = Convert.FromBase64String(cipherBase64);
        using var dec = OneSecurityTransform(encrypt: false);
        return Encoding.UTF8.GetString(dec.TransformFinalBlock(cipher, 0, cipher.Length));
    }

    /// <summary>
    /// Build a TripleDES ECB/PKCS7 transform keyed with MD5("GBHPOS"). The 16-byte (2-key)
    /// key is normally fine, but .NET's TripleDES.Key setter rejects keys it deems "weak";
    /// if that happens we fall back to a manual DES-EDE (K1|K2|K1) which yields identical
    /// bytes while bypassing the guard (the legacy .NET Framework app effectively did the same).
    /// </summary>
    private static ICryptoTransform OneSecurityTransform(bool encrypt)
    {
        byte[] key = MD5.HashData(Encoding.ASCII.GetBytes(OneSecurityKey)); // 16 bytes
        try
        {
            using var tdes = TripleDES.Create();
            tdes.Mode = CipherMode.ECB;
            tdes.Padding = PaddingMode.PKCS7;
            tdes.Key = key; // throws CryptographicException if flagged weak
            return encrypt ? tdes.CreateEncryptor() : tdes.CreateDecryptor();
        }
        catch (CryptographicException)
        {
            return new DesEdeEcbPkcs7Transform(key, encrypt); // identical output, no weak-key guard
        }
    }

    /// <summary>
    /// Manual two-key Triple-DES (EDE, K3 = K1) in ECB mode with PKCS7 padding — a faithful
    /// fallback for the rare key that DES is happy with but TripleDES rejects as weak.
    /// </summary>
    private sealed class DesEdeEcbPkcs7Transform : ICryptoTransform
    {
        private readonly byte[] _k1, _k2;
        private readonly bool _encrypt;

        public DesEdeEcbPkcs7Transform(byte[] key16, bool encrypt)
        {
            _k1 = key16[..8];
            _k2 = key16[8..16];
            _encrypt = encrypt;
        }

        public int InputBlockSize => 8;
        public int OutputBlockSize => 8;
        public bool CanTransformMultipleBlocks => false;
        public bool CanReuseTransform => false;

        public int TransformBlock(byte[] inputBuffer, int inputOffset, int inputCount,
            byte[] outputBuffer, int outputOffset)
            => throw new NotSupportedException("Use TransformFinalBlock.");

        public byte[] TransformFinalBlock(byte[] inputBuffer, int inputOffset, int inputCount)
        {
            byte[] data = new byte[inputCount];
            Buffer.BlockCopy(inputBuffer, inputOffset, data, 0, inputCount);
            if (_encrypt)
            {
                data = Pkcs7Pad(data);
                return RunEde(data, encrypt: true);
            }
            byte[] plain = RunEde(data, encrypt: false);
            return Pkcs7Unpad(plain);
        }

        private byte[] RunEde(byte[] data, bool encrypt)
        {
            var outBuf = new byte[data.Length];
            for (int o = 0; o < data.Length; o += 8)
            {
                var block = data[o..(o + 8)];
                // EDE with K3 = K1:  encrypt = E_k1(D_k2(E_k1(b))),  decrypt = D_k1(E_k2(D_k1(b)))
                block = encrypt
                    ? Des(_k1, block, true)
                    : Des(_k1, block, false);
                block = encrypt
                    ? Des(_k2, block, false)
                    : Des(_k2, block, true);
                block = encrypt
                    ? Des(_k1, block, true)
                    : Des(_k1, block, false);
                Buffer.BlockCopy(block, 0, outBuf, o, 8);
            }
            return outBuf;
        }

        private static byte[] Des(byte[] key, byte[] block, bool encrypt)
        {
            using var des = DES.Create();
            des.Mode = CipherMode.ECB;
            des.Padding = PaddingMode.None;
            des.Key = key;
            using var t = encrypt ? des.CreateEncryptor() : des.CreateDecryptor();
            return t.TransformFinalBlock(block, 0, 8);
        }

        private static byte[] Pkcs7Pad(byte[] data)
        {
            int pad = 8 - (data.Length % 8);
            if (pad == 0) pad = 8;
            var padded = new byte[data.Length + pad];
            Buffer.BlockCopy(data, 0, padded, 0, data.Length);
            for (int i = data.Length; i < padded.Length; i++) padded[i] = (byte)pad;
            return padded;
        }

        private static byte[] Pkcs7Unpad(byte[] data)
        {
            if (data.Length == 0) return data;
            int pad = data[^1];
            if (pad < 1 || pad > 8 || pad > data.Length) return data;
            return data[..^pad];
        }

        public void Dispose() { }
    }
}
#pragma warning restore SYSLIB0021
#pragma warning restore CA5350, CA5351, CA5358
