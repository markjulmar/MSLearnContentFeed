using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using MSLearnContentFeed.Models;
using MSLearnCatalogAPI;
using System.ServiceModel.Syndication;
using System.Xml;
using System.Text.RegularExpressions;
using System.IO;
using System.Text;
using System.Xml.Linq;

namespace MSLearnContentFeed.Controllers
{
    public class HomeController : Controller
    {
        private readonly ILogger<HomeController> _logger;
        private static List<SyndicationItem> LatestData;
        private static DateTimeOffset? LastRefresh;
        private const int RefreshTimeoutInMinutes = -60; // todo: move this to config
        const string RSS = "rss";
        const string ATOM = "atom";

        public HomeController(ILogger<HomeController> logger)
        {
            _logger = logger;
        }

        public async Task<IActionResult> Index(string format = null)
        {
            if (format != RSS && format != ATOM)
                format = RSS;

            _logger.LogInformation($"Request for feed in {format} format.");

            bool atomFeed = string.Compare(format ?? RSS, ATOM, true) == 0;

            if (LatestData == null
                || LastRefresh == null
                || LastRefresh < DateTime.UtcNow.AddMinutes(RefreshTimeoutInMinutes))
            {
                _logger.LogInformation("Hitting CatalogAPI for latest info.");
                var catalog = await CatalogApi.GetCatalog();
                var products = Flatten(catalog.Products).ToList();
                LatestData = catalog.Modules.OrderByDescending(m => m.LastModified)
                                    .Select(m => CreateSyndicationItem(m, products)).ToList();
                LastRefresh = DateTime.UtcNow;
            }

            string url = $"{this.Request.Scheme}://{this.Request.Host}{this.Request.PathBase}";
            var feed = new SyndicationFeed("Microsoft Learn Catalog", "Published educational content in Microsoft Learn", new Uri(url)) {
                Generator = "LearnBot",
                Copyright = new TextSyndicationContent("Microsoft"),
                LastUpdatedTime = LastRefresh.Value,
                Items = LatestData
            };

            XNamespace atom = "http://www.w3.org/2005/Atom";
            feed.ElementExtensions.Add(
                new XElement(atom + "link",
                new XAttribute("href", url),
                new XAttribute("rel", "self"),
                new XAttribute("type", "application/rss+xml")));

            var sb = new StringWriterWithEncoding(Encoding.UTF8);
            using (var writer = XmlWriter.Create(sb, new XmlWriterSettings { Indent = true, Encoding = Encoding.UTF8 }))
            {
                SyndicationFeedFormatter formatter = atomFeed == true
                    ? (SyndicationFeedFormatter) new Atom10FeedFormatter(feed)
                    : new Rss20FeedFormatter(feed, false);
                formatter.WriteTo(writer);
            }

            return this.Content(sb.ToString(), $"application/{format}+xml");
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }

        static readonly Regex htmlRegex = new Regex("(<.*?>\\s*)+", RegexOptions.Singleline);
        private static SyndicationItem CreateSyndicationItem(Module m, List<(string Id, string Name)> products)
        {
            string url = RemoveTracker(m.Url);
            var item = new SyndicationItem(m.Title, htmlRegex.Replace(m.Summary, " ").Trim(), new Uri(url), url, m.LastModified)
            {
                PublishDate = m.LastModified
            };
            item.Authors.Add(new SyndicationPerson { Name = "MSLearn" });

            foreach (var product in m.Products)
            {
                var (Id, Name) = products.FirstOrDefault(p => p.Id == product);
                if (Id != null)
                {
                    item.Categories.Add(new SyndicationCategory(Name));
                }
            }
            return item;
        }

        private static string RemoveTracker(string url)
        {
            int index = url.LastIndexOf('/');
            return url.Substring(0, index);
        }

        static IEnumerable<(string Id, string Name)> Flatten(IEnumerable<Product> products)
        {
            foreach (var p in products)
            {
                yield return (p.Id, p.Name);

                string parentText = p.Name;
                if (p.Children?.Count > 0)
                {
                    foreach (var child in p.Children)
                    {
                        string childText = child.Name.StartsWith(parentText) ? child.Name : parentText + " " + child.Name;
                        yield return (child.Id, childText);
                    }
                }
            }
        }

        sealed class StringWriterWithEncoding : StringWriter
        {
            private readonly Encoding encoding;
            public override Encoding Encoding => encoding;
            public StringWriterWithEncoding(Encoding encoding)
            {
                this.encoding = encoding;
            }
        }
    }
}
