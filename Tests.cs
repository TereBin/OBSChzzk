using System;
using System.Web.Script.Serialization;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;
static class Tests {
    static string Json(string name, string digest, string url, bool prerelease,string assetUrl=null) {
        return new JavaScriptSerializer().Serialize(new { draft=false, prerelease=prerelease, tag_name="v1.2.3", assets=new [] {
            new { name=name, digest=digest, browser_download_url=url, size=123, url=assetUrl }
        }});
    }
    static int Main() {
        try { return Check(); } catch (Exception ex) { Console.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    static int Check() {
        string downloadUrl="https://github.com/TereBin/OBSChzzkPing-Release/releases/download/v0.8.3/OBSChzzkPing-Setup.exe?download=1";
        string freshA=Program.FreshDownloadUrl(downloadUrl),freshB=Program.FreshDownloadUrl(downloadUrl);
        if(freshA==freshB || new Uri(freshA).GetLeftPart(UriPartial.Path)!=new Uri(downloadUrl).GetLeftPart(UriPartial.Path) || !new Uri(freshA).Query.Contains("download=1&obschzzk_request=")) throw new Exception("Fresh download request failed");
        Console.WriteLine("PASS: fresh request identity, original official path and query preservation");
        var visited=new System.Collections.Generic.List<int>();
        var failedItems=new System.Collections.Generic.List<int>();
        bool finished=Program.ProcessIndependently(new [] {0,1,2},item=> {
            visited.Add(item);
            if(item==0) throw new HttpFailure(504,"LiveEditor fixture");
            return Task.FromResult(true);
        },(item,ex)=>failedItems.Add(item)).GetAwaiter().GetResult();
        if(!finished || visited.Count!=3 || failedItems.Count!=1 || failedItems[0]!=0) throw new Exception("Download failure blocked remaining plugins");
        visited.Clear();failedItems.Clear();
        finished=Program.ProcessIndependently(new [] {0,1,2},async item=> {
            await Task.FromResult(0);
            visited.Add(item);
            if(item==1) throw new Exception("Installer failure fixture");
            return true;
        },(item,ex)=>failedItems.Add(item)).GetAwaiter().GetResult();
        if(!finished || visited.Count!=3 || failedItems.Count!=1 || failedItems[0]!=1) throw new Exception("Install failure blocked remaining plugins");
        visited.Clear();
        finished=Program.ProcessIndependently(new [] {0,1,2},item=> {visited.Add(item);return Task.FromResult(false);},(item,ex)=>{throw new Exception("Cancellation treated as failure");}).GetAwaiter().GetResult();
        if(finished || visited.Count!=1) throw new Exception("User exit must stop all remaining plugins");
        Console.WriteLine("PASS: download/install failure isolation, remaining plugins continue, explicit user exit stops batch");
        int attempts=0,waits=0;
        var retried=Program.RetryNetwork(()=> {
            if(++attempts<3) throw new HttpFailure(504,"https://github.com/test");
            return Task.FromResult("ok");
        },ms=>{waits++;return Task.FromResult(0);}).GetAwaiter().GetResult();
        if(retried!="ok" || attempts!=3 || waits!=2) throw new Exception("504 retry recovery failed");
        attempts=0;
        try { Program.RetryNetwork<string>(()=>{attempts++;throw new HttpFailure(503,"test");},ms=>Task.FromResult(0)).GetAwaiter().GetResult(); } catch(HttpFailure) { }
        if(attempts!=3) throw new Exception("Retry limit failed");
        attempts=0;
        try { Program.RetryNetwork<string>(()=>{attempts++;throw new HttpFailure(404,"test");},ms=>Task.FromResult(0)).GetAwaiter().GetResult(); } catch(HttpFailure) { }
        if(attempts!=1 || Program.IsTransient(new HttpFailure(403,"test")) || Program.IsTransient(new Exception("Hash mismatch"))) throw new Exception("Permanent error retry policy failed");
        Console.WriteLine("PASS: 504 recovery, bounded retries, permanent error and integrity failure exclusion");
        int checks=0,prompts=0,closes=0,errors=0;
        Action<Exception> onError=ex=>errors++;
        if(!Program.WaitForObsExit(()=>false,()=>{prompts++;return DialogResult.Cancel;},()=>{closes++;return Task.FromResult(0);},onError).GetAwaiter().GetResult() || prompts!=0 || closes!=0) throw new Exception("Closed OBS gate failed");
        if(Program.WaitForObsExit(()=>true,()=>DialogResult.Cancel,()=>Task.FromResult(0),onError).GetAwaiter().GetResult()) throw new Exception("Exit must stop installer");
        checks=0;prompts=0;
        if(!Program.WaitForObsExit(()=>++checks<=2,()=>{prompts++;return DialogResult.Retry;},()=>{throw new Exception("Continue must not terminate OBS");},onError).GetAwaiter().GetResult() || prompts!=2) throw new Exception("Continue recheck loop failed");
        bool running=true;
        if(!Program.WaitForObsExit(()=>running,()=>DialogResult.Yes,()=>{closes++;running=false;return Task.FromResult(0);},onError).GetAwaiter().GetResult() || closes!=1) throw new Exception("Close-and-continue failed");
        prompts=0;
        if(Program.WaitForObsExit(()=>true,()=>++prompts==1?DialogResult.Yes:DialogResult.Cancel,()=>{throw new Exception("Access denied fixture");},onError).GetAwaiter().GetResult() || errors!=1 || prompts!=2) throw new Exception("Termination failure must not advance");
        prompts=0;
        if(Program.WaitForObsExit(()=>true,()=>++prompts==1?DialogResult.Yes:DialogResult.Cancel,()=>Task.FromResult(0),onError).GetAwaiter().GetResult() || prompts!=2) throw new Exception("Successful close request must recheck actual process state");
        Console.WriteLine("PASS: OBS gate, continue recheck, exit, explicit close, termination failure/still-running guard (no real OBS termination)");
        var editor=new Plugin {Name="OBSChzzkLiveEditor",Dll="OBSChzzkLiveEditor.dll"};
        if(Program.IsPluginAvailable(editor,"32.2.2") || Program.IsPluginAvailable(editor,null) ||
           !Program.IsPluginAvailable(editor,"33.0.0") || !Program.IsPluginAvailable(editor,"34.0.1") ||
           !Program.IsPluginAvailable(new Plugin {Name="OBSChzzkEvent"},"32.2.2") ||
           !Program.IsPluginAvailable(new Plugin {Name="OBSChzzkPing"},null)) throw new Exception("Plugin availability failed");
        Console.WriteLine("PASS: LiveEditor availability on OBS 32/33/34/unknown; Event/Ping unaffected");
        if(!Program.RequiresObs33(new Release {Plugin=editor,Tag="v0.4.0"}) || Program.RequiresObs33(new Release {Plugin=editor,Tag="v0.3.6"}) ||
            Program.SupportsObs33("32.2.2") || !Program.SupportsObs33("33.0.0.0") || !Program.SupportsObs33("34.0.1") || Program.SupportsObs33(null)) throw new Exception("OBS requirements failed");
        string pathsRoot=Path.Combine(Path.GetTempPath(),"OBSChzzk-path-test-"+Guid.NewGuid().ToString("N"));
        try {
            string obs=Path.Combine(pathsRoot,"obs"), common=Path.Combine(pathsRoot,"common");
            string old=Path.Combine(obs,@"obs-plugins\64bit\OBSChzzkLiveEditor.dll");
            string modern=Path.Combine(common,@"obs-studio\plugins\OBSChzzkLiveEditor\OBSChzzkLiveEditor.dll");
            if(Program.FindInstalledDll(editor,obs,common)!=null) throw new Exception("Missing DLL accepted");
            Directory.CreateDirectory(Path.GetDirectoryName(old)); File.WriteAllText(old,"");
            if(Program.FindInstalledDll(editor,obs,common)!=old) throw new Exception("Legacy detection failed");
            var modernRelease=new Release {Plugin=editor,Tag="v0.4.0"};
            bool missingRejected=false;
            try {Program.VerifyInstalled(modernRelease,obs,common);} catch {missingRejected=true;}
            if(!missingRejected) throw new Exception("Old DLL accepted as modern install");
            Directory.CreateDirectory(Path.GetDirectoryName(modern)); File.WriteAllText(modern,"");
            if(Program.FindInstalledDll(editor,obs,common)!=modern) throw new Exception("ProgramData priority failed");
            foreach(string relative in new [] {@"tls\qschannelbackend.dll",@"data\locale\ko-KR.ini"}) {
                string path=Path.Combine(Path.GetDirectoryName(modern),relative);Directory.CreateDirectory(Path.GetDirectoryName(path));File.WriteAllText(path,"");
            }
            Program.VerifyInstalled(modernRelease,obs,common);
            Program.VerifyInstalled(new Release {Plugin=editor,Tag="v0.3.6"},obs,common);
        } finally {if(Directory.Exists(pathsRoot)) Directory.Delete(pathsRoot,true);}
        Console.WriteLine("PASS: ProgramData/legacy detection, modern install validation, OBS 33 requirements");
        if(!Program.ShouldOfferUpdate("1.1.8","v1.1.9",null) ||
           Program.ShouldOfferUpdate("1.1.8","v1.1.9","v1.1.9") ||
           Program.ShouldOfferUpdate("1.1.9","v1.1.9",null) ||
           Program.ShouldOfferUpdate("1.2.0","v1.1.9",null) ||
           !Program.ShouldOfferUpdate("1.1.8","v1.2.0","v1.1.9") ||
           Program.ShouldOfferUpdate("1.1.8","v1.1.9","1.1.9.0")) throw new Exception("Self-update notification policy failed");
        Console.WriteLine("PASS: self-update offer, defer, skip, next version, same/older version");
        string folder=Path.Combine(Path.GetTempPath(),"OBSChzzk-test-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try {
            string target=Path.Combine(folder,"target.exe"), payload=Path.Combine(folder,"update.exe");
            File.WriteAllText(target,"old"); File.WriteAllText(payload,"new");
            string hash;
            using(var sha=SHA256.Create()) hash=BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(payload))).Replace("-","");
            bool launched=false;
            Program.ReplaceAndLaunch(target,payload,hash,()=> { if(File.ReadAllText(target)!="new") throw new Exception("Not replaced"); launched=true; });
            if(!launched || Directory.GetFiles(folder).Length!=2) throw new Exception("Replacement/cleanup failed");
            File.WriteAllText(target,"old");
            try { Program.ReplaceAndLaunch(target,payload,hash,()=> { throw new Exception("Launch failed"); }); } catch { }
            if(File.ReadAllText(target)!="old" || Directory.GetFiles(folder).Length!=2) throw new Exception("Rollback failed");
            bool hashRejected=false;
            try { Program.ReplaceAndLaunch(target,payload,new string('0',64),()=> { throw new Exception("Must not launch"); }); } catch { hashRejected=true; }
            if(!hashRejected || File.ReadAllText(target)!="old") throw new Exception("Corrupt payload accepted");
        } finally { Directory.Delete(folder,true); }
        Console.WriteLine("PASS: verified replacement, relaunch callback, launch failure rollback, corrupt download rejection");
        string[][] versions={
            new [] {"0.8.1.0","v0.8.2","업데이트 가능"},
            new [] {"0.8.2.0","v0.8.2","최신 버전"},
            new [] {"0.10.0","v0.9.0","설치 버전이 더 높음"},
            new [] {"미설치","v0.8.2","미설치"},
            new [] {"설치됨 · 버전 정보 없음","v0.8.2","버전 비교 불가"},
            new [] {"0.8.2-beta","v0.8.2","버전 비교 불가"},
            new [] {"0.3.1 (이전 이름)","v0.3.6","업데이트 가능"}
        };
        foreach(var v in versions) if(Program.CompareVersions(v[0],v[1])!=v[2]) throw new Exception("Version comparison failed: "+v[0]);
        Console.WriteLine("PASS: installed/latest version comparisons (7 cases)");
        var plugin=new Plugin { Name="OBSChzzkPing", Repo="OBSChzzkPing-Release" };
        string url="https://github.com/TereBin/OBSChzzkPing-Release/releases/download/v1.2.3/OBSChzzkPing-Setup.exe";
        string digest="sha256:"+new string('a',64);
        var self=new Plugin {Name="OBSChzzk",Repo="OBSChzzk"};
        foreach(var name in new [] {"OBSChzzk-Setup.exe","OBSChzzk-1.1.12-windows-x64-setup.exe"}) {
            var selfRelease=Program.ParseRelease(self,Json(name,digest,"https://github.com/TereBin/OBSChzzk/releases/download/v1.2.3/"+name,false));
            if(!selfRelease.Url.EndsWith(name)) throw new Exception("Self-update asset name compatibility failed");
        }
        Console.WriteLine("PASS: stable and previous versioned installer asset names");
        var release=Program.ParseRelease(plugin,Json("OBSChzzkPing-Setup.exe",digest,url,false));
        string assetUrl="https://api.github.com/repos/TereBin/OBSChzzkPing-Release/releases/assets/123";
        if(Program.ParseRelease(plugin,Json("OBSChzzkPing-Setup.exe",digest,url,false,assetUrl)).AssetUrl!=assetUrl) throw new Exception("Official asset API route missing");
        if (release.Tag!="v1.2.3" || release.Size!=123 || release.Sha256.Length!=64) throw new Exception("Valid release failed");
        string[] rejected={ Json("OBSChzzkPing-Setup.exe",digest,url,false,"https://api.github.com/repos/Other/Repo/releases/assets/123"), Json("OBSChzzkPing-Setup.exe",null,url,false), Json("OBSChzzkPing-Setup.exe",digest,"https://example.com/setup.exe",false), Json("OBSChzzkPing-windows-x64.zip",digest,url,false), Json("OBSChzzkPing-Setup.exe",digest,url,true) };
        foreach (string json in rejected) {
            bool failed=false;
            try { Program.ParseRelease(plugin,json); } catch { failed=true; }
            if (!failed) throw new Exception("Unsafe release accepted");
        }
        Console.WriteLine("PASS: valid release, missing digest, foreign URL, ZIP-only release, prerelease");
        return 0;
    }
}
