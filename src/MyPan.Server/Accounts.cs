using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyPan.Contracts;

namespace MyPan.Server;

public sealed record StoredUser(Guid Id, string Username, string DisplayName, bool IsAdmin, bool Disabled, string Salt, string PasswordHash);
public sealed record StoredSession(string Hash, Guid UserId, DateTimeOffset ExpiresUtc, bool Device = false);
public sealed record RequestActor(Guid Id, string Username, bool IsAdmin, bool WebAdmin = true);
public sealed record StoredVersion(Guid EntryId, string Version, long Size, DateTimeOffset ModifiedUtc);

public sealed partial class CloudStore
{
    private static readonly Guid BootstrapId = new("184997cf-2f9d-4341-a33c-d8ac6d10742f");
    private TaskCompletionSource pulse = NewPulse();
    private static TaskCompletionSource NewPulse() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private void SignalChange() => Interlocked.Exchange(ref pulse, NewPulse()).TrySetResult();
    public long Revision => Volatile.Read(ref state).Revision;
    public async Task WaitForChange(long revision, CancellationToken ct)
    {
        var pending = Volatile.Read(ref pulse).Task;
        if (Revision != revision) return;
        await pending.WaitAsync(TimeSpan.FromSeconds(15), ct);
    }
    private void InitializeSecurity(string key, string administrator)
    {
        if (state.Users.Count != 0) return;
        var old = System.IO.Path.Combine(root, "catalog.json");
        var (salt, hash) = HashPassword(key);
        if (File.Exists(old)) File.Copy(old, old + ".v1-backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff"), false);
        state = state with { Users = [new(BootstrapId, administrator, "管理员", true, false, salt, hash)], JournalGeneration = Guid.NewGuid() };
        AtomicFile.WriteText(old, JsonSerializer.Serialize(state, StoreJson.Default.StoreState));
        contentJournal.Reset(state.JournalGeneration);
    }
    private static (string Salt, string Hash) HashPassword(string password)
    {
        if (password.Length is < 12 or > 256) throw new StoreException(400, "密码长度须为 12–256 个字符。");
        var salt = RandomNumberGenerator.GetBytes(16);
        return (Convert.ToBase64String(salt), Convert.ToBase64String(Rfc2898DeriveBytes.Pbkdf2(password, salt, 600000, HashAlgorithmName.SHA256, 32)));
    }
    private static string TokenHash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    private static UserInfo PublicUser(StoredUser user) => new(user.Id, user.Username, user.DisplayName, user.IsAdmin, user.Disabled);
    public RequestActor? BootstrapActor() => ActorFor(BootstrapId);
    private RequestActor? ActorFor(Guid id)
    {
        var user = Volatile.Read(ref state).Users.Find(x => x.Id == id && !x.Disabled);
        return user == null ? null : new(user.Id, user.Username, user.IsAdmin);
    }
    public RequestActor? Authenticate(string token)
    {
        if (token.Length is < 32 or > 256) return null;
        var hash = TokenHash(token);
        var session = Volatile.Read(ref state).Sessions.Find(x => x.Hash == hash && x.ExpiresUtc > DateTimeOffset.UtcNow);
        return session == null ? null : ActorFor(session.UserId) is { } actor ? actor with { WebAdmin = !session.Device } : null;
    }
    public UserInfo Me(RequestActor actor) => PublicUser(state.Users.Single(x => x.Id == actor.Id));
    public async Task<LoginResult> Login(LoginRequest request, bool device = false)
    {
        var user = VerifyPassword(request);
        return await CreateSession(new(user.Id, user.Username, user.IsAdmin, !device), user.PasswordHash);
    }
    private StoredUser VerifyPassword(LoginRequest request)
    {
        if (request.Username.Length > 64 || request.Password.Length > 256) throw new StoreException(401, "用户名或密码错误。");
        var user = Volatile.Read(ref state).Users.Find(x => x.Username.Equals(request.Username.Trim(), StringComparison.OrdinalIgnoreCase));
        // Use the same password KDF even when the username doesn't exist.
        var salt = user?.Salt ?? Convert.ToBase64String(new byte[16]);
        var expected = user?.PasswordHash ?? Convert.ToBase64String(new byte[32]);
        var hash = Rfc2898DeriveBytes.Pbkdf2(request.Password, Convert.FromBase64String(salt), 600000, HashAlgorithmName.SHA256, 32);
        if (!CryptographicOperations.FixedTimeEquals(hash, Convert.FromBase64String(expected)) || user == null || user.Disabled)
            throw new StoreException(401, "用户名或密码错误。");
        return user;
    }
    public RequestActor PasswordActor(string username, string password)
    { var user = VerifyPassword(new(username, password)); return new(user.Id, user.Username, user.IsAdmin, false); }
    public RequestActor ProtocolActor(string username) => (ActorFor(state.Users.Find(x => x.Username.Equals(username, StringComparison.OrdinalIgnoreCase))?.Id ?? Guid.Empty)
        ?? throw new StoreException(401, "账户已停用或不存在。")) with { WebAdmin = false };
    public void RequireAdmin(RequestActor actor) => Admin(actor);
    public async Task<LoginResult> CreateSession(RequestActor actor, string? verifiedHash = null)
    {
        await gate.WaitAsync();
        try
        {
            var current = ActorFor(actor.Id) ?? throw new StoreException(401, "账户已停用。");
            if (verifiedHash != null && state.Users.Single(x => x.Id == actor.Id).PasswordHash != verifiedHash) throw new StoreException(401, "密码已变更，请重新登录。");
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            var expires = actor.WebAdmin ? DateTimeOffset.UtcNow.AddHours(12) : DateTimeOffset.UtcNow.AddDays(90);
            var sessions = state.Sessions.Where(x => x.ExpiresUtc > DateTimeOffset.UtcNow).ToList();
            if (sessions.Count(x => x.UserId == actor.Id) >= 20) sessions.Remove(sessions.First(x => x.UserId == actor.Id));
            sessions.Add(new(TokenHash(token), actor.Id, expires, !actor.WebAdmin));
            await CommitState(state with { Sessions = sessions });
            return new(token, expires, Me(current));
        }
        finally { gate.Release(); }
    }
    public async Task Logout(string token)
    {
        await gate.WaitAsync();
        try { await CommitState(state with { Sessions = state.Sessions.Where(x => x.Hash != TokenHash(token)).ToList() }); }
        finally { gate.Release(); }
    }
    public async Task<LoginResult> ResumeDevice(string token)
    {
        await gate.WaitAsync();
        try
        {
            var hash = TokenHash(token);
            var stored = state.Sessions.Find(x => x.Hash == hash && x.Device && x.ExpiresUtc > DateTimeOffset.UtcNow)
                ?? throw new StoreException(401, "设备登录已失效，请重新登录。");
            var actor = ActorFor(stored.UserId) ?? throw new StoreException(401, "账户已停用。");
            var expires = DateTimeOffset.UtcNow.AddDays(90);
            // The credential is stable so a crash or lost response cannot invalidate the saved login.
            if (stored.ExpiresUtc < expires.AddDays(-1))
                await CommitState(state with { Sessions = state.Sessions.Select(x => x.Hash == hash ? x with { ExpiresUtc = expires } : x).ToList() });
            else expires = stored.ExpiresUtc;
            return new(token, expires, Me(actor));
        }
        finally { gate.Release(); }
    }
    private void Admin(RequestActor actor) { if (!actor.WebAdmin || !state.Users.Any(x => x.Id == actor.Id && x.IsAdmin && !x.Disabled)) throw new StoreException(403, "此操作仅限网页管理员。"); }
    public List<UserInfo> Users(RequestActor actor) { Admin(actor); return state.Users.Select(PublicUser).ToList(); }
    public async Task<PasswordResult> ResetPassword(RequestActor actor, Guid id, ResetPassword request)
    {
        Admin(actor);
        var password = request.Password ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(24));
        var (salt, hash) = HashPassword(password);
        await gate.WaitAsync();
        try
        {
            Admin(actor);
            var user = state.Users.Find(x => x.Id == id) ?? throw new StoreException(404, "用户不存在。");
            await CommitState(state with { Users = state.Users.Select(x => x.Id == id ? x with { Salt = salt, PasswordHash = hash } : x).ToList(), Sessions = state.Sessions.Where(x => x.UserId != id).ToList() });
            return new(user.Username, password);
        }
        finally { gate.Release(); }
    }
    public async Task ChangePassword(RequestActor actor, ChangePassword request)
    {
        StoredUser verified;
        try { verified = VerifyPassword(new(actor.Username, request.CurrentPassword)); }
        catch (StoreException ex) when (ex.Status == 401) { throw new StoreException(400, "当前密码不正确。"); }
        var (salt, hash) = HashPassword(request.NewPassword);
        await gate.WaitAsync();
        try
        {
            var current = state.Users.Find(x => x.Id == actor.Id && !x.Disabled);
            if (current == null || current.PasswordHash != verified.PasswordHash) throw new StoreException(401, "账户已变更，请重新登录。");
            await CommitState(state with { Users = state.Users.Select(x => x.Id == actor.Id ? x with { Salt = salt, PasswordHash = hash } : x).ToList(), Sessions = state.Sessions.Where(x => x.UserId != actor.Id).ToList() });
        }
        finally { gate.Release(); }
    }
    public List<GroupInfo> Groups(RequestActor actor) { Admin(actor); return [.. state.Groups]; }
    public List<UserInfo> Principals() => state.Users.Where(x => !x.Disabled).Select(PublicUser).ToList();
    public List<GroupInfo> GroupNames() => state.Groups.Select(x => x with { Members = [] }).ToList();
    public async Task<UserInfo> SaveUser(RequestActor actor, Guid? id, SaveUser request)
    {
        Admin(actor);
        var name = request.Username.Trim();
        if (name.Length is < 3 or > 64 || name.Any(x => !char.IsAsciiLetterOrDigit(x) && x is not ('.' or '_' or '-')))
            throw new StoreException(400, "用户名须为 3–64 位英文字母、数字、点、横线或下划线。");
        if (request.DisplayName.Length > 100) throw new StoreException(400, "显示名称过长。");
        (string Salt, string Hash)? password = string.IsNullOrEmpty(request.Password) ? null : HashPassword(request.Password);
        await gate.WaitAsync();
        try
        {
            Admin(actor);
            var old = id.HasValue ? state.Users.Find(x => x.Id == id.Value) ?? throw new StoreException(404, "用户不存在。") : null;
            if (old != null && old.Username != name) throw new StoreException(400, "用户名不可修改，请修改显示名称。");
            if (state.Users.Any(x => x.Id != id && x.Username.Equals(name, StringComparison.OrdinalIgnoreCase))) throw new StoreException(409, "用户名已存在。");
            if (old == null && password == null) throw new StoreException(400, "新用户需要设置密码。");
            if (old is { IsAdmin: true, Disabled: false } && (!request.IsAdmin || request.Disabled) && !state.Users.Any(x => x.Id != old.Id && x.IsAdmin && !x.Disabled))
                throw new StoreException(409, "必须至少保留一个可用管理员。");
            var user = new StoredUser(old?.Id ?? Guid.NewGuid(), name, request.DisplayName.Trim(), request.IsAdmin, request.Disabled,
                password?.Salt ?? old!.Salt, password?.Hash ?? old!.PasswordHash);
            var users = state.Users.Where(x => x.Id != user.Id).Append(user).ToList();
            var entries = new List<CloudEntry>(state.Entries);
            var policies = new List<FolderPolicy>(state.Policies);
            if (old == null)
            {
                CloudEntry Ensure(string path)
                {
                    var existing = entries.Find(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
                    if (existing is { IsDirectory: false }) throw new StoreException(409, "用户目录位置被文件占用。");
                    if (existing != null) return existing;
                    var folder = new CloudEntry(Guid.NewGuid(), path, true, 0, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N")); entries.Add(folder); return folder;
                }
                _ = Ensure("个人");
                var homePath = "个人/" + name;
                if (entries.Any(x => x.Path.Equals(homePath, StringComparison.OrdinalIgnoreCase))) throw new StoreException(409, "个人目录已存在，不能自动接管。");
                var home = Ensure(homePath);
                policies.Add(new(home.Id, user.Id, "private", 0, []));
                var shared = Ensure("公共");
                if (!policies.Any(x => x.FolderId == shared.Id)) policies.Add(new(shared.Id, BootstrapId, "public", 3, []));
            }
            var sessions = password != null || request.Disabled || old?.IsAdmin != request.IsAdmin
                ? state.Sessions.Where(x => x.UserId != user.Id).ToList() : state.Sessions;
            await CommitState(state with { Users = users, Entries = entries, Policies = policies, Sessions = sessions });
            return PublicUser(user);
        }
        finally { gate.Release(); }
    }
    public async Task<GroupInfo> SaveGroup(RequestActor actor, Guid? id, SaveGroup request)
    {
        Admin(actor);
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > 100) throw new StoreException(400, "用户组名称无效。");
        await gate.WaitAsync();
        try
        {
            Admin(actor);
            if (id.HasValue && !state.Groups.Any(x => x.Id == id.Value)) throw new StoreException(404, "用户组不存在。");
            if (state.Groups.Any(x => x.Id != id && x.Name.Equals(request.Name.Trim(), StringComparison.OrdinalIgnoreCase))) throw new StoreException(409, "用户组名称已存在。");
            if (request.Members.Any(member => !state.Users.Any(x => x.Id == member))) throw new StoreException(400, "用户组成员不存在。");
            var group = new GroupInfo(id ?? Guid.NewGuid(), request.Name.Trim(), request.Members.Distinct().ToList());
            await CommitState(state with { Groups = state.Groups.Where(x => x.Id != group.Id).Append(group).ToList() });
            return group;
        }
        finally { gate.Release(); }
    }
    public async Task DeleteGroup(RequestActor actor, Guid id)
    {
        Admin(actor); await gate.WaitAsync();
        try
        {
            Admin(actor);
            if (!state.Groups.Any(x => x.Id == id)) throw new StoreException(404, "用户组不存在。");
            await CommitState(state with { Groups = state.Groups.Where(x => x.Id != id).ToList(),
                Policies = state.Policies.Select(x => x with { Grants = x.Grants.Where(g => g.Kind != "group" || g.Id != id).ToList() }).ToList() });
        }
        finally { gate.Release(); }
    }
}
