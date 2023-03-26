using iRLeagueApiCore.Client.Http;

namespace StatsBot;
internal sealed class DummyTokenStore : ITokenStore
{
    public bool IsLoggedIn => false;

    public event EventHandler? TokenChanged;

    public Task ClearTokenAsync()
    {
        return Task.CompletedTask;
    }

    public Task<string> GetTokenAsync()
    {
        return Task.FromResult(string.Empty);
    }

    public Task SetTokenAsync(string token)
    {
        TokenChanged?.Invoke(this, new EventArgs());
        return Task.CompletedTask;
    }
}
