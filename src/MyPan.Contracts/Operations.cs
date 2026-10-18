namespace MyPan.Contracts;

public sealed record ProtocolSettings(bool WebDav = false, bool Ftp = false, bool Smb = false,
    string ListenAddress = "127.0.0.1", int FtpPort = 2121, int SmbPort = 1445);
public sealed record ProtocolStatus(ProtocolSettings Settings, bool FtpRunning, bool SmbRunning, string? Error);
public sealed record SmbCredential(string Username, string Password);
public sealed record AuditEntry(DateTimeOffset Time, string User, string Source, string Action, string Target, int Status, string? Detail = null,
    string? DeviceName = null, string? RemoteAddress = null);
