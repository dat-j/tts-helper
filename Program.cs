// tts-helper — Copyright (C) 2026 dooxx
// This program is free software: you can redistribute it and/or modify it under the terms of the GNU General Public
// License as published by the Free Software Foundation, either version 3 of the License, or (at your option) any later
// version. This program is distributed in the hope that it will be useful, but WITHOUT ANY WARRANTY; without even the
// implied warranty of MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the LICENSE file for details.

using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Mso.TtsHelper;

internal static class Program
{
    public const string Version = "1.0.0";
    public const int ProtocolVersion = 1;

    private static int Main(string[] args)
    {
        if (args.Contains("--version"))
        {
            Console.WriteLine($"tts-helper {Version} (protocol {ProtocolVersion}) — GPL-3.0 — https://github.com/dat-j/tts-helper");
            return 0;
        }

        // Protocol stream = the original stdout handle. Everything else that might print to "stdout" (native libraries,
        // Console.Out) is pointed at stderr BEFORE sherpa-onnx is loaded: its statically linked C runtime picks up the
        // standard handles when the DLL initialises, so stray printf output can never corrupt the binary framing.
        var output = TakeOverStdout();
        Console.SetOut(Console.Error);

        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false), false, 1 << 16);
        using var server = new TtsServer(output);
        Log($"tts-helper {Version} ready");
        string? line;
        while ((line = input.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            if (!server.Handle(line)) break;
        }
        Log("exit");
        return 0;
    }

    internal static void Log(string message) => Console.Error.WriteLine("[tts-helper] " + message);

    private const int StdOutput = -11, StdError = -12;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int nStdHandle, IntPtr handle);

    private static Stream TakeOverStdout()
    {
        if (!OperatingSystem.IsWindows()) return Console.OpenStandardOutput();
        var stdout = GetStdHandle(StdOutput);
        if (stdout == IntPtr.Zero || stdout == new IntPtr(-1)) return Console.OpenStandardOutput();
        SetStdHandle(StdOutput, GetStdHandle(StdError));
        return new FileStream(new SafeFileHandle(stdout, ownsHandle: true), FileAccess.Write, 1);
    }
}
