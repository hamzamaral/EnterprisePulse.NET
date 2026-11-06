using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Server.ProtectedBrowserStorage;
using Microsoft.JSInterop;

namespace EnterprisePulse.Web.Services;

public sealed class ApiException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public sealed record MeBranch(Guid Id, string Code, string Name);
public sealed record MeCompany(Guid Id, string Code, string Name, string BaseCurrency, bool IsDefault, List<MeBranch> Branches);
public sealed record MeUser(Guid Id, string Email, string FirstName, string LastName, string? Phone, string Locale, bool IsSuperAdmin, bool TwoFactorEnabled, Guid? EmployeeId)
{
    public string FullName => $"{FirstName} {LastName}";
}
public sealed record MeResponse(MeUser User, List<MeCompany> Companies, Guid ActiveCompanyId, Guid? ActiveBranchId, List<string> Permissions);
public sealed record TokenPair(string AccessToken, string RefreshToken, DateTime RefreshExpiresAt);
public sealed record LoginResponse(bool RequiresTwoFactor, string? AccessToken, string? RefreshToken);
public sealed record PageMeta(int Page, int PageSize, int Total, int TotalPages);
public sealed record Paged<T>(List<T> Data, PageMeta Meta);
public sealed record LookupItem(string Id, string Label);

/// <summary>Per-circuit login state; the refresh token and preferences survive reloads in protected local storage.</summary>
public sealed class Session(ProtectedLocalStorage storage, IHttpClientFactory http)
{
    public string? AccessToken { get; private set; }
    public string? RefreshToken { get; private set; }
    public MeResponse? Me { get; private set; }
    public Guid? CompanyId { get; private set; }
    public Guid? BranchId { get; private set; }
    public string Locale { get; private set; } = "tr";
    public bool DarkMode { get; private set; }
    public bool Initialized { get; private set; }
    public HashSet<string> Permissions { get; private set; } = [];
    public bool IsAuthenticated => Me != null;
    public MeCompany? Company => Me?.Companies.FirstOrDefault(c => c.Id == CompanyId);

    public event Action? Changed;

    public bool Has(string code) => Me?.User.IsSuperAdmin == true || Permissions.Contains(code);
    public bool HasAny(IEnumerable<string> codes) => !codes.Any() || codes.Any(Has);

    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    public async Task InitializeAsync(ApiClient api)
    {
        if (Initialized) return;
        try
        {
            Locale = (await storage.GetAsync<string>("locale")).Value ?? "tr";
            DarkMode = (await storage.GetAsync<bool>("dark")).Value;
            CompanyId = (await storage.GetAsync<Guid?>("company")).Value;
            BranchId = (await storage.GetAsync<Guid?>("branch")).Value;
            RefreshToken = (await storage.GetAsync<string>("refresh")).Value;
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            await storage.DeleteAsync("refresh");
        }
        if (RefreshToken != null && await TryRefreshAsync()) await LoadMeAsync(api);
        Initialized = true;
        Changed?.Invoke();
    }

    public async Task SignInAsync(ApiClient api, string accessToken, string refreshToken)
    {
        AccessToken = accessToken;
        RefreshToken = refreshToken;
        await storage.SetAsync("refresh", refreshToken);
        await LoadMeAsync(api);
        Changed?.Invoke();
    }

    public async Task LoadMeAsync(ApiClient api)
    {
        try
        {
            Me = await api.GetAsync<MeResponse>("/api/auth/me");
        }
        catch (ApiException) when (CompanyId != null)
        {
            CompanyId = null;
            BranchId = null;
            Me = await api.GetAsync<MeResponse>("/api/auth/me");
        }
        CompanyId = Me.ActiveCompanyId;
        BranchId = Me.ActiveBranchId;
        Permissions = Me.Permissions.ToHashSet();
        if (CompanyId != null) await storage.SetAsync("company", CompanyId);
        Changed?.Invoke();
    }

    public async Task SwitchCompanyAsync(ApiClient api, Guid companyId, Guid? branchId = null)
    {
        CompanyId = companyId;
        BranchId = branchId;
        await storage.SetAsync("company", companyId);
        if (branchId != null) await storage.SetAsync("branch", branchId); else await storage.DeleteAsync("branch");
        await LoadMeAsync(api);
    }

    public async Task SetLocaleAsync(string locale)
    {
        Locale = locale;
        await storage.SetAsync("locale", locale);
        Changed?.Invoke();
    }

    public async Task SetDarkAsync(bool dark)
    {
        DarkMode = dark;
        await storage.SetAsync("dark", dark);
        Changed?.Invoke();
    }

    /// <summary>Single-flight refresh so parallel 401s rotate the token only once.</summary>
    public async Task<bool> TryRefreshAsync(string? failedAccessToken = null)
    {
        await _refreshLock.WaitAsync();
        try
        {
            if (failedAccessToken != null && AccessToken != failedAccessToken) return AccessToken != null;
            if (RefreshToken == null) return false;
            using var client = http.CreateClient("api");
            var res = await client.PostAsJsonAsync("/api/auth/refresh", new { refreshToken = RefreshToken });
            if (!res.IsSuccessStatusCode)
            {
                await ClearAsync();
                return false;
            }
            var pair = (await res.Content.ReadFromJsonAsync<TokenPair>(ApiClient.Json))!;
            AccessToken = pair.AccessToken;
            RefreshToken = pair.RefreshToken;
            await storage.SetAsync("refresh", pair.RefreshToken);
            return true;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public async Task SignOutAsync()
    {
        if (RefreshToken != null)
        {
            using var client = http.CreateClient("api");
            try { await client.PostAsJsonAsync("/api/auth/logout", new { refreshToken = RefreshToken }); } catch (HttpRequestException) { }
        }
        await ClearAsync();
        Changed?.Invoke();
    }

    private async Task ClearAsync()
    {
        AccessToken = null;
        RefreshToken = null;
        Me = null;
        Permissions = [];
        await storage.DeleteAsync("refresh");
    }
}

public sealed class ApiClient(IHttpClientFactory http, Session session, NavigationManager nav, IJSRuntime js)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public Session Session => session;

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> build, bool retry = true)
    {
        using var client = http.CreateClient("api");
        var request = build();
        var token = session.AccessToken;
        if (token != null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (session.CompanyId != null) request.Headers.Add("x-company-id", session.CompanyId.ToString());
        if (session.BranchId != null) request.Headers.Add("x-branch-id", session.BranchId.ToString());
        request.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue(session.Locale));
        HttpResponseMessage res;
        try
        {
            res = await client.SendAsync(request);
        }
        catch (HttpRequestException)
        {
            throw new ApiException(0, session.Locale == "en" ? "Cannot reach the API" : "API'ye ulaşılamıyor");
        }
        if (res.StatusCode == HttpStatusCode.Unauthorized && retry && token != null)
        {
            if (await session.TryRefreshAsync(token)) return await SendAsync(build, retry: false);
            nav.NavigateTo("/login", replace: true);
        }
        if (!res.IsSuccessStatusCode)
        {
            var message = res.ReasonPhrase ?? "Hata";
            try
            {
                var body = await res.Content.ReadFromJsonAsync<JsonObject>();
                message = body?["message"]?.ToString() ?? message;
            }
            catch (JsonException) { }
            throw new ApiException((int)res.StatusCode, res.StatusCode == HttpStatusCode.TooManyRequests ? "Çok fazla istek, biraz bekleyin" : message);
        }
        return res;
    }

    private static HttpContent Body(object? body) => JsonContent.Create(body, options: Json);

    public async Task<T> GetAsync<T>(string url)
    {
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url));
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public async Task<T?> GetOrDefaultAsync<T>(string url) where T : class
    {
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url));
        if (res.StatusCode == HttpStatusCode.NoContent) return null;
        return await res.Content.ReadFromJsonAsync<T>(Json);
    }

    public Task<JsonObject> GetJsonAsync(string url) => GetAsync<JsonObject>(url);

    public async Task<T> PostAsync<T>(string url, object? body = null)
    {
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url) { Content = Body(body ?? new { }) });
        if (res.StatusCode == HttpStatusCode.NoContent) return default!;
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public async Task PostAsync(string url, object? body = null)
    {
        using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, url) { Content = Body(body ?? new { }) });
    }

    public async Task<T> PatchAsync<T>(string url, object body)
    {
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Patch, url) { Content = Body(body) });
        if (res.StatusCode == HttpStatusCode.NoContent) return default!;
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public async Task<T> PutAsync<T>(string url, object body)
    {
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Put, url) { Content = Body(body) });
        if (res.StatusCode == HttpStatusCode.NoContent) return default!;
        return (await res.Content.ReadFromJsonAsync<T>(Json))!;
    }

    public async Task DeleteAsync(string url)
    {
        using var _ = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, url));
    }

    public async Task<byte[]> GetBytesAsync(string url)
    {
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url));
        return await res.Content.ReadAsByteArrayAsync();
    }

    /// <summary>Downloads a file (export, PDF document) and hands it to the browser.</summary>
    public async Task DownloadAsync(string url, string fallbackName = "download")
    {
        using var res = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, url));
        var name = res.Content.Headers.ContentDisposition?.FileNameStar ?? res.Content.Headers.ContentDisposition?.FileName?.Trim('"') ?? fallbackName;
        var bytes = await res.Content.ReadAsByteArrayAsync();
        using var stream = new DotNetStreamReference(new MemoryStream(bytes));
        await js.InvokeVoidAsync("ep.download", name, stream);
    }

    public async Task<JsonObject> UploadAsync(string url, Stream content, string fileName, string contentType, Dictionary<string, string>? fields = null)
    {
        using var res = await SendAsync(() =>
        {
            var form = new MultipartFormDataContent();
            var file = new StreamContent(content);
            file.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrEmpty(contentType) ? "application/octet-stream" : contentType);
            form.Add(file, "file", fileName);
            foreach (var (k, v) in fields ?? []) form.Add(new StringContent(v), k);
            return new HttpRequestMessage(HttpMethod.Post, url) { Content = form };
        }, retry: false);
        return (await res.Content.ReadFromJsonAsync<JsonObject>())!;
    }
}

/// <summary>The metadata document is identical for every user, so one copy is cached per server.</summary>
public sealed class MetaCache
{
    private MetadataDocument? _doc;
    private readonly SemaphoreSlim _lock = new(1, 1);

    public MetadataDocument? Document => _doc;

    public async Task<MetadataDocument> LoadAsync(ApiClient api)
    {
        if (_doc != null) return _doc;
        await _lock.WaitAsync();
        try
        {
            return _doc ??= await api.GetAsync<MetadataDocument>("/api/meta");
        }
        finally
        {
            _lock.Release();
        }
    }
}
