// Unity-free update core. Compiled into the plugin, the preloader patcher and the test harness (netstandard2.0 compatible).
using System;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace SmoothNet.Updating
{
    internal sealed class LatestInfo
    {
        public string Version, Dll, Sig, Sha256;
    }

    internal static class UpdateCore
    {
        public const string Repo = "ryuto-alt/SmoothNet";
        public const string LatestUrl = "https://github.com/" + Repo + "/releases/latest/download/latest.json";
        public const string AssemblyNameExpected = "SmoothNet";
        public const string DllName = "SmoothNet.dll";
        public const string UpdateDirName = ".update";
        public const int MaxFailedLaunches = 2;

        // RSA-3072 public key (the private key never leaves the author's machine).
        public const string PublicModulusB64 = "0ps/26rO8+EAuMYCQCErvjbYSQCtllhI/6lUiVcrtaLnndXbajPXw7f8w2Za9e0MeqPqGILe4F5QklcopeEQZgg9leGMCW06Jd0Ij2cz8THdYc1Qn1gB++KHaJKK+7O/7NLoDeGGfRfsNsy7SDZJnKaoZyck6jhUj8K5U34z907fX8cMYzU2TePKGP91135pKBfuCNvRQ/tLNpuFh0tripr+GolZPsV0RGnhZj/RL7Vd3wynBxVHGoU+5Ng2eB7EV33FMuZ6IEhXmFHl/K3IfJFcSAcxoljKVRFnftKPpsWWKt3Qn6Pwe5fuFrXQRWxo+CvpDEltG6IiCFO7iUvO73o0onQhrU2k4DlHji4Gf+YuB69mnoqCKllxyK+9mVT0bre+c/+x8E5PG9YZR9Eq/0BpnmBCHmeGT7PURovrIJ0t2gS4xdzFiD5GiHY0XH+LpZQyoLcn8Ab3iVuT4e2SeCvyLTIS/Sv0DSUHp4zZBCFxws1IsD4CLgtq1LOsBLnJ";
        public const string PublicExponentB64 = "AQAB";

        // ---------- latest.json ----------

        public static string JsonString(string json, string key)
        {
            var m = Regex.Match(json ?? "", "\"" + Regex.Escape(key) + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? m.Groups[1].Value.Replace("\\/", "/").Replace("\\\\", "\\") : null;
        }

        public static bool TryParseLatest(string json, out LatestInfo info, out string error)
        {
            info = null; error = null;
            var li = new LatestInfo
            {
                Version = JsonString(json, "version"),
                Dll = JsonString(json, "dll"),
                Sig = JsonString(json, "sig"),
                Sha256 = JsonString(json, "sha256"),
            };
            if (string.IsNullOrEmpty(li.Version) || string.IsNullOrEmpty(li.Dll) || string.IsNullOrEmpty(li.Sig) || string.IsNullOrEmpty(li.Sha256))
            { error = "latest.json is missing fields"; return false; }
            System.Version dummy; string pre;
            if (!TryParseSemver(li.Version, out dummy, out pre)) { error = "bad version: " + li.Version; return false; }
            if (!IsAllowedUrl(li.Dll) || !IsAllowedUrl(li.Sig)) { error = "download URL not allowed"; return false; }
            if (!Regex.IsMatch(li.Sha256, "^[0-9a-fA-F]{64}$")) { error = "bad sha256"; return false; }
            info = li;
            return true;
        }

        /// <summary>https only; github.com (this repo's release downloads) or GitHub's release CDN hosts.</summary>
        public static bool IsAllowedUrl(string url)
        {
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u)) return false;
            if (u.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(u.UserInfo)) return false;
            string host = u.Host.ToLowerInvariant();
            if (host == "github.com")
                return u.AbsolutePath.StartsWith("/" + Repo + "/releases/download/", StringComparison.OrdinalIgnoreCase);
            return host == "objects.githubusercontent.com" || host == "release-assets.githubusercontent.com";
        }

        // ---------- versions ----------

        public static bool TryParseSemver(string s, out System.Version v, out string pre)
        {
            v = null; pre = "";
            if (string.IsNullOrEmpty(s)) return false;
            var m = Regex.Match(s.Trim(), @"^v?(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+.*)?$");
            if (!m.Success) return false;
            v = new System.Version(int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture), int.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture));
            pre = m.Groups[4].Success ? m.Groups[4].Value : "";
            return true;
        }

        /// <summary>Semver compare; returns null when either side is unparsable.</summary>
        public static int? CompareVersions(string a, string b)
        {
            System.Version va, vb; string pa, pb;
            if (!TryParseSemver(a, out va, out pa) || !TryParseSemver(b, out vb, out pb)) return null;
            int c = va.CompareTo(vb);
            if (c != 0) return c < 0 ? -1 : 1;
            if (pa == pb) return 0;
            if (pa == "") return 1;   // release > prerelease
            if (pb == "") return -1;
            return string.CompareOrdinal(pa, pb) < 0 ? -1 : 1;
        }

        // ---------- crypto / identity ----------

        public static string Sha256Hex(byte[] data)
        {
            using (var h = SHA256.Create())
            {
                var hash = h.ComputeHash(data);
                var sb = new StringBuilder();
                foreach (var b in hash) sb.Append(b.ToString("x2"));
                return sb.ToString();
            }
        }

        public static RSAParameters PublicKey()
        {
            return PublicKey(PublicModulusB64, PublicExponentB64);
        }

        public static RSAParameters PublicKey(string modulusB64, string exponentB64)
        {
            return new RSAParameters { Modulus = Convert.FromBase64String(modulusB64), Exponent = Convert.FromBase64String(exponentB64) };
        }

        public static bool VerifySignature(byte[] data, byte[] sig, RSAParameters pub)
        {
            try
            {
                using (var rsa = RSA.Create())
                {
                    rsa.ImportParameters(pub);
                    return rsa.VerifyData(data, sig, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                }
            }
            catch { return false; }
        }

        /// <summary>Reads assembly name/version from the metadata without loading the assembly into the AppDomain.</summary>
        public static bool TryGetIdentity(byte[] dll, out string name, out string version)
        {
            name = version = null;
            string tmp = Path.Combine(Path.GetTempPath(), "smoothnet-id-" + Guid.NewGuid().ToString("N") + ".bin");
            try
            {
                File.WriteAllBytes(tmp, dll);
                var an = AssemblyName.GetAssemblyName(tmp);
                name = an.Name;
                version = an.Version.Major + "." + an.Version.Minor + "." + an.Version.Build;
                return true;
            }
            catch { return false; }
            finally { try { File.Delete(tmp); } catch { } }
        }

        /// <summary>Full verification of a downloaded/staged DLL. currentVersion may be null (no newer-than check).</summary>
        public static bool VerifyUpdate(byte[] dll, byte[] sig, string expectedVersion, string expectedSha256, string currentVersion, RSAParameters pub, out string error)
        {
            error = null;
            if (dll == null || dll.Length < 1024 || sig == null || sig.Length == 0) { error = "empty download"; return false; }
            if (expectedSha256 != null && !string.Equals(Sha256Hex(dll), expectedSha256, StringComparison.OrdinalIgnoreCase)) { error = "sha256 mismatch"; return false; }
            if (!VerifySignature(dll, sig, pub)) { error = "signature invalid"; return false; }
            string name, ver;
            if (!TryGetIdentity(dll, out name, out ver)) { error = "not a .NET assembly"; return false; }
            if (name != AssemblyNameExpected) { error = "unexpected assembly name " + name; return false; }
            if (CompareVersions(ver, expectedVersion) != 0) { error = "assembly version " + ver + " != " + expectedVersion; return false; }
            if (currentVersion != null && CompareVersions(expectedVersion, currentVersion) != 1) { error = "not newer than " + currentVersion; return false; }
            return true;
        }

        // ---------- staging (plugin side) ----------

        public static string UpdateDir(string pluginDir) { return Path.Combine(pluginDir, UpdateDirName); }
        public static string BootMarker(string pluginDir, string version) { return Path.Combine(UpdateDir(pluginDir), "booted-" + version + ".ok"); }

        public static void WriteBootMarker(string pluginDir, string version)
        {
            Directory.CreateDirectory(UpdateDir(pluginDir));
            File.WriteAllText(BootMarker(pluginDir, version), DateTime.UtcNow.ToString("o"));
        }

        public static bool IsRejected(string pluginDir, string version)
        {
            try
            {
                string f = Path.Combine(UpdateDir(pluginDir), "rejected.txt");
                if (!File.Exists(f)) return false;
                foreach (var l in File.ReadAllLines(f)) if (l.Trim() == version) return true;
            }
            catch { }
            return false;
        }

        public static string PendingVersion(string pluginDir)
        {
            try { return JsonString(File.ReadAllText(Path.Combine(UpdateDir(pluginDir), "pending.json")), "version"); }
            catch { return null; }
        }

        public static void Stage(string pluginDir, byte[] dll, byte[] sig, LatestInfo info)
        {
            string d = UpdateDir(pluginDir);
            Directory.CreateDirectory(d);
            File.WriteAllBytes(Path.Combine(d, "SmoothNet.dll.pending"), dll);
            File.WriteAllBytes(Path.Combine(d, "SmoothNet.dll.sig"), sig);
            File.WriteAllText(Path.Combine(d, "pending.json"), "{\"version\":\"" + info.Version + "\",\"sha256\":\"" + info.Sha256.ToLowerInvariant() + "\"}");
        }

        // ---------- apply / rollback (patcher side) ----------

        /// <summary>
        /// Runs once per launch before the plugin loads: finishes/rolls back the previous update, then applies a staged one.
        /// Never throws.
        /// </summary>
        public static void OnLaunch(string pluginDir, RSAParameters pub, Action<string> log)
        {
            try { CheckRollback(pluginDir, log); } catch (Exception e) { log("rollback check failed: " + e.Message); }
            try { ApplyPending(pluginDir, pub, log); } catch (Exception e) { log("apply failed: " + e.Message); }
        }

        public static void CheckRollback(string pluginDir, Action<string> log)
        {
            string d = UpdateDir(pluginDir);
            string appliedPath = Path.Combine(d, "applied.json");
            if (!File.Exists(appliedPath)) return;
            string text = File.ReadAllText(appliedPath);
            string ver = JsonString(text, "version");
            int failed = 0;
            var m = Regex.Match(text, "\"launchesWithoutBoot\"\\s*:\\s*(\\d+)");
            if (m.Success) failed = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);

            if (ver != null && File.Exists(BootMarker(pluginDir, ver)))
            {
                File.Delete(appliedPath);
                log("update " + ver + " confirmed (plugin booted)");
                return;
            }
            failed++;
            if (failed < MaxFailedLaunches)
            {
                File.WriteAllText(appliedPath, "{\"version\":\"" + ver + "\",\"launchesWithoutBoot\":" + failed + "}");
                log("update " + ver + " did not boot (" + failed + "/" + MaxFailedLaunches + ")");
                return;
            }
            string bak = Path.Combine(d, "SmoothNet.dll.bak");
            string cur = Path.Combine(pluginDir, DllName);
            if (File.Exists(bak))
            {
                File.Copy(bak, cur, true);
                log("update " + ver + " failed to boot " + failed + " times; restored previous SmoothNet.dll");
            }
            else log("update " + ver + " failed to boot but no backup exists");
            File.AppendAllText(Path.Combine(d, "rejected.txt"), ver + Environment.NewLine);
            File.Delete(appliedPath);
        }

        public static void ApplyPending(string pluginDir, RSAParameters pub, Action<string> log)
        {
            string d = UpdateDir(pluginDir);
            string pendingJson = Path.Combine(d, "pending.json");
            string pendingDll = Path.Combine(d, "SmoothNet.dll.pending");
            string pendingSig = Path.Combine(d, "SmoothNet.dll.sig");
            if (!File.Exists(pendingJson)) return;
            if (File.Exists(Path.Combine(d, "applied.json"))) { log("previous update not confirmed yet; keeping pending update for later"); return; }

            string json = File.ReadAllText(pendingJson);
            string ver = JsonString(json, "version");
            string sha = JsonString(json, "sha256");
            string cur = Path.Combine(pluginDir, DllName);
            string curVer = null;
            if (File.Exists(cur))
            {
                string n, v;
                if (TryGetIdentity(File.ReadAllBytes(cur), out n, out v)) curVer = v;
            }

            string err = null;
            bool ok = ver != null && File.Exists(pendingDll) && File.Exists(pendingSig)
                && VerifyUpdate(File.ReadAllBytes(pendingDll), File.ReadAllBytes(pendingSig), ver, sha, curVer, pub, out err);
            if (!ok)
            {
                log("discarding pending update: " + (err ?? "files missing"));
                DiscardPending(d);
                return;
            }

            if (File.Exists(cur)) File.Copy(cur, Path.Combine(d, "SmoothNet.dll.bak"), true);
            string tmp = cur + ".new";
            File.Copy(pendingDll, tmp, true);
            if (File.Exists(cur)) File.Delete(cur);
            File.Move(tmp, cur);
            File.WriteAllText(Path.Combine(d, "applied.json"), "{\"version\":\"" + ver + "\",\"launchesWithoutBoot\":0}");
            DiscardPending(d);
            log("applied SmoothNet " + ver + " (was " + (curVer ?? "none") + ")");
        }

        private static void DiscardPending(string d)
        {
            foreach (var f in new[] { "pending.json", "SmoothNet.dll.pending", "SmoothNet.dll.sig" })
                try { File.Delete(Path.Combine(d, f)); } catch { }
        }
    }
}
