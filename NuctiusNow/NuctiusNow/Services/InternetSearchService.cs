using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NuctiusNow.Services
{
    public class NewsArticle
    {
        public string Title { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string Snippet { get; set; } = string.Empty;
    }

    // Fix IDE0290: Use primary constructor to remove boilerplate field assignment
    public class InternetSearchService(ILocalLlmService llmService)
    {
        public static List<(string Category, string SearchQuery)> GetCategoryMapping()
        {
            // Fix IDE0028: Simplified collection initialization
            return
            [
                ("Technology", "technology news"),
                ("Sports", "sports news"),
                ("Politics", "politics news"),
                ("Health", "health news"),
                ("Business", "business news"),
                ("Entertainment", "entertainment news")
            ];
        }

        public async Task<NewsArticle?> SearchCategoryAsync(string category)
        {
            var mappings = GetCategoryMapping();

            // Fix IDE0042: Deconstruct variable declaration for the tuple
            var (matchCategory, matchSearchQuery) = mappings.FirstOrDefault(m => m.Category.Equals(category, StringComparison.OrdinalIgnoreCase));

            // Fix CS0019: ValueTuple cannot be compared to <null>. Check for default/null string instead.
            if (string.IsNullOrEmpty(matchCategory))
            {
                return null;
            }

            // Leverage the LocalLlmService to perform the actual AI-powered web search
            var results = await llmService.SearchWebAsync(matchSearchQuery, 1);
            var first = results?.FirstOrDefault();

            if (first != null && !string.IsNullOrEmpty(first.Value.Url))
            {
                // Fix IDE0042: Further deconstruct the result tuple for cleaner property assignment
                var (title, url, snippet) = first.Value;

                return new NewsArticle
                {
                    Title = title,
                    Url = url,
                    Snippet = snippet
                };
            }

            return null;
        }
    }
}