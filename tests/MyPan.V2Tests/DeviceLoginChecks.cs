using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using MyPan.Contracts;
using MyPan.Sync;

internal static class DeviceLoginChecks
{
    internal static async Task<string> Run(HttpClient webAdmin, string url, string adminPassword, Guid privateFile, Action<bool, string> assert)
    {
        using var connection = new CloudConnection(url);
        var login = await connection.Login("admin", adminPassword, default);
        assert(login.User.IsAdmin && login.ExpiresUtc > DateTimeOffset.UtcNow.AddDays(89), "Remembered device login retains account identity and has a renewable 90-day lifetime");
        var folders = await connection.Http.GetFromJsonAsync("/api/sync-folders", ApiJson.Default.ListSyncFolder) ?? [];
        assert(folders.Any(x => x.Path == "个人/admin") && !folders.Any(x => x.Path.StartsWith("个人/alice", StringComparison.Ordinal) || x.Path.StartsWith("个人/bob", StringComparison.Ordinal)) && !folders.Any(x => x.Path == "个人"), "Administrator device sees its own private home without exposing other personal folders or duplicate containers");
        foreach (var route in new[] { "/api/content/" + privateFile, "/api/preview/" + privateFile, "/api/entries/" + privateFile + "/versions", "/api/admin/users", "/api/admin/backup" })
        { using var denied = await connection.Http.GetAsync(route); assert(denied.StatusCode == HttpStatusCode.Forbidden, "Device administrator cannot bypass private access or web administration: " + route.Split('/')[2]); }
        connection.Http.DefaultRequestHeaders.Remove("X-MyPan-Client"); connection.Http.DefaultRequestHeaders.Add("X-MyPan-Client", "web");
        using (var denied = await connection.Http.GetAsync("/api/content/" + privateFile)) assert(denied.StatusCode == HttpStatusCode.Forbidden, "Forging a web client header cannot elevate a device credential");
        using (var web = await webAdmin.GetAsync("/api/content/" + privateFile)) assert(web.IsSuccessStatusCode, "Web administrator retains authorized private-file administration");
        using (var response = await connection.Http.PostAsync("/api/auth/session", null))
        {
            response.EnsureSuccessStatusCode(); var renewed = (await response.Content.ReadFromJsonAsync(ApiJson.Default.LoginResult))!;
            using var stillDevice = new CloudConnection(url, renewed.AccessToken);
            using var denied = await stillDevice.Http.GetAsync("/api/content/" + privateFile);
            assert(denied.StatusCode == HttpStatusCode.Forbidden, "Session exchange preserves device restrictions instead of upgrading administrator authority");
        }
        var resumed = await connection.Resume(default);
        assert(resumed.AccessToken == login.AccessToken && resumed.User.Id == login.User.Id, "Resume validates and renews the saved credential without requiring a password or unsafe token rotation");
        var userName = "remember-test";
        using var create = await webAdmin.PostAsJsonAsync("/api/admin/users", new SaveUser(userName, "Remember test", adminPassword), ApiJson.Default.SaveUser); create.EnsureSuccessStatusCode();
        var user = (await create.Content.ReadFromJsonAsync(ApiJson.Default.UserInfo))!;
        using var remembered = new CloudConnection(url); var initial = await remembered.Login(userName, adminPassword, default);
        using (var reset = await webAdmin.PostAsJsonAsync("/api/admin/users/" + user.Id + "/reset-password", new ResetPassword(adminPassword + "-changed"), ApiJson.Default.ResetPassword)) reset.EnsureSuccessStatusCode();
        using (var rejected = await remembered.Http.PostAsync("/api/auth/device/resume", null)) assert(rejected.StatusCode == HttpStatusCode.Unauthorized, "Password reset revokes remembered device login immediately");
        await remembered.Login(userName, adminPassword + "-changed", default);
        using (var disable = await webAdmin.PutAsJsonAsync("/api/admin/users/" + user.Id, new SaveUser(userName, "Remember test", null, false, true), ApiJson.Default.SaveUser)) disable.EnsureSuccessStatusCode();
        using (var rejected = await remembered.Http.PostAsync("/api/auth/device/resume", null)) assert(rejected.StatusCode == HttpStatusCode.Unauthorized, "Disabling an account revokes remembered login");
        using var logoutConnection = new CloudConnection(url); var logoutLogin = await logoutConnection.Login("admin", adminPassword, default);
        using (var logout = await logoutConnection.Http.PostAsync("/api/auth/logout", null)) logout.EnsureSuccessStatusCode();
        using (var rejected = await logoutConnection.Http.PostAsync("/api/auth/device/resume", null)) assert(rejected.StatusCode == HttpStatusCode.Unauthorized, "Explicit sign-out revokes a saved credential on the server");
        assert(SyncProfiles.DisplayRemote("个人/alice", "alice") == "个人" && SyncProfiles.DisplayRemote("个人/alice/documents", "alice") == "个人/documents", "Personal paths display without the username while preserving stored remote identity");
        return login.AccessToken;
    }
}
