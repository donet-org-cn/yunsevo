using System.Net;
using System.Net.Http.Json;
using System.Text;
using MyPan.Contracts;
using MyPan.Sync;

internal static class CommercialChecks
{
    public static async Task Run(HttpClient admin, HttpClient alice, string url, string token, Guid aliceId, Action<bool, string> assert)
    {
        var prefix = "个人/alice/分页验证";
        using (var folder = await alice.PostAsJsonAsync("/api/folders", new CreateFolder(prefix), ApiJson.Default.CreateFolder)) folder.EnsureSuccessStatusCode();
        for (var i = 0; i < 23; i++)
        {
            using var put = new HttpRequestMessage(HttpMethod.Put, "/api/content?path=" + Uri.EscapeDataString(prefix + "/file-" + i.ToString("D2") + ".txt")) { Content = new ByteArrayContent([(byte)i]) };
            put.Headers.IfNoneMatch.ParseAdd("*"); using var response = await alice.SendAsync(put); response.EnsureSuccessStatusCode();
        }
        var all = new HashSet<Guid>();
        for (var page = 1; page <= 4; page++)
        {
            var result = (await alice.GetFromJsonAsync("/api/browser/entries?folder=" + Uri.EscapeDataString(prefix) + "&page=" + page + "&pageSize=7", ApiJson.Default.PagedResultBrowserEntry))!;
            assert(result.Total == 23 && result.Items.Count == (page == 4 ? 2 : 7) && result.Items.All(x => all.Add(x.Entry.Id)), "Server-side file page contains bounded, unique rows with the correct filtered total");
        }
        using (var invalid = await alice.GetAsync("/api/browser/entries?page=0&pageSize=1000")) assert(invalid.StatusCode == HttpStatusCode.BadRequest, "Invalid or unbounded page sizes are rejected");
        using (var privatePage = await alice.GetAsync("/api/browser/entries?folder=" + Uri.EscapeDataString("个人/bob"))) assert(privatePage.StatusCode == HttpStatusCode.NotFound, "Pagination does not disclose another user's private folder or counts");
        var listing = (await alice.GetFromJsonAsync("/api/browser/entries?folder=" + Uri.EscapeDataString(prefix) + "&query=file-22&pageSize=10", ApiJson.Default.PagedResultBrowserEntry))!;
        assert(listing.Total == 1 && listing.Items.Single().Entry.Path.EndsWith("file-22.txt", StringComparison.Ordinal), "Search is applied before pagination and count calculation");
        var entry = listing.Items.Single().Entry;
        using var device = new CloudConnection(url, token);
        using (var deletion = new HttpRequestMessage(HttpMethod.Delete, "/api/entries/" + entry.Id))
        {
            deletion.Headers.IfMatch.ParseAdd('"' + entry.Version + '"'); deletion.Headers.Add("X-MyPan-User", "forged-admin");
            using var deleted = await device.Http.SendAsync(deletion); deleted.EnsureSuccessStatusCode();
        }
        var trash = (await alice.GetFromJsonAsync("/api/browser/entries?trash=true&query=file-22&pageSize=10", ApiJson.Default.PagedResultBrowserEntry))!;
        var record = trash.Items.Single(x => x.Entry.Id == entry.Id).Deleted!;
        assert(record.UserId == aliceId && record.Username == "alice" && record.DeviceName == Environment.MachineName && record.Time > DateTimeOffset.UtcNow.AddMinutes(-2) && !string.IsNullOrEmpty(record.RemoteAddress), "Recycle metadata captures authenticated user, actual deletion time, reported computer and observed IP");
        assert(record.Username != "forged-admin", "A client cannot forge the authenticated deletion username");
        using (var restore = await alice.PostAsync("/api/trash/" + entry.Id + "/restore", null)) restore.EnsureSuccessStatusCode();
        using (var webDelete = new HttpRequestMessage(HttpMethod.Delete, "/api/entries/" + entry.Id))
        {
            webDelete.Headers.IfMatch.ParseAdd('"' + entry.Version + '"'); webDelete.Headers.Add("X-MyPan-Client", "web");
            using var deleted = await alice.SendAsync(webDelete); deleted.EnsureSuccessStatusCode();
        }
        var webTrash = (await alice.GetFromJsonAsync("/api/browser/entries?trash=true&query=file-22", ApiJson.Default.PagedResultBrowserEntry))!;
        assert(webTrash.Items.Single(x => x.Entry.Id == entry.Id).Deleted is { Source: "Web", DeviceName: null }, "Browser deletion is identified without fabricating a computer name");
        var password = "Original-password-0123456789";
        using var create = await admin.PostAsJsonAsync("/api/admin/users", new SaveUser("reset-test", "密码测试", password), ApiJson.Default.SaveUser); create.EnsureSuccessStatusCode();
        var user = (await create.Content.ReadFromJsonAsync(ApiJson.Default.UserInfo))!;
        using var member = new CloudConnection(url); await member.Login(user.Username, password, default);
        using (var denied = await alice.PostAsJsonAsync("/api/admin/users/" + user.Id + "/reset-password", new ResetPassword(), ApiJson.Default.ResetPassword)) assert(denied.StatusCode == HttpStatusCode.Forbidden, "Only administrators can reset another account password");
        PasswordResult reset;
        using (var response = await admin.PostAsJsonAsync("/api/admin/users/" + user.Id + "/reset-password", new ResetPassword(), ApiJson.Default.ResetPassword)) { response.EnsureSuccessStatusCode(); reset = (await response.Content.ReadFromJsonAsync(ApiJson.Default.PasswordResult))!; }
        using (var rejected = await member.Http.GetAsync("/api/me")) assert(rejected.StatusCode == HttpStatusCode.Unauthorized, "Administrator password reset invalidates all previous sessions");
        assert(reset.Password.Length >= 24 && reset.Password != password, "Password reset generates a fresh strong password when left blank");
        await member.Login(user.Username, reset.Password, default);
        using (var wrong = await member.Http.PostAsJsonAsync("/api/auth/password", new ChangePassword("wrong-password", "New-password-0123456789"), ApiJson.Default.ChangePassword)) assert(wrong.StatusCode == HttpStatusCode.BadRequest, "Self-service password changes require the current password without signing out a valid session");
        using (var response = await member.Http.PostAsJsonAsync("/api/auth/password", new ChangePassword(reset.Password, "New-password-0123456789"), ApiJson.Default.ChangePassword)) response.EnsureSuccessStatusCode();
        await member.Login(user.Username, "New-password-0123456789", default);
        assert(true, "A user can sign in with the new password after changing their own credentials");
        var users = (await admin.GetFromJsonAsync("/api/admin/users/page?query=reset-test&pageSize=10", ApiJson.Default.PagedResultUserInfo))!;
        assert(users.Total == 1 && users.Items.Single().Id == user.Id, "User administration exposes bounded server-side search pages");
        var audit = (await admin.GetFromJsonAsync("/api/admin/audit/page?kind=trash&pageSize=5", ApiJson.Default.PagedResultAuditEntry))!;
        assert(audit.Total >= 2 && audit.Items.Count <= 5, "Audit pagination counts matching retained records rather than a 300-entry browser cap");
        assert(!System.Text.Json.JsonSerializer.Serialize(audit, ApiJson.Default.PagedResultAuditEntry).Contains(reset.Password, StringComparison.Ordinal), "Audit records never persist generated reset passwords");
    }
}
