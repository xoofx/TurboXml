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
            Assert.AreEqual(result, handler2.Writer.ToString(), "Result between stream and string is not matching");

            foreach (var options in GetParserOptionsVariations())
            {
                var handler3 = new XmlReadPrintHandler() { Writer = new StringWriter() };
                XmlParser.Parse(xml, ref handler3, options);
                Assert.AreEqual(result, handler3.Writer.ToString(), $"Result between string and SIMD/CheckBeginEndTag is not matching with options {options}");

                // Cast to force using a class
                handler3 = new XmlReadPrintHandler() { Writer = new StringWriter() };
                var handler3Class = (IXmlReadHandler)handler3;
                XmlParser.Parse(xml, handler3Class, options);
                Assert.AreEqual(result, ((XmlReadPrintHandler)handler3Class).Writer.ToString(), $"Result between string and SIMD/CheckBeginEndTag is not matching with options {options}");
                
                var handler4 = new XmlReadPrintHandler() { Writer = new StringWriter() };
                stream.Position = 0;
                XmlParser.Parse(stream, ref handler4, options);
                Assert.AreEqual(result, handler4.Writer.ToString(), $"Result between stream and SIMD/CheckBeginEndTag is not matching with options {options}");

                // Cast to force using a class
                handler4 = new XmlReadPrintHandler() { Writer = new StringWriter() };
                var handler4Class = (IXmlReadHandler)handler4;
                stream.Position = 0;
                XmlParser.Parse(stream, handler4Class, options);
                Assert.AreEqual(result, ((XmlReadPrintHandler)handler4Class).Writer.ToString(), $"Result between stream and SIMD/CheckBeginEndTag is not matching with options {options}");
            }
        }

        var settings = new VerifySettings();
        settings.UseDirectory(folder);
        settings.DisableDiff();
        var fileOutput = Path.GetFileNameWithoutExtension(sourceFile);
        settings.UseFileName(fileOutput);

        return Verify(result, settings);
    }

    private IEnumerable<XmlParserOptions> GetParserOptionsVariations()
    {
        yield return new XmlParserOptions();
        yield return new XmlParserOptions() { Encoding = null, UseSimd = false, CheckBeginEndTag = false };
        yield return new XmlParserOptions() { Encoding = null, UseSimd = true, CheckBeginEndTag = false };
        yield return new XmlParserOptions() { Encoding = null, UseSimd = false, CheckBeginEndTag = true };
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