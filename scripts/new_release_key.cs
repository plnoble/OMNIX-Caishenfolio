// Generates the release signing key pair for the in-app updater.
//
// Run it with:  dotnet run scripts\new_release_key.cs
//
// The private key is written to a gitignored file and never printed: it is the one secret that
// makes the updater's signature check mean anything, and a key that has appeared on a screen or
// in a terminal scrollback should be treated as compromised. Only the public key is displayed,
// because that one is meant to be published — it gets compiled into the app.
//
// This uses the same APIs as the two places that must agree with it: the release workflow's
// ImportPkcs8PrivateKey, and ReleaseSignature.ImportSubjectPublicKeyInfo. It signs and verifies
// a sample before writing anything, so a key that would fail in the pipeline fails here first.

using System.Security.Cryptography;
using System.Text;

const string PrivateKeyFileName = "release-private-key.txt";

using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);

var privateKey = Convert.ToBase64String(ecdsa.ExportPkcs8PrivateKey());
var publicKey = Convert.ToBase64String(ecdsa.ExportSubjectPublicKeyInfo());

// Prove the pair round-trips through exactly the calls the pipeline makes, before it is trusted.
var sample = Encoding.UTF8.GetBytes("omnix release key self-test");

using var signer = ECDsa.Create();
signer.ImportPkcs8PrivateKey(Convert.FromBase64String(privateKey), out _);
var signature = signer.SignData(sample, HashAlgorithmName.SHA256);

using var verifier = ECDsa.Create();
verifier.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);

if (!verifier.VerifyData(sample, signature, HashAlgorithmName.SHA256))
{
    Console.Error.WriteLine("自检失败：生成的密钥对无法互相验证，未写入任何文件。");
    return 1;
}

var repoRoot = Directory.GetCurrentDirectory();
var privatePath = Path.Combine(repoRoot, PrivateKeyFileName);

if (File.Exists(privatePath))
{
    Console.Error.WriteLine($"已存在 {PrivateKeyFileName}，未覆盖。");
    Console.Error.WriteLine("若确定要换新密钥，请先手动删除该文件；换key后旧版本的签名将不再通过。");
    return 1;
}

File.WriteAllText(privatePath, privateKey, new UTF8Encoding(false));

Console.WriteLine();
Console.WriteLine("密钥对已生成，自检通过。");
Console.WriteLine();
Console.WriteLine($"私钥已写入： {privatePath}");
Console.WriteLine("  这是唯一一份，没有备份。它已被 .gitignore 排除，不会进仓库。");
Console.WriteLine("  用完请从磁盘删除——GitHub secret 里那份才是长期存放的地方。");
Console.WriteLine();
Console.WriteLine("下一步 1／2  把私钥设为仓库 secret（名字必须一模一样）：");
Console.WriteLine("  gh secret set OMNIX_RELEASE_PRIVATE_KEY < " + PrivateKeyFileName);
Console.WriteLine();
Console.WriteLine("下一步 2／2  把下面这行公钥交给 Claude，填进 ReleaseSignature.PublicKeyBase64：");
Console.WriteLine();
Console.WriteLine(publicKey);
Console.WriteLine();

return 0;
