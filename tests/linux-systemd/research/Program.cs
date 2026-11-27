using System.Security.Cryptography;
using System.Text;
using NAPS2.Images.Gtk;
using NAPS2.Scan;

#pragma warning disable CA2252

// Исследовательский пробник, не часть продукта и не сетевой эмулятор.
if (args.Length != 1) throw new ArgumentException("Ожидается каталог установленного продукта.");
var root = Environment.GetEnvironmentVariable("SANE_CONFIG_DIR")
    ?? throw new InvalidOperationException("Не задан изолированный SANE_CONFIG_DIR.");
if (!Path.GetFileName(root).StartsWith("webassistant-b-managed.", StringComparison.Ordinal))
    throw new InvalidOperationException("Пробник требует собственный временный каталог.");
foreach (var assembly in new[] { typeof(ScanController).Assembly, typeof(GtkImageContext).Assembly })
{
    var name = Path.GetFileName(assembly.Location);
    var probeHash = SHA256.HashData(File.ReadAllBytes(assembly.Location));
    var productHash = SHA256.HashData(File.ReadAllBytes(Path.Combine(args[0], name)));
    Require(probeHash.SequenceEqual(productHash), "Не совпала сборка " + name);
    Console.WriteLine($"B_ALT_SDK_ASSEMBLY {name} SHA256={Convert.ToHexString(probeHash)} MATCH");
}

Directory.CreateDirectory(Path.Combine(root, "dll.d"));
File.WriteAllText(Path.Combine(root, "dll.conf"), "# empty\n");
File.WriteAllText(Path.Combine(root, "airscan.conf"),
    "[devices]\n\"Fixture\" = http://192.0.2.1/eSCL, eSCL\n[options]\ndiscovery = disable\n");
var registration = Path.Combine(root, "dll.d", "airscan");
File.WriteAllText(registration, "airscan\n");

using var context = new ScanningContext(new GtkImageContext());
var controller = new ScanController(context);
static ScanOptions Ending() => new()
{
    Driver = Driver.Sane,
    SaneOptions = new SaneOptions { KeepInitialized = false }
};

var baseline = await controller.GetDeviceList(Driver.Sane);
var nativeId = SingleFixture(baseline);
File.WriteAllText(registration, "# airscan\n");
Require(SingleFixture(await controller.GetDeviceList(Ending())) == nativeId, "Неожиданная исходная сессия");
Require((await controller.GetDeviceList(Driver.Sane)).Count == 0, "Ожидался пустой список");

File.WriteAllText(registration, "airscan\n");
var live = await controller.GetDeviceList(Driver.Sane);
using var otherContext = new ScanningContext(new GtkImageContext());
var other = new ScanController(otherContext);
var freshContext = await other.GetDeviceList(Driver.Sane);
var ending = await controller.GetDeviceList(Ending());
var refreshed = await controller.GetDeviceList(Driver.Sane);
var refreshedId = SingleFixture(refreshed);
var publicId = PublicId(nativeId);
Console.WriteLine($"B_ALT_SDK_MATRIX baseline={nativeId} live={live.Count} new_context={freshContext.Count} ending={ending.Count} next={refreshed.Count}");
Console.WriteLine($"B_ALT_SDK_ID before={publicId} after={PublicId(refreshedId)}");
Require(live.Count == 0 && freshContext.Count == 0 && ending.Count == 0, "Предсказание старой сессии не подтвердилось");
Require(nativeId == refreshedId && publicId == "wa2-sane-kB7oUFx_zHPkiRHt", "Изменилась идентичность");
for (var i = 0; i < 2; i++)
{
    await controller.GetDeviceList(Ending());
    Require(SingleFixture(await controller.GetDeviceList(Driver.Sane)) == nativeId, "Повтор изменил идентичность");
}
await controller.GetDeviceList(Ending());
Console.WriteLine("B_ALT_SDK_MATRIX=PASS");

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static string SingleFixture(List<ScanDevice> devices)
{
    Require(devices.Count == 1 && devices[0].ID == "airscan:e0:Fixture", "Неожиданный список AirScan");
    return devices[0].ID;
}
// Независимая проекция для сравнения с фактическим HTTP ID из B1.
static string PublicId(string nativeId) => "wa2-sane-" +
    Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes("sane\0" + nativeId)), 0, 12)
        .Replace('+', '-').Replace('/', '_');
