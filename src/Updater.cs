// Update check against the project's GitHub releases. A newer release's exe is downloaded,
// checked against the release's SHA256SUMS.txt, and then run with --install, which installs
// it over this copy and restarts it.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Web.Script.Serialization;

namespace NubMouse
{
    sealed class UpdateInfo
    {
        public Version Version;
        public string Tag, PageUrl, ExeUrl, SumsUrl;
    }

    static class Updater
    {
        const string LatestApi = "https://api.github.com/repos/dstarikov/NubMouse/releases/latest";

        static WebClient Client()
        {
            // .NET Framework 4.x may default to TLS 1.0; GitHub needs 1.2+.
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            var c = new WebClient();
            c.Headers[HttpRequestHeader.UserAgent] = "NubMouse-updater";
            return c;
        }

        /// Returns the latest release if it is newer than this exe, otherwise null.
        /// Throws on network or format errors.
        public static UpdateInfo CheckLatest()
        {
            string json;
            using (var c = Client())
            {
                c.Headers[HttpRequestHeader.Accept] = "application/vnd.github+json";
                try { json = c.DownloadString(LatestApi); }
                catch (WebException ex)
                {
                    var r = ex.Response as HttpWebResponse;
                    if (r != null && r.StatusCode == HttpStatusCode.NotFound)
                        throw new InvalidOperationException("No public release was found at " + Installer.RepoUrl + "/releases.");
                    throw;
                }
            }

            var release = new JavaScriptSerializer().Deserialize<Dictionary<string, object>>(json);
            var info = new UpdateInfo
            {
                Tag = (string)release["tag_name"],
                PageUrl = (string)release["html_url"],
                Version = ParseVersion((string)release["tag_name"]),
            };
            foreach (Dictionary<string, object> asset in (System.Collections.ArrayList)release["assets"])
            {
                var name = (string)asset["name"];
                var url = (string)asset["browser_download_url"];
                if (name.Equals(Installer.ExeName, StringComparison.OrdinalIgnoreCase)) info.ExeUrl = url;
                else if (name.Equals("SHA256SUMS.txt", StringComparison.OrdinalIgnoreCase)) info.SumsUrl = url;
            }

            return info.Version > Installer.CurrentVersion ? info : null;
        }

        /// Downloads and verifies the release exe; returns its local path.
        public static string Download(UpdateInfo u)
        {
            if (u.ExeUrl == null || u.SumsUrl == null)
                throw new InvalidDataException("The release is missing " + Installer.ExeName + " or SHA256SUMS.txt.");

            string dir = Path.Combine(Path.GetTempPath(), "NubMouse-update-" + u.Version);
            Directory.CreateDirectory(dir);
            string exe = Path.Combine(dir, Installer.ExeName);

            string sums;
            using (var c = Client())
            {
                sums = c.DownloadString(u.SumsUrl);
                c.DownloadFile(u.ExeUrl, exe);
            }

            string expected = null;
            foreach (var line in sums.Split('\n'))
            {
                var parts = line.Trim().Split(new[] { ' ', '*' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 2 && parts[1].Equals(Installer.ExeName, StringComparison.OrdinalIgnoreCase))
                    expected = parts[0].ToLowerInvariant();
            }

            string actual;
            using (var sha = SHA256.Create())
            using (var f = File.OpenRead(exe))
                actual = BitConverter.ToString(sha.ComputeHash(f)).Replace("-", "").ToLowerInvariant();

            if (expected == null || expected != actual)
            {
                try { File.Delete(exe); } catch (Exception) { }
                throw new InvalidDataException("The downloaded file's checksum does not match SHA256SUMS.txt; it was deleted.");
            }
            return exe;
        }

        /// Runs the downloaded exe as an installer for the given scope. It replaces this copy,
        /// asks this process to exit, and starts the new version.
        public static void Apply(string newExe, InstallScope scope)
        {
            string args = "--install --quiet" + (scope == InstallScope.AllUsers ? " --all-users" : "");
            Process.Start(new ProcessStartInfo(newExe, args) { UseShellExecute = false });
        }

        public static Version ParseVersion(string tag)
        {
            string s = tag.TrimStart('v', 'V').Split('-', '+')[0];
            Version v;
            if (!Version.TryParse(s, out v)) return new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }
}
