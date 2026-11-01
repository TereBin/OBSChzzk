using System;
using System.Web.Script.Serialization;
static class Tests {
    static string Json(string name, string digest, string url, bool prerelease) {
        return new JavaScriptSerializer().Serialize(new { draft=false, prerelease=prerelease, tag_name="v1.2.3", assets=new [] {
            new { name=name, digest=digest, browser_download_url=url, size=123 }
        }});
    }
    static int Main() {
        try { return Check(); } catch (Exception ex) { Console.WriteLine(ex.GetType().Name + ": " + ex.Message); return 1; }
    }
    static int Check() {
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
        var release=Program.ParseRelease(plugin,Json("OBSChzzkPing-Setup.exe",digest,url,false));
        if (release.Tag!="v1.2.3" || release.Size!=123 || release.Sha256.Length!=64) throw new Exception("Valid release failed");
        string[] rejected={ Json("OBSChzzkPing-Setup.exe",null,url,false), Json("OBSChzzkPing-Setup.exe",digest,"https://example.com/setup.exe",false), Json("OBSChzzkPing-windows-x64.zip",digest,url,false), Json("OBSChzzkPing-Setup.exe",digest,url,true) };
        foreach (string json in rejected) {
            bool failed=false;
            try { Program.ParseRelease(plugin,json); } catch { failed=true; }
            if (!failed) throw new Exception("Unsafe release accepted");
        }
        Console.WriteLine("PASS: valid release, missing digest, foreign URL, ZIP-only release, prerelease");
        return 0;
    }
}
