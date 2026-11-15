using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.AspNetCore.DataProtection.XmlEncryption;
using Npgsql;

namespace Winche.Server;

/// <summary>
/// Keeps ASP.NET's data-protection keys (console sign-in cookies, reset links) in the database, so restarts and
/// several servers share them: nobody is signed out when a container is replaced. With an
/// <paramref name="encryptor"/>, keys stored before encryption was on are encrypted in place on first use.
/// </summary>
internal sealed class DataProtectionKeys(NpgsqlDataSource db, IXmlEncryptor? encryptor = null, Winche.Core.WincheKeys? keys = null) : IXmlRepository
{
    private static readonly XNamespace Dp = "http://schemas.asp.net/2015/03/dataProtection";
    private static readonly XName RequiresEncryption = Dp + "requiresEncryption";
    private static readonly XName EncryptedSecret = Dp + "encryptedSecret";

    private bool _ready;

    private void Ensure()
    {
        if (_ready) return;
        // IXmlRepository is synchronous; this runs once, on the first key read or write.
        Winche.Core.WincheSchema.EnsureAsync(db, "server",
            ["CREATE TABLE IF NOT EXISTS winche_server_keys (name text PRIMARY KEY, xml text NOT NULL, created_at timestamptz NOT NULL DEFAULT now())"])
            .GetAwaiter().GetResult();
        if (encryptor is not null) EncryptStoredKeys(encryptor);
        if (encryptor is not null && keys is { PreviousKeys.Count: > 0 }) ReencryptPrevious(encryptor, keys);
        _ready = true;
    }

    /// <summary>
    /// Wraps every secret still in plain text the way ASP.NET does for a new key (<c>encryptedSecret</c> naming the
    /// decryptor), so keys stored before encryption was on stop being readable from the database alone.
    /// </summary>
    internal void EncryptStoredKeys(IXmlEncryptor with)
    {
        var rows = new List<(string Name, XElement Xml)>();
        using (var read = db.CreateCommand("SELECT name, xml FROM winche_server_keys"))
        using (var r = read.ExecuteReader())
            while (r.Read()) rows.Add((r.GetString(0), XElement.Parse(r.GetString(1))));
        foreach (var (name, xml) in rows)
        {
            var plain = xml.Descendants().Where(e => (bool?)e.Attribute(RequiresEncryption) == true && e.Ancestors(EncryptedSecret).FirstOrDefault() is null).ToList();
            if (plain.Count == 0) continue;
            foreach (var secret in plain)
            {
                var info = with.Encrypt(new XElement(secret));
                secret.ReplaceWith(new XElement(EncryptedSecret,
                    new XAttribute("decryptorType", info.DecryptorType.AssemblyQualifiedName!), info.EncryptedElement));
            }
            using var write = db.CreateCommand("UPDATE winche_server_keys SET xml = $2 WHERE name = $1");
            write.Parameters.AddWithValue(name);
            write.Parameters.AddWithValue(xml.ToString(SaveOptions.DisableFormatting));
            write.ExecuteNonQuery();
        }
    }

    /// <summary>After a master key change: keys encrypted under a previous master key are encrypted with the current one.</summary>
    internal void ReencryptPrevious(IXmlEncryptor with, Winche.Core.WincheKeys keys)
    {
        var current = MasterKeyXmlDecryptor.KeyFrom(keys);
        var previous = MasterKeyXmlDecryptor.PreviousKeysFrom(keys).ToList();
        var rows = new List<(string Name, string Raw)>();
        using (var read = db.CreateCommand("SELECT name, xml FROM winche_server_keys"))
        using (var r = read.ExecuteReader())
            while (r.Read()) rows.Add((r.GetString(0), r.GetString(1)));
        foreach (var (name, raw) in rows)
        {
            var xml = XElement.Parse(raw);
            var changed = false;
            foreach (var secret in xml.Descendants(EncryptedSecret).ToList())
            {
                if ((string?)secret.Attribute("decryptorType") is not { } type || !type.Contains(nameof(MasterKeyXmlDecryptor))) continue;
                if (secret.Elements().FirstOrDefault() is not { } inner || MasterKeyXmlDecryptor.TryDecrypt(current, inner) is not null) continue;
                if (previous.Select(k => MasterKeyXmlDecryptor.TryDecrypt(k, inner)).FirstOrDefault(x => x is not null) is not { } plain) continue;
                inner.ReplaceWith(with.Encrypt(plain).EncryptedElement);
                changed = true;
            }
            if (!changed) continue;
            // Only if nobody changed it meanwhile (another project or server doing the same).
            using var write = db.CreateCommand("UPDATE winche_server_keys SET xml = $3 WHERE name = $1 AND xml = $2");
            write.Parameters.AddWithValue(name);
            write.Parameters.AddWithValue(raw);
            write.Parameters.AddWithValue(xml.ToString(SaveOptions.DisableFormatting));
            write.ExecuteNonQuery();
        }
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        Ensure();
        using var cmd = db.CreateCommand("SELECT xml FROM winche_server_keys ORDER BY created_at");
        using var r = cmd.ExecuteReader();
        var list = new List<XElement>();
        while (r.Read()) list.Add(XElement.Parse(r.GetString(0)));
        return list;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        Ensure();
        using var cmd = db.CreateCommand("INSERT INTO winche_server_keys (name, xml) VALUES ($1, $2) ON CONFLICT (name) DO UPDATE SET xml = EXCLUDED.xml");
        cmd.Parameters.AddWithValue(friendlyName);
        cmd.Parameters.AddWithValue(element.ToString(SaveOptions.DisableFormatting));
        cmd.ExecuteNonQuery();
    }
}
