// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace TurboXml.Tests;

/// <summary>
/// This test is using the `dotnet --list-sdks` command to find all installed SDKs and then parse all *.targets and *.props files.
///
/// If there are any errors, it will fail the test.
/// </summary>
/// <remarks>
/// This test could fail in weird ways with an SDK that contains invalid XML files. Let's see if it happens in practice.
/// This test is not used for checking the coverage.
/// </remarks>
[TestClass]
[ExcludeFromCodeCoverage]
public class XmlCheckDotNetSDKFiles : VerifyBase
{
    [XmlSdkSource]
    [DataTestMethod]
    public void TestValidFiles(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        var handler = new XmlReadPrintErrorOnlyHandler() { Writer = new StringWriter() };
        XmlParser.Parse(stream, ref handler);
        var result = handler.Writer.ToString();

        // We should not have any error when parsing these files
        Assert.AreEqual(string.Empty, result);
    }

    private class XmlSdkSource : Attribute, ITestDataSource
    {
        public IEnumerable<object?[]> GetData(MethodInfo methodInfo) => DotNetHelper.GetTargetsAndPropsFromLatestSdkInstalled().Select(file => new object[] { file });
        public string? GetDisplayName(MethodInfo methodInfo, object?[]? data) => data == null ? null : (string)data[0]!;
    }

    public readonly struct XmlReadPrintErrorOnlyHandler : IXmlReadHandler
    {
        public required TextWriter Writer { get; init; }

        public void OnError(string message, int line, int column)
        {
            Writer.WriteLine($"Error({line + 1}:{column + 1}): {message}");
        }
    }
}