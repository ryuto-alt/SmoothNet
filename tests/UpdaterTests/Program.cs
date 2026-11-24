using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using SmoothNet.Updating;

// Console harness for UpdateCore. Usage: dotnet run -c Release [-- --live]
// Never touches the real game folder: everything happens under a temp directory.
internal static class Program
{
    private static int pass, fail;

    private static void Check(bool ok, string name, string detail = null)
    {
        if (ok) pass++; else fail++;
        Console.WriteLine((ok ? "PASS  " : "FAIL  ") + name + (!ok && detail != null ? "  -> " + detail : ""));
    }

    private static byte[] MakeDll(string name, string version)
    {
        string src = "[assembly: System.Reflection.AssemblyVersion(\"" + version + ".0\")]\n" +
                     "public static class Probe { public const string Pad = \"" + new string('x', 3000) + "\"; }";
        var tree = CSharpSyntaxTree.ParseText(src);
        string rt = Path.GetDirectoryName(typeof(object).Assembly.Location);
        var refs = new List<MetadataReference>
        {
            MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
            MetadataReference.CreateFromFile(Path.Combine(rt, "System.Runtime.dll")),
        };
        var comp = CSharpCompilation.Create(name, new[] { tree }, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        using (var ms = new MemoryStream())
        {
            var r = comp.Emit(ms);
            if (!r.Success) throw new Exception(string.Join("\n", r.Diagnostics));
            return ms.ToArray();
        }
    }

    private static byte[] Sign(byte[] data, RSA key) => key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

    private static string Latest(string ver, string dllUrl, string sigUrl, byte[] dll) =>
        "{\"version\":\"" + ver + "\",\"dll\":\"" + dllUrl + "\",\"sig\":\"" + sigUrl + "\",\"sha256\":\"" + UpdateCore.Sha256Hex(dll) + "\"}";

    private static string NewPluginDir(string root, string name, byte[] installed)
    {
        string d = Path.Combine(root, name);
        Directory.CreateDirectory(d);
        File.WriteAllBytes(Path.Combine(d, "SmoothNet.dll"), installed);
        return d;
    }

    private static string Ver(string path)
    {
        string n, v;
        return UpdateCore.TryGetIdentity(File.ReadAllBytes(path), out n, out v) ? v : null;
    }

    private static int Main(string[] args)
    {
        string tmp = Path.Combine(Path.GetTempPath(), "SmoothNetUpdaterTests_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmp);
        try
        {
            if (Array.IndexOf(args, "--live") >= 0) Live(tmp).GetAwaiter().GetResult();
            else Unit(tmp);
        }
        catch (Exception e) { Check(false, "unexpected exception", e.ToString()); }
        finally { try { Directory.Delete(tmp, true); } catch { } }
        Console.WriteLine("RESULT: {0} passed, {1} failed", pass, fail);
        return fail == 0 ? 0 : 1;
    }

    private static void Unit(string tmp)
    {
        using (var key = RSA.Create(3072))
        using (var other = RSA.Create(3072))
        {
            var pub = key.ExportParameters(false);
            var d120 = MakeDll("SmoothNet", "1.2.0");
            var s120 = Sign(d120, key);
            string err;

            Check(UpdateCore.VerifyUpdate(d120, s120, "1.2.0", UpdateCore.Sha256Hex(d120), "1.1.0", pub, out err), "happy path verifies", err);

            var tampered = (byte[])d120.Clone(); tampered[tampered.Length / 2] ^= 0xFF;
            Check(!UpdateCore.VerifyUpdate(tampered, s120, "1.2.0", null, "1.1.0", pub, out err), "tampered DLL rejected (signature)", err);
            Check(!UpdateCore.VerifyUpdate(tampered, s120, "1.2.0", UpdateCore.Sha256Hex(d120), "1.1.0", pub, out err) && err == "sha256 mismatch", "tampered DLL rejected (sha256)", err);
            Check(!UpdateCore.VerifyUpdate(d120, Sign(d120, other), "1.2.0", null, "1.1.0", pub, out err), "signature from another key rejected", err);

            var evil = MakeDll("NotSmoothNet", "1.2.0");
            Check(!UpdateCore.VerifyUpdate(evil, Sign(evil, key), "1.2.0", null, "1.1.0", pub, out err) && err.Contains("assembly name"), "wrong assembly name rejected", err);
            Check(!UpdateCore.VerifyUpdate(d120, s120, "1.3.0", null, "1.1.0", pub, out err) && err.Contains("assembly version"), "assembly version != latest.json rejected", err);
            Check(!UpdateCore.VerifyUpdate(d120, s120, "1.2.0", null, "1.2.0", pub, out err), "equal version ignored", err);
            Check(!UpdateCore.VerifyUpdate(d120, s120, "1.2.0", null, "1.3.0", pub, out err), "older version ignored", err);
            Check(!UpdateCore.VerifyUpdate(new byte[10], new byte[10], "1.2.0", null, "1.1.0", pub, out err), "garbage rejected", err);

            Check(UpdateCore.CompareVersions("1.10.0", "1.9.9") == 1 && UpdateCore.CompareVersions("1.2.0", "1.2.0") == 0
                  && UpdateCore.CompareVersions("1.2.0-beta", "1.2.0") == -1 && UpdateCore.CompareVersions("x", "1.0.0") == null, "semver compare");

            // latest.json parsing / URL allow-list
            string good = Latest("1.2.0", "https://github.com/ryuto-alt/SmoothNet/releases/download/v1.2.0/SmoothNet.dll",
                "https://github.com/ryuto-alt/SmoothNet/releases/download/v1.2.0/SmoothNet.dll.sig", d120);
            LatestInfo li;
            Check(UpdateCore.TryParseLatest(good, out li, out err) && li.Version == "1.2.0", "latest.json parses", err);
            foreach (var bad in new[]
            {
                "https://evil.example.com/SmoothNet.dll",
                "http://github.com/ryuto-alt/SmoothNet/releases/download/v1.2.0/SmoothNet.dll",
                "https://github.com.evil.com/ryuto-alt/SmoothNet/releases/download/v1.2.0/SmoothNet.dll",
                "https://github.com/someone-else/Repo/releases/download/v1/SmoothNet.dll",
                "https://user@github.com/ryuto-alt/SmoothNet/releases/download/v1.2.0/SmoothNet.dll",
                "https://objects.githubusercontent.com.evil.com/x",
            })
                Check(!UpdateCore.TryParseLatest(Latest("1.2.0", bad, bad + ".sig", d120), out li, out err), "non-GitHub URL rejected: " + bad, err);
            Check(UpdateCore.IsAllowedUrl("https://objects.githubusercontent.com/github-production-release-asset/1/x?sig=1")
                  && UpdateCore.IsAllowedUrl("https://release-assets.githubusercontent.com/x"), "GitHub CDN hosts allowed");
            Check(!UpdateCore.TryParseLatest("{\"version\":\"1.2.0\"}", out li, out err), "incomplete latest.json rejected");

            // stage -> apply -> boot marker -> no rollback
            var d110 = MakeDll("SmoothNet", "1.1.0");
            var log = new List<string>();
            Action<string> L = s => { log.Add(s); };
            string p1 = NewPluginDir(tmp, "case1", d110);
            UpdateCore.Stage(p1, d120, s120, li = new LatestInfo { Version = "1.2.0", Sha256 = UpdateCore.Sha256Hex(d120) });
            Check(UpdateCore.PendingVersion(p1) == "1.2.0" && Ver(Path.Combine(p1, "SmoothNet.dll")) == "1.1.0", "staged, not applied yet");
            UpdateCore.OnLaunch(p1, pub, L);
            Check(Ver(Path.Combine(p1, "SmoothNet.dll")) == "1.2.0", "apply swaps DLL to 1.2.0");
            Check(Ver(Path.Combine(p1, ".update", "SmoothNet.dll.bak")) == "1.1.0", "backup holds 1.1.0");
            Check(File.Exists(Path.Combine(p1, ".update", "applied.json")) && UpdateCore.PendingVersion(p1) == null, "applied.json written, pending cleared");
            UpdateCore.WriteBootMarker(p1, "1.2.0");
            UpdateCore.OnLaunch(p1, pub, L);
            UpdateCore.OnLaunch(p1, pub, L);
            Check(Ver(Path.Combine(p1, "SmoothNet.dll")) == "1.2.0" && !File.Exists(Path.Combine(p1, ".update", "applied.json")), "booted -> no rollback, applied.json cleared");

            // stage -> apply -> 2 launches without marker -> rollback
            string p2 = NewPluginDir(tmp, "case2", d110);
            UpdateCore.Stage(p2, d120, s120, li);
            UpdateCore.OnLaunch(p2, pub, L);                      // launch A: apply
            Check(Ver(Path.Combine(p2, "SmoothNet.dll")) == "1.2.0", "case2 applied");
            UpdateCore.OnLaunch(p2, pub, L);                      // launch B: A failed (1)
            Check(Ver(Path.Combine(p2, "SmoothNet.dll")) == "1.2.0", "1 failed launch -> still 1.2.0");
            UpdateCore.OnLaunch(p2, pub, L);                      // launch C: B failed (2) -> restore
            Check(Ver(Path.Combine(p2, "SmoothNet.dll")) == "1.1.0", "2 failed launches -> restored 1.1.0");
            Check(UpdateCore.IsRejected(p2, "1.2.0") && !File.Exists(Path.Combine(p2, ".update", "applied.json")), "rolled-back version recorded as rejected");

            // tampered staged files are discarded, installed DLL untouched
            string p3 = NewPluginDir(tmp, "case3", d110);
            UpdateCore.Stage(p3, tampered, s120, li);
            UpdateCore.OnLaunch(p3, pub, L);
            Check(Ver(Path.Combine(p3, "SmoothNet.dll")) == "1.1.0" && UpdateCore.PendingVersion(p3) == null && !File.Exists(Path.Combine(p3, ".update", "applied.json")), "tampered staged update discarded at apply");

            // staged update that is not newer is discarded
            string p4 = NewPluginDir(tmp, "case4", d120);
            UpdateCore.Stage(p4, d120, s120, li);
            UpdateCore.OnLaunch(p4, pub, L);
            Check(UpdateCore.PendingVersion(p4) == null && !File.Exists(Path.Combine(p4, ".update", "applied.json")), "staged equal-version update discarded");

            // missing plugin dir does not throw
            UpdateCore.OnLaunch(Path.Combine(tmp, "does-not-exist"), pub, L);
            Check(true, "OnLaunch on missing dir does not throw");

            OnlineTests(tmp, key, pub);
        }
    }

    // ---------- online (patcher) path against a local fake HTTP server + patcher self-update ----------

    private sealed class FakeServer : IDisposable
    {
        public readonly System.Net.HttpListener L = new System.Net.HttpListener();
        public readonly Dictionary<string, byte[]> Files = new Dictionary<string, byte[]>();
        public int DelayMs;
        public string Base;
        private readonly System.Threading.Thread t;

        public FakeServer()
        {
            int port = new Random().Next(20000, 40000);
            Base = "http://127.0.0.1:" + port + "/";
            L.Prefixes.Add(Base);
            L.Start();
            t = new System.Threading.Thread(Loop) { IsBackground = true };
            t.Start();
        }

        private void Loop()
        {
            while (L.IsListening)
            {
                try
                {
                    var c = L.GetContext();
                    if (DelayMs > 0) System.Threading.Thread.Sleep(DelayMs);
                    byte[] body;
                    if (Files.TryGetValue(c.Request.Url.AbsolutePath, out body)) { c.Response.OutputStream.Write(body, 0, body.Length); c.Response.Close(); }
                    else { c.Response.StatusCode = 404; c.Response.Close(); }
                }
                catch { }
            }
        }

        public void Dispose() { try { L.Stop(); L.Close(); } catch { } }
    }

    private static string FakeLatest(FakeServer s, string ver, byte[] dll, string patchVer, byte[] pdll)
    {
        string j = "{\"version\":\"" + ver + "\",\"dll\":\"" + s.Base + "SmoothNet.dll\",\"sig\":\"" + s.Base + "SmoothNet.dll.sig\",\"sha256\":\"" + UpdateCore.Sha256Hex(dll) + "\"";
        if (pdll != null)
            j += ",\"patcherVersion\":\"" + patchVer + "\",\"patcherDll\":\"" + s.Base + "SmoothNetUpdater.dll\",\"patcherSig\":\"" + s.Base + "SmoothNetUpdater.dll.sig\",\"patcherSha256\":\"" + UpdateCore.Sha256Hex(pdll) + "\"";
        return j + "}";
    }

    private static void OnlineTests(string tmp, RSA key, RSAParameters pub)
    {
        var log = new List<string>();
        Action<string> L = s => { lock (log) log.Add(s); };
        Func<string, int, byte[]> dl = (u, ms) => UpdateCore.Download(u, ms, 4 * 1024 * 1024);
        var d110 = MakeDll("SmoothNet", "1.1.0");
        var d120 = MakeDll("SmoothNet", "1.2.0");
        var p100 = MakeDll("SmoothNetUpdater", "1.0.0");
        var p120 = MakeDll("SmoothNetUpdater", "1.2.0");
        using (var srv = new FakeServer())
        {
            Func<string, bool> allow = u => u.StartsWith(srv.Base);
            srv.Files["/latest.json"] = Encoding.UTF8.GetBytes(FakeLatest(srv, "1.2.0", d120, "1.2.0", p120));
            srv.Files["/SmoothNet.dll"] = d120; srv.Files["/SmoothNet.dll.sig"] = Sign(d120, key);
            srv.Files["/SmoothNetUpdater.dll"] = p120; srv.Files["/SmoothNetUpdater.dll.sig"] = Sign(p120, key);
            string latestUrl = srv.Base + "latest.json";
            Func<string, int, byte[]> dlMap = (u, ms) => dl(u == UpdateCore.LatestUrl ? latestUrl : u, ms);

            // happy path: applied within the same call
            string o1 = NewPluginDir(tmp, "online1", d110);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            UpdateCore.TryOnlineUpdate(o1, pub, L, 4000, dlMap, allow);
            Check(Ver(Path.Combine(o1, "SmoothNet.dll")) == "1.2.0", "online: newer release downloaded and applied in the same launch", string.Join(" | ", log));
            Check(Ver(Path.Combine(o1, ".update", "SmoothNet.dll.bak")) == "1.1.0" && File.Exists(Path.Combine(o1, ".update", "applied.json")), "online: backup + applied.json written");

            // already up to date
            string o2 = NewPluginDir(tmp, "online2", d120);
            UpdateCore.TryOnlineUpdate(o2, pub, L, 4000, dlMap, allow);
            Check(Ver(Path.Combine(o2, "SmoothNet.dll")) == "1.2.0" && !File.Exists(Path.Combine(o2, ".update", "applied.json")), "online: up to date -> nothing applied");

            // unconfirmed previous update blocks a second apply
            string o3 = NewPluginDir(tmp, "online3", d110);
            Directory.CreateDirectory(Path.Combine(o3, ".update"));
            File.WriteAllText(Path.Combine(o3, ".update", "applied.json"), "{\"version\":\"1.1.0\",\"launchesWithoutBoot\":0}");
            UpdateCore.TryOnlineUpdate(o3, pub, L, 4000, dlMap, allow);
            Check(Ver(Path.Combine(o3, "SmoothNet.dll")) == "1.1.0", "online: unconfirmed previous update -> skipped");

            // bad signature served
            srv.Files["/SmoothNet.dll.sig"] = Sign(d120, RSA.Create(3072));
            string o4 = NewPluginDir(tmp, "online4", d110);
            UpdateCore.TryOnlineUpdate(o4, pub, L, 4000, dlMap, allow);
            Check(Ver(Path.Combine(o4, "SmoothNet.dll")) == "1.1.0", "online: wrong signature -> not applied");
            srv.Files["/SmoothNet.dll.sig"] = Sign(d120, key);

            // rejected version
            string o5 = NewPluginDir(tmp, "online5", d110);
            Directory.CreateDirectory(Path.Combine(o5, ".update"));
            File.WriteAllText(Path.Combine(o5, ".update", "rejected.txt"), "1.2.0\n");
            UpdateCore.TryOnlineUpdate(o5, pub, L, 4000, dlMap, allow);
            Check(Ver(Path.Combine(o5, "SmoothNet.dll")) == "1.1.0", "online: previously rolled-back version skipped");

            // disallowed host in latest.json (default allow-list) -> rejected
            string o6 = NewPluginDir(tmp, "online6", d110);
            UpdateCore.TryOnlineUpdate(o6, pub, L, 4000, dlMap, UpdateCore.IsAllowedUrl);
            Check(Ver(Path.Combine(o6, "SmoothNet.dll")) == "1.1.0", "online: non-GitHub URLs in latest.json are refused by the default allow-list");

            // slow server: hard budget respected, nothing applied (even after the late response)
            srv.DelayMs = 3000;
            string o7 = NewPluginDir(tmp, "online7", d110);
            sw.Restart();
            UpdateCore.TryOnlineUpdate(o7, pub, L, 800, dlMap, allow);
            long took = sw.ElapsedMilliseconds;
            Check(took < 2500, "online: slow server -> returned within the budget (" + took + " ms)");
            System.Threading.Thread.Sleep(4500);
            Check(Ver(Path.Combine(o7, "SmoothNet.dll")) == "1.1.0", "online: late response after the budget is discarded");
            srv.DelayMs = 0;
        }
        // unreachable server
        string o8 = NewPluginDir(tmp, "online8", d110);
        var sw8 = System.Diagnostics.Stopwatch.StartNew();
        UpdateCore.TryOnlineUpdate(o8, pub, L, 2000, (u, ms) => dl("http://127.0.0.1:9/latest.json", ms), u => true);
        Check(Ver(Path.Combine(o8, "SmoothNet.dll")) == "1.1.0" && sw8.ElapsedMilliseconds < 3500, "online: unreachable server -> no change, no hang");

        // ---- patcher self-update ----
        using (var srv = new FakeServer())
        {
            LatestInfo li; string err;
            string json = FakeLatest(srv, "1.2.0", d120, "1.2.0", p120);
            Check(UpdateCore.TryParseLatest(json, out li, out err, u => u.StartsWith(srv.Base)) && li.PatcherVersion == "1.2.0" && li.PatcherSha256 == UpdateCore.Sha256Hex(p120), "latest.json patcher block parses", err);
            Check(UpdateCore.TryParseLatest(FakeLatest(srv, "1.2.0", d120, null, null), out li, out err, u => u.StartsWith(srv.Base)) && li.PatcherVersion == null, "latest.json without patcher block still parses");
            string mixed = "{\"version\":\"1.2.0\",\"dll\":\"https://github.com/ryuto-alt/SmoothNet/releases/download/v1.2.0/SmoothNet.dll\",\"sig\":\"https://github.com/ryuto-alt/SmoothNet/releases/download/v1.2.0/SmoothNet.dll.sig\",\"sha256\":\"" + UpdateCore.Sha256Hex(d120) + "\",\"patcherVersion\":\"1.2.0\",\"patcherDll\":\"https://evil.example.com/x.dll\",\"patcherSig\":\"https://evil.example.com/x.sig\",\"patcherSha256\":\"" + UpdateCore.Sha256Hex(p120) + "\"}";
            Check(UpdateCore.TryParseLatest(mixed, out li, out err) && li.PatcherVersion == null, "evil patcher URL dropped, plugin update kept", err);

            var s120 = Sign(p120, key);
            Check(UpdateCore.VerifyPatcher(p120, s120, "1.2.0", UpdateCore.Sha256Hex(p120), "1.0.0", pub, out err), "patcher verifies", err);
            Check(!UpdateCore.VerifyPatcher(d120, Sign(d120, key), "1.2.0", null, "1.0.0", pub, out err) && err.Contains("assembly name"), "plugin DLL is not accepted as a patcher", err);
            Check(!UpdateCore.VerifyPatcher(p120, Sign(p120, RSA.Create(3072)), "1.2.0", null, "1.0.0", pub, out err), "patcher with foreign signature rejected", err);
            Check(!UpdateCore.VerifyUpdate(p120, s120, "1.2.0", null, "1.0.0", pub, out err), "patcher is not accepted as the plugin", err);

            // staging
            string pd = NewPluginDir(tmp, "patch1", d110);
            string patchers = Path.Combine(tmp, "patchers", "ryuto-SmoothNet");
            Directory.CreateDirectory(patchers);
            string target = Path.Combine(patchers, "SmoothNetUpdater.dll");
            File.WriteAllBytes(target, p100);
            UpdateCore.StagePatcher(pd, p120, s120, li = new LatestInfo { PatcherVersion = "1.2.0", PatcherSha256 = UpdateCore.Sha256Hex(p120) }, target);
            string sv, ss, st;
            Check(UpdateCore.TryGetStagedPatcher(pd, out sv, out ss, out st) && sv == "1.2.0" && st == target && ss == UpdateCore.Sha256Hex(p120), "patcher staged; target path round-trips", st);

            // helper script: wait on an already-exited pid, verify sha, swap, keep .bak
            string script = Path.Combine(tmp, "helper.ps1");
            File.WriteAllText(script, UpdateCore.HelperScript, new UTF8Encoding(true));
            int deadPid = DeadPid();
            Check(RunHelper(script, deadPid, UpdateCore.StagedPatcherPath(pd), target, ss), "helper ran");
            Check(Ver2(target) == "1.2.0" && Ver2(target + ".bak") == "1.0.0" && !File.Exists(UpdateCore.StagedPatcherPath(pd)), "helper moved the patcher into place and kept .bak");

            // helper with a wrong sha256 must not touch the target
            string pd2 = NewPluginDir(tmp, "patch2", d110);
            string target2 = Path.Combine(patchers, "Other.dll");
            File.WriteAllBytes(target2, p100);
            UpdateCore.StagePatcher(pd2, p120, s120, li, target2);
            Check(RunHelper(script, deadPid, UpdateCore.StagedPatcherPath(pd2), target2, new string('0', 64)) && Ver2(target2) == "1.0.0" && !File.Exists(target2 + ".bak"), "helper with sha256 mismatch leaves the patcher alone");

            // the real SpawnHelper (hidden, detached)
            string pd3 = NewPluginDir(tmp, "patch3", d110);
            string target3 = Path.Combine(patchers, "Third.dll");
            File.WriteAllBytes(target3, p100);
            UpdateCore.StagePatcher(pd3, p120, s120, li, target3);
            Check(UpdateCore.SpawnHelper(pd3, deadPid, false, L), "SpawnHelper started");
            bool moved = false;
            for (int i = 0; i < 60 && !moved; i++) { System.Threading.Thread.Sleep(500); moved = Ver2(target3) == "1.2.0"; }
            Check(moved && File.Exists(target3 + ".bak"), "SpawnHelper end-to-end: patcher swapped after the game exited");
            Check(!UpdateCore.SpawnHelper(NewPluginDir(tmp, "patch4", d110), deadPid, false, L), "SpawnHelper does nothing when there is nothing to do");
        }
    }

    private static string Ver2(string path)
    {
        try { var an = AssemblyName.GetAssemblyName(path); return an.Version.Major + "." + an.Version.Minor + "." + an.Version.Build; }
        catch { return null; }
    }

    private static int DeadPid()
    {
        var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit 0") { CreateNoWindow = true, UseShellExecute = false });
        int id = p.Id; p.WaitForExit(); return id;
    }

    private static bool RunHelper(string script, int pid, string staged, string target, string sha)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("powershell.exe",
            "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"" + script + "\" -GamePid " + pid + " -Staged \"" + staged + "\" -Target \"" + target + "\" -Sha \"" + sha + "\" -Relaunch 0")
        { CreateNoWindow = true, UseShellExecute = false };
        using (var p = System.Diagnostics.Process.Start(psi)) return p.WaitForExit(60000) && p.ExitCode == 0;
    }

    private static async System.Threading.Tasks.Task Live(string tmp)
    {
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("SmoothNet-Updater/1.1.0");
            string json = await http.GetStringAsync(UpdateCore.LatestUrl);
            Console.WriteLine("latest.json: " + json.Trim());
            LatestInfo info; string err;
            Check(UpdateCore.TryParseLatest(json, out info, out err), "live latest.json parses and URLs allowed", err);
            if (info == null) return;
            Check(UpdateCore.CompareVersions(info.Version, "1.1.0") == 1, "live version is newer than 1.1.0 (" + info.Version + ")");
            var dll = await http.GetByteArrayAsync(info.Dll);
            var sig = await http.GetByteArrayAsync(info.Sig);
            Console.WriteLine("downloaded dll {0} bytes, sig {1} bytes", dll.Length, sig.Length);
            Check(UpdateCore.VerifyUpdate(dll, sig, info.Version, info.Sha256, "1.1.0", UpdateCore.PublicKey(), out err), "live download verifies with embedded public key", err);

            string dir = NewPluginDir(tmp, "live", MakeDll("SmoothNet", "1.1.0"));
            UpdateCore.Stage(dir, dll, sig, info);
            var log = new List<string>();
            UpdateCore.OnLaunch(dir, UpdateCore.PublicKey(), s => log.Add(s));
            foreach (var l in log) Console.WriteLine("  patcher: " + l);
            Check(Ver(Path.Combine(dir, "SmoothNet.dll")) == info.Version, "live: patcher logic applied " + info.Version + " in temp folder");
            Check(Ver(Path.Combine(dir, ".update", "SmoothNet.dll.bak")) == "1.1.0", "live: backup of 1.1.0 kept");
        }
    }
}
