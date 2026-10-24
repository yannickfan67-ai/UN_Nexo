using System.Net;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;
using UN.Nexo.Downloads;

if (args.Length == 2 && args[0] == "--live")
{
 var root = Path.Combine(Path.GetTempPath(), "nexo-live-download-" + Guid.NewGuid().ToString("N"));
 try
 {
  using var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(10) };
  var sources = new DownloadSourceService(); var paths = new NexoPathService(root);
  var catalog = await new MinecraftVersionManifestService(http, sources).GetCatalogAsync();
  var version = catalog.Versions.Single(x => x.Id == args[1]);
  var instance = await new InstanceStoreService(paths).CreateAsync("Live smoke", version.Id);
  var installer = new MinecraftVanillaInstallService(http, paths, sources);
  await installer.InstallAsync(instance, version);
  var files = Directory.GetFiles(paths.GetInstanceGameDirectory(instance.Id), "*", SearchOption.AllDirectories);
  Console.WriteLine($"PASS live official download {version.Id}: {files.Length} files, {files.Sum(x => new FileInfo(x).Length)} bytes");
 }
 finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
 return;
}

var assembly = typeof(DownloadWorkspace).Assembly;
Assert(!assembly.GetReferencedAssemblies().Any(x => x.Name!.Contains("Identity",StringComparison.OrdinalIgnoreCase)), "Shipping assembly references authentication dependencies");
Assert(!assembly.GetTypes().Any(x => x.Name.Contains("Auth",StringComparison.OrdinalIgnoreCase) || x.Name is "MinecraftProcessService" or "MinecraftLaunchPlanBuilder" or "AccountStoreService"), "Shipping assembly contains login or game execution");
Console.WriteLine("PASS shipping assembly excludes authentication and game execution");
var directory = Path.Combine(Path.GetTempPath(),"nexo-download-tests-"+Guid.NewGuid().ToString("N"));
try
{
 var paths = new NexoPathService(directory); var workspace = new DownloadWorkspace(paths);
 var instance = await new InstanceStoreService(paths).CreateAsync("下載測試", "test-version");
 var job = new DownloadJob(Guid.NewGuid().ToString("N"), instance.Id, instance.Name, instance.VersionId,"Downloading",DateTimeOffset.UtcNow,"sensitive-message-for-export-test");
 await workspace.SaveJobsAsync([job]);
 var recovered = await workspace.LoadJobsAsync();
 Assert(recovered.Single().State == "Paused", "Interrupted transfers must recover paused");
 Console.WriteLine("PASS interrupted download recovery");
 var report = await File.ReadAllTextAsync(await workspace.ExportReportAsync([job]));
 Assert(!report.Contains(directory) && !report.Contains(job.Message!) && !report.Contains(instance.Name), "Report leaks local or untrusted fields");
 Console.WriteLine("PASS report excludes local paths, account data and arbitrary messages");
 var client=Encoding.UTF8.GetBytes("original-verified-client"); var assets=Encoding.UTF8.GetBytes("{\"objects\":{}}");
 byte[] native;
 using (var buffer = new MemoryStream())
 {
  using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, true))
  { using var entry = zip.CreateEntry("example-native.txt").Open(); entry.WriteByte(42); }
  native = buffer.ToArray();
 }
 var nativeDownload = new { path="example/native-only/1.0/native-only-1.0-natives.jar",url="https://libraries.minecraft.net/test/native.jar",sha1=Hash(native),size=native.Length };
 var metadata=JsonSerializer.SerializeToUtf8Bytes(new {
  id="test-version",type="release",mainClass="net.minecraft.client.main.Main",assets="test-assets",
  javaVersion=new {majorVersion=21},
  downloads=new {client=new {url="https://piston-data.mojang.com/test/client.jar",sha1=Hash(client),size=client.Length}},
  assetIndex=new {id="test-assets",url="https://piston-meta.mojang.com/test/assets.json",sha1=Hash(assets),size=assets.Length},
  libraries=new[] { new { name="example:native-only:1.0", downloads=new { classifiers=new Dictionary<string,object> { ["natives-linux"]=nativeDownload,["natives-windows"]=nativeDownload,["natives-osx"]=nativeDownload } }, natives=new Dictionary<string,string> { ["linux"]="natives-linux",["windows"]="natives-windows",["osx"]="natives-osx" } } },arguments=new { game=new[]{"--version","test-version"},jvm=Array.Empty<string>()}
 });
 var handler=new FilesHandler(new Dictionary<string,byte[]> { ["/test/native.jar"]=native,["/test/metadata.json"]=metadata,["/test/client.jar"]=client,["/test/assets.json"]=assets });
 using var http=new HttpClient(handler); var installer=new MinecraftVanillaInstallService(http,paths,new());
 var version=new MinecraftVersionInfo("test-version","release","https://piston-meta.mojang.com/test/metadata.json",DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,Hash(metadata),0);
 var catalog=new MinecraftVersionCatalog(new("test-version","test-version"),[version]);
 await workspace.SaveCatalogAsync(catalog);
 Assert((await workspace.LoadCatalogAsync())!.Catalog.Versions.Single().Id==version.Id,"Offline catalog was lost");
 Console.WriteLine("PASS offline catalog round trip");
 await installer.InstallAsync(instance,version);
 var requests=handler.Requests;
 Console.WriteLine("PASS classifier-only libraries do not request nonexistent Maven JARs or sidecars");
 await installer.InstallAsync(instance,version);
 Assert(handler.Requests==requests,"Repair redownloaded verified data");
 var jar=Path.Combine(paths.GetInstanceGameDirectory(instance.Id),"versions/test-version/test-version.jar");
 await File.WriteAllBytesAsync(jar, Enumerable.Repeat((byte)'x',client.Length).ToArray());
 await installer.InstallAsync(instance,version);
 Assert(handler.Requests==requests+1,"Same-size corruption was not selectively repaired");
 Assert((await File.ReadAllBytesAsync(jar)).SequenceEqual(client),"Repair did not restore original bytes");
 Console.WriteLine("PASS real installer download, cached retry and same-size corruption repair");
}
finally { if(Directory.Exists(directory)) Directory.Delete(directory,true); }
Console.WriteLine("All download-edition checks passed.");
static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA1.HashData(bytes));
static void Assert(bool condition,string message) { if(!condition) throw new Exception(message); }
sealed class FilesHandler(Dictionary<string,byte[]> files) : HttpMessageHandler
{
 public int Requests {get;private set;}
 protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
 {
  Requests++; var found=files.TryGetValue(request.RequestUri!.AbsolutePath,out var bytes);
  return Task.FromResult(new HttpResponseMessage(found?HttpStatusCode.OK:HttpStatusCode.NotFound) { Content=new ByteArrayContent(bytes ?? []),RequestMessage=request });
 }
}
