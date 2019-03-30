// Copyright (c) 2018-2019 fate/loli
// 
// This software is released under the MIT License.
// https://opensource.org/licenses/MIT

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using HtmlAgilityPack;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace nhitomi.Core
{
    public static class Tsumino
    {
        public const int RequestCooldown = 1000;

        public const string GalleryRegex =
            @"\b((http|https):\/\/)?(www\.)?tsumino(\.com)?\/(Book\/Info\/)?(?<Tsumino>[0-9]{1,5})\b";

        public static string Book(int id) => $"https://www.tsumino.com/Book/Info/{id}/";
        public static string ImageObject(string name) => $"https://www.tsumino.com/Image/Object?name={name}";
        public const string ReadLoad = "https://www.tsumino.com/Read/Load";
        public const string Operate = "https://www.tsumino.com/Books/Operate";

        public static class XPath
        {
            public const string BookTitle = @"//*[@id=""Title""]";
            public const string BookUploader = @"//*[@id=""Uploader""]";
            public const string BookUploaded = @"//*[@id=""Uploaded""]";
            public const string BookPages = @"//*[@id=""Pages""]";
            public const string BookRating = @"//*[@id=""Rating""]";
            public const string BookCategory = @"//*[@id=""Category""]/a";
            public const string BookCollection = @"//*[@id=""Collection""]/a";
            public const string BookGroup = @"//*[@id=""Group""]/a";
            public const string BookArtist = @"//*[@id=""Artist""]/a";
            public const string BookParody = @"//*[@id=""Parody""]/a";
            public const string BookCharacter = @"//*[@id=""Character""]/a";
            public const string BookTag = @"//*[@id=""Tag""]/a";
        }

        public sealed class DoujinData
        {
            public readonly DateTime _processed = DateTime.UtcNow;

            public int id;
            public string title;
            public string uploader;
            public string uploaded;
            public int pages;

            public Rating rating;

            public struct Rating
            {
                static readonly Regex _ratingRegex =
                    new Regex(@"(?<value>\d*\.?\d+)\s\((?<users>\d+)\susers\s\/\s(?<favs>\d+)\sfavs\)",
                        RegexOptions.IgnoreCase | RegexOptions.Compiled);

                public Rating(string str)
                {
                    var match = _ratingRegex.Match(str);

                    value = double.Parse(match.Groups["value"].Value);
                    users = int.Parse(match.Groups["users"].Value);
                    favs = int.Parse(match.Groups["favs"].Value);
                }

                public double value;
                public int users;
                public int favs;
            }

            public string category;
            public string collection;
            public string group;
            public string artist;
            public string parody;
            public string[] characters;
            public string[] tags;

            public Reader reader;

            public struct Reader
            {
                public int reader_page_number;
                public string reader_end_url;
                public string[] reader_page_urls;
                public int reader_page_total;
                public string reader_base_url;
                public string reader_start_url;
                public string reader_process_url;
            }
        }

        public sealed class ListData
        {
            public ListItem[] Data;

            public class ListItem
            {
                public ListEntry Entry;

                public struct ListEntry
                {
                    public int Id;
                    public string Title;
                    public double Rating;

                    public int Pages;
                    // Tsumino actually returns more than this but they have no meaning
                }

                public int Impression;
                public int HistoryPage;
            }

            public int PageCount;
            public int PageNumber;
        }
    }

    public sealed class TsuminoClient : IDoujinClient
    {
        public string Name => nameof(Tsumino);
        public string Url => "https://www.tsumino.com/";

        public string IconUrl =>
            "https://cdn.discordapp.com/icons/167128230908657664/b2089ee1d26a7e168d63960d6ed31b66.png";

        public double RequestThrottle => 100;

        public DoujinClientMethod Method => DoujinClientMethod.Html;

        public Regex GalleryRegex => new Regex(Tsumino.GalleryRegex, RegexOptions.IgnoreCase | RegexOptions.Compiled);

        readonly HttpClient _http;
        readonly JsonSerializer _json;
        readonly ILogger<TsuminoClient> _logger;

        public TsuminoClient(
            IHttpClientFactory httpFactory,
            JsonSerializer json,
            ILogger<TsuminoClient> logger)
        {
            _http = httpFactory?.CreateClient(Name);
            _json = json;
            _logger = logger;
        }

        public async Task<IDoujin> GetAsync(string id, CancellationToken cancellationToken = default)
        {
            if (!int.TryParse(id, out var intId))
                return null;

            try
            {
                HtmlNode root;

                using (var response = await _http.GetAsync(Tsumino.Book(intId), cancellationToken))
                using (var reader = new StringReader(await response.Content.ReadAsStringAsync()))
                {
                    var doc = new HtmlDocument();
                    doc.Load(reader);

                    root = doc.DocumentNode;
                }

                // Scrape data from HTML using XPath
                var data = new Tsumino.DoujinData
                {
                    id = intId,
                    title = InnerSanitized(root.SelectSingleNode(Tsumino.XPath.BookTitle)),
                    uploader = InnerSanitized(root.SelectSingleNode(Tsumino.XPath.BookUploader)),
                    uploaded = InnerSanitized(root.SelectSingleNode(Tsumino.XPath.BookUploaded)),
                    pages = int.Parse(InnerSanitized(root.SelectSingleNode(Tsumino.XPath.BookPages))),
                    rating = new Tsumino.DoujinData.Rating(
                        InnerSanitized(root.SelectSingleNode(Tsumino.XPath.BookRating))),
                    category = InnerSanitized(root.SelectSingleNode(Tsumino.XPath.BookCategory)),
                    collection = InnerSanitized(root.SelectSingleNode(Tsumino.XPath.BookCollection)),
                    @group = InnerSanitized(root.SelectSingleNode(Tsumino.XPath.BookGroup)),
                    artist = InnerSanitized(root.SelectSingleNode(Tsumino.XPath.BookArtist)),
                    parody = InnerSanitized(root.SelectSingleNode(Tsumino.XPath.BookParody)),
                    characters = root.SelectNodes(Tsumino.XPath.BookCharacter)?.Select(InnerSanitized).ToArray(),
                    tags = root.SelectNodes(Tsumino.XPath.BookTag)?.Select(InnerSanitized).ToArray()
                };

                // Parse images
                using (var response = await _http.PostAsync(Tsumino.ReadLoad, new FormUrlEncodedContent(
                    new Dictionary<string, string>
                    {
                        {"q", id}
                    }), cancellationToken))
                using (var textReader = new StringReader(await response.Content.ReadAsStringAsync()))
                using (var jsonReader = new JsonTextReader(textReader))
                    data.reader = _json.Deserialize<Tsumino.DoujinData.Reader>(jsonReader);

                _logger.LogDebug($"Got doujin {id}: {data.title}");

                return new TsuminoDoujin(this, data);
            }
            catch (Exception)
            {
                return null;
            }
        }

        static string InnerSanitized(HtmlNode node) =>
            node == null ? null : HtmlEntity.DeEntitize(node.InnerText).Trim();

        public bool CompletelyExcludeHated { get; set; } = true;

        public Task<IAsyncEnumerable<IDoujin>> SearchAsync(
            string query,
            CancellationToken cancellationToken = default) =>
            AsyncEnumerable.CreateEnumerable(() =>
                {
                    Tsumino.ListData current = null;
                    var index = 0;

                    return AsyncEnumerable.CreateEnumerator(
                        async token =>
                        {
                            try
                            {
                                // Load list
                                using (var response = await _http.PostAsync(Tsumino.Operate, new FormUrlEncodedContent(
                                    new Dictionary<string, string>
                                    {
                                        {"PageNumber", (index + 1).ToString()},
                                        {"Text", query?.Trim()},
                                        {"Sort", "Newest"},
                                        {"CompletelyExcludeHated", CompletelyExcludeHated ? "true" : "false"}
                                    }), token))
                                using (var textReader = new StringReader(await response.Content.ReadAsStringAsync()))
                                using (var jsonReader = new JsonTextReader(textReader))
                                    current = _json.Deserialize<Tsumino.ListData>(jsonReader);

                                index++;

                                _logger.LogDebug($"Got page {index}: {current.Data?.Length ?? 0} items");

                                return !Array.IsNullOrEmpty(current.Data);
                            }
                            catch (Exception)
                            {
                                return false;
                            }
                        },
                        () => current,
                        () => { }
                    );
                })
                .SelectMany(list => AsyncEnumerable.CreateEnumerable(() =>
                {
                    IDoujin current = null;
                    var index = 0;

                    return AsyncEnumerable.CreateEnumerator(
                        async token =>
                        {
                            if (index == list.Data.Length)
                                return false;

                            current = await GetAsync(list.Data[index++].Entry.Id.ToString(), token);
                            return true;
                        },
                        () => current,
                        () => { }
                    );
                }))
                .AsCompletedTask();

        public override string ToString() => Name;

        public void Dispose()
        {
        }
    }
}