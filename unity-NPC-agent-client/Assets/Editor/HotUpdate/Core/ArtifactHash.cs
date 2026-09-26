using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json.Linq;

public static class ArtifactHash
{
    public static string Sha256(byte[] bytes)
    {
        if (bytes == null)
            throw new ArgumentNullException(nameof(bytes));
        using SHA256 sha = SHA256.Create();
        return BitConverter.ToString(sha.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }

    public static string Sha256(string value) =>
        Sha256(Encoding.UTF8.GetBytes(value ?? throw new ArgumentNullException(nameof(value))));

    public static string Sha256File(string path) => Sha256(File.ReadAllBytes(path));

    public static JObject FileRecord(string repositoryRoot, string path) => new JObject
    {
        ["path"] = Path.GetRelativePath(repositoryRoot, path).Replace('\\', '/'),
        ["length"] = new FileInfo(path).Length,
        ["sha256"] = Sha256File(path)
    };
}
