using RedditSharp;
using RedditSharp.Things; // Necesario para la clase Post
using System.Web;
using System.Text.Json;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System;

namespace NuctiusNow.Services.Reddit;

/// <summary>
/// Service for fetching news from Reddit subreddits.
/// Maps subreddit names to news categories (e.g., r/technology ? Technology).
/// </summary>
public class RedditService
{
    private readonly string _clientId = string.Empty;
    private readonly string _clientSecret = string.Empty;
    private readonly string _userAgent = "web:NuctiusNow:v1.0 (by /u/Fantasy003)";
    // Add this shared client field
    private readonly RedditSharp.Reddit _redditClient;
    public RedditService()
    {
        try
        {
            var settingsPath = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory,
                "appsettings.json");

            if (File.Exists(settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                
                // Read Reddit credentials from appsettings.json
                if (doc.RootElement.TryGetProperty("reddit", out var redditSection) || 
                    doc.RootElement.TryGetProperty("Reddit", out var redditSection))
                {
                    if (redditSection.TryGetProperty("clientId", out var clientIdProp))
                    {
                        _clientId = clientIdProp.GetString() ?? string.Empty;
                    }
                    
                    if (redditSection.TryGetProperty("clientSecret", out var clientSecretProp))
                    {
                        _clientSecret = clientSecretProp.GetString() ?? string.Empty;
                    }
                }
            }
            
            // Validate that credentials are properly configured
            if (!string.IsNullOrEmpty(_clientId) && !string.IsNullOrEmpty(_clientSecret))
            {
                // Use authenticated Reddit client with OAuth 2.0
                var webAgent = new WebAgent(_userAgent);
                _redditClient = new Reddit(webAgent, _clientId, _clientSecret);
                Console.WriteLine("[RedditService] Authenticated Reddit client initialized");
            }
            else
            {
                // Fall back to unauthenticated client (may have rate limits)
                Console.WriteLine("[RedditService] Warning: Reddit API credentials missing - using unauthenticated client (will hit rate limits)");
                var webAgent = new WebAgent(_userAgent);
                _redditClient = new Reddit(webAgent);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RedditService] Error initializing Reddit client: {ex.Message}");
            // Fall back to unauthenticated client on error
            try
            {
                var webAgent = new WebAgent(_userAgent);
                _redditClient = new Reddit(webAgent);
            }
            catch
            {
                // Use minimal client if all else fails
                _redditClient = new Reddit();
            }
        }
    }

    public static List<(string Subreddit, string Category)> GetSubredditMapping()
    {
        return new List<(string, string)>
        {
            ("r/technology", "Technology"),
            ("r/worldnews", "Politics"),
            ("r/news", "General"),
            ("r/sports", "Sports"),
            ("r/funny", "Entertainment"),
            ("r/gaming", "Gaming"),
            ("r/science", "Science"),
            ("r/askreddit", "Society"),
            ("r/pics", "Visual Arts"),
            ("r/todayilearned", "Education"),
            ("r/movies", "Movies"),
            ("r/music", "Music"),
            ("r/economics", "Economy"),
            ("r/health", "Health"),
            ("r/travel", "Tourism")
        };
    }

    /// <summary>
    /// Get hot posts from a subreddit with their titles and URLs.
    /// </summary>
    public async Task<List<NewsArticle>> GetHotPostsAsync(string subreddit)
    {
        var posts = new List<NewsArticle>();

        try
        {
            var reddit = GetRedditClient();
            // Usamos GetListing apuntando directo al endpoint de la API.
            // Como "subreddit" ya incluye el prefijo (ej. "r/news"), la URL queda "/r/news/hot.json"
            var hotPosts = reddit.GetListing<Post>($"/{subreddit}/hot.json");

            int count = 0;
            await foreach (var post in hotPosts)
            {
                if (count >= 25) break; // Limitamos a 25 manualmente

                string url = (post.Url != null && !string.IsNullOrEmpty(post.Url.ToString()))
                    ? post.Url.ToString()
                    : $"https://www.reddit.com/{subreddit}/{HttpUtility.UrlEncode(post.Id)}";

                posts.Add(new NewsArticle
                {
                    Title = post.Title,
                    Url = url,
                    Subreddit = subreddit,
                    Upvotes = post.Score,
                    Comments = post.CommentCount, 
                    Posted = post.CreatedUTC 
                });
                
                count++;
            }
        }
        catch (RedditHttpException ex)
        {
            Console.WriteLine($"[RedditService] Rate limited or blocked on {subreddit}. Status: {ex.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RedditService] Error fetching posts from {subreddit}: {ex.Message}");
        }

        return posts;
    }

    /// <summary>
    /// Get rising posts from a subreddit.
    /// </summary>
    public async Task<List<NewsArticle>> GetRisingPostsAsync(string subreddit)
    {
        var posts = new List<NewsArticle>();

        try
        {
            var reddit = GetRedditClient();
            var risingPosts = reddit.GetListing<Post>($"/{subreddit}/rising.json");

            int count = 0;
            await foreach (var post in risingPosts)
            {
                if (count >= 25) break;

                string url = (post.Url != null && !string.IsNullOrEmpty(post.Url.ToString()))
                    ? post.Url.ToString()
                    : $"https://www.reddit.com/{subreddit}/{HttpUtility.UrlEncode(post.Id)}";

                posts.Add(new NewsArticle
                {
                    Title = post.Title,
                    Url = url,
                    Subreddit = subreddit,
                    Upvotes = post.Score,
                    Comments = post.CommentCount, 
                    Posted = post.CreatedUTC 
                });

                count++;
            }
        }
        catch (RedditHttpException ex)
        {
            Console.WriteLine($"[RedditService] Rate limited or blocked on {subreddit}. Status: {ex.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RedditService] Error fetching posts from {subreddit}: {ex.Message}");
        }

        return posts;
    }

    /// <summary>
    /// Get top posts of all time from a subreddit.
    /// </summary>
    public async Task<List<NewsArticle>> GetTopPostsAsync(string subreddit)
    {
        var posts = new List<NewsArticle>();

        try
        {
            var reddit = GetRedditClient();
            // Para top posts histórico ("all time"), la API de Reddit utiliza el query parameter ?t=all
            var topPosts = reddit.GetListing<Post>($"/{subreddit}/top.json?t=all");

            int count = 0;
            await foreach (var post in topPosts)
            {
                if (count >= 25) break;

                string url = (post.Url != null && !string.IsNullOrEmpty(post.Url.ToString()))
                    ? post.Url.ToString()
                    : $"https://www.reddit.com/{subreddit}/{HttpUtility.UrlEncode(post.Id)}";

                posts.Add(new NewsArticle
                {
                    Title = post.Title,
                    Url = url,
                    Subreddit = subreddit,
                    Upvotes = post.Score,
                    Comments = post.CommentCount, 
                    Posted = post.CreatedUTC 
                });
                
                count++;
            }
        }
        catch (RedditHttpException ex)
        {
            Console.WriteLine($"[RedditService] Rate limited or blocked on {subreddit}. Status: {ex.StatusCode}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[RedditService] Error fetching posts from {subreddit}: {ex.Message}");
        }

        return posts;
    }

    /// <summary>
    /// Get the hottest news article for a given category by combining hot, rising, and top posts.
    /// </summary>
public async Task<NewsArticle?> GetHottestNewsAsync(string subreddit)
{
    // Temporarily just fetch Hot posts to reduce requests from 45 to 15
    var hotPosts = await GetHotPostsAsync(subreddit);

    if (hotPosts.Count == 0)
        return null;

    return hotPosts.OrderByDescending(p => p.Upvotes / (p.Comments > 0 ? (double)p.Comments : 1)).First();
}

    private RedditSharp.Reddit GetRedditClient()
    {
        return _redditClient; // Reuse the shared client
    }

    /// <summary>
    /// Logs Reddit API errors with detailed information.
    /// </summary>
    private void LogRedditError(string subreddit, int statusCode, string? message)
    {
        var errorMessage = message ?? $"Status {statusCode}";
        
        switch (statusCode)
        {
            case 429:
                Console.WriteLine($"[RedditService] Rate limited on {subreddit}. Wait and retry later.");
                break;
            case 403:
                Console.WriteLine($"[RedditService] Forbidden on {subreddit}. Check API credentials or subreddit permissions.");
                break;
            default:
                Console.WriteLine($"[RedditService] API error on {subreddit}: {statusCode} - {errorMessage}");
                break;
        }
    }
}

public class NewsArticle
{
    public string Title { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public string Subreddit { get; set; } = string.Empty;
    public int Upvotes { get; set; }
    public int Comments { get; set; }
    public DateTime Posted { get; set; }
}