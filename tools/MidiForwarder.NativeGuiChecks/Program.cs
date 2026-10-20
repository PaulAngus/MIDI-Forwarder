using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using ApplicationTestLab;

// External managed runtime for the existing approved Mac desktop launcher.
// Never replace or re-sign the native .app. This driver performs native AX actions;
// it does not host, patch, or inject code into the product application.
internal static class Program
{
    private const string AX = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    [DllImport("/System/Library/Frameworks/AppKit.framework/AppKit")]
    [return: MarshalAs(UnmanagedType.I1)] private static extern bool NSApplicationLoad();
    [DllImport(AX)][return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrusted();
    [DllImport(CG)][return: MarshalAs(UnmanagedType.I1)] private static extern bool CGPreflightScreenCaptureAccess();
    [DllImport(CG)] private static extern nint CGSessionCopyCurrentDictionary();
    [DllImport(CG)] private static extern nint CGWindowListCopyWindowInfo(uint options, uint relativeTo);
    [DllImport(AX)] private static extern nint AXUIElementCreateApplication(int pid);
    [DllImport(AX)] private static extern int AXUIElementCopyAttributeValue(nint element, nint attribute, out nint value);
    [DllImport(AX)] private static extern int AXUIElementPerformAction(nint element, nint action);
    [DllImport(CF)] private static extern nint CFStringCreateWithCString(nint allocator, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint encoding);
    [DllImport(CF)][return: MarshalAs(UnmanagedType.I1)] private static extern bool CFStringGetCString(nint value, byte[] buffer, nint size, uint encoding);
    [DllImport(CF)] private static extern nint CFArrayGetCount(nint array);
    [DllImport(CF)] private static extern nint CFArrayGetValueAtIndex(nint array, nint index);
    [DllImport(CF)] private static extern nint CFGetTypeID(nint value);
    [DllImport(CF)] private static extern nint CFStringGetTypeID();
    [DllImport(CF)] private static extern nint CFBooleanGetTypeID();
    [DllImport(CF)][return: MarshalAs(UnmanagedType.I1)] private static extern bool CFBooleanGetValue(nint value);
    [DllImport(CF)] private static extern nint CFDictionaryGetValue(nint dictionary, nint key);
    [DllImport(CF)][return: MarshalAs(UnmanagedType.I1)] private static extern bool CFNumberGetValue(nint value, int type, out int result);
    [DllImport(CF)] private static extern nint CFRetain(nint value);
    [DllImport(CF)] private static extern void CFRelease(nint value);
    private static nint Str(string value) => CFStringCreateWithCString(0, value, 0x08000100);
    private static void Release(nint value) { if (value != 0) CFRelease(value); }
    private static nint Attribute(nint element, string name)
    {
        nint key = Str(name);
        try { return AXUIElementCopyAttributeValue(element, key, out nint value) == 0 ? value : 0; }
        finally { Release(key); }
    }
    private static string Text(nint element, string name)
    {
        nint value = Attribute(element, name);
        try
        {
            if (value == 0 || CFGetTypeID(value) != CFStringGetTypeID()) return "";
            byte[] bytes = new byte[8192];
            return CFStringGetCString(value, bytes, bytes.Length, 0x08000100) ? Encoding.UTF8.GetString(bytes).TrimEnd('\0') : "";
        }
        finally { Release(value); }
    }
    private static bool? SessionFlag(string name)
    {
        nint session = CGSessionCopyCurrentDictionary(), key = Str(name);
        try
        {
            nint value = session == 0 ? 0 : CFDictionaryGetValue(session, key);
            return value != 0 && CFGetTypeID(value) == CFBooleanGetTypeID() ? CFBooleanGetValue(value) : null;
        }
        finally { Release(key); Release(session); }
    }
    private static object Preflight() => new
    {
        machine = Environment.MachineName,
        pid = Environment.ProcessId,
        trusted = AXIsProcessTrusted(), screenCapture = CGPreflightScreenCaptureAccess(),
        onConsole = SessionFlag("kCGSSessionOnConsoleKey"), locked = SessionFlag("CGSSessionScreenIsLocked"),
    };
    private static void RequireDesktop()
    {
        if (!AXIsProcessTrusted() || SessionFlag("kCGSSessionOnConsoleKey") != true || SessionFlag("CGSSessionScreenIsLocked") == true)
            throw new InvalidOperationException("Desktop access unavailable. No UI action was performed.");
    }
    private static void Walk(nint element, List<object> rows, int depth, string? selector, List<nint>? matches)
    {
        // AX proxy object addresses can be reused after releasing a children array.
        // Retain each matched element, and bound traversal by depth rather than pointer identity.
        if (depth > 35 || rows.Count > 1200) return;
        string id = Text(element, "AXIdentifier"), title = Text(element, "AXTitle"), role = Text(element, "AXRole");
        rows.Add(new { depth, id, role, title, value = Text(element, "AXValue"), description = Text(element, "AXDescription") });
        if (selector is not null && (id == selector || title == selector)) matches!.Add(CFRetain(element));
        foreach (string name in new[] { "AXChildren" })
        {
            nint children = Attribute(element, name);
            try
            {
                if (children != 0)
                    for (nint i = 0; i < CFArrayGetCount(children); i++) Walk(CFArrayGetValueAtIndex(children, i), rows, depth + 1, selector, matches);
            }
            finally { Release(children); }
        }
    }
    private static object Operate(JsonElement request)
    {
        string op = request.GetProperty("op").GetString()!;
        if (op == "preflight") return Preflight();
        RequireDesktop();
        int pid = request.GetProperty("pid").GetInt32();
        if (op == "capture")
        {
            if (!CGPreflightScreenCaptureAccess()) throw new InvalidOperationException("Screen capture is not approved.");
            nint windows = CGWindowListCopyWindowInfo(1, 0), ownerKey = Str("kCGWindowOwnerPID"), numberKey = Str("kCGWindowNumber");
            int number = 0;
            try
            {
                for (nint i = 0; i < CFArrayGetCount(windows); i++)
                {
                    nint row = CFArrayGetValueAtIndex(windows, i);
                    if (CFNumberGetValue(CFDictionaryGetValue(row, ownerKey), 9, out int owner) && owner == pid)
                    { CFNumberGetValue(CFDictionaryGetValue(row, numberKey), 9, out number); break; }
                }
            }
            finally { Release(windows); Release(ownerKey); Release(numberKey); }
            if (number == 0) throw new InvalidOperationException("No application window found.");
            string path = Path.GetFullPath(request.GetProperty("path").GetString()!);
            var start = new ProcessStartInfo("/usr/sbin/screencapture") { UseShellExecute = false };
            foreach (string arg in new[] { "-x", "-l", number.ToString(System.Globalization.CultureInfo.InvariantCulture), path }) start.ArgumentList.Add(arg);
            using var capture = Process.Start(start)!;
            if (!capture.WaitForExit(10000) || capture.ExitCode != 0) throw new IOException("Screenshot failed.");
            return new { path, bytes = new FileInfo(path).Length };
        }
        nint application = AXUIElementCreateApplication(pid);
        var matches = new List<nint>();
        try
        {
            var rows = new List<object>();
            string? selector = op == "press" ? request.GetProperty("selector").GetString() : null;
            Walk(application, rows, 0, selector, matches);
            if (op == "inspect") return new { pid, rows };
            if (op != "press") throw new ArgumentException("Unknown operation.");
            if (matches.Count != 1) throw new InvalidOperationException($"Expected one {selector} control; found {matches.Count}.");
            nint action = Str("AXPress");
            try
            {
                int error = AXUIElementPerformAction(matches[0], action);
                if (error != 0) throw new IOException($"AXPress failed: {error}");
            }
            finally { Release(action); }
            return new { pid, pressed = selector };
        }
        finally { foreach (nint element in matches) Release(element); Release(application); }
    }
    [STAThread]
    private static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS() || args.Length != 1 || !Path.IsPathFullyQualified(args[0])) return 1;
        if (!NSApplicationLoad()) return 2;
        string root = args[0];
        Directory.CreateDirectory(root);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(35));
        using var lease = HostResourceLease.AcquireAsync(timeout.Token).GetAwaiter().GetResult();
        File.WriteAllText(Path.Combine(root, "ready.json"), JsonSerializer.Serialize(Preflight()));
        int sequence = 1;
        while (!timeout.IsCancellationRequested)
        {
            string input = Path.Combine(root, $"request-{sequence:D4}.json");
            if (!File.Exists(input)) { Thread.Sleep(80); continue; }
            bool quit = false;
            object reply;
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(input));
                quit = document.RootElement.GetProperty("op").GetString() == "quit";
                reply = new { ok = true, result = quit ? new { stopped = true } : Operate(document.RootElement) };
            }
            catch (Exception error) { reply = new { ok = false, error = error.ToString() }; }
            string output = Path.Combine(root, $"reply-{sequence:D4}.json");
            File.WriteAllText(output + ".tmp", JsonSerializer.Serialize(reply));
            File.Move(output + ".tmp", output);
            sequence++;
            if (quit) break;
        }
        return 0;
    }
}
