using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace T7Drop
{
    /// <summary>
    /// No answer from t7drop: a network error, or a response without t7drop's header (an nginx
    /// error page, or an unknown token). Try again later; never drop items because of it.
    /// </summary>
    class TransientException : Exception
    {
        public TransientException(string message) : base(message) { }
    }

    /// <summary>t7drop refused the request.</summary>
    class ApiException : Exception
    {
        public readonly int Status;
        public readonly string Code;
        public readonly Dictionary<string, object> Details;

        public ApiException(int status, string code, Dictionary<string, object> details)
            : base(code + " (HTTP " + status + ")")
        {
            Status = status;
            Code = code;
            Details = details;
        }

        public bool Transient
        {
            get { return Status >= 500 || Status == 429 || Status == 507; }
        }
    }

    /// <summary>
    /// The t7drop protocol over HTTPS (docs/design.md, "Protocol v1"), as cli/t7drop_cli/api.py
    /// does it. Blocking: call it off the UI thread. One HttpClient serves every Api.
    /// </summary>
    class Api
    {
        public const string InstanceHeader = "X-T7drop-Instance";
        static readonly HttpClient client = NewClient();

        readonly string baseUrl;
        readonly string token;
        volatile string instance;

        public Api(string url, string token)
        {
            if (!IsServerUrl(url))
                throw new ArgumentException("Not an https:// address: " + url);
            baseUrl = url.TrimEnd('/') + "/api/v1";
            this.token = token;
        }

        public static bool IsServerUrl(string url)
        {
            Uri uri;
            return Uri.TryCreate(url, UriKind.Absolute, out uri) && (uri.Scheme == "https" || uri.Scheme == "http")
                && uri.Host.Length > 0 && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment);
        }

        /// <summary>The server instance of the last answer; it changes when the server restarts.</summary>
        public string Instance
        {
            get { return instance; }
        }

        static HttpClient NewClient()
        {
            ServicePointManager.Expect100Continue = false;  // the server reads bodies at once
            ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 8);
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
            var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("T7Drop/" + Updater.Text(Updater.CurrentVersion));
            http.DefaultRequestHeaders.ExpectContinue = false;
            return http;
        }

        /// <summary>
        /// One request. Throws TransientException when t7drop does not answer, ApiException when it
        /// refuses, and OperationCanceledException when cancel is cancelled.
        /// </summary>
        public byte[] Request(string method, string path, byte[] body, string contentType, int timeoutSeconds,
            CancellationToken cancel, bool auth = true, Action<long> sent = null)
        {
            cancel.ThrowIfCancellationRequested();
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token, cancel))
            using (var request = new HttpRequestMessage(new HttpMethod(method), baseUrl + path))
            {
                if (auth && token != null)
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                if (body != null)
                {
                    request.Content = new UploadContent(body, sent);
                    request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
                }
                int status;
                string answeredBy;
                byte[] data;
                try
                {
                    using (HttpResponseMessage response = client.SendAsync(request, linked.Token).GetAwaiter().GetResult())
                    {
                        status = (int)response.StatusCode;
                        IEnumerable<string> values;
                        answeredBy = response.Headers.TryGetValues(InstanceHeader, out values) ? values.FirstOrDefault() : null;
                        data = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    }
                }
                catch (OperationCanceledException)
                {
                    cancel.ThrowIfCancellationRequested();
                    throw new TransientException("no answer in " + timeoutSeconds + " s");
                }
                catch (Exception e)
                {
                    if (cancel.IsCancellationRequested)
                        throw new OperationCanceledException(cancel);
                    if (e is HttpRequestException || e is IOException || e is WebException || e is ObjectDisposedException)
                        throw new TransientException(Describe(e));
                    throw;
                }
                if (answeredBy == null)
                    throw new TransientException("HTTP " + status + " without t7drop's header");
                instance = answeredBy;
                if (status >= 400)
                {
                    Dictionary<string, object> details = null;
                    try
                    {
                        details = Json.ParseObject(Encoding.UTF8.GetString(data));
                    }
                    catch (Exception)
                    {
                        // no JSON: details stay empty
                    }
                    details = details ?? new Dictionary<string, object>();
                    throw new ApiException(status, Json.Str(details, "error") ?? "error", details);
                }
                return data;
            }
        }

        /// <summary>The innermost message: "Unable to connect to the remote server" says little.</summary>
        static string Describe(Exception e)
        {
            string text = e.Message;
            for (Exception inner = e.InnerException; inner != null; inner = inner.InnerException)
                text = inner.Message;
            return text.TrimEnd('.');
        }

        public Dictionary<string, object> Call(string method, string path, object payload, CancellationToken cancel,
            int timeoutSeconds = 30, bool auth = true)
        {
            byte[] body = payload == null ? null : Encoding.UTF8.GetBytes(Json.Write(payload));
            byte[] data = Request(method, path, body, "application/json", timeoutSeconds, cancel, auth);
            if (data.Length == 0)
                return new Dictionary<string, object>();
            try
            {
                return Json.ParseObject(Encoding.UTF8.GetString(data));
            }
            catch (Exception)
            {
                throw new TransientException("t7drop answered something that is not JSON");
            }
        }

        public Dictionary<string, object> Pair(string code, string name, CancellationToken cancel)
        {
            var payload = new Dictionary<string, object> { { "code", code }, { "name", name } };
            return Call("POST", "/pair", payload, cancel, 30, false);
        }

        public Dictionary<string, object> Invite(CancellationToken cancel)
        {
            return Call("POST", "/invites", null, cancel);
        }

        public object[] Devices(CancellationToken cancel)
        {
            return Json.Arr(Call("GET", "/devices", null, cancel), "devices");
        }

        public Dictionary<string, object> CreateItem(string id, string to, long size, string sha256, byte[] meta,
            byte[] inline, CancellationToken cancel)
        {
            var payload = new Dictionary<string, object>
            {
                { "to", to }, { "size", size }, { "sha256", sha256 }, { "meta", Convert.ToBase64String(meta) },
            };
            if (inline != null)
                payload["data"] = Convert.ToBase64String(inline);
            return Call("PUT", "/items/" + id, payload, cancel, 120);
        }

        public void PutChunk(string id, int n, byte[] data, Action<long> sent, CancellationToken cancel)
        {
            Request("PUT", "/items/" + id + "/chunks/" + n, data, "application/octet-stream", 300, cancel, true, sent);
        }

        public byte[] GetChunk(string id, int n, CancellationToken cancel)
        {
            return Request("GET", "/items/" + id + "/chunks/" + n, null, null, 300, cancel);
        }

        public Dictionary<string, object> Commit(string id, CancellationToken cancel)
        {
            return Call("POST", "/items/" + id + "/commit", null, cancel, 120);
        }

        public Dictionary<string, object> Ack(string id, CancellationToken cancel)
        {
            return Call("POST", "/items/" + id + "/ack", null, cancel);
        }

        public Dictionary<string, object> Delete(string id, CancellationToken cancel)
        {
            return Call("DELETE", "/items/" + id, null, cancel);
        }

        /// <summary>The long poll: answers when something changed since version `since`, or after `wait` seconds.</summary>
        public Dictionary<string, object> Sync(long since, int wait, CancellationToken cancel)
        {
            return Call("GET", "/sync?since=" + since + "&wait=" + wait, null, cancel, wait + 20);
        }

        /// <summary>A request body that reports how much of it has been sent.</summary>
        sealed class UploadContent : HttpContent
        {
            const int Slice = 64 * 1024;
            readonly byte[] data;
            readonly Action<long> sent;

            public UploadContent(byte[] data, Action<long> sent)
            {
                this.data = data;
                this.sent = sent;
            }

            protected override async Task SerializeToStreamAsync(Stream stream, TransportContext context)
            {
                for (int offset = 0; offset < data.Length; offset += Slice)
                {
                    int count = Math.Min(Slice, data.Length - offset);
                    await stream.WriteAsync(data, offset, count).ConfigureAwait(false);
                    if (sent != null)
                        sent(offset + count);
                }
            }

            protected override bool TryComputeLength(out long length)
            {
                length = data.Length;
                return true;
            }
        }
    }
}
