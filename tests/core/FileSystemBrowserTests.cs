using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using Microsoft.Playwright;
using Xunit;

namespace WebAssistant.CoreTests;

public sealed class FileSystemBrowserTests
{
    [Fact]
    public async Task Browser_ExercisesFilesystemMvpNavigationExternalMutationAndFailures()
    {
        var repo=FindRoot();var temp=Path.Combine(Path.GetTempPath(),"webassistant-browser",Guid.NewGuid().ToString("N"));var root=Path.Combine(temp,"root");var logs=Path.Combine(temp,"logs");Directory.CreateDirectory(root);Directory.CreateDirectory(logs);var port=FreePort();
        var product=Path.Combine(repo,"webassist","src","WebAssistant");var psi=new ProcessStartInfo("dotnet",$"run --no-build --project \"{Path.Combine(product,"WebAssistant.csproj")}\" --configuration Release"){UseShellExecute=false,WorkingDirectory=product};psi.Environment["WebAssistant__Port"]=port.ToString();psi.Environment["WebAssistant__FileSystem__RootDirectory"]=root;psi.Environment["WebAssistant__LogDirectory"]=logs;using var service=Process.Start(psi)!;
        try
        {
            var baseUrl=$"http://127.0.0.1:{port}";await WaitHealthy(baseUrl);using var playwright=await Playwright.CreateAsync();await using var browser=await playwright.Chromium.LaunchAsync(new(){Headless=true});var page=await browser.NewPageAsync();var answers=new Queue<string>();page.Dialog+=async(_,d)=>await d.AcceptAsync(d.Type=="prompt"?answers.Dequeue():null);await page.GotoAsync(baseUrl+"/filesystem.html");
            ILocator Row(string name)=>page.Locator("#filesystem-entries tr").Filter(new(){HasTextString=name});async Task Prompt(string selector,string value){answers.Enqueue(value);await page.ClickAsync(selector);}async Task Visible(string name)=>await Row(name).WaitForAsync();
            Assert.Equal("Root",(await page.Locator("#filesystem-breadcrumb").InnerTextAsync()).Trim());await Prompt("#filesystem-create-directory","dir-a");await Visible("dir-a");await Row("dir-a").Locator("[data-action=open]").ClickAsync();await Prompt("#filesystem-create-directory","nested");await Visible("nested");await Row("nested").Locator("[data-action=open]").ClickAsync();await page.GetByRole(AriaRole.Button,new(){Name="dir-a",Exact=true}).ClickAsync();await page.ClickAsync("#filesystem-root");Assert.True(await page.Locator("#filesystem-up").IsDisabledAsync());Assert.Equal("Root",(await page.Locator("#filesystem-breadcrumb").InnerTextAsync()).Trim());
            var upload=Path.Combine(temp,"a.bin");var bytes="browser-opaque-payload"u8.ToArray();await File.WriteAllBytesAsync(upload,bytes);await page.Locator("#filesystem-upload-input").SetInputFilesAsync(upload);await page.ClickAsync("#filesystem-upload");await Visible("a.bin");var download=await page.RunAndWaitForDownloadAsync(async()=>await Row("a.bin").Locator("[data-action=download]").ClickAsync());Assert.Equal(SHA256.HashData(bytes),SHA256.HashData(await File.ReadAllBytesAsync(await download.PathAsync())));
            answers.Enqueue("b.bin");await Row("a.bin").Locator("[data-action=rename]").ClickAsync();await Visible("b.bin");await Prompt("#filesystem-create-directory","dir-b");await Visible("dir-b");answers.Enqueue("dir-b/b.bin");await Row("b.bin").Locator("[data-action=move]").ClickAsync();await Row("dir-b").Locator("[data-action=open]").ClickAsync();await Visible("b.bin");await Row("b.bin").Locator("[data-action=delete]").ClickAsync();await Row("b.bin").WaitForAsync(new(){State=WaitForSelectorState.Detached});await page.ClickAsync("#filesystem-root");await Row("dir-b").Locator("[data-action=delete]").ClickAsync();
            await Row("dir-a").Locator("[data-action=delete]").ClickAsync();await page.Locator("#filesystem-status").GetByText("directory_not_empty",new(){Exact=false}).WaitForAsync();await Prompt("#filesystem-create-directory","dup");await Visible("dup");await Prompt("#filesystem-create-directory","dup");await page.Locator("#filesystem-status").GetByText("destination_exists",new(){Exact=false}).WaitForAsync();await Prompt("#filesystem-create-empty-file","bad.sh");await page.Locator("#filesystem-status").GetByText("blocked_file_type",new(){Exact=false}).WaitForAsync();
            var external=Path.Combine(root,"external.txt");await File.WriteAllTextAsync(external,"external");await page.ClickAsync("#filesystem-refresh");await Visible("external.txt");var renamed=Path.Combine(root,"renamed.txt");File.Move(external,renamed);await page.ClickAsync("#filesystem-refresh");await Visible("renamed.txt");File.Delete(renamed);await page.ClickAsync("#filesystem-refresh");await Row("renamed.txt").WaitForAsync(new(){State=WaitForSelectorState.Detached});var vanished=Path.Combine(root,"vanish.txt");await File.WriteAllTextAsync(vanished,"x");await page.ClickAsync("#filesystem-refresh");await Visible("vanish.txt");File.Delete(vanished);await Row("vanish.txt").Locator("[data-action=delete]").ClickAsync();await page.Locator("#filesystem-status").GetByText("not_found",new(){Exact=false}).WaitForAsync();
            await Row("dup").Locator("[data-action=delete]").ClickAsync();await Row("dir-a").Locator("[data-action=open]").ClickAsync();await Row("nested").Locator("[data-action=delete]").ClickAsync();await page.ClickAsync("#filesystem-root");await Row("dir-a").Locator("[data-action=delete]").ClickAsync();
        }
        finally{if(!service.HasExited)service.Kill(entireProcessTree:true);try{Directory.Delete(temp,true);}catch{}}
    }

    private static int FreePort(){using var l=new TcpListener(IPAddress.Loopback,0);l.Start();return((IPEndPoint)l.LocalEndpoint).Port;}
    private static async Task WaitHealthy(string url){using var c=new HttpClient();for(var i=0;i<100;i++){try{if((await c.GetAsync(url+"/v1/health")).IsSuccessStatusCode)return;}catch{}await Task.Delay(100);}throw new TimeoutException("WebAssistant browser test server did not start.");}
    private static string FindRoot(){for(var d=new DirectoryInfo(AppContext.BaseDirectory);d is not null;d=d.Parent)if(Directory.Exists(Path.Combine(d.FullName,"webassist"))&&Directory.Exists(Path.Combine(d.FullName,"tests")))return d.FullName;throw new DirectoryNotFoundException();}
}
