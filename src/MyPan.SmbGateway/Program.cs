using System.Net;
using System.Net.Http.Json;
using MyPan.Contracts;
using MyPan.SmbGateway;
using SMBLibrary;
using SMBLibrary.Authentication.GSSAPI;
using SMBLibrary.Authentication.NTLM;
using SMBLibrary.Server;

string Value(string name) => Array.IndexOf(args, name) is var i && i >= 0 && i + 1 < args.Length ? args[i + 1] : throw new ArgumentException(name);
try
{
    var url = Value("--server"); var key = Environment.GetEnvironmentVariable("MYPAN_GATEWAY_KEY") ?? throw new InvalidOperationException("Gateway key missing.");
    using var http = new HttpClient { BaseAddress = new Uri(url), Timeout = TimeSpan.FromSeconds(60) }; http.DefaultRequestHeaders.Add("X-MyPan-Gateway", key);
    string? Password(string user)
    {
        try { using var response = http.GetAsync("/api/internal/smb-password?username=" + Uri.EscapeDataString(user)).GetAwaiter().GetResult(); return response.IsSuccessStatusCode ? response.Content.ReadFromJsonAsync(ApiJson.Default.SmbCredential).GetAwaiter().GetResult()?.Password : null; }
        catch (HttpRequestException) { return null; }
    }
    var provider = new AuditedAuthentication(Password, entry =>
    { try { using var response = http.PostAsJsonAsync("/api/internal/audit", entry, ApiJson.Default.AuditEntry).GetAwaiter().GetResult(); } catch (HttpRequestException) { } });
    var shares = new SMBShareCollection { new FileSystemShare("mypan", new AccountFileStore(http, Value("--staging"))) };
    var server = new GatewayServer(shares, new GSSProvider(provider));
    server.Listen(IPAddress.Parse(Value("--listen")), int.Parse(Value("--port")));
    Console.WriteLine("MYPAN_SMB_READY");
    var parent = Environment.GetEnvironmentVariable("MYPAN_GATEWAY_PARENT");
    using var stop = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
    try
    {
        while (!stop.IsCancellationRequested)
        {
            await Task.Delay(1000, stop.Token);
            if (int.TryParse(parent, out var parentId)) { try { if (System.Diagnostics.Process.GetProcessById(parentId).HasExited) break; } catch (ArgumentException) { break; } }
        }
    }
    catch (OperationCanceledException) { }
    finally { server.Stop(); }
}
catch (Exception ex) { Console.WriteLine("MYPAN_SMB_FAILED " + ex.Message); Environment.ExitCode = 1; }

internal sealed class GatewayServer(SMBShareCollection shares, GSSProvider provider) : SMBServer(shares, provider)
{
    public void Listen(IPAddress address, int port) => Start(address, SMBTransportType.DirectTCPTransport, port, false, true, true, TimeSpan.FromMinutes(10));
}

internal sealed class AuditedAuthentication(GetUserPassword password, Action<AuditEntry> record) : IndependentNTLMAuthenticationProvider(password)
{
    public override NTStatus Authenticate(object context, byte[] authenticateMessageBytes)
    {
        var status = base.Authenticate(context, authenticateMessageBytes);
        record(new(DateTimeOffset.UtcNow, (context as AuthContext)?.UserName ?? "anonymous", "SMB", "LOGIN", "/", status == NTStatus.STATUS_SUCCESS ? 200 : 401));
        return status;
    }
}
