using System;
using System.Text.RegularExpressions;
using System.Linq;

partial class TestParse
{
    // Replace [GeneratedRegex] with standard compiled Regex instances
    private static readonly Regex LineSplitRegex = new(@"\n\n+", RegexOptions.Compiled);
    private static readonly Regex UrlRegex = new(@"https?://[^\s]+", RegexOptions.Compiled);

    static void RunTest()
    {
        string text = @"[{""title"": ""How AI is transforming technology in 2025"", ""url"": ""https://ai-tech-news.com/ai-transforming-2025"", ""summary"": ""A comprehensive look at how artificial intelligence is reshaping industries and daily life with new tools and innovations.""}, {""title"": ""Top Tech Trends to Watch This Year"", ""url"": ""https://tech-trends-blog.com/top-trends-2025"", ""Query: Technology""]
[{""title"": ""How AI is transforming technology in 2025"", ""url"": ""https://ai-tech-news.com/ai-transform-2025"", ""summary"": ""A comprehensive look at how artificial intelligence is reshaping industries and daily life with new tools and innovations.""}, {""title"": ""Top Tech Trends to Watch This Year"", ""url"": ""extract the full text of the article from this URL: https://www.npr.org/2025/08/19/1306274846/ai-is-redefining-the-future-of-technology

You are a specialized web scraping and data extraction engine. Your sole purpose is to extract the full text of an article from a provided URL. You must return ONLY the raw extracted content, nothing else. No formatting, no headers, no explanations.
URL: https://www.npr.org/2025/08/19/1306274846/ai-is-redefining-the-future-of-technology

You are a specialized web scraping and as an expert in the domain of data extraction, your task is to extract the full text of the article from this URL: https://www.npr.org/2025/01/13/1207984391/ai-is-redefining-the-future-of-technology""]";

        // Remove the parentheses "()" after the Regex variables since they are now properties/fields
        var parts = LineSplitRegex.Split(text.Trim()).Select(p => p.Trim()).Where(p => p.Length > 0).ToList();

        foreach (var part in parts)
        {
            var m = UrlRegex.Match(part);
            if (m.Success)
            {
                string url = m.Value;
                string title = part.Replace(url, "").Trim();
                Console.WriteLine("URL: " + url);
                Console.WriteLine("TITLE length: " + title.Length);
            }
        }
    }
}