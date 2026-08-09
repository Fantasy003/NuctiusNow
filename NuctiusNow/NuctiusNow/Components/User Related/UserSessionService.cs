using Microsoft.JSInterop;

namespace NuctiusNow.Components.User_Related;

public class UserSessionService
{
    private readonly IJSRuntime _jsRuntime;
    private bool _initialized;

    public bool IsAuthenticated { get; private set; }
    public int? UserId { get; private set; }
    public string? Username { get; private set; }

    public UserSessionService(IJSRuntime jsRuntime)
    {
        _jsRuntime = jsRuntime;
    }

    public async Task InitializeAsync()
    {
        if (_initialized)
            return;

        var storedUser = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", "nuctiusnow_user");
        var storedUserId = await _jsRuntime.InvokeAsync<string>("localStorage.getItem", "nuctiusnow_userid");

        if (!string.IsNullOrWhiteSpace(storedUser) && int.TryParse(storedUserId, out var parsedId))
        {
            Username = storedUser;
            UserId = parsedId;
            IsAuthenticated = true;
        }
        else
        {
            Username = null;
            UserId = null;
            IsAuthenticated = false;
        }

        _initialized = true;
    }

    public async Task SetUserAsync(int userId, string username)
    {
        UserId = userId;
        Username = username;
        IsAuthenticated = true;

        await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "nuctiusnow_user", username);
        await _jsRuntime.InvokeVoidAsync("localStorage.setItem", "nuctiusnow_userid", userId.ToString());
    }

    public async Task ClearAsync()
    {
        Username = null;
        UserId = null;
        IsAuthenticated = false;
        await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nuctiusnow_user");
        await _jsRuntime.InvokeVoidAsync("localStorage.removeItem", "nuctiusnow_userid");
    }
}
