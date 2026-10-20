using System.Text.Json;
using Yunsevo.Contracts;

namespace Yunsevo.Server;

public sealed partial class CloudStore
{
    private static string HomePath(StoredUser user) => "个人/" + user.Username;
    private static StoredUser? HomeOwner(StoreState snapshot, string path) => snapshot.Users.Find(x =>
        path.Equals(HomePath(x), StringComparison.OrdinalIgnoreCase) || CloudPath.IsUnder(path, HomePath(x)));
    private static void ProtectPersonalRoot(StoreState snapshot, string path)
    {
        if (snapshot.Users.Any(x => HomePath(x).Equals(path, StringComparison.OrdinalIgnoreCase) || CloudPath.IsUnder(HomePath(x), path)))
            throw new StoreException(409, "默认个人文件夹及其分组不能删除或移动，可以管理其中的文件和子文件夹。");
    }
    private void EnsurePersonalHomes()
    {
        var entries = state.Entries.ToList(); var policies = state.Policies.ToList(); var changed = false;
        CloudEntry Ensure(string path)
        {
            var existing = entries.Find(x => x.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                if (!existing.IsDirectory) throw new InvalidDataException("默认个人目录位置被文件占用，请先处理路径：" + path);
                return existing;
            }
            var folder = new CloudEntry(Guid.NewGuid(), path, true, 0, DateTimeOffset.UtcNow, Guid.NewGuid().ToString("N"));
            entries.Add(folder); changed = true; return folder;
        }
        Ensure("个人");
        foreach (var user in state.Users)
        {
            var folder = Ensure(HomePath(user)); var old = policies.Find(x => x.FolderId == folder.Id);
            if (old != null && old.OwnerId == user.Id && old.Visibility == "private" && old.PublicAccess == 0 && old.Grants.Count == 0) continue;
            policies.RemoveAll(x => x.FolderId == folder.Id);
            policies.Add(new(folder.Id, user.Id, "private", 0, [], old?.QuotaBytes, old?.KeepVersions)); changed = true;
        }
        if (!changed) return;
        var catalog = System.IO.Path.Combine(root, "catalog.json");
        File.Copy(catalog, catalog + ".homes-backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfffffff"));
        state = state with { Entries = entries, Policies = policies, Revision = state.Revision + 1, JournalGeneration = Guid.NewGuid() };
        AtomicFile.WriteText(catalog, JsonSerializer.Serialize(state, StoreJson.Default.StoreState));
        contentJournal.Reset(state.JournalGeneration);
    }
}
