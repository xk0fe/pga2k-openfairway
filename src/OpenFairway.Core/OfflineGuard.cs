using System;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace OpenFairway.Core;

/// <summary>
/// Takes the game process offline without a firewall rule or admin rights: the Windows name-resolution
/// functions are made to answer "host not found" inside this process only. That is the same condition the
/// game already handles when the PC has no connection. Steam itself is a separate process and is unaffected.
/// </summary>
internal static unsafe class OfflineGuard
{
    private const uint PageExecuteReadWrite = 0x40;

    // mov eax, 11001 (WSAHOST_NOT_FOUND); ret   - these functions return the error code directly
    private static readonly byte[] ReturnHostNotFound = [0xB8, 0xF9, 0x2A, 0x00, 0x00, 0xC3];
    // xor eax, eax; ret                         - gethostbyname returns a null pointer on failure
    private static readonly byte[] ReturnNull = [0x33, 0xC0, 0xC3];

    [DllImport("kernel32", SetLastError = true)]
    private static extern bool VirtualProtect(IntPtr address, nuint size, uint newProtect, out uint oldProtect);

    [DllImport("kernel32")]
    private static extern bool FlushInstructionCache(IntPtr process, IntPtr address, nuint size);

    [DllImport("kernel32")]
    private static extern IntPtr GetCurrentProcess();

    /// <summary>Blocks name resolution and verifies it. Returns false if the process could not be isolated.</summary>
    public static bool Enforce(string[] officialHosts)
    {
        try
        {
            var winsock = NativeLibrary.Load("ws2_32.dll");
            foreach (var name in new[] { "getaddrinfo", "GetAddrInfoW", "GetAddrInfoExW", "GetAddrInfoExA" })
                Patch(winsock, name, ReturnHostNotFound);
            Patch(winsock, "gethostbyname", ReturnNull);
        }
        catch (Exception e)
        {
            Log.Error("Could not isolate the process from the network: " + e.Message);
            return false;
        }

        foreach (var host in officialHosts)
        {
            if (!Resolves(host)) continue;
            Log.Error($"'{host}' still resolves after isolation.");
            return false;
        }
        return true;
    }

    private static void Patch(IntPtr module, string export, byte[] code)
    {
        if (!NativeLibrary.TryGetExport(module, export, out var address)) return;
        if (!VirtualProtect(address, (nuint)code.Length, PageExecuteReadWrite, out var old))
            throw new InvalidOperationException($"VirtualProtect failed for {export} (error {Marshal.GetLastWin32Error()}).");
        Marshal.Copy(code, 0, address, code.Length);
        VirtualProtect(address, (nuint)code.Length, old, out _);
        FlushInstructionCache(GetCurrentProcess(), address, (nuint)code.Length);
    }

    private static bool Resolves(string host)
    {
        try
        {
            return Dns.GetHostAddresses(host).Length > 0;
        }
        catch (SocketException)
        {
            return false;
        }
    }
}
