using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Microsoft.Extensions.DependencyInjection;
using Winche.Core;

namespace Winche.Server;

/// <summary>
/// Encrypts ASP.NET's data-protection keys (console sign-in cookies, invite and reset links) at rest with AES-GCM, under
/// a key derived from the project's master key (<see cref="WincheKeys"/>, purpose <c>data-protection</c>): a copy of the
/// database alone can't forge a console session.
/// </summary>
internal sealed class MasterKeyXmlEncryptor(WincheKeys keys) : IXmlEncryptor
{
    public EncryptedXmlInfo Encrypt(XElement plaintextElement)
    {
        var plain = Encoding.UTF8.GetBytes(plaintextElement.ToString(SaveOptions.DisableFormatting));
        var nonce = RandomNumberGenerator.GetBytes(12);
        var tag = new byte[16];
        var cipher = new byte[plain.Length];
        using (var aes = new AesGcm(MasterKeyXmlDecryptor.KeyFrom(keys), 16)) aes.Encrypt(nonce, plain, cipher, tag);
        var element = new XElement("encryptedKey",
            new XComment(" Encrypted with a key derived from the Winche master key. "),
            new XElement("value", Convert.ToBase64String([.. nonce, .. tag, .. cipher])));
        return new EncryptedXmlInfo(element, typeof(MasterKeyXmlDecryptor));
    }
}

/// <summary>Decrypts what <see cref="MasterKeyXmlEncryptor"/> wrote; ASP.NET creates it with the app's services.</summary>
internal sealed class MasterKeyXmlDecryptor(IServiceProvider services) : IXmlDecryptor
{
    /// <summary>The 32-byte AES key: HKDF over the master-key-derived key for <c>data-protection</c>.</summary>
    internal static byte[] KeyFrom(WincheKeys keys) => KeyFrom(keys.Derive("data-protection"));

    private static byte[] KeyFrom(string derived) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(derived), 32, Encoding.UTF8.GetBytes("winche-data-protection"));

    /// <summary>The previous master keys' keys, newest first.</summary>
    internal static IEnumerable<byte[]> PreviousKeysFrom(WincheKeys keys) => keys.DerivePrevious("data-protection").Select(KeyFrom);

    /// <summary>The key, or null when <paramref name="key"/> isn't the one it was encrypted with.</summary>
    internal static XElement? TryDecrypt(byte[] key, XElement encryptedElement)
    {
        var all = Convert.FromBase64String((string?)encryptedElement.Element("value")
            ?? throw new InvalidOperationException("An encrypted data-protection key has no value."));
        var plain = new byte[all.Length - 28];
        try
        {
            using var aes = new AesGcm(key, 16);
            aes.Decrypt(all.AsSpan(0, 12), all.AsSpan(28), all.AsSpan(12, 16), plain);
        }
        catch (AuthenticationTagMismatchException) { return null; }
        return XElement.Parse(Encoding.UTF8.GetString(plain));
    }

    public XElement Decrypt(XElement encryptedElement)
    {
        var keys = services.GetRequiredService<WincheKeys>();
        return new[] { KeyFrom(keys) }.Concat(PreviousKeysFrom(keys)).Select(k => TryDecrypt(k, encryptedElement)).FirstOrDefault(x => x is not null)
            ?? throw new CryptographicException("A console sign-in key can't be read with this master key: was it changed? Set the old one as a previous master key.");
    }
}
