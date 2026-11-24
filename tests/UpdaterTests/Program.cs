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
        }
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
