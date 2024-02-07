// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Reflection;
using System.Text;

namespace TurboXml.Tests;

/// <summary>
/// Tests for the XML parsing using the files in the Valid/Invalid folders.
/// </summary>
[TestClass]
public class XmlFileTests : VerifyBase
{
    [XmlTestSource("Valid")]
    [DataTestMethod]
    public Task TestValidFiles(string folder, string filePath)
    {
        var xml = File.ReadAllText(filePath, Encoding.UTF8);
        return VerifyXml(folder, xml, filePath);
    }

    [XmlTestSource("Invalid")]
    [DataTestMethod]
    public Task TestInvalidFiles(string folder, string filePath)
    {
        var xml = File.ReadAllText(filePath, Encoding.UTF8);
        return VerifyXml(folder, xml, filePath);
    }

    private Task VerifyXml(string folder, string xml, string sourceFile)
    {
        var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        XmlParser.Parse(xml, ref handler);
        var result = handler.Writer.ToString();

        // Check that the stream version is identical
        if (folder == "Valid")
        {
            var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
            var handler2 = new XmlReadPrintHandler() { Writer = new StringWriter() };
            XmlParser.Parse(stream, ref handler2);
            Assert.AreEqual(result, handler2.Writer.ToString());
        }

        var settings = new VerifySettings();
        settings.UseDirectory(folder);
        settings.DisableDiff();
        var fileOutput = Path.GetFileNameWithoutExtension(sourceFile);
        settings.UseFileName(fileOutput);

        return Verify(result, settings);
    }

    private class XmlTestSource : Attribute, ITestDataSource
    {
        private readonly string _folder;

        public XmlTestSource(string folder)
        {
            _folder = folder;
        }

        public IEnumerable<object?[]> GetData(MethodInfo methodInfo)
        {
            var testFolder = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", _folder);
            if (!Directory.Exists(testFolder))
            {
                throw new DirectoryNotFoundException($"Unable to find folder {testFolder}");
            }

            foreach (var file in Directory.EnumerateFiles(testFolder, "*.xml"))
            {
                yield return new object[] { _folder, Path.GetFullPath(file) };
            }
        }

        public string? GetDisplayName(MethodInfo methodInfo, object?[]? data) => data == null ? null : $"{data[0]}-{Path.GetFileNameWithoutExtension((string)data[1]!)}";
    }
}