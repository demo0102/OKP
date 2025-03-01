﻿using OKP.Core.Utils;
using Serilog;
using System.Net;
using System.Text.RegularExpressions;
using static OKP.Core.Interface.TorrentContent;

namespace OKP.Core.Interface.Acgrip
{
    internal class AcgripAdapter : AdapterBase
    {
        private readonly HttpClient httpClient;
        private readonly Template template;
        private readonly TorrentContent torrent;
        private readonly Uri baseUrl = new("https://acg.rip/");
        private const string pingUrl = "cp/posts/upload";
        private const string postUtl = "cp/posts";
        private string category;
        private readonly Regex personalReg = new(@"class=""panel-title""\>(.*?)\</div\>");
        private readonly Regex teamReg = new(@"class=""panel-title-right""\>(.*?)\</div\>");
        private readonly Regex tokenReg = new(@"\<meta\sname=""csrf-token""\scontent=""(.*)""\s/\>");
        private readonly List<string> trackers = new() { "http://t.acg.rip:6699/announce" };
        private string authenticityToken = "";
        private const string site = "acgrip";
        public AcgripAdapter(TorrentContent torrent, Template template)
        {
            var httpClientHandler = new HttpClientHandler()
            {
                CookieContainer = HttpHelper.GlobalCookieContainer,
                AllowAutoRedirect = false
            };
            httpClient = new(httpClientHandler)
            {
                BaseAddress = baseUrl,
            };
            httpClient.DefaultRequestHeaders.Add("user-agent", HttpHelper.GlobalUserAgent);
            this.template = template;
            this.torrent = torrent;
            category = CategoryHelper.SelectCategory(torrent.Tags, site);

            if (template.Proxy is not null)
            {
                httpClientHandler.Proxy = new WebProxy(
                    new Uri(template.Proxy),
                    BypassOnLocal: false);
            }

            if (!Valid())
            {
                IOHelper.ReadLine();
                throw new();
            }
        }

        public override async Task<HttpResult> PingAsync()
        {
            var (result, _) = await PingInternalAsync(httpClient, pingUrl, site);
            if (!result.IsSuccess) return result;
            var raw = result.Message;

            if (raw.Contains(@"继续操作前请注册或者登录"))
            {
                Log.Error("{Site} login failed", site);
                return new(403, "Login failed" + raw, false);
            }

            // Some accounts don’t belong to team
            var match = teamReg.Match(raw);
            if (match == Match.Empty)
            {
                match = personalReg.Match(raw);
            }

            if (match == Match.Empty || match.Groups[1].Value != template.Name)
            {
                Log.Error("你设置了{Site}的发布身份为{Team},但是你的Cookie对应的账户是{Name}。", site, template.Name, match?.Groups[1].Value ?? "undefined");
                return new(500, "Cannot find your team number." + raw, false);
            }
            var tokenMatch = tokenReg.Match(raw);
            if (!tokenMatch.Success)
            {
                Log.Error("登录失败，找不到authenticityToken");
                return new(500, "Cannot find authenticityToken." + raw, false);
            }
            authenticityToken = tokenMatch.Groups[1].Value;
            Log.Debug("{Site} login success", site);
            return new(200, "Success", true);
        }

        public override async Task<HttpResult> PostAsync()
        {
            Log.Information("正在发布{Site}", site);
            if (torrent.Data is null)
            {
                Log.Fatal("{Site} torrent.Data is null", site);
                throw new NotImplementedException();
            }
            MultipartFormDataContent form = new()
            {
                { new StringContent(authenticityToken), "authenticity_token" },
                { new StringContent(category), "post[category_id]" },
                // { new StringContent("2022"), "year" },
                // { new StringContent("0"), "post[series_id]" },
                { new StringContent("1"), "post[post_as_team]"},
                { torrent.Data.ByteArrayContent, "post[torrent]", torrent.Data.FileInfo.Name},
                { new StringContent(template.DisplayName ?? torrent.DisplayName ?? ""), "post[title]" },
                { new StringContent(template.Content??""), "post[content]" },
                { new StringContent("发布"), "commit" }
            };
            Log.Verbose("{Site} formdata content: {@MultipartFormDataContent}", site, form);
            var result = await httpClient.PostAsyncWithRetry(postUtl, form);
            var raw = await result.Content.ReadAsStringAsync();
            if (result.IsSuccessStatusCode)
            {
                if (raw.Contains("<div class=\"alert alert-warning\">已存在相同的种子</div>"))
                {
                    Log.Information("{Site} has already exist", site);
                    return new(200, "Success", true);
                }
                if (raw.Contains("<div class=\"alert alert-success\">种子发布成功</div>"))
                {
                    Log.Information("{Site} post success", site);
                    return new(200, "Success", true);
                }
                if (raw.Contains("<div class=\"alert alert-warning\">种子内的资源太小</div>"))
                {
                    Log.Error("{Site} upload failed", site);
                    return new(500, "Upload failed, files too small.", false);
                }
                Log.Error("{Site} upload failed. Unknown reson. {NewLine} {Raw}", Environment.NewLine, site, raw);
                return new(500, "Upload failed" + raw, false);
            }
            Log.Error("{Site} upload failed.{NewLine}" +
                "Code: {Code}{NewLine}" +
                "{Raw}", site, Environment.NewLine, result.StatusCode, Environment.NewLine, raw);
            return new((int)result.StatusCode, "Failed" + raw, false);
        }
        private bool Valid()
        {
            if (torrent.Data?.TorrentObject is null)
            {
                Log.Fatal("{Site} torrent.Data?.TorrentObject is null", site);
                throw new ArgumentNullException(nameof(torrent.Data.TorrentObject));
            }
            foreach (var tracker in trackers)
            {
                if (!torrent.Data.TorrentObject.Trackers.SelectMany(p => p).Any(p => p.TrimEnd('/').Equals(tracker.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
                {
                    Log.Error("缺少Tracker：{0}", tracker);
                    return false;
                }
            }
            return ValidTemplate(template, site, torrent.SettingPath);
        }
    }
}
