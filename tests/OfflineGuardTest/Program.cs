using System;
using System.Net;
using System.Reflection;

// Smoke test: name resolution works before the guard and fails after it, in this process only.
static bool Resolves(string host)
{
    try { return Dns.GetHostAddresses(host).Length > 0; } catch (Exception) { return false; }
}

const string host = "example.com";
var before = Resolves(host);
var guard = typeof(Doorstop.Entrypoint).Assembly.GetType("OpenFairway.Core.OfflineGuard")!;
var ok = (bool)guard.GetMethod("Enforce", BindingFlags.Public | BindingFlags.Static)!.Invoke(null, [new[] { host }])!;
var after = Resolves(host);
bool asyncAfter;
try { asyncAfter = Dns.GetHostAddressesAsync("example.org").GetAwaiter().GetResult().Length > 0; } catch (Exception) { asyncAfter = false; }
Console.WriteLine($"before={before} enforce={ok} after={after} asyncAfter={asyncAfter}");
return ok && !after && !asyncAfter ? 0 : 1;
