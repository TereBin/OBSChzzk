using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.Win32;
using System.Reflection;

[assembly: AssemblyVersion("1.1.19.0")]
[assembly: AssemblyFileVersion("1.1.19.0")]
[assembly: AssemblyInformationalVersion("1.1.19")]

class Plugin {
    public string Name, Repo, Description, Dll, Help;
    public CheckBox Choice;
    public Label VersionLabel;
    public Label StatusLabel;
    public bool Available=true;
}
class Release {
    public Plugin Plugin;
    public string Tag, Url, Sha256, AssetUrl;
    public long Size;
}
class HttpFailure : HttpRequestException {
    public readonly int Code;
    public HttpFailure(int code,string url):base("HTTP "+code+": "+url) { Code=code; }
}
static class Program {
    public static async Task<bool> ProcessIndependently<T>(IEnumerable<T> items,Func<T,Task<bool>> process,Action<T,Exception> failed) {
        foreach(var item in items) {
            try { if(!await process(item)) return false; }
            catch(Exception ex) { failed(item,ex); }
        }
        return true;
    }
    static string NetworkLogPath;
    static void NetworkLog(string message) {
        try { if(NetworkLogPath!=null) File.AppendAllText(NetworkLogPath,DateTime.Now.ToString("O")+" "+message+Environment.NewLine); } catch { }
    }
    public static bool IsTransient(Exception ex) {
        var http=ex as HttpFailure;
        if(http!=null) return new [] {408,429,500,502,503,504}.Contains(http.Code);
        return ex is HttpRequestException || ex is TaskCanceledException || ex is IOException;
    }
    public static async Task<T> RetryNetwork<T>(Func<Task<T>> operation,Func<int,Task> delay) {
        for(int attempt=1;;attempt++) {
            try { return await operation(); }
            catch(Exception ex) {
                NetworkLog("Attempt "+attempt+": "+ex.Message);
                if(attempt>=3 || !IsTransient(ex)) throw;
            }
            await delay(attempt*1000);
        }
    }
    public static string FreshDownloadUrl(string url) {
        var builder=new UriBuilder(url);
        builder.Query=(builder.Query.Length>1?builder.Query.Substring(1)+"&":"")+"obschzzk_request="+Guid.NewGuid().ToString("N");
        return builder.Uri.AbsoluteUri;
    }
    static async Task<HttpResponseMessage> SendOnce(string url,bool freshDownload=false,bool binaryAsset=false) {
        NetworkLog("GET "+url+(freshDownload?" (fresh download request)":""));
        HttpResponseMessage response;
        using(var request=new HttpRequestMessage(HttpMethod.Get,freshDownload?FreshDownloadUrl(url):url)) {
            if(freshDownload) request.Headers.CacheControl=new System.Net.Http.Headers.CacheControlHeaderValue { NoCache=true,NoStore=true };
            if(binaryAsset) request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/octet-stream"));
            response=await Client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead);
        }
        if(!response.IsSuccessStatusCode) {
            int code=(int)response.StatusCode;
            response.Dispose();
            throw new HttpFailure(code,url);
        }
        return response;
    }
    static Task<HttpResponseMessage> GetResponse(string url) {
        return RetryNetwork(()=>SendOnce(url),ms=>Task.Delay(ms));
    }
    static Task<string> GetText(string url) {
        return RetryNetwork(async ()=> {
            using(var response=await SendOnce(url)) return await response.Content.ReadAsStringAsync();
        },ms=>Task.Delay(ms));
    }
    public const string AppVersion="1.1.19";
    static readonly Plugin Self=new Plugin { Name="OBSChzzk",Repo="OBSChzzk" };
    static string SkipFile { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OBSChzzk","skipped-installer-version.txt"); } }
    public static bool ShouldOfferUpdate(string current,string latest,string skipped) {
        return CompareVersions(current,latest)=="업데이트 가능" && CompareVersions(skipped ?? "",latest)!="최신 버전";
    }
    static string ReadSkipped() { try { return File.ReadAllText(SkipFile).Trim(); } catch { return null; } }
    static void SaveSkipped(string tag) {
        Directory.CreateDirectory(Path.GetDirectoryName(SkipFile));
        File.WriteAllText(SkipFile,tag);
    }
    static DialogResult AskUpdate(Form owner,string tag) {
        using(var dialog=new Form { Text="OBSChzzk 업데이트",ClientSize=new System.Drawing.Size(610,195),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,AutoScaleMode=AutoScaleMode.Dpi }) {
            dialog.Controls.Add(new Label { Left=20,Top=20,Width=570,Height=100,Text="통합 설치 프로그램의 새 버전이 있습니다.\r\n현재: "+AppVersion+"   최신: "+tag+"\r\n\r\n지금 설치하면 프로그램을 새 버전으로 교체하고 다시 실행합니다." });
            string[] labels={"지금 설치","다음","이 버전 건너뛰기"};
            DialogResult[] results={DialogResult.Yes,DialogResult.No,DialogResult.Ignore};
            for(int i=0;i<labels.Length;i++) {
                var button=new Button { Text=labels[i],Left=20+i*195,Top=135,Width=180,Height=38,DialogResult=results[i] };
                dialog.Controls.Add(button);
                if(i==1) dialog.CancelButton=button;
            }
            return dialog.ShowDialog(owner);
        }
    }
    // Replace through a sibling file so an incomplete copy never overwrites the running program.
    public static void ReplaceAndLaunch(string target,string payload,string expectedHash,Action launch) {
        if(!Hash(payload).Equals(expectedHash,StringComparison.OrdinalIgnoreCase)) throw new Exception("업데이트 파일의 SHA-256이 일치하지 않습니다.");
        string staged=target+".update-"+Guid.NewGuid().ToString("N");
        string backup=target+".backup-"+Guid.NewGuid().ToString("N");
        bool safeToDeleteBackup=false;
        try {
            File.Copy(payload,staged,false);
            if(!Hash(staged).Equals(expectedHash,StringComparison.OrdinalIgnoreCase)) throw new Exception("교체할 파일의 검증에 실패했습니다.");
            File.Replace(staged,target,backup);
            try { launch(); }
            catch { File.Copy(backup,target,true); safeToDeleteBackup=true; throw; }
            safeToDeleteBackup=true;
        } finally {
            if(File.Exists(staged)) File.Delete(staged);
            // If rollback itself failed, keep the backup for recovery.
            if(File.Exists(backup) && safeToDeleteBackup) File.Delete(backup);
        }
    }
    static int ApplySelfUpdate(string configPath) {
        try {
            var config=new JavaScriptSerializer().Deserialize<Dictionary<string,string>>(File.ReadAllText(configPath));
            string target=Path.GetFullPath(config["target"]);
            string payload=Path.Combine(Path.GetDirectoryName(configPath),"OBSChzzk-setup.exe");
            if(!target.EndsWith(".exe",StringComparison.OrdinalIgnoreCase) || target==Application.ExecutablePath) throw new Exception("잘못된 업데이트 대상입니다.");
            int pid=Int32.Parse(config["pid"]);
            try { using(var parent=Process.GetProcessById(pid)) if(!parent.WaitForExit(60000)) throw new Exception("기존 프로그램 종료를 기다리는 시간이 초과되었습니다."); }
            catch(ArgumentException) { }
            ReplaceAndLaunch(target,payload,config["hash"],()=> {
                using(var started=Process.Start(new ProcessStartInfo(target) { UseShellExecute=true,WorkingDirectory=Path.GetDirectoryName(target) })) {
                    if(started==null) throw new Exception("새 버전을 실행하지 못했습니다.");
                }
            });
            File.Delete(payload); File.Delete(configPath);
            return 0;
        } catch(Exception ex) {
            MessageBox.Show("통합 설치 프로그램 업데이트를 완료하지 못했습니다. 기존 EXE를 다시 실행하거나 최신 릴리스를 직접 받아 주세요.\r\n\r\n"+ex.Message,"OBSChzzk 업데이트",MessageBoxButtons.OK,MessageBoxIcon.Error);
            return 1;
        }
    }
    static async Task<bool> CheckSelfUpdate(Form form,TextBox status) {
        string folder=null;
        bool applying=false;
        try {
            status.Text="통합 설치 프로그램의 최신 버전을 확인하고 있습니다...";
            var lookup=Latest(Self);
            if(await Task.WhenAny(lookup,Task.Delay(20000))!=lookup) { status.Text="통합 설치 프로그램 업데이트 확인 시간 초과. 현재 버전으로 계속합니다."; return false; }
            var release=await lookup;
            if(!ShouldOfferUpdate(AppVersion,release.Tag,ReadSkipped())) return false;
            var choice=AskUpdate(form,release.Tag);
            if(choice==DialogResult.Ignore) { SaveSkipped(release.Tag); return false; }
            if(choice!=DialogResult.Yes) return false;
            applying=true;
            status.Text="통합 설치 프로그램 "+release.Tag+" 다운로드·검증 중...";
            folder=Path.Combine(Path.GetTempPath(),"OBSChzzk-self-update-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            string payload=await Download(release,folder);
            if(CompareVersions(FileVersionInfo.GetVersionInfo(payload).ProductVersion ?? "",release.Tag)!="최신 버전") throw new Exception("다운로드한 EXE 버전이 릴리스 버전과 다릅니다.");
            string helper=Path.Combine(folder,"update-helper.exe");
            File.Copy(Application.ExecutablePath,helper);
            string config=Path.Combine(folder,"update.json");
            File.WriteAllText(config,new JavaScriptSerializer().Serialize(new Dictionary<string,string> {
                {"target",Application.ExecutablePath},{"pid",Process.GetCurrentProcess().Id.ToString()},{"hash",release.Sha256}
            }));
            using(var process=Process.Start(new ProcessStartInfo(helper,"--apply-self-update \""+config+"\"") { UseShellExecute=false,CreateNoWindow=true })) {
                if(process==null) throw new Exception("업데이트 도우미를 실행하지 못했습니다.");
            }
            return true;
        } catch(Exception ex) {
            if(applying) MessageBox.Show(form,"프로그램 업데이트를 적용하지 못했습니다. 현재 버전으로 계속합니다.\r\n\r\n"+ex.Message,"OBSChzzk 업데이트",MessageBoxButtons.OK,MessageBoxIcon.Information);
            else status.Text="통합 설치 프로그램 업데이트 확인 실패. 현재 버전으로 계속합니다.";
            return false;
        }
    }
    static void SetStatus(Plugin plugin, string text) {
        if(!plugin.Available) text="사용 불가";
        plugin.StatusLabel.Text=text;
        plugin.StatusLabel.ForeColor=text=="완료" ? System.Drawing.Color.DarkGreen :
            text=="실패" ? System.Drawing.Color.Firebrick :
            text=="선택 안 함" || text=="중단됨" || text=="사용 불가" ? System.Drawing.Color.DimGray : System.Drawing.Color.RoyalBlue;
    }
    static readonly Plugin[] Plugins = {
        new Plugin { Name="OBSChzzkLiveEditor", Repo="OBSChzzkLiveEditor-Releases", Description="OBS 도크에서 치지직 방송 제목·카테고리·태그를 수정합니다.", Dll="OBSChzzkLiveEditor.dll", Help="LiveEditor: OBS 도크 메뉴에서 열고 로그인하세요." },
        new Plugin { Name="OBSChzzkEvent", Repo="OBSChzzkEvent-Releases", Description="치지직 신규 팔로우를 감지해 화면과 소리로 알립니다.", Dll="obs-chzzk-alerts.dll", Help="Event: Chrome/Edge에서 웹 스토어 확장을 설치하세요. 기존 개발자 모드 설치본은 비활성화하거나 제거하세요." },
        new Plugin { Name="OBSChzzkPing", Repo="OBSChzzkPing-Release", Description="방송 시작 시 Discord에 알리고, 방송 정보가 담긴 X 작성창을 엽니다.", Dll="streamping.dll", Help="Ping: OBS 도구 메뉴에서 채널과 알림을 설정하세요." }
    };
    static readonly HttpClient Client = CreateClient();
    static readonly Dictionary<string,Release> Releases=new Dictionary<string,Release>();
    static bool useReleasePages;
    static HttpClient CreateClient() {
        ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
        var client = new HttpClient { Timeout=TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("OBSChzzkInstaller/"+AppVersion);
        return client;
    }
    public static Release ParseRelease(Plugin plugin, string json) {
        var data = new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(json);
        if ((bool)data["draft"] || (bool)data["prerelease"]) throw new Exception("정식 릴리스가 아닙니다.");
        var assets = ((System.Collections.IEnumerable)data["assets"]).Cast<Dictionary<string,object>>().Where(a => {
            string name = (string)a["name"];
            return name.StartsWith(plugin.Name + "-", StringComparison.OrdinalIgnoreCase) &&
                name.EndsWith("setup.exe", StringComparison.OrdinalIgnoreCase);
        }).ToArray();
        if (assets.Length != 1) throw new Exception(plugin.Name + ": Windows 설치 EXE를 하나로 확인할 수 없습니다.");
        var asset = assets[0];
        string digest = asset.ContainsKey("digest") ? asset["digest"] as string : null;
        if (digest == null || !System.Text.RegularExpressions.Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$"))
            throw new Exception(plugin.Name + ": GitHub SHA-256 정보가 없습니다.");
        string url = (string)asset["browser_download_url"];
        string prefix = "https://github.com/TereBin/" + plugin.Repo + "/releases/download/";
        if (!url.StartsWith(prefix, StringComparison.Ordinal)) throw new Exception("공식 저장소의 다운로드 주소가 아닙니다.");
        string assetUrl=asset.ContainsKey("url")?asset["url"] as string:null;
        if(assetUrl!=null && !System.Text.RegularExpressions.Regex.IsMatch(assetUrl,"^https://api\\.github\\.com/repos/TereBin/"+System.Text.RegularExpressions.Regex.Escape(plugin.Repo)+"/releases/assets/[0-9]+$"))
            throw new Exception("공식 저장소의 자산 API 주소가 아닙니다.");
        return new Release { Plugin=plugin, Tag=(string)data["tag_name"], Url=url, AssetUrl=assetUrl, Sha256=digest.Substring(7), Size=Convert.ToInt64(asset["size"]) };
    }
    static async Task<Release> Latest(Plugin plugin) {
        Release cached;
        if (Releases.TryGetValue(plugin.Repo,out cached)) return cached;
        if (!useReleasePages) {
            try {
                cached=ParseRelease(plugin,await GetText("https://api.github.com/repos/TereBin/"+plugin.Repo+"/releases/latest"));
                Releases[plugin.Repo]=cached;
                return cached;
            } catch(Exception ex) {
                var http=ex as HttpFailure;
                if(!IsTransient(ex) && !(http!=null && http.Code==403)) throw;
                useReleasePages=true;
                NetworkLog("API unavailable; falling back to official release page: "+plugin.Name);
            }
        }
        cached=await LatestFromPage(plugin);
        Releases[plugin.Repo]=cached;
        return cached;
    }
    static async Task<Release> LatestFromPage(Plugin plugin) {
        string root="https://github.com/TereBin/"+plugin.Repo;
        string tag;
        using (var response=await GetResponse(root+"/releases/latest")) {
            string target=response.RequestMessage.RequestUri.AbsoluteUri;
            string prefix=root+"/releases/tag/";
            if (!target.StartsWith(prefix,StringComparison.Ordinal)) throw new Exception("최신 릴리스 주소를 확인할 수 없습니다.");
            tag=Uri.UnescapeDataString(target.Substring(prefix.Length));
        }
        string html=await GetText(root+"/releases/expanded_assets/"+Uri.EscapeDataString(tag));
        string assetPrefix="/TereBin/"+plugin.Repo+"/releases/download/";
        var urls=System.Text.RegularExpressions.Regex.Matches(html,"href=\"([^\"]+)\"").Cast<System.Text.RegularExpressions.Match>()
            .Select(m=>WebUtility.HtmlDecode(m.Groups[1].Value)).Where(u=>u.StartsWith(assetPrefix,StringComparison.Ordinal))
            .Select(u=>"https://github.com"+u).Distinct().ToArray();
        var setups=urls.Where(u=>Uri.UnescapeDataString(new Uri(u).Segments.Last()).StartsWith(plugin.Name+"-",StringComparison.OrdinalIgnoreCase) && u.EndsWith("setup.exe",StringComparison.OrdinalIgnoreCase)).ToArray();
        var sums=urls.Where(u=>u.EndsWith("/SHA256SUMS.txt",StringComparison.Ordinal)).ToArray();
        if (setups.Length!=1 || sums.Length!=1) throw new Exception(plugin.Name+": 공식 설치 EXE 또는 체크섬 파일을 확인할 수 없습니다.");
        string name=Uri.UnescapeDataString(new Uri(setups[0]).Segments.Last());
        string checksums=await GetText(sums[0]);
        string hash=null;
        foreach (var line in checksums.Split('\n')) {
            var match=System.Text.RegularExpressions.Regex.Match(line.Trim(),@"^([a-fA-F0-9]{64})\s+\*?(.+)$");
            if (match.Success && match.Groups[2].Value==name) hash=match.Groups[1].Value;
        }
        if (hash==null) throw new Exception(plugin.Name+": 설치 EXE의 SHA-256이 없습니다.");
        return new Release { Plugin=plugin,Tag=tag,Url=setups[0],Sha256=hash,Size=-1 };
    }
    static string Hash(string path) {
        using (var s=File.OpenRead(path)) using (var h=SHA256.Create())
            return BitConverter.ToString(h.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
    }
    static async Task<string> Download(Release release, string folder) {
        var file = Path.Combine(folder, release.Plugin.Name + "-setup.exe");
        string downloadUrl=release.AssetUrl ?? release.Url;
        Exception primaryError=null;
        try {
            await RetryNetwork(async ()=> {
                using(var response=await SendOnce(downloadUrl,true,release.AssetUrl!=null))
                using(var input=await response.Content.ReadAsStreamAsync())
                using(var output=File.Create(file)) await input.CopyToAsync(output);
                return true;
            },ms=>Task.Delay(ms));
        } catch(Exception ex) {
            if(!IsTransient(ex)) throw;
            primaryError=ex;
        }
        if(primaryError!=null) {
            NetworkLog("Primary download failed; trying Windows curl: "+release.Plugin.Name);
            await Task.Run(()=> {
                string curl=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"curl.exe");
                if(!File.Exists(curl)) throw new Exception("기본 다운로드 실패 및 Windows 대체 다운로드 도구 없음: "+primaryError.Message,primaryError);
                string accept=release.AssetUrl!=null?" --header \"Accept: application/octet-stream\"":"";
                var start=new ProcessStartInfo(curl,"--fail --location --silent --show-error --proto =https --proto-redir =https --connect-timeout 20 --max-time 120"+accept+" --output \""+file+"\" \""+FreshDownloadUrl(downloadUrl)+"\"") {
                    UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden,RedirectStandardError=true
                };
                using(var process=Process.Start(start)) {
                    string error=process.StandardError.ReadToEnd();
                    process.WaitForExit();
                    if(process.ExitCode!=0) throw new Exception("Windows 대체 다운로드도 실패했습니다 ("+process.ExitCode+"): "+error.Trim());
                }
            });
        }
        if ((release.Size>=0 && new FileInfo(file).Length != release.Size) || !Hash(file).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new Exception(release.Plugin.Name + ": 다운로드 무결성 검사 실패. 설치를 중단합니다.");
        return file;
    }
    static bool IsObs(string path) { return !String.IsNullOrEmpty(path) && File.Exists(Path.Combine(path, @"bin\64bit\obs64.exe")); }
    public static string FindInstalledDll(Plugin plugin,string obs,string commonData) {
        if(plugin.Name=="OBSChzzkLiveEditor") {
            string modern=Path.Combine(commonData,@"obs-studio\plugins\OBSChzzkLiveEditor\OBSChzzkLiveEditor.dll");
            if(File.Exists(modern)) return modern;
        }
        string dll=Path.Combine(obs,@"obs-plugins\64bit",plugin.Dll);
        if(File.Exists(dll)) return dll;
        if(plugin.Name=="OBSChzzkLiveEditor") {
            string legacy=Path.Combine(obs,@"obs-plugins\64bit\obs-live-editor.dll");
            if(File.Exists(legacy)) return legacy;
        }
        return null;
    }
    public static bool RequiresObs33(Release release) {
        string comparison=CompareVersions("0.4.0",release.Tag);
        return release.Plugin.Name=="OBSChzzkLiveEditor" && (comparison=="최신 버전" || comparison=="업데이트 가능");
    }
    public static bool SupportsObs33(string version) {
        string comparison=CompareVersions("33.0.0",version ?? "");
        return comparison=="최신 버전" || comparison=="업데이트 가능";
    }
    public static bool IsPluginAvailable(Plugin plugin,string obsVersion) {
        return plugin.Name!="OBSChzzkLiveEditor" || SupportsObs33(obsVersion);
    }
    static void CheckRequirements(Release release,string obs) {
        if(!RequiresObs33(release)) return;
        var info=FileVersionInfo.GetVersionInfo(Path.Combine(obs,@"bin\64bit\obs64.exe"));
        string version=info.FileVersion;
        if(!SupportsObs33(version)) throw new Exception("LiveEditor "+release.Tag+" 설치에는 OBS Studio 33.0.0 이상이 필요합니다. 현재 OBS: "+(version ?? "버전 확인 불가")+". OBS를 업데이트하거나 LiveEditor 선택을 해제하세요.");
    }
    public static void VerifyInstalled(Release release,string obs,string commonData) {
        if(RequiresObs33(release)) {
            string root=Path.Combine(commonData,@"obs-studio\plugins\OBSChzzkLiveEditor");
            foreach(string relative in new [] {"OBSChzzkLiveEditor.dll",@"tls\qschannelbackend.dll",@"data\locale\ko-KR.ini"})
                if(!File.Exists(Path.Combine(root,relative))) throw new Exception("LiveEditor 설치 파일 확인 실패: "+Path.Combine(root,relative));
        } else if(!File.Exists(Path.Combine(obs,@"obs-plugins\64bit",release.Plugin.Dll)))
            throw new Exception(release.Plugin.Name+": 설치된 DLL을 찾지 못했습니다.");
    }
    static string InstalledVersion(Plugin plugin, string obs) {
        string dll=FindInstalledDll(plugin,obs,Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        if(dll==null) return "미설치";
        bool legacy=String.Equals(Path.GetFileName(dll),"obs-live-editor.dll",StringComparison.OrdinalIgnoreCase);
        var info=FileVersionInfo.GetVersionInfo(dll);
        string version=info.ProductVersion;
        if (String.IsNullOrWhiteSpace(version)) version=info.FileVersion;
        if (String.IsNullOrWhiteSpace(version)) return "설치됨 · 버전 정보 없음";
        return version + (legacy ? " (이전 이름)" : "");
    }
    public static string CompareVersions(string installed, string latest) {
        if (installed == "미설치") return "미설치";
        var pattern=@"^v?(\d+)\.(\d+)\.(\d+)(?:\.(\d+))?(?:\+[^ ]+)?$";
        var a=System.Text.RegularExpressions.Regex.Match(installed.Replace(" (이전 이름)",""),pattern);
        var b=System.Text.RegularExpressions.Regex.Match(latest,pattern);
        if (!a.Success || !b.Success) return "버전 비교 불가";
        for (int i=1;i<=4;i++) {
            long x,y;
            if (!Int64.TryParse(a.Groups[i].Success?a.Groups[i].Value:"0",out x) ||
                !Int64.TryParse(b.Groups[i].Success?b.Groups[i].Value:"0",out y)) return "버전 비교 불가";
            if (x<y) return "업데이트 가능";
            if (x>y) return "설치 버전이 더 높음";
        }
        return "최신 버전";
    }
    static string Detect() {
        using (var root=RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32))
        using (var key=root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\OBS Studio")) {
            var command=key == null ? null : key.GetValue("UninstallString") as string;
            if (!String.IsNullOrEmpty(command)) {
                var candidate=Path.GetDirectoryName(command.Trim('"'));
                if (IsObs(candidate)) return candidate;
            }
        }
        var fallback=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "obs-studio");
        if (IsObs(fallback)) return fallback;
        throw new Exception("정식 설치된 64비트 OBS Studio를 찾지 못했습니다. 포터블 OBS는 지원하지 않습니다.");
    }
    static Process[] GetObsProcesses() {
        return Process.GetProcessesByName("obs64").Concat(Process.GetProcessesByName("obs32")).ToArray();
    }
    static bool IsObsRunning() {
        var processes=GetObsProcesses();
        try { return processes.Any(p=>!p.HasExited); }
        finally { foreach(var process in processes) process.Dispose(); }
    }
    static void CheckObsClosed() {
        if(IsObsRunning()) throw new Exception("OBS Studio를 완전히 종료한 후 다시 설치하세요.");
    }
    public static async Task<bool> WaitForObsExit(Func<bool> isRunning,Func<DialogResult> prompt,Func<Task> closeObs,Action<Exception> showError) {
        while(isRunning()) {
            var choice=prompt();
            if(choice==DialogResult.Cancel || choice==DialogResult.None) return false;
            if(choice==DialogResult.Yes) {
                try { await closeObs(); }
                catch(Exception ex) { showError(ex); }
            }
            // Continue always rechecks. Never advance based only on a button or close request.
        }
        return true;
    }
    static DialogResult AskObsRunning(Form owner) {
        using(var dialog=new Form { Text="OBS Studio가 실행 중입니다",ClientSize=new System.Drawing.Size(640,250),StartPosition=FormStartPosition.CenterParent,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,AutoScaleMode=AutoScaleMode.Dpi }) {
            dialog.Controls.Add(new Label { Left=20,Top=20,Width=600,Height=160,
                Text="계속하려면 OBS Studio를 종료해야 합니다.\r\n\r\n• 계속: OBS 실행 여부를 다시 확인합니다.\r\n• 종료: 통합 설치 프로그램을 종료합니다.\r\n• OBS 종료 후 계속: 프로그램이 OBS를 종료하고 다시 확인합니다.\r\n\r\nOBS 종료 시 방송·녹화가 중단됩니다. 먼저 작업을 저장하세요.\r\n정상 종료에 응답하지 않으면 강제 종료하여 녹화가 손상될 수 있습니다." });
            string[] labels={"계속","종료","OBS 종료 후 계속"};
            DialogResult[] results={DialogResult.Retry,DialogResult.Cancel,DialogResult.Yes};
            for(int i=0;i<labels.Length;i++) {
                var button=new Button { Text=labels[i],Left=20+i*205,Top=195,Width=190,Height=38,DialogResult=results[i] };
                dialog.Controls.Add(button);
                if(i==1) dialog.CancelButton=button;
            }
            return dialog.ShowDialog(owner);
        }
    }
    static async Task CloseObsProcesses() {
        var processes=GetObsProcesses();
        try {
            await Task.WhenAll(processes.Select(process=>Task.Run(()=> {
                if(process.HasExited) return;
                process.CloseMainWindow();
                if(process.WaitForExit(5000)) return;
                if(!process.HasExited) process.Kill();
                if(!process.WaitForExit(5000)) throw new Exception("OBS 종료 대기 시간이 초과되었습니다.");
            })));
        } finally { foreach(var process in processes) process.Dispose(); }
    }
    static async Task<bool> EnsureObsClosed(Form owner) {
        try {
            return await WaitForObsExit(IsObsRunning,()=>AskObsRunning(owner),CloseObsProcesses,
                ex=>MessageBox.Show(owner,"OBS를 종료하지 못했습니다. OBS를 직접 종료한 뒤 '계속'을 누르세요.\r\n\r\n"+ex.Message,"OBS 종료 실패",MessageBoxButtons.OK,MessageBoxIcon.Information));
        } catch(Exception ex) {
            MessageBox.Show(owner,"OBS 실행 여부를 확인하지 못해 프로그램을 종료합니다.\r\n\r\n"+ex.Message,"OBS 확인 실패",MessageBoxButtons.OK,MessageBoxIcon.Error);
            return false;
        }
    }
    static bool ShowWelcome() {
        using(var welcome=new Form { Text="OBSChzzk 통합 설치 안내",ClientSize=new System.Drawing.Size(660,430),StartPosition=FormStartPosition.CenterScreen,FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false,MinimizeBox=false,AutoScaleMode=AutoScaleMode.Dpi }) {
            welcome.Controls.Add(new Label { Left=24,Top=24,Width=610,Height=34,Text="OBSChzzk 통합 설치에 오신 것을 환영합니다.",Font=new System.Drawing.Font(System.Drawing.SystemFonts.MessageBoxFont.FontFamily,14,System.Drawing.FontStyle.Bold) });
            welcome.Controls.Add(new Label { Left=24,Top=72,Width=610,Height=48,
                Text="OBS에서 치지직 방송에 사용할 플러그인을 선택해 설치·업데이트하는 프로그램입니다.\r\n아래 세 플러그인 중 원하는 항목만 골라 최신 정식 버전으로 설치할 수 있습니다." });
            for(int i=0;i<Plugins.Length;i++) {
                var plugin=Plugins[i];
                welcome.Controls.Add(new Label { Left=32,Top=132+i*54,Width=595,Height=48,Text=plugin.Name+"\r\n"+plugin.Description });
            }
            welcome.Controls.Add(new Label { Left=24,Top=300,Width=610,Height=44,
                Text="Event는 브라우저 확장 설치가, LiveEditor는 OBS 33 이상이 필요합니다.\r\n계속을 누르면 OBS 실행 확인 → 버전 확인 → 플러그인 선택 순서로 진행합니다." });
            var proceed=new Button { Left=446,Top=355,Width=190,Height=38,Text="계속",DialogResult=DialogResult.OK };
            welcome.Controls.Add(proceed);
            welcome.AcceptButton=proceed;
            welcome.Controls.Add(new Label { Left=24,Top=403,Width=610,Height=20,Text="현재 버전: "+AppVersion,ForeColor=System.Drawing.Color.DimGray });
            return welcome.ShowDialog()==DialogResult.OK;
        }
    }
    static void Run(string file, string log, string obs, Release release) {
        var plugin=release.Plugin;
        CheckObsClosed();
        CheckRequirements(release,obs);
        string args="/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /NOCLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /LOG=\"" + log + "\"";
        if (plugin.Name == "OBSChzzkPing") args += " /OBSPATH=\"" + obs + "\"";
        using (var p=Process.Start(new ProcessStartInfo(file,args) { UseShellExecute=false })) {
            p.WaitForExit();
            if (p.ExitCode != 0) throw new Exception(plugin.Name + " 설치 실패 (코드 " + p.ExitCode + "). 로그: " + log);
        }
        VerifyInstalled(release,obs,Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
    }
    [STAThread] static int Main(string[] args) {
        if(args.Length==2 && args[0]=="--apply-self-update") return ApplySelfUpdate(args[1]);
        // Read-only/network verification path: never launches a plugin installer.
        if (args.Length == 2 && (args[0] == "--check-downloads" || args[0] == "--check-web-downloads")) {
            useReleasePages=args[0]=="--check-web-downloads";
            try {
                Directory.CreateDirectory(args[1]);
                NetworkLogPath=Path.Combine(args[1],"network.log");
                bool anyFailed=false;
                foreach (var plugin in Plugins) {
                    try {
                        var release=Latest(plugin).GetAwaiter().GetResult();
                        var file=Download(release,args[1]).GetAwaiter().GetResult();
                        File.WriteAllText(file + ".verified.txt", release.Tag + "\n" + release.Url + "\n" + Hash(file));
                    } catch(Exception ex) {
                        anyFailed=true;
                        File.WriteAllText(Path.Combine(args[1],plugin.Name+"-error.txt"),ex.ToString());
                    }
                }
                return anyFailed?1:0;
            } catch (Exception ex) { File.WriteAllText(Path.Combine(args[1],"error.txt"),ex.ToString()); return 1; }
        }
        Application.EnableVisualStyles();
        if(!ShowWelcome()) return 0;
        string obsVersion=null;
        try { obsVersion=FileVersionInfo.GetVersionInfo(Path.Combine(Detect(),@"bin\64bit\obs64.exe")).FileVersion; } catch { }
        var form=new Form { Text="OBSChzzk 통합 설치", ClientSize=new System.Drawing.Size(680,630), StartPosition=FormStartPosition.CenterScreen, FormBorderStyle=FormBorderStyle.FixedDialog, MaximizeBox=false, AutoScaleMode=AutoScaleMode.Dpi };
        form.Controls.Add(new Label { Left=22,Top=18,Width=635,Height=40,Text="원하는 플러그인을 선택하세요. 설치할 때 GitHub의 최신 정식 버전을 받습니다." });
        for (int i=0;i<Plugins.Length;i++) {
            var plugin=Plugins[i]; int y=65+i*86;
            plugin.Available=IsPluginAvailable(plugin,obsVersion);
            plugin.Choice=new CheckBox { Left=24,Top=y,Width=290,Height=25,Text=plugin.Name,Checked=plugin.Available,Enabled=plugin.Available };
            plugin.VersionLabel=new Label { Left=335,Top=y,Width=320,Height=43,Text="버전 확인 대기" };
            plugin.StatusLabel=new Label { Left=44,Top=y+26,Width=280,Height=20,Text="선택됨",ForeColor=System.Drawing.Color.RoyalBlue };
            plugin.Choice.CheckedChanged += (s,e) => SetStatus(plugin,plugin.Choice.Checked?"선택됨":"선택 안 함");
            SetStatus(plugin,plugin.Choice.Checked?"선택됨":"선택 안 함");
            form.Controls.Add(plugin.Choice); form.Controls.Add(plugin.VersionLabel);
            form.Controls.Add(plugin.StatusLabel);
            form.Controls.Add(new Label { Left=44,Top=y+46,Width=610,Height=20,Text=plugin.Description });
            if(plugin.Name=="OBSChzzkLiveEditor") form.Controls.Add(new Label { Left=44,Top=y+66,Width=610,Height=18,
                Text="OBS Studio 33.0.0 이상이 필요합니다. (현재 OBS: "+(obsVersion ?? "확인 불가")+")",
                ForeColor=plugin.Available?System.Drawing.Color.DimGray:System.Drawing.Color.Firebrick });
        }
        var status=new TextBox { Left=24,Top=330,Width=630,Height=200,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Text="OBS와 치지직 리모컨을 종료하세요.\r\n인터넷 연결이 필요합니다. 정식 설치된 OBS 경로를 자동으로 사용합니다." };
        var install=new Button { Text="선택한 플러그인 설치",Left=424,Top=553,Width=230,Height=36 };
        form.Controls.Add(status); form.Controls.Add(install);
        form.Controls.Add(new Label { Left=24,Top=601,Width=630,Height=20,Text="현재 버전: "+AppVersion,ForeColor=System.Drawing.Color.DimGray });
        bool busy=false, done=false;
        Action<bool> setBusy=value => { busy=value; install.Enabled=!value; foreach (var plugin in Plugins) plugin.Choice.Enabled=!value && plugin.Available; };
        Action<string> append=message => status.AppendText("\r\n" + message);
        Func<Task> refreshVersions=async () => {
            setBusy(true);
            string obs=null;
            try {
                obs=Detect();
                status.Text="설치된 플러그인 확인 위치: " + obs + "\r\n설치 버튼을 누르면 선택한 플러그인의 최신 정식 버전을 받습니다.";
            } catch (Exception ex) {
                status.Text=ex.Message;
            }
            foreach (var plugin in Plugins) {
                SetStatus(plugin,"버전 확인 중...");
                string installed="OBS 경로 확인 필요";
                if (obs!=null) {
                    try { installed=InstalledVersion(plugin,obs); }
                    catch { installed="설치 버전 확인 실패"; }
                }
                plugin.VersionLabel.Text="설치: "+installed+"\r\n최신: 확인 중...";
                try {
                    var release=await Latest(plugin);
                    plugin.VersionLabel.Text="설치: "+installed+" / 최신: "+release.Tag+"\r\n"+CompareVersions(installed,release.Tag);
                } catch {
                    plugin.VersionLabel.Text="설치: "+installed+"\r\n최신: 확인 실패 · 다시 시도하세요";
                }
                SetStatus(plugin,plugin.Choice.Checked?"선택됨":"선택 안 함");
            }
            setBusy(false);
        };
        form.Shown += async (s,e) => {
            setBusy(true);
            if(!await EnsureObsClosed(form)) { busy=false; form.Close(); return; }
            if(await CheckSelfUpdate(form,status)) { busy=false; form.Close(); return; }
            await refreshVersions();
        };
        form.FormClosing += (s,e) => { if (busy) e.Cancel=true; };
        install.Click += async (s,e) => {
            if (done) { form.Close(); return; }
            var selected=Plugins.Where(p=>p.Available && p.Choice.Checked).ToArray();
            if (selected.Length == 0) { status.Text="설치할 플러그인을 하나 이상 선택하세요."; return; }
            setBusy(true);
            if(!await EnsureObsClosed(form)) { busy=false; form.Close(); return; }
            foreach (var plugin in Plugins) SetStatus(plugin,plugin.Choice.Checked?"대기 중":"선택 안 함");
            Plugin active=null;
            string folder=Path.Combine(Path.GetTempPath(),"OBSChzzk-"+Guid.NewGuid().ToString("N"));
            string logs=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OBSChzzk","Logs",DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8));
            try {
                string obs=Detect(); CheckObsClosed(); Directory.CreateDirectory(folder); Directory.CreateDirectory(logs);
                NetworkLogPath=Path.Combine(logs,"network.log");
                status.Text="OBS: " + obs;
                var completed=new List<Plugin>();
                var failures=new List<Plugin>();
                bool proceed=await ProcessIndependently(selected,async plugin=> {
                    active=plugin;
                    SetStatus(plugin,"릴리스 확인 중...");
                    append(plugin.Name+": 최신 릴리스 확인 중...");
                    var release=await Latest(plugin);
                    CheckRequirements(release,obs);
                    append(plugin.Name+" "+release.Tag+": 다운로드 및 SHA-256 확인 중...");
                    SetStatus(plugin,"다운로드·검증 중...");
                    var file=await Download(release,folder);
                    File.AppendAllText(Path.Combine(logs,"releases.txt"),plugin.Name+" "+release.Tag+" "+release.Sha256+" "+release.Url+Environment.NewLine);
                    SetStatus(plugin,"설치 대기");
                    if(!await EnsureObsClosed(form)) return false;
                    SetStatus(plugin,"설치 중...");
                    append(plugin.Name+": 설치 중...");
                    await Task.Run(()=>Run(file,Path.Combine(logs,plugin.Name+".log"),obs,release));
                    append(plugin.Name+": 완료");
                    SetStatus(plugin,"완료");
                    completed.Add(plugin);
                    active=null;
                    return true;
                },(plugin,ex)=> {
                    string phase=plugin.StatusLabel.Text;
                    failures.Add(plugin);
                    try { File.WriteAllText(Path.Combine(logs,plugin.Name+"-error.txt"),phase+Environment.NewLine+ex.ToString()); } catch { }
                    SetStatus(plugin,"실패");
                    append(plugin.Name+" 실패 ["+phase+"]: "+ex.Message+"\r\n다음 플러그인을 계속 진행합니다.");
                    active=null;
                });
                if(!proceed) { busy=false; form.Close(); return; }
                append("\r\n설치 결과: 완료 "+completed.Count+"개 / 실패 "+failures.Count+"개");
                if(failures.Count>0) append("실패한 플러그인: "+String.Join(", ",failures.Select(p=>p.Name))+"\r\n완료된 플러그인은 사용할 수 있습니다. 실패 항목만 나중에 다시 시도하세요.");
                append("로그: "+logs);
                foreach (var plugin in completed) append(plugin.Help);
                var guides=new Panel { Dock=DockStyle.Fill,AutoScroll=true };
                guides.Controls.Add(new Label { Left=2,Top=5,Width=595,Height=30,Text="설치 완료한 플러그인의 사용 설명서(README)를 확인하세요." });
                for (int i=0;i<completed.Count;i++) {
                    var plugin=completed[i];
                    string url="https://github.com/TereBin/"+plugin.Repo+"#readme";
                    var link=new LinkLabel { Left=4,Top=36+i*27,Width=625,Height=25,Text=plugin.Name+" 사용 설명서" };
                    link.Links.Clear();
                    link.Links.Add(0,link.Text.Length,url);
                    link.LinkClicked += (sender,ev) => {
                        try {
                            Process.Start(new ProcessStartInfo((string)ev.Link.LinkData) { UseShellExecute=true });
                            ((LinkLabel)sender).LinkVisited=true;
                        } catch (Exception ex) {
                            MessageBox.Show(form,"브라우저를 열지 못했습니다. 아래 주소를 브라우저에 입력하세요.\r\n"+(string)ev.Link.LinkData+"\r\n\r\n"+ex.Message,"사용 설명서",MessageBoxButtons.OK,MessageBoxIcon.Information);
                        }
                    };
                    guides.Controls.Add(link);
                }
                if (completed.Any(p=>p.Name=="OBSChzzkEvent")) {
                    const string storeUrl="https://chromewebstore.google.com/detail/pakicliihbiimebagjgmmjkalfpejikc?hl=ko";
                    int top=36+completed.Count*27;
                    guides.Controls.Add(new Label { Left=4,Top=top,Width=625,Height=40,
                        Text="Event 확장은 Chrome/Edge에서 웹 스토어로 설치하세요. 이미 설치했다면 유지하세요.\r\n기존 개발자 모드 설치본은 비활성화하거나 제거하고 스토어 설치본만 사용하세요." });
                    var storeLink=new LinkLabel { Left=4,Top=top+41,Width=625,Height=40,
                        Text="Event 브라우저 확장 프로그램 설치 (Chrome 웹 스토어)" };
                    storeLink.Links.Clear();
                    storeLink.Links.Add(0,storeLink.Text.Length);
                    storeLink.LinkClicked += (sender,ev) => {
                        try {
                            Process.Start(new ProcessStartInfo(storeUrl) { UseShellExecute=true });
                            storeLink.LinkVisited=true;
                        } catch (Exception ex) {
                            MessageBox.Show(form,"브라우저를 열지 못했습니다. 아래 주소를 Chrome/Edge에서 열어 주세요.\r\n"+storeUrl+"\r\n\r\n"+ex.Message,"Event 확장 프로그램",MessageBoxButtons.OK,MessageBoxIcon.Information);
                        }
                    };
                    guides.Controls.Add(storeLink);
                }
                var tabs=new TabControl { Left=22,Top=330,Width=635,Height=205 };
                var resultsTab=new TabPage("설치 결과");
                resultsTab.Controls.Add(new TextBox { Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Text=status.Text });
                var guidesTab=new TabPage("사용 설명서");
                guidesTab.Controls.Add(guides);
                tabs.TabPages.Add(resultsTab);
                if(completed.Count>0) tabs.TabPages.Add(guidesTab);
                if(failures.Count==0 && completed.Count>0) tabs.SelectedTab=guidesTab;
                form.Controls.Add(tabs); tabs.BringToFront();
                done=true; install.Text="닫기";
            } catch (Exception ex) {
                string phase=active==null?"사전 확인":active.Name+" / "+active.StatusLabel.Text;
                try { Directory.CreateDirectory(logs); File.WriteAllText(Path.Combine(logs,"error.txt"),phase+Environment.NewLine+ex.ToString()); } catch { }
                if (active!=null) SetStatus(active,"실패");
                foreach (var plugin in selected)
                    if (plugin!=active && plugin.StatusLabel.Text!="완료") SetStatus(plugin,"중단됨");
                append("설치를 완료하지 못했습니다 ["+phase+"]: "+ex.Message);
                append("완료된 설치는 유지됩니다. 문제 해결 후 다시 실행하세요.\r\n로그: "+logs);
            } finally {
                NetworkLogPath=null;
                if(!form.IsDisposed) {
                    setBusy(false);
                    if (done) { foreach (var p in Plugins) p.Choice.Enabled=false; }
                }
                try { if (Directory.Exists(folder)) Directory.Delete(folder,true); } catch { }
            }
        };
        Application.Run(form); return 0;
    }
}
