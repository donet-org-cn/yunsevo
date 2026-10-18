using MyPan.Contracts;

namespace MyPan.Server;

public sealed partial class CloudStore
{
    // A request-local index of one immutable snapshot. Never reuse across revisions or accounts.
    private sealed class LivePermissions
    {
        private readonly RequestActor actor;
        private readonly StoredUser? user;
        private readonly Dictionary<string, (FolderPolicy Policy, int Rights)> rules = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Guid> homes;
        internal Dictionary<string, CloudEntry> Folders { get; }
        internal LivePermissions(StoreState snapshot, RequestActor actor)
        {
            this.actor = actor;
            user = snapshot.Users.Find(x => x.Id == actor.Id && !x.Disabled);
            homes = snapshot.Users.ToDictionary(HomePath, x => x.Id, StringComparer.OrdinalIgnoreCase);
            var entries = snapshot.Entries.ToDictionary(x => x.Id);
            Folders = snapshot.Entries.Where(x => x.IsDirectory).ToDictionary(x => x.Path, StringComparer.OrdinalIgnoreCase);
            var groups = snapshot.Groups.Where(x => x.Members.Contains(actor.Id)).Select(x => x.Id).ToHashSet();
            foreach (var policy in snapshot.Policies)
            {
                if (!entries.TryGetValue(policy.FolderId, out var folder)) continue;
                var rights = policy.OwnerId == actor.Id ? 7 : policy.Visibility == "private" ? 0 :
                    policy.Grants.Where(x => x.Kind == "user" && x.Id == actor.Id || x.Kind == "group" && groups.Contains(x.Id))
                        .Aggregate(policy.Visibility == "public" ? policy.PublicAccess : 0, (value, grant) => value | grant.Access);
                rules.TryAdd(folder.Path, (policy, rights));
            }
        }
        internal FolderPolicy? Rule(string path) => Find(path)?.Policy;
        private (FolderPolicy Policy, int Rights)? Find(string path)
        {
            for (string? cursor = path; cursor != null; cursor = CloudPath.Parent(cursor))
                if (rules.TryGetValue(cursor, out var result)) return result;
            return null;
        }
        internal int Access(string path)
        {
            if (user == null) return 0;
            if (user.IsAdmin && actor.WebAdmin) return 7;
            for (string? cursor = path; cursor != null; cursor = CloudPath.Parent(cursor))
                if (homes.TryGetValue(cursor, out var owner) && owner != actor.Id) return 0;
            return Find(path)?.Rights ?? 0;
        }
    }
}
