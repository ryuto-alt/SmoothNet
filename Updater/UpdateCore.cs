// Unity-free update core. Compiled into the plugin, the preloader patcher and the test harness (netstandard2.0 compatible).
using System;
using System.Diagnostics;
using System.Net;
using System.Threading;
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
        // optional: the signed preloader patcher (SmoothNetUpdater.dll) published with the same release
        public string PatcherVersion, PatcherDll, PatcherSig, PatcherSha256;
    }

    internal static class UpdateCore
    {
        public const string Repo = "ryuto-alt/SmoothNet";
        public const string LatestUrl = "https://github.com/" + Repo + "/releases/latest/download/latest.json";
        public const string AssemblyNameExpected = "SmoothNet";
        public const string DllName = "SmoothNet.dll";
        public const string PatcherAssemblyName = "SmoothNetUpdater";
        public const string PatcherDllName = "SmoothNetUpdater.dll";
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

        public static bool TryParseLatest(string json, out LatestInfo info, out string error, Func<string, bool> allowUrl = null)
        {
            if (allowUrl == null) allowUrl = IsAllowedUrl;
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
            if (!allowUrl(li.Dll) || !allowUrl(li.Sig)) { error = "download URL not allowed"; return false; }
            if (!Regex.IsMatch(li.Sha256, "^[0-9a-fA-F]{64}$")) { error = "bad sha256"; return false; }
            // optional patcher block: only kept when complete and valid (never makes the plugin update fail)
            string pv = JsonString(json, "patcherVersion"), pd = JsonString(json, "patcherDll"), ps = JsonString(json, "patcherSig"), ph = JsonString(json, "patcherSha256");
            System.Version pdummy; string ppre;
            if (pv != null && pd != null && ps != null && ph != null && TryParseSemver(pv, out pdummy, out ppre)
                && allowUrl(pd) && allowUrl(ps) && Regex.IsMatch(ph, "^[0-9a-fA-F]{64}$"))
            { li.PatcherVersion = pv; li.PatcherDll = pd; li.PatcherSig = ps; li.PatcherSha256 = ph; }
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
            return VerifyAssembly(dll, sig, AssemblyNameExpected, expectedVersion, expectedSha256, currentVersion, pub, out error);
        }

        /// <summary>Same checks for the patcher DLL (assembly name SmoothNetUpdater).</summary>
        public static bool VerifyPatcher(byte[] dll, byte[] sig, string expectedVersion, string expectedSha256, string currentVersion, RSAParameters pub, out string error)
        {
            return VerifyAssembly(dll, sig, PatcherAssemblyName, expectedVersion, expectedSha256, currentVersion, pub, out error);
        }

        private static bool VerifyAssembly(byte[] dll, byte[] sig, string expectedName, string expectedVersion, string expectedSha256, string currentVersion, RSAParameters pub, out string error)
        {
            error = null;
            if (dll == null || dll.Length < 1024 || sig == null || sig.Length == 0) { error = "empty download"; return false; }
            if (expectedSha256 != null && !string.Equals(Sha256Hex(dll), expectedSha256, StringComparison.OrdinalIgnoreCase)) { error = "sha256 mismatch"; return false; }
            if (!VerifySignature(dll, sig, pub)) { error = "signature invalid"; return false; }
            string name, ver;
            if (!TryGetIdentity(dll, out name, out ver)) { error = "not a .NET assembly"; return false; }
            if (name != expectedName) { error = "unexpected assembly name " + name; return false; }
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

        // ---------- patcher self-update (staged by the plugin, moved by a helper after the game exits) ----------

        public static string StagedPatcherPath(string pluginDir) { return Path.Combine(UpdateDir(pluginDir), PatcherDllName + ".pending"); }

        public static void StagePatcher(string pluginDir, byte[] dll, byte[] sig, LatestInfo info, string targetPath)
        {
            string d = UpdateDir(pluginDir);
            Directory.CreateDirectory(d);
            File.WriteAllBytes(StagedPatcherPath(pluginDir), dll);
            File.WriteAllBytes(Path.Combine(d, PatcherDllName + ".sig"), sig);
            File.WriteAllText(Path.Combine(d, "patcher-pending.json"),
                "{\"version\":\"" + info.PatcherVersion + "\",\"sha256\":\"" + info.PatcherSha256.ToLowerInvariant() + "\",\"target\":\"" + targetPath.Replace("\\", "\\\\") + "\"}");
        }

        /// <summary>Returns the staged patcher's version, sha256 and target path, or false when nothing valid is staged.</summary>
        public static bool TryGetStagedPatcher(string pluginDir, out string version, out string sha256, out string target)
        {
            version = sha256 = target = null;
            try
            {
                string j = File.ReadAllText(Path.Combine(UpdateDir(pluginDir), "patcher-pending.json"));
                version = JsonString(j, "version"); sha256 = JsonString(j, "sha256"); target = JsonString(j, "target");
                return version != null && sha256 != null && target != null && File.Exists(StagedPatcherPath(pluginDir));
            }
            catch { return false; }
        }

        /// <summary>
        /// Hidden helper (Windows PowerShell): waits for the game process to exit, then (1) moves a staged patcher DLL into place
        /// after re-checking its sha256 (keeps .bak), and (2) optionally relaunches the game through Steam.
        /// </summary>
        public const string HelperScript = @"param([int]$GamePid, [string]$Staged, [string]$Target, [string]$Sha, [string]$Relaunch)
$ErrorActionPreference = 'SilentlyContinue'
try { $p = Get-Process -Id $GamePid; if ($p) { [void]$p.WaitForExit(180000) } } catch { }
if ($Staged -and $Target -and (Test-Path -LiteralPath $Staged)) {
  $h = ([BitConverter]::ToString([Security.Cryptography.SHA256]::Create().ComputeHash([IO.File]::ReadAllBytes($Staged))) -replace '-', '').ToLowerInvariant()
  if ($h -eq $Sha.ToLowerInvariant()) {
    for ($i = 0; $i -lt 30; $i++) {
      try {
        if (Test-Path -LiteralPath $Target) { Copy-Item -LiteralPath $Target -Destination ($Target + '.bak') -Force }
        Copy-Item -LiteralPath $Staged -Destination ($Target + '.new') -Force
        Move-Item -LiteralPath ($Target + '.new') -Destination $Target -Force -ErrorAction Stop
        Remove-Item -LiteralPath $Staged -Force
        break
      } catch { Start-Sleep -Milliseconds 500 }
    }
  } else { Remove-Item -LiteralPath $Staged -Force }
}
if ($Relaunch -eq '1') { Start-Process 'steam://rungameid/3241660' }
";

        /// <summary>Spawns the hidden helper. Returns false (and logs) on failure; never throws.</summary>
        public static bool SpawnHelper(string pluginDir, int gamePid, bool relaunch, Action<string> log)
        {
            try
            {
                string d = UpdateDir(pluginDir);
                Directory.CreateDirectory(d);
                string ver, sha, target;
                bool hasPatcher = TryGetStagedPatcher(pluginDir, out ver, out sha, out target);
                if (!hasPatcher && !relaunch) return false;
                string script = Path.Combine(d, "apply-helper.ps1");
                File.WriteAllText(script, HelperScript, new UTF8Encoding(true));
                var psi = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File " + Q(script) + " -GamePid " + gamePid
                        + " -Staged " + Q(hasPatcher ? StagedPatcherPath(pluginDir) : "") + " -Target " + Q(hasPatcher ? target : "")
                        + " -Sha " + Q(hasPatcher ? sha : "") + " -Relaunch " + (relaunch ? "1" : "0"),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                };
                Process.Start(psi);
                log("helper spawned (patcher " + (hasPatcher ? ver : "none") + ", relaunch=" + relaunch + ")");
                return true;
            }
            catch (Exception e) { log("helper failed: " + e.Message); return false; }
        }

        private static string Q(string s) { return "\"" + (s ?? "").Replace("\"", "") + "\""; }

        // ---------- same-launch online update (patcher side) ----------

        /// <summary>Plain HTTPS GET with a hard per-call timeout. Returns null on any failure.</summary>
        public static byte[] Download(string url, int timeoutMs, int maxBytes)
        {
            if (timeoutMs <= 0) return null;
            try
            {
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072; } catch { } // Tls12
                var req = (HttpWebRequest)WebRequest.Create(url);
                req.Timeout = timeoutMs;
                req.ReadWriteTimeout = timeoutMs;
                req.UserAgent = "SmoothNet-Patcher";
                req.AllowAutoRedirect = true;
                var sw = Stopwatch.StartNew();
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var st = resp.GetResponseStream())
                using (var ms = new MemoryStream())
                {
                    var buf = new byte[16384];
                    int n;
                    while ((n = st.Read(buf, 0, buf.Length)) > 0)
                    {
                        ms.Write(buf, 0, n);
                        if (ms.Length > maxBytes || sw.ElapsedMilliseconds > timeoutMs) return null;
                    }
                    return ms.Length == 0 ? null : ms.ToArray();
                }
            }
            catch { return null; }
        }

        /// <summary>
        /// Synchronous check-download-verify-apply with a hard total budget. Runs on a worker thread so even a hung DNS lookup
        /// cannot hold the game longer than budgetMs; a late result is discarded. Every failure is logged and swallowed.
        /// </summary>
        public static void TryOnlineUpdate(string pluginDir, RSAParameters pub, Action<string> log, int budgetMs, Func<string, int, byte[]> download, Func<string, bool> allowUrl)
        {
            var gate = new object();
            bool abandoned = false;
            var sw = Stopwatch.StartNew();
            var t = new Thread(() =>
            {
                try { OnlineWorker(pluginDir, pub, log, budgetMs, sw, download, allowUrl, gate, () => abandoned); }
                catch (Exception e) { log("online update failed: " + e.Message); }
            }) { IsBackground = true, Name = "SmoothNetOnlineUpdate" };
            t.Start();
            if (!t.Join(budgetMs + 300))
            {
                lock (gate) { abandoned = true; }
                log("online update: budget of " + budgetMs + " ms exceeded; continuing (the plugin will retry in the background)");
            }
        }

        private static void OnlineWorker(string pluginDir, RSAParameters pub, Action<string> log, int budgetMs, Stopwatch sw, Func<string, int, byte[]> download, Func<string, bool> allowUrl, object gate, Func<bool> isAbandoned)
        {
            Func<int> left = () => budgetMs - (int)sw.ElapsedMilliseconds;
            string d = UpdateDir(pluginDir);
            if (File.Exists(Path.Combine(d, "applied.json"))) { log("online update: previous update not confirmed yet; skipping"); return; }
            string cur = Path.Combine(pluginDir, DllName);
            string curVer = null;
            if (File.Exists(cur)) { string n, v; if (TryGetIdentity(File.ReadAllBytes(cur), out n, out v)) curVer = v; }
            if (curVer == null) { log("online update: installed SmoothNet.dll unreadable; skipping"); return; }

            var jb = download(LatestUrl, left());
            if (jb == null) { log("online update: latest.json not reachable (" + sw.ElapsedMilliseconds + " ms)"); return; }
            LatestInfo info; string err;
            if (!TryParseLatest(Encoding.UTF8.GetString(jb), out info, out err, allowUrl)) { log("online update: " + err); return; }
            if (CompareVersions(info.Version, curVer) != 1) { log("online update: up to date (" + curVer + ")"); return; }
            if (IsRejected(pluginDir, info.Version)) { log("online update: " + info.Version + " was rolled back before; skipping"); return; }

            var dll = download(info.Dll, left());
            var sig = dll == null ? null : download(info.Sig, left());
            if (dll == null || sig == null) { log("online update: download failed/timed out"); return; }
            if (!VerifyUpdate(dll, sig, info.Version, info.Sha256, curVer, pub, out err)) { log("online update: " + info.Version + " rejected: " + err); return; }

            lock (gate)
            {
                if (isAbandoned()) { log("online update: too late, discarded"); return; }
                Stage(pluginDir, dll, sig, info);
                ApplyPending(pluginDir, pub, log);
            }
        }

        private static void DiscardPending(string d)
        {
            foreach (var f in new[] { "pending.json", "SmoothNet.dll.pending", "SmoothNet.dll.sig" })
                try { File.Delete(Path.Combine(d, f)); } catch { }
        }
    }
}
