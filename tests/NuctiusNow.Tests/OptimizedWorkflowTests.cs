using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using Npgsql;
using NuctiusNow.Components.User_Related;
using NuctiusNow.Data;
using NuctiusNow.Services;

namespace NuctiusNow.Tests;

public sealed class OptimizedWorkflowTests : IAsyncLifetime
{
    private const string Password = "Test-password-123";
    private readonly string _database = "nuctius_test_" + Guid.NewGuid().ToString("N");
    private readonly ServiceProvider _services = new ServiceCollection().AddHttpClient().BuildServiceProvider();
    private readonly EphemeralDataProtectionProvider _protection = new();
    private readonly CapturingEmailSender _sender = new();
    private string _admin = null!;
    private ContextFactory _factory = null!;
    private ApplicationDbContext _db = null!;
    private AuthService Auth => new(_factory, _services.GetRequiredService<IHttpClientFactory>(), new ConfigurationBuilder().Build());
    private EmailVerificationService Email => new(_factory, _sender, new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Application:PublicBaseUrl"] = "https://example.test" }).Build());

    public async Task InitializeAsync()
    {
        var supplied = Environment.GetEnvironmentVariable("NUCTIUS_TEST_CONNECTION")
            ?? throw new InvalidOperationException("Set NUCTIUS_TEST_CONNECTION to PostgreSQL credentials allowed to create isolated test databases.");
        var connection = new NpgsqlConnectionStringBuilder(supplied) { Database="postgres" };
        _admin = connection.ConnectionString;
        await using var admin = new NpgsqlConnection(_admin);
        await admin.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{_database}\"", admin);
        await command.ExecuteNonQueryAsync();
        connection.Database = _database;
        _factory = new ContextFactory(connection.ConnectionString);
        _db = _factory.CreateDbContext();
        await _db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (_db != null) await _db.DisposeAsync();
        _services.Dispose();
        if (_admin == null) return;
        await using var connection = new NpgsqlConnection(_admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{_database}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task<User> Register(string username="reader", bool verified=true)
    {
        var user = new User { Username=username, Email=username+"@example.test", Birthday=new DateTime(2000, 1, 1) };
        var result = await Auth.RegistrarAsync(user, Password);
        Assert.True(result.success, result.message);
        if (verified)
        {
            await using var db = _factory.CreateDbContext();
            await db.Users.Where(u => u.IdUser == user.IdUser).ExecuteUpdateAsync(s => s.SetProperty(u => u.EmailVerified, true));
            user.EmailVerified = true;
        }
        return user;
    }

    [Fact]
    public async Task MigrationsApplyEmailVerificationAndKeepExistingUsersVerified()
    {
        await _db.GetService<IMigrator>().MigrateAsync("20260924230000_AddUserInterestKeywordsAndReaction");
        await _db.Database.ExecuteSqlRawAsync("""
            INSERT INTO users(username,email,password,password_hash,language,birthday,created_at,is_premium,is_banned)
            VALUES ('legacy','legacy@example.test','unused','unused','en','2000-01-01',NOW(),FALSE,FALSE)
            """);
        await _db.Database.MigrateAsync();
        Assert.True((await _db.Users.SingleAsync()).EmailVerified);
        Assert.Empty(await _db.UserBlockedSources.ToListAsync());
        await _db.Database.MigrateAsync();
        Assert.False(_db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task LoginRejectsUnverifiedBannedAndInvalidPasswordAccounts()
    {
        var unverified = await Register("pending", false);
        Assert.Null((await Auth.LoginAsync(unverified.Username, Password)).user);
        var user = await Register();
        Assert.NotNull((await Auth.LoginAsync(user.Email, Password)).user);
        Assert.Null((await Auth.LoginAsync(user.Username, "wrong")).user);
        Assert.True(await Auth.CambiarEstadoBanAsync(user.IdUser, true));
        Assert.Null((await Auth.LoginAsync(user.Username, Password)).user);
        Assert.Null((await Auth.LoginAsync(user.Username, "wrong")).user);
    }

    [Theory]
    [InlineData("", "reader@example.test", "Test-password-123")]
    [InlineData("reader", "invalid", "Test-password-123")]
    [InlineData("reader", "reader@example.test", "short")]
    [InlineData("email@example.test", "reader@example.test", "Test-password-123")]
    public async Task InvalidRegistrationIsRejected(string username, string email, string password)
    {
        Assert.False((await Auth.RegistrarAsync(new User { Username=username, Email=email }, password)).success);
        Assert.Empty(await _db.Users.ToListAsync());
    }

    [Fact]
    public async Task InvalidHashesAndOversizedBcryptPasswordsDoNotAuthenticate()
    {
        Assert.False((await Auth.RegistrarAsync(new User {Username="long", Email="long@example.test"}, new string('é', 37))).success);
        var user = await Register();
        await _db.Users.Where(u => u.IdUser == user.IdUser).ExecuteUpdateAsync(s => s.SetProperty(u => u.Password, "broken hash"));
        Assert.Null((await Auth.LoginAsync(user.Username, Password)).user);
    }

    [Theory]
    [InlineData("Spanish", "es")]
    [InlineData("2", "es")]
    [InlineData("English", "en")]
    public async Task ProfileLanguageAndBirthdayPersist(string inputLanguage, string expected)
    {
        var user = await Register();
        var result = await Auth.UpdateProfileAsync(user.IdUser, user.Username, user.Email,
            new DateTime(2000,2,29), inputLanguage, null, []);
        Assert.True(result.Success, result.Message);
        var saved = await Auth.GetUserByIdAsync(user.IdUser);
        Assert.Equal(expected, saved!.Language);
        Assert.Equal(new DateTime(2000,2,29), saved.Birthday);
        Assert.False((await Auth.GetProfileUpdateStatusAsync(user.IdUser)).CanUpdate);
    }

    [Fact]
    public async Task ProfileRejectsInvalidFieldsBeforeStartingCooldown()
    {
        var user = await Register();
        Assert.False((await Auth.UpdateProfileAsync(user.IdUser,user.Username,user.Email,DateTime.Today.AddDays(1),"en",null,[])).Success);
        Assert.False((await Auth.UpdateProfileAsync(user.IdUser,user.Username,"bad",user.Birthday,"en",null,[])).Success);
        Assert.False((await Auth.UpdateProfileAsync(user.IdUser,user.Username,user.Email,user.Birthday,"invalid",null,[])).Success);
        Assert.False((await Auth.UpdateProfileAsync(user.IdUser,user.Username,user.Email,user.Birthday,"en","short",[])).Success);
        Assert.True((await Auth.GetProfileUpdateStatusAsync(user.IdUser)).CanUpdate);
    }

    [Fact]
    public async Task ParallelProfileChangesCannotBypassCooldown()
    {
        var user = await Register();
        var results = await Task.WhenAll(Enumerable.Range(0,2).Select(_ =>
            Auth.UpdateProfileAsync(user.IdUser,user.Username,user.Email,user.Birthday,"en",null,[])));
        Assert.Equal(1,results.Count(r => r.Success));
        Assert.Single(results.Where(r => r.NextAvailableAt.HasValue));
    }

    [Fact]
    public async Task SavingProfileKeepsZeroLearnedWeightAndReusesCaseInsensitiveTags()
    {
        var user = await Register();
        Assert.True((await Auth.SavePreferencesForUserAsync(user.IdUser,["Tech"])).success);
        await _db.UserPreferences.Where(p => p.IdUser == user.IdUser).ExecuteUpdateAsync(s => s.SetProperty(p => p.Weight,0));
        Assert.True((await Auth.UpdateProfileAsync(user.IdUser,user.Username,user.Email,user.Birthday,"en",null,["TECH"])).Success);
        Assert.Equal(0,(await _db.UserPreferences.SingleAsync()).Weight);
        Assert.Equal(1,await _db.Tags.CountAsync());
    }

    [Fact]
    public async Task EmailChangesInvalidateVerificationAndExistingSession()
    {
        var user = await Register();
        var js = new StorageRuntime();
        var session = new UserSessionService(js,_factory,_protection);
        await session.SetUserAsync(user.IdUser,true);
        var result = await Auth.UpdateProfileAsync(user.IdUser,user.Username,"changed@example.test",user.Birthday,"en",null,[]);
        Assert.True(result.Success,result.Message);
        Assert.True(result.RequiresEmailVerification);
        await session.InitializeAsync();
        Assert.False(session.IsAuthenticated);
        Assert.Null((await Auth.LoginAsync(user.Username,Password)).user);
    }

    [Fact]
    public async Task ActivationIsSingleUseAndFailedResendPreservesExistingToken()
    {
        var user = await Register(verified:false);
        await Email.SendActivationEmailAsync(user.IdUser);
        var token = TokenFrom(_sender.LastBody!);
        var before = (await Auth.GetUserByIdAsync(user.IdUser))!.EmailVerificationTokenHash;
        _sender.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Email.SendActivationEmailAsync(user.IdUser));
        Assert.Equal(before,(await Auth.GetUserByIdAsync(user.IdUser))!.EmailVerificationTokenHash);
        var results = await Task.WhenAll(Email.ActivateAsync(token),Email.ActivateAsync(token));
        Assert.Equal(1,results.Count(r => r.Success));
        Assert.NotNull((await Auth.LoginAsync(user.Username,Password)).user);
    }

    [Fact]
    public async Task MissingEmailConfigurationDoesNotInvalidateTokenAndExpiredTokensFail()
    {
        var user = await Register(verified:false);
        await Email.SendActivationEmailAsync(user.IdUser);
        var token = TokenFrom(_sender.LastBody!);
        var hash = (await Auth.GetUserByIdAsync(user.IdUser))!.EmailVerificationTokenHash;
        var missing = new EmailVerificationService(_factory,_sender,new ConfigurationBuilder().Build());
        await Assert.ThrowsAsync<InvalidOperationException>(() => missing.SendActivationEmailAsync(user.IdUser));
        Assert.Equal(hash,(await Auth.GetUserByIdAsync(user.IdUser))!.EmailVerificationTokenHash);
        await _db.Users.Where(u => u.IdUser == user.IdUser).ExecuteUpdateAsync(s =>
            s.SetProperty(u => u.EmailVerificationExpiresAt,DateTime.UtcNow.AddMinutes(-1)));
        Assert.False((await Email.ActivateAsync(token)).Success);
    }

    [Fact]
    public async Task SessionsRejectEditableLegacyIdsTamperingAndPasswordChanges()
    {
        var user = await Register();
        var js = new StorageRuntime();
        js.Values["localStorage:nuctiusnow_user"] = user.Username;
        js.Values["localStorage:nuctiusnow_userid"] = user.IdUser.ToString();
        var session = new UserSessionService(js,_factory,_protection);
        await session.InitializeAsync();
        Assert.False(session.IsAuthenticated);
        await session.SetUserAsync(user.IdUser,false);
        Assert.True(js.Values.ContainsKey("sessionStorage:nuctiusnow_session"));
        await session.SetUserAsync(user.IdUser,true);
        Assert.False(js.Values.ContainsKey("sessionStorage:nuctiusnow_session"));
        var restored = new UserSessionService(js,_factory,_protection);
        await restored.InitializeAsync();
        Assert.True(restored.IsAuthenticated);
        Assert.True(restored.RememberMe);
        js.Values["localStorage:nuctiusnow_session"] = "tampered";
        await restored.InitializeAsync();
        Assert.False(restored.IsAuthenticated);
        await restored.SetUserAsync(user.IdUser,true);
        Assert.True((await Auth.UpdateProfileAsync(user.IdUser,user.Username,user.Email,user.Birthday,"en","Changed-password-456",[])).Success);
        await restored.InitializeAsync();
        Assert.False(restored.IsAuthenticated);
    }

    [Fact]
    public async Task SourceFiltersPersistAndApplyToDatabaseFallbackAndExploration()
    {
        var user = await Register();
        var blocked = new UserBlockedSourceService(_factory);
        Assert.True((await blocked.ReplaceAsync(user.IdUser,["www.blocked.test","https://blocked.test/path"])).Success);
        Assert.True((await blocked.ReplaceAsync(user.IdUser,["blocked.test"])).Success);
        Assert.Single(await blocked.GetAsync(user.IdUser));
        var tag = new Tag {Name="Technology"};
        var blockedNews = MakeNews(tag,"https://sub.blocked.test/article");
        var allowedNews = MakeNews(tag,"https://allowed.test/article");
        _db.News.AddRange(blockedNews,allowedNews);
        await _db.SaveChangesAsync();
        var category = new CategoryNewsService(_factory,_services.GetRequiredService<IServiceScopeFactory>(),NullLogger<CategoryNewsService>.Instance);
        var result = await category.GetNewsForCategoryAsync("Technology",user.IdUser,forceRandomSelection:true);
        Assert.Equal(allowedNews.IdNews,result.News!.IdNews);
        result = await category.GetNewsForCategoryAsync("Technology",user.IdUser);
        Assert.Equal(allowedNews.IdNews,result.News!.IdNews);
        Assert.True(UserBlockedSourceService.IsBlocked("https://sub.blocked.test/article",["blocked.test"]));
        Assert.False(UserBlockedSourceService.IsBlocked("https://notblocked.test/article",["blocked.test"]));
    }

    [Fact]
    public async Task FreshSearchScopesKeepTheExplicitUserSourceFilters()
    {
        var user = await Register();
        Assert.True((await new UserBlockedSourceService(_factory).ReplaceAsync(user.IdUser,["blocked.test"])).Success);
        var handler = new SearchHandler();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?>
        {
            ["LocalLlm:SearXngUrl"]="https://search.example.test", ["LocalLlm:Model"]="test"
        }).Build();
        var emptySession = new UserSessionService(new StorageRuntime(),_factory,_protection);
        var search = new UserAwareLocalLlmService(new SearchHttpFactory(handler),config,
            NullLogger<LocalLlmService>.Instance,emptySession,_factory) { SearchUserId=user.IdUser };
        var results = await ((ILocalLlmService)search).SearchNewsAsync("technology",5);
        Assert.Single(results);
        Assert.Contains("allowed.test",results[0].Url);
        Assert.Contains("-site:blocked.test",handler.LastQuery);
    }

    private sealed class SearchHttpFactory(HttpMessageHandler handler):IHttpClientFactory
    {
        public HttpClient CreateClient(string name)=>new(handler,disposeHandler:false);
    }
    private sealed class SearchHandler:HttpMessageHandler
    {
        public string LastQuery {get;private set;}="";
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            LastQuery=Uri.UnescapeDataString(request.RequestUri!.Query);
            var json=JsonSerializer.Serialize(new { results = new[] {
                new { title="Blocked technology article",url="https://blocked.test/news/technology-update",content="A sufficiently detailed snippet of current technology news." },
                new { title="Allowed technology article",url="https://allowed.test/news/technology-update",content="A sufficiently detailed snippet of current technology news." }
            }});
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent(json)});
        }
    }

    private static News MakeNews(Tag tag,string url) => new()
    {
        Tag=tag,Hyperlink=url,DetailNews=new DetailNews { Keyword="Headline",Context="Summary",AiSummary="Summary",
            NewsDate=DateTime.UtcNow,ImageUrl="https://example.test/image.jpg",BodySource="AiGenerated" }
    };
    private static string TokenFrom(string html) => Regex.Match(html,@"/activate-account/([^""\s]+)").Groups[1].Value;

    private sealed class ContextFactory(string connection) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => new(new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options);
    }
    private sealed class CapturingEmailSender : IEmailSender
    {
        public string? LastBody {get;private set;}
        public bool Fail {get;set;}
        public Task SendHtmlAsync(string recipient,string subject,string htmlBody,CancellationToken cancellationToken=default)
        {
            if (Fail) throw new InvalidOperationException("Simulated mail failure.");
            LastBody=htmlBody;
            return Task.CompletedTask;
        }
    }
    private sealed class StorageRuntime : IJSRuntime
    {
        public Dictionary<string,string> Values {get;} = new();
        public ValueTask<T> InvokeAsync<T>(string identifier,object?[]? args) => InvokeAsync<T>(identifier,CancellationToken.None,args);
        public ValueTask<T> InvokeAsync<T>(string identifier,CancellationToken cancellationToken,object?[]? args)
        {
            var parts=identifier.Split('.'); var key=parts[0]+":"+args![0]; object? result=null;
            if(parts[1]=="getItem" && Values.TryGetValue(key,out var value)) result=value;
            if(parts[1]=="setItem") Values[key]=(string)args[1]!;
            if(parts[1]=="removeItem") Values.Remove(key);
            return ValueTask.FromResult(result is null ? default! : (T)result);
        }
    }
}
