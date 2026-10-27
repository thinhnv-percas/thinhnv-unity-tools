using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine.Networking;

namespace Thinhnv.UnityTools.FigEditor
{
    /// <summary>A Figma link taken apart: the file (or branch) key and the layers it points at.</summary>
    public sealed class FigmaLink
    {
        public string Url;
        public string FileKey;
        public string NodeIds;

        private static readonly Regex Path = new Regex(@"figma\.com/(?:file|design|proto|board|slides|buzz)/([A-Za-z0-9]+)(?:/branch/([A-Za-z0-9]+))?", RegexOptions.IgnoreCase);
        private static readonly Regex NodeParam = new Regex(@"[?&]node-id=([^&#]+)", RegexOptions.IgnoreCase);
        private static readonly Regex BareKey = new Regex(@"^[A-Za-z0-9]{15,64}$");

        /// <summary>Parse a figma.com link (file, design, proto; branches; <c>node-id</c>) or a bare file key.</summary>
        public static bool TryParse(string text, out FigmaLink link, out string error)
        {
            link = null;
            error = null;
            text = (text ?? "").Trim();
            if (text.Length == 0)
            {
                error = "Paste a Figma link (figma.com/design/…) or a file key.";
                return false;
            }

            string key;
            Match m = Path.Match(text);
            if (m.Success)
            {
                // A branch is its own file to the API.
                key = m.Groups[2].Success && m.Groups[2].Length > 0 ? m.Groups[2].Value : m.Groups[1].Value;
            }
            else if (BareKey.IsMatch(text))
            {
                key = text;
            }
            else
            {
                error = "That isn't a Figma file link. Copy it from Figma: Share › Copy link, or the browser's address bar.";
                return false;
            }

            string nodes = null;
            Match n = NodeParam.Match(text);
            if (n.Success)
            {
                nodes = NodeId(Uri.UnescapeDataString(n.Groups[1].Value));
            }

            link = new FigmaLink { Url = text, FileKey = key, NodeIds = nodes };
            return true;
        }

        /// <summary>Links write node ids as <c>12-34</c>; the API wants <c>12:34</c>.</summary>
        private static string NodeId(string raw)
        {
            raw = raw.Trim();
            return Regex.IsMatch(raw, @"^\d+-\d+$") ? raw.Replace('-', ':') : raw;
        }
    }

    /// <summary>What a download from Figma produced.</summary>
    public sealed class FigmaDownload
    {
        public byte[] Fig;
        public string Name;
        public string Version;
        public string LastModified;
        public bool UpToDate;
        public readonly List<string> Warnings = new();
    }

    public sealed class FigmaException : Exception
    {
        public FigmaException(string message) : base(message)
        {
        }
    }

    /// <summary>
    /// Figma's REST API: reads a file as JSON (with layer outlines) plus its image fills and
    /// thumbnail, and builds a <c>.fig</c> from them with <see cref="FigRestImport"/>. The API can't
    /// export a <c>.fig</c> itself, so this is the file as the REST API describes it.
    ///
    /// The personal access token lives in this computer's Unity preferences (EditorPrefs), never in
    /// the project; the <c>FIGMA_TOKEN</c> environment variable is used when none is set.
    /// </summary>
    public static class FigmaApi
    {
        private const string TokenPref = "Thinhnv.FigEditor.FigmaToken";
        private const string FolderPref = "Thinhnv.FigEditor.ImportFolder";
        public const string TokenHelpUrl = "https://help.figma.com/hc/en-us/articles/8085703771159-Manage-personal-access-tokens";
        private const int ParallelDownloads = 4;

        /// <summary>The API root (tests point it elsewhere).</summary>
        public static string BaseUrl = "https://api.figma.com";

        /// <summary>
        /// The HTTP GET used for everything: (url, send the token?, cancel) → (status, body, Retry-After).
        /// Defaults to UnityWebRequest on the main thread; tests replace it.
        /// </summary>
        public static Func<string, string, CancellationToken, Task<Response>> Get = UnityGet;

        public struct Response
        {
            public long Status;
            public byte[] Body;
            public string RetryAfter;
            public string NetworkError;
        }

        // ─── Settings ────────────────────────────────────────────────────────────

        public static string Token
        {
            get
            {
                string token = EditorPrefs.GetString(TokenPref, "");
                if (string.IsNullOrEmpty(token))
                {
                    token = Environment.GetEnvironmentVariable("FIGMA_TOKEN") ?? Environment.GetEnvironmentVariable("FIGMA_ACCESS_TOKEN") ?? "";
                }

                return token.Trim();
            }
            set
            {
                if (string.IsNullOrEmpty(value))
                {
                    EditorPrefs.DeleteKey(TokenPref);
                }
                else
                {
                    EditorPrefs.SetString(TokenPref, value.Trim());
                }
            }
        }

        public static bool TokenFromEnvironment => string.IsNullOrEmpty(EditorPrefs.GetString(TokenPref, "")) && Token.Length > 0;

        /// <summary>The token with all but its ends hidden, for display.</summary>
        public static string MaskedToken
        {
            get
            {
                string t = Token;
                return t.Length <= 10 ? new string('•', t.Length) : t.Substring(0, 5) + "…" + t.Substring(t.Length - 4);
            }
        }

        public static string ImportFolder
        {
            get { return EditorPrefs.GetString(FolderPref, "Assets/Figma"); }
            set { EditorPrefs.SetString(FolderPref, value ?? ""); }
        }

        // ─── Calls ───────────────────────────────────────────────────────────────

        /// <summary>Who the token belongs to ("handle (email)").</summary>
        public static async Task<string> Me(string token, CancellationToken cancel)
        {
            object me = await Json(BaseUrl + "/v1/me", token, cancel);
            string handle = FigJson.Str(me, "handle") ?? "?";
            string email = FigJson.Str(me, "email");
            return string.IsNullOrEmpty(email) ? handle : handle + " (" + email + ")";
        }

        /// <summary>
        /// Download <paramref name="link"/> as a <c>.fig</c>. With <paramref name="onlyLinkedLayers"/> and a
        /// <c>node-id</c> in the link, only those layers (with their pages) are fetched. When
        /// <paramref name="skipIfVersion"/> matches Figma's current version nothing is downloaded and the
        /// result says <see cref="FigmaDownload.UpToDate"/>.
        /// </summary>
        public static async Task<FigmaDownload> Download(FigmaLink link, bool onlyLinkedLayers, string token, string skipIfVersion,
            Action<string, float> progress, CancellationToken cancel)
        {
            if (string.IsNullOrEmpty(token))
            {
                throw new FigmaException("No Figma access token. Set one in Preferences › Thinhnv › Fig Editor.");
            }

            var result = new FigmaDownload();
            string key = Uri.EscapeDataString(link.FileKey);
            string ids = onlyLinkedLayers && !string.IsNullOrEmpty(link.NodeIds) ? "&ids=" + Uri.EscapeDataString(link.NodeIds) : "";

            if (!string.IsNullOrEmpty(skipIfVersion))
            {
                progress?.Invoke("Checking the file's version…", 0.02f);
                object head = await Json(BaseUrl + "/v1/files/" + key + "?depth=1", token, cancel);
                if (FigJson.Str(head, "version") == skipIfVersion)
                {
                    result.UpToDate = true;
                    result.Name = FigJson.Str(head, "name");
                    result.Version = skipIfVersion;
                    result.LastModified = FigJson.Str(head, "lastModified");
                    return result;
                }
            }

            progress?.Invoke("Reading the file from Figma…", 0.05f);
            Response fileResponse = await Checked(BaseUrl + "/v1/files/" + key + "?geometry=paths" + ids, token, cancel);
            progress?.Invoke("Reading the layers…", 0.35f);
            object file = await Task.Run(() => FigJson.Parse(Encoding.UTF8.GetString(fileResponse.Body)), cancel);
            result.Name = FigJson.Str(file, "name") ?? link.FileKey;
            result.Version = FigJson.Str(file, "version");
            result.LastModified = FigJson.Str(file, "lastModified");

            // Image fills: their refs, then where to download them.
            var refs = new HashSet<string>();
            CollectImageRefs(FigJson.Obj(file, "document"), refs);
            var images = new Dictionary<string, byte[]>();
            if (refs.Count > 0)
            {
                progress?.Invoke("Finding the images…", 0.4f);
                object listing = await Json(BaseUrl + "/v1/files/" + key + "/images", token, cancel);
                Dictionary<string, object> urls = FigJson.Obj(FigJson.Obj(listing, "meta"), "images") ?? new Dictionary<string, object>();
                var wanted = new List<KeyValuePair<string, string>>();
                foreach (string r in refs)
                {
                    if (urls.TryGetValue(r, out object u) && u is string url && url.Length > 0)
                    {
                        wanted.Add(new KeyValuePair<string, string>(r, url));
                    }
                }

                int done = 0, failed = 0;
                for (int start = 0; start < wanted.Count; start += ParallelDownloads)
                {
                    var batch = new List<Task<Response>>();
                    for (int i = start; i < Math.Min(wanted.Count, start + ParallelDownloads); i++)
                    {
                        // Image URLs are signed storage links: no token for them.
                        batch.Add(Get(wanted[i].Value, null, cancel));
                    }

                    Response[] got = await Task.WhenAll(batch);
                    for (int i = 0; i < got.Length; i++)
                    {
                        if (got[i].Status == 200 && got[i].Body != null && got[i].Body.Length > 0)
                        {
                            images[wanted[start + i].Key] = got[i].Body;
                        }
                        else
                        {
                            failed++;
                        }
                    }

                    done += got.Length;
                    progress?.Invoke($"Downloading images {done}/{wanted.Count}…", 0.4f + 0.45f * done / Math.Max(1, wanted.Count));
                }

                if (failed > 0 || wanted.Count < refs.Count)
                {
                    result.Warnings.Add($"{failed + refs.Count - wanted.Count} of {refs.Count} images couldn't be downloaded.");
                }
            }

            byte[] thumbnail = null;
            string thumbnailUrl = FigJson.Str(file, "thumbnailUrl");
            if (!string.IsNullOrEmpty(thumbnailUrl))
            {
                progress?.Invoke("Downloading the thumbnail…", 0.87f);
                Response t = await Get(thumbnailUrl, null, cancel);
                thumbnail = t.Status == 200 ? t.Body : null;
            }

            progress?.Invoke("Building the .fig file…", 0.9f);
            var source = new FigmaSource
            {
                Url = link.Url,
                FileKey = link.FileKey,
                NodeIds = onlyLinkedLayers ? link.NodeIds : null,
                Name = result.Name,
                Version = result.Version,
                LastModified = result.LastModified,
                ImportedAt = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            };

            List<string> warnings = result.Warnings;
            result.Fig = await Task.Run(() => FigRestImport.Build(file, images, thumbnail, source, warnings), cancel);
            progress?.Invoke("Done", 1f);
            return result;
        }

        private static void CollectImageRefs(Dictionary<string, object> node, HashSet<string> refs)
        {
            if (node == null)
            {
                return;
            }

            foreach (string field in new[] { "fills", "strokes", "background" })
            {
                List<object> paints = FigJson.Arr(node, field);
                if (paints == null)
                {
                    continue;
                }

                foreach (object p in paints)
                {
                    if (FigJson.Str(p, "type") == "IMAGE" && FigJson.Str(p, "imageRef") is string r && r.Length > 0)
                    {
                        refs.Add(r);
                    }
                }
            }

            if (FigJson.Obj(node, "styleOverrideTable") is Dictionary<string, object> table)
            {
                foreach (object style in table.Values)
                {
                    CollectImageRefs(style as Dictionary<string, object>, refs);
                }
            }

            List<object> children = FigJson.Arr(node, "children");
            if (children != null)
            {
                foreach (object c in children)
                {
                    CollectImageRefs(c as Dictionary<string, object>, refs);
                }
            }
        }

        private static async Task<object> Json(string url, string token, CancellationToken cancel)
        {
            Response r = await Checked(url, token, cancel);
            return FigJson.Parse(Encoding.UTF8.GetString(r.Body));
        }

        /// <summary>GET with the token; a failure becomes a <see cref="FigmaException"/> saying what to do about it.</summary>
        private static async Task<Response> Checked(string url, string token, CancellationToken cancel)
        {
            Response r = await Get(url, token, cancel);
            cancel.ThrowIfCancellationRequested();
            if (r.Status == 200 && r.Body != null)
            {
                return r;
            }

            string detail = null;
            if (r.Body != null && r.Body.Length > 0)
            {
                try
                {
                    object json = FigJson.Parse(Encoding.UTF8.GetString(r.Body));
                    detail = FigJson.Str(json, "err") ?? FigJson.Str(json, "message");
                }
                catch (FormatException)
                {
                }
            }

            string tail = string.IsNullOrEmpty(detail) ? "" : " (Figma: " + detail + ")";
            switch (r.Status)
            {
                case 0:
                    throw new FigmaException("Couldn't reach Figma: " + (r.NetworkError ?? "no connection") + ".");
                case 400:
                    throw new FigmaException("Figma rejected the request" + tail + ". Check the link.");
                case 401:
                case 403:
                    throw new FigmaException("Figma refused the token" + tail + ". Check it in Preferences › Thinhnv › Fig Editor, and that its account can open this file (scope: file content, read).");
                case 404:
                    throw new FigmaException("Figma has no such file, or the token's account can't open it" + tail + ".");
                case 429:
                    throw new FigmaException("Figma's rate limit was reached" + (string.IsNullOrEmpty(r.RetryAfter) ? "" : "; try again in " + r.RetryAfter + " s") + tail + ".");
                default:
                    throw new FigmaException($"Figma answered {r.Status}{tail}.");
            }
        }

        // ─── Transport ───────────────────────────────────────────────────────────

        private static Task<Response> UnityGet(string url, string token, CancellationToken cancel)
        {
            var tcs = new TaskCompletionSource<Response>();
            var request = UnityWebRequest.Get(url);
            request.timeout = 600;
            if (!string.IsNullOrEmpty(token))
            {
                request.SetRequestHeader("X-Figma-Token", token);
            }

            CancellationTokenRegistration registration = cancel.Register(() =>
            {
                // Cancel is pressed on the main thread, where the request may be aborted.
                request.Abort();
                tcs.TrySetCanceled();
            });

            UnityWebRequestAsyncOperation op = request.SendWebRequest();
            op.completed += _ =>
            {
                registration.Dispose();
                var response = new Response
                {
                    Status = request.responseCode,
                    Body = request.downloadHandler?.data,
                    RetryAfter = request.GetResponseHeader("Retry-After"),
                    NetworkError = request.result == UnityWebRequest.Result.ConnectionError ? request.error : null,
                };

                request.Dispose();
                tcs.TrySetResult(response);
            };

            return tcs.Task;
        }
    }
}
