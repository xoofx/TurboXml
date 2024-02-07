// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics;
using System.Text.RegularExpressions;

namespace TurboXml.Tests;

/// <summary>
/// Helper class to get information from the .NET SDK
/// </summary>
internal static class DotNetHelper
{
    /// <summary>
    /// Gets all the XML MSBuild targets and props files from the latest SDK installed.
    /// </summary>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="DirectoryNotFoundException"></exception>
    public static IEnumerable<string> GetTargetsAndPropsFromLatestSdkInstalled()
    {
        var process = new Process();

        process.StartInfo.FileName = "dotnet";
        process.StartInfo.Arguments = "--list-sdks";
        process.StartInfo.RedirectStandardOutput = true;
        process.Start();

        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit();

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0)
        {
            yield break;
        }
        var regex = new Regex(@"^(\d+[^\s]+)\s*\[(.*)\]");
        var match = regex.Match(lines[^1]);
        if (!match.Success)
        {
            throw new InvalidOperationException($"Unable to parse the last line of `dotnet --list-sdks` output: {lines[^1]}");
        }

        var sdkVersion = match.Groups[1].Value;
        var sdkFolder = Path.Combine(match.Groups[2].Value, sdkVersion);

        if (!Directory.Exists(sdkFolder))
        {
            throw new DirectoryNotFoundException($"Unable to find folder {sdkFolder}");
        }

        foreach (var file in Directory.EnumerateFiles(sdkFolder, "*.targets", SearchOption.AllDirectories))
        {
            yield return Path.GetFullPath(file);
        }

        foreach (var file in Directory.EnumerateFiles(sdkFolder, "*.props", SearchOption.AllDirectories))
        {
            yield return Path.GetFullPath(file);
        }
    }
}