using Microsoft.JSInterop;

namespace IAMS.Web.Services;

public class ThemeService(IJSRuntime js)
{
    private bool _isDarkMode;

    public bool IsDarkMode => _isDarkMode;

    public event Action? OnThemeChanged;

    public async Task InitializeAsync()
    {
        try
        {
            var theme = await js.InvokeAsync<string>("themeManager.getTheme");
            _isDarkMode = theme == "dark";
        }
        catch
        {
            // Fallback if JS not available
            _isDarkMode = false;
        }
    }

    public async Task ToggleThemeAsync()
    {
        _isDarkMode = !_isDarkMode;
        await js.InvokeVoidAsync("themeManager.setTheme", _isDarkMode);
        OnThemeChanged?.Invoke();
    }

    public async Task SetThemeAsync(bool isDark)
    {
        _isDarkMode = isDark;
        await js.InvokeVoidAsync("themeManager.setTheme", isDark);
        OnThemeChanged?.Invoke();
    }
}
