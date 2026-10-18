using Android.Content;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using System.Text.Json;
using MyPan.Mobile.Core;

namespace MyPan.Mobile.Android;

internal sealed class AndroidAccountVault(Context context) : IAccountVault
{
    private const string Alias = "Yunsevo.Mobile.Session.v1";
    private Java.Security.IKey Key()
    {
        using var store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);
        if (store.ContainsAlias(Alias)) return store.GetKey(Alias, null)!;
        using var generator = KeyGenerator.GetInstance("AES", "AndroidKeyStore")!;
        using var spec = new KeyGenParameterSpec.Builder(Alias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
            .SetBlockModes("GCM")!.SetEncryptionPaddings("NoPadding")!.SetRandomizedEncryptionRequired(true)!.Build();
        generator.Init(spec); return generator.GenerateKey()!;
    }
    public MobileAccount? Load()
    {
        var value = context.GetSharedPreferences("yunsevo-vault", FileCreationMode.Private)!.GetString("session", null);
        if (value == null) return null;
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length < 29) throw new InvalidDataException("登录凭据格式无效。");
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.DecryptMode, Key(), new GCMParameterSpec(128, bytes[..12]));
        var json = cipher.DoFinal(bytes[12..])!;
        return JsonSerializer.Deserialize(json, MobileJson.Default.MobileAccount);
    }
    public void Save(MobileAccount account)
    {
        using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
        cipher.Init(CipherMode.EncryptMode, Key());
        var encrypted = cipher.DoFinal(JsonSerializer.SerializeToUtf8Bytes(account, MobileJson.Default.MobileAccount))!;
        var bytes = cipher.GetIV()!.Concat(encrypted).ToArray();
        if (!context.GetSharedPreferences("yunsevo-vault", FileCreationMode.Private)!.Edit()!.PutString("session", Convert.ToBase64String(bytes))!.Commit())
            throw new IOException("无法安全保存手机登录。");
    }
    public void Clear() => context.GetSharedPreferences("yunsevo-vault", FileCreationMode.Private)!.Edit()!.Remove("session")!.Commit();
}
