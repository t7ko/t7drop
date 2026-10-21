using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace T7Drop
{
    /// <summary>A GitHub release that has the exe attached.</summary>
    public class Release
    {
        public Version Version;
        public string PageUrl;
        public string DownloadUrl;
        public long Size;
        public string Sha256;  // lower-case hex, or null when GitHub gave no digest
    }

    /// <summary>
    /// Finds the latest GitHub release and replaces the running exe with it. A running exe cannot
    /// be overwritten but can be renamed: the new exe is downloaded next to it as T7Drop.exe.new,
    /// the running one becomes T7Drop.exe.old, the new one takes its name and is started with
    /// --updated-from, and this process exits. The new process waits for that, then deletes the
    /// old exe. The path stays the same, so Start with Windows keeps working.
    /// </summary>
    public static class Updater
    {
        public const string Repository = "t7ko/t7drop";
        public const string AssetName = "T7Drop.exe";
        const string ProgramName = "T7Drop";
        const string UpdatedFromArgument = "--updated-from";

        /// <summary>GitHub's latest-release API; T7DROP_RELEASES_URL replaces it to try updates locally.</summary>
        static string LatestReleaseUrl
        {
            get
            {
                return Environment.GetEnvironmentVariable("T7DROP_RELEASES_URL")
                    ?? "https://api.github.com/repos/" + Repository + "/releases/latest";
            }
        }

        public static Version CurrentVersion
        {
            get { return Normalize(typeof(Updater).Assembly.GetName().Version); }
        }

        /// <summary>"1.2.0" for version 1.2.0 with revision 0: the form release tags use.</summary>
        public static string Text(Version version)
        {
            return version.Revision > 0 ? version.ToString() : version.ToString(3);
        }

        /// <summary>The latest release if it is newer than this exe; null if not, or if nothing is released yet.</summary>
        public static async Task<Release> FindUpdateAsync()
        {
            string json;
            try
            {
                using (WebClient client = NewClient())
                {
                    client.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                    json = await client.DownloadStringTaskAsync(LatestReleaseUrl).ConfigureAwait(false);
                }
            }
            catch (WebException e)
            {
                var response = e.Response as HttpWebResponse;
                if (response != null && response.StatusCode == HttpStatusCode.NotFound)
                    return null;
                throw;
            }
            Release release = Parse(json);
            return release != null && release.Version > CurrentVersion ? release : null;
        }

        /// <summary>Reads GitHub's release JSON; null if the tag is not a version or the exe is not attached.</summary>
        public static Release Parse(string json)
        {
            GitHubRelease data;
            using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                data = (GitHubRelease)new DataContractJsonSerializer(typeof(GitHubRelease)).ReadObject(stream);
            Version version = ParseVersion(data.TagName);
            GitHubAsset asset = data.Assets == null ? null : data.Assets.FirstOrDefault(
                a => string.Equals(a.Name, AssetName, StringComparison.OrdinalIgnoreCase));
            if (version == null || asset == null || string.IsNullOrEmpty(asset.DownloadUrl))
                return null;
            const string sha256Prefix = "sha256:";
            return new Release
            {
                Version = version,
                PageUrl = data.HtmlUrl,
                DownloadUrl = asset.DownloadUrl,
                Size = asset.Size,
                Sha256 = asset.Digest != null && asset.Digest.StartsWith(sha256Prefix, StringComparison.OrdinalIgnoreCase)
                    ? asset.Digest.Substring(sha256Prefix.Length).ToLowerInvariant() : null,
            };
        }

        /// <summary>"v1.2" or "1.2.0" as version 1.2.0 with revision 0; null for anything else, such as "1.2.0-beta".</summary>
        public static Version ParseVersion(string tag)
        {
            if (string.IsNullOrEmpty(tag))
                return null;
            Version version;
            string text = tag.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? tag.Substring(1) : tag;
            return Version.TryParse(text, out version) ? Normalize(version) : null;
        }

        /// <summary>Downloads the release next to the exe, checks it, and returns its path.</summary>
        public static async Task<string> DownloadAsync(Release release, string exePath)
        {
            string path = exePath + ".new";
            File.Delete(path);
            using (WebClient client = NewClient())
                await client.DownloadFileTaskAsync(new Uri(release.DownloadUrl), path).ConfigureAwait(false);
            try
            {
                Verify(path, release);
            }
            catch
            {
                File.Delete(path);
                throw;
            }
            return path;
        }

        /// <summary>Throws unless the file is T7Drop with the size, SHA-256 and version of the release.</summary>
        public static void Verify(string path, Release release)
        {
            long length = new FileInfo(path).Length;
            if (release.Size > 0 && length != release.Size)
                throw new InvalidDataException("The download has " + length + " bytes, not " + release.Size + ".");
            if (release.Sha256 != null && Sha256(path) != release.Sha256)
                throw new InvalidDataException("The download's SHA-256 differs from the release's.");
            AssemblyName name;
            try
            {
                name = System.Reflection.AssemblyName.GetAssemblyName(path);
            }
            catch (BadImageFormatException)
            {
                throw new InvalidDataException("The download is not a .NET program.");
            }
            if (name.Name != ProgramName)
                throw new InvalidDataException("The download is " + name.Name + ", not T7Drop.");
            if (Normalize(name.Version) != release.Version)
                throw new InvalidDataException("The download is version " + Text(Normalize(name.Version)) +
                    ", not " + Text(release.Version) + ".");
        }

        /// <summary>Puts the downloaded exe in place of the running one and starts it; the caller then exits.</summary>
        public static void Install(string exePath, string downloaded)
        {
            Swap(exePath, downloaded);
            try
            {
                using (Process.Start(exePath, UpdatedFromArgument + " " + Process.GetCurrentProcess().Id)) { }
            }
            catch
            {
                SwapBack(exePath, downloaded);
                throw;
            }
        }

        public static void Swap(string exePath, string downloaded)
        {
            string old = exePath + ".old";
            File.Delete(old);
            File.Move(exePath, old);
            try
            {
                File.Move(downloaded, exePath);
            }
            catch
            {
                File.Move(old, exePath);
                throw;
            }
        }

        public static void SwapBack(string exePath, string downloaded)
        {
            File.Move(exePath, downloaded);
            File.Move(exePath + ".old", exePath);
        }

        /// <summary>
        /// Runs first in every instance. After an update, waits for the old process to exit, so that
        /// the single-instance check passes. Deletes what an update leaves behind, even a failed one.
        /// Returns true if this instance was started by an update.
        /// </summary>
        public static bool FinishUpdate(string[] args, string exePath)
        {
            int oldProcess = 0;
            bool updated = args.Length >= 2 && args[0] == UpdatedFromArgument && int.TryParse(args[1], out oldProcess);
            if (updated)
            {
                try
                {
                    using (Process old = Process.GetProcessById(oldProcess))
                        old.WaitForExit(15000);
                }
                catch (ArgumentException)
                {
                    // already gone
                }
            }
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    File.Delete(exePath + ".old");
                    File.Delete(exePath + ".new");
                    break;
                }
                catch (Exception)
                {
                    if (attempt == 10)
                        break;  // still locked (by a virus scanner?): the next start deletes it
                    Thread.Sleep(300);
                }
            }
            return updated;
        }

        static WebClient NewClient()
        {
            var client = new WebClient { Encoding = Encoding.UTF8 };
            client.Headers[HttpRequestHeader.UserAgent] = "T7Drop/" + Text(CurrentVersion);  // GitHub requires one
            if (client.Proxy != null)
                client.Proxy.Credentials = CredentialCache.DefaultCredentials;  // for company proxies
            return client;
        }

        static string Sha256(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Fills in missing parts with 0: 1.2 would sort before 1.2.0 with revision 0.</summary>
        static Version Normalize(Version version)
        {
            return new Version(version.Major, version.Minor, Math.Max(version.Build, 0), Math.Max(version.Revision, 0));
        }
    }

    // The parts of GitHub's release JSON that Updater reads; the serializer skips the rest.
    // Public, because the compiler warns that non-public fields here are never assigned.

    [DataContract]
    public class GitHubRelease
    {
        [DataMember(Name = "tag_name")] public string TagName;
        [DataMember(Name = "html_url")] public string HtmlUrl;
        [DataMember(Name = "assets")] public GitHubAsset[] Assets;
    }

    [DataContract]
    public class GitHubAsset
    {
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "browser_download_url")] public string DownloadUrl;
        [DataMember(Name = "size")] public long Size;
        [DataMember(Name = "digest")] public string Digest;  // "sha256:<hex>"
    }
}
