namespace Yunsevo.Mobile.Core;

// A VPN can be marked metered even when its transport is Wi-Fi. Never infer transport from billing.
public sealed record UploadNetworkState(bool Connected, bool Wifi, bool Cellular, bool Vpn, bool Metered)
{
    // Mixed or undisclosed underlying transports are conservative: do not risk cellular uploads.
    public bool AllowsWifiOnly => Connected && Wifi && !Cellular;
}
