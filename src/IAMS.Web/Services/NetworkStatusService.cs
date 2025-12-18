using Microsoft.JSInterop;

namespace IAMS.Web.Services;

public class NetworkStatusService : IAsyncDisposable
{
    private readonly IJSRuntime _js;
    private DotNetObjectReference<NetworkStatusService>? _dotNetRef;
    private bool _isOnline = true;
    private bool _initialized;

    public event Func<bool, Task>? OnStatusChanged;

    public bool IsOnline => _isOnline;

    public NetworkStatusService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task InitializeAsync()
    {
        if (_initialized) return;

        try
        {
            _dotNetRef = DotNetObjectReference.Create(this);
            _isOnline = await _js.InvokeAsync<bool>("iamsOffline.registerNetworkCallback", _dotNetRef);
            _initialized = true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Failed to initialize network status service: {ex.Message}");
            _isOnline = true; // Assume online if we can't detect
        }
    }

    [JSInvokable]
    public async Task OnNetworkStatusChanged(bool isOnline)
    {
        if (_isOnline != isOnline)
        {
            _isOnline = isOnline;

            if (OnStatusChanged != null)
            {
                await OnStatusChanged.Invoke(isOnline);
            }
        }
    }

    public async Task<bool> CheckOnlineAsync()
    {
        try
        {
            _isOnline = await _js.InvokeAsync<bool>("iamsOffline.isOnline");
        }
        catch
        {
            // If JS fails, assume offline
            _isOnline = false;
        }

        return _isOnline;
    }

    public async ValueTask DisposeAsync()
    {
        if (_initialized)
        {
            try
            {
                await _js.InvokeVoidAsync("iamsOffline.unregisterNetworkCallback");
            }
            catch
            {
                // Ignore disposal errors
            }
        }

        _dotNetRef?.Dispose();
    }
}
