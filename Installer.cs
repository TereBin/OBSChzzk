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

[assembly: AssemblyVersion("1.1.12.0")]
[assembly: AssemblyFileVersion("1.1.12.0")]
[assembly: AssemblyInformationalVersion("1.1.12")]

class Plugin {
    public string Name, Repo, Description, Dll, Help;
    public CheckBox Choice;
    public Label VersionLabel;
    public Label StatusLabel;
    public bool Available=true;
}
class Release {
    public Plugin Plugin;
    public string Tag, Url, Sha256;
    public long Size;
}
static class Program {
    public const string AppVersion="1.1.12";
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
        return new Release { Plugin=plugin, Tag=(string)data["tag_name"], Url=url, Sha256=digest.Substring(7), Size=Convert.ToInt64(asset["size"]) };
    }
    static async Task<Release> Latest(Plugin plugin) {
        Release cached;
        if (Releases.TryGetValue(plugin.Repo,out cached)) return cached;
        if (!useReleasePages) {
            using (var response=await Client.GetAsync("https://api.github.com/repos/TereBin/"+plugin.Repo+"/releases/latest")) {
                if ((int)response.StatusCode==403 || (int)response.StatusCode==429) useReleasePages=true;
                else {
                    response.EnsureSuccessStatusCode();
                    cached=ParseRelease(plugin,await response.Content.ReadAsStringAsync());
                    Releases[plugin.Repo]=cached;
                    return cached;
                }
            }
        }
        cached=await LatestFromPage(plugin);
        Releases[plugin.Repo]=cached;
        return cached;
    }
    static async Task<Release> LatestFromPage(Plugin plugin) {
        string root="https://github.com/TereBin/"+plugin.Repo;
        string tag;
        using (var response=await Client.GetAsync(root+"/releases/latest",HttpCompletionOption.ResponseHeadersRead)) {
            response.EnsureSuccessStatusCode();
            string target=response.RequestMessage.RequestUri.AbsoluteUri;
            string prefix=root+"/releases/tag/";
            if (!target.StartsWith(prefix,StringComparison.Ordinal)) throw new Exception("최신 릴리스 주소를 확인할 수 없습니다.");
            tag=Uri.UnescapeDataString(target.Substring(prefix.Length));
        }
        string html=await Client.GetStringAsync(root+"/releases/expanded_assets/"+Uri.EscapeDataString(tag));
        string assetPrefix="/TereBin/"+plugin.Repo+"/releases/download/";
        var urls=System.Text.RegularExpressions.Regex.Matches(html,"href=\"([^\"]+)\"").Cast<System.Text.RegularExpressions.Match>()
            .Select(m=>WebUtility.HtmlDecode(m.Groups[1].Value)).Where(u=>u.StartsWith(assetPrefix,StringComparison.Ordinal))
            .Select(u=>"https://github.com"+u).Distinct().ToArray();
        var setups=urls.Where(u=>Uri.UnescapeDataString(new Uri(u).Segments.Last()).StartsWith(plugin.Name+"-",StringComparison.OrdinalIgnoreCase) && u.EndsWith("setup.exe",StringComparison.OrdinalIgnoreCase)).ToArray();
        var sums=urls.Where(u=>u.EndsWith("/SHA256SUMS.txt",StringComparison.Ordinal)).ToArray();
        if (setups.Length!=1 || sums.Length!=1) throw new Exception(plugin.Name+": 공식 설치 EXE 또는 체크섬 파일을 확인할 수 없습니다.");
        string name=Uri.UnescapeDataString(new Uri(setups[0]).Segments.Last());
        string checksums=await Client.GetStringAsync(sums[0]);
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
        using (var response = await Client.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead)) {
            response.EnsureSuccessStatusCode();
            using (var input = await response.Content.ReadAsStreamAsync())
            using (var output = File.Create(file)) await input.CopyToAsync(output);
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
    static void CheckObsClosed() {
        if (Process.GetProcessesByName("obs64").Length != 0) throw new Exception("OBS Studio를 완전히 종료한 후 다시 설치하세요.");
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
                foreach (var plugin in Plugins) {
                    var release=Latest(plugin).GetAwaiter().GetResult();
                    var file=Download(release,args[1]).GetAwaiter().GetResult();
                    File.WriteAllText(file + ".verified.txt", release.Tag + "\n" + release.Url + "\n" + Hash(file));
                }
                return 0;
            } catch (Exception ex) { File.WriteAllText(Path.Combine(args[1],"error.txt"),ex.ToString()); return 1; }
        }
        Application.EnableVisualStyles();
        string obsVersion=null;
        try { obsVersion=FileVersionInfo.GetVersionInfo(Path.Combine(Detect(),@"bin\64bit\obs64.exe")).FileVersion; } catch { }
        var form=new Form { Text="OBSChzzk 통합 설치 "+AppVersion, ClientSize=new System.Drawing.Size(680,610), StartPosition=FormStartPosition.CenterScreen, FormBorderStyle=FormBorderStyle.FixedDialog, MaximizeBox=false, AutoScaleMode=AutoScaleMode.Dpi };
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
            if(await CheckSelfUpdate(form,status)) { busy=false; form.Close(); return; }
            await refreshVersions();
        };
        form.FormClosing += (s,e) => { if (busy) e.Cancel=true; };
        install.Click += async (s,e) => {
            if (done) { form.Close(); return; }
            var selected=Plugins.Where(p=>p.Available && p.Choice.Checked).ToArray();
            if (selected.Length == 0) { status.Text="설치할 플러그인을 하나 이상 선택하세요."; return; }
            setBusy(true);
            foreach (var plugin in Plugins) SetStatus(plugin,plugin.Choice.Checked?"대기 중":"선택 안 함");
            Plugin active=null;
            string folder=Path.Combine(Path.GetTempPath(),"OBSChzzk-"+Guid.NewGuid().ToString("N"));
            string logs=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"OBSChzzk","Logs",DateTime.Now.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8));
            try {
                string obs=Detect(); CheckObsClosed(); Directory.CreateDirectory(folder); Directory.CreateDirectory(logs);
                status.Text="OBS: " + obs;
                var packages=new List<Tuple<Release,string>>();
                foreach (var plugin in selected) {
                    active=plugin;
                    SetStatus(plugin,"릴리스 확인 중...");
                    append(plugin.Name+": 최신 릴리스 확인 중...");
                    var release=await Latest(plugin);
                    CheckRequirements(release,obs);
                    append(plugin.Name+" "+release.Tag+": 다운로드 및 SHA-256 확인 중...");
                    SetStatus(plugin,"다운로드·검증 중...");
                    var file=await Download(release,folder);
                    packages.Add(Tuple.Create(release,file));
                    SetStatus(plugin,"설치 대기");
                }
                active=null;
                File.WriteAllLines(Path.Combine(logs,"releases.txt"),packages.Select(p=>p.Item1.Plugin.Name+" "+p.Item1.Tag+" "+p.Item1.Sha256+" "+p.Item1.Url));
                // Complete every selected download before changing the OBS installation.
                foreach (var package in packages) {
                    var plugin=package.Item1.Plugin;
                    active=plugin;
                    SetStatus(plugin,"설치 중...");
                    append(plugin.Name+": 설치 중...");
                    await Task.Run(()=>Run(package.Item2,Path.Combine(logs,plugin.Name+".log"),obs,package.Item1));
                    append(plugin.Name+": 완료");
                    SetStatus(plugin,"완료");
                    active=null;
                }
                append("\r\n선택한 플러그인 설치가 완료되었습니다. OBS를 직접 실행하세요.");
                foreach (var plugin in selected) append(plugin.Help);
                var guides=new Panel { Left=22,Top=330,Width=635,Height=200 };
                guides.Controls.Add(new Label { Left=2,Top=5,Width=625,Height=30,Text="설치 완료! OBS를 실행하기 전 사용 설명서(README)를 확인하세요." });
                for (int i=0;i<selected.Length;i++) {
                    var plugin=selected[i];
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
                if (selected.Any(p=>p.Name=="OBSChzzkEvent")) {
                    const string storeUrl="https://chromewebstore.google.com/detail/pakicliihbiimebagjgmmjkalfpejikc?hl=ko";
                    int top=36+selected.Length*27;
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
                form.Controls.Add(guides); guides.BringToFront();
                done=true; install.Text="닫기";
            } catch (Exception ex) {
                if (active!=null) SetStatus(active,"실패");
                foreach (var plugin in selected)
                    if (plugin!=active && plugin.StatusLabel.Text!="완료") SetStatus(plugin,"중단됨");
                append("설치를 완료하지 못했습니다: "+ex.Message);
                append("완료된 설치는 유지됩니다. 문제 해결 후 다시 실행하세요.\r\n로그: "+logs);
            } finally {
                setBusy(false);
                if (done) { foreach (var p in Plugins) p.Choice.Enabled=false; }
                try { if (Directory.Exists(folder)) Directory.Delete(folder,true); } catch { }
            }
        };
        Application.Run(form); return 0;
    }
}
