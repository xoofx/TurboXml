// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text;
using System.Xml;

namespace TurboXml.Tests;

/// <summary>
/// Various tests that cannot be covered by test files
/// </summary>
[TestClass]
public class XmlRuntimeTests
{
    [TestMethod]
    public void TestReadmeSample()
    {
        var xml = "<?xml version=\"1.0\"?><root enabled=\"true\">Hello World!</root>";
        var handler = new MyHandler();
        XmlParser.Parse(xml, ref handler);
        // Will print:
        //
        // BeginTag(1:23): root
        // Attribute(1:28)-(1:36): enabled="true"
        // Content(1:43): Hello World!
        // EndTag(1:57): root
    }

    struct MyHandler : IXmlReadHandler
    {
        public void OnBeginTag(ReadOnlySpan<char> name, int line, int column)
            => Console.WriteLine($"BeginTag({line + 1}:{column + 1}): {name}");

        public void OnEndTagEmpty()
            => Console.WriteLine($"EndTagEmpty");

        public void OnEndTag(ReadOnlySpan<char> name, int line, int column)
            => Console.WriteLine($"EndTag({line + 1}:{column + 1}): {name}");

        public void OnAttribute(ReadOnlySpan<char> name, ReadOnlySpan<char> value, int nameLine, int nameColumn, int valueLine, int valueColumn)
            => Console.WriteLine($"Attribute({nameLine + 1}:{nameColumn + 1})-({valueLine + 1}:{valueColumn + 1}): {name}=\"{value}\"");

        public void OnText(ReadOnlySpan<char> text, int line, int column)
            => Console.WriteLine($"Content({line + 1}:{column + 1}): {text}");
    }

    [TestMethod]
    public void TestNameStartChar()
    {
        // [4] NameStartChar	   ::=   	":" | [A-Z] | "_" | [a-z] | [#xC0-#xD6] | [#xD8-#xF6] | [#xF8-#x2FF] | [#x370-#x37D] | [#x37F-#x1FFF] | [#x200C-#x200D] | [#x2070-#x218F] | [#x2C00-#x2FEF] | [#x3001-#xD7FF] | [#xF900-#xFDCF] | [#xFDF0-#xFFFD] | [#x10000-#xEFFFF]
        var ranges = new (int, int)[]
        {
            (':', ':'),
            ('A', 'Z'),
            ('_', '_'),
            ('a', 'z'),
            (0xc0, 0xd6),
            (0xD8, 0xF6),
            (0xF8, 0x2FF),
            (0x370, 0x37D),
            (0x37F, 0x1FFF),
            (0x200C, 0x200D),
            (0x2070, 0x218F),
            (0x2C00, 0x2FEF),
            (0x3001, 0xD7FF),
            (0xF900, 0xFDCF),
            (0xFDF0, 0xFFFD),
            (0x10000, 0xEFFFF)
        };
        CheckCharRange(ranges.Where(x => x.Item1 < 0x10000).ToArray(), XmlChar.IsNameStartChar);
        CheckCharRange(ranges.Where(x => x.Item1 < 0x10000).ToArray(), XmlChar.IsNameChar);
    }

    [TestMethod]
    public void TestNameChar()
    {
        // [4a] NameStartChar | "-" | "." | [0-9] | #xB7 | [#x0300-#x036F] | [#x203F-#x2040]
        // We don't test NameStartChar as it is already tested in TestNameStartChar
        var ranges = new (int, int)[]
        {
            ('-', '-'),
            ('.', '.'),
            ('0', '9'),
            (0xB7, 0xB7),
            (0x0300, 0x036F),
            (0x203F, 0x2040),
        };
        CheckCharRange(ranges, XmlChar.IsNameChar);
    }

    [TestMethod]
    public void TestEncodingDetection()
    {
        var xml = """<?xml version="1.0"?><root></root>""";
        var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        XmlParser.Parse(xml, ref handler);
        var expecting = NormalizeNewLines(handler.Writer.ToString()!);

        foreach (var pair in new (Encoding, string)[]
                 {
                     (new UTF8Encoding(false), "UTF-8"),
                     (new UTF8Encoding(true), "UTF-8 BOM"),
                     (new UnicodeEncoding(false, false), "UTF-16"),
                     (new UnicodeEncoding(false, true), "UTF-16 BOM"),
                     (new UnicodeEncoding(true, false), "UTF-16 be"),
                     (new UnicodeEncoding(true, true), "UTF-16 be BOM"),
                     (new UTF32Encoding(false, false), "UTF-32"),
                     (new UTF32Encoding(false, true), "UTF-32 BOM"),
                     (new UTF32Encoding(true, false), "UTF-32 be"),
                     (new UTF32Encoding(true, true), "UTF-32 be BOM"),
                 })

        {
            var encoding = pair.Item1;
            var encodingName = pair.Item2;
            var preamble = encoding.GetPreamble();
            var bytesWithoutBom = encoding.GetBytes(xml);
            byte[] bytes = [..preamble, ..bytesWithoutBom];
            var stream = new MemoryStream(bytes);
            handler.Writer = new StringWriter();
            XmlParser.Parse(stream, ref handler);

            var result = NormalizeNewLines(handler.Writer.ToString()!);
            Assert.AreEqual(expecting, result, $"Error with encoding {encodingName}");
        }
    }

    [TestMethod]
    public void TestEndOfLines()
    {
        // Check \r\n
        var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        XmlParser.Parse(GetXml("\r\n"), ref handler);
        var result = NormalizeNewLines(handler.Writer.ToString()!);

        var expecting = """
                        XmlDeclaration(1:2): version="1.0" encoding="utf-8" standalone=""
                        Content(1:39): \r\n
                        Comment(2:5):  comment\r\n 
                        BeginTag(3:6): root
                        Attribute(4:1)-(4:6): attr="hello\nworld"
                        Content(5:8): content\r\n
                        CData(6:10):  \r\n 
                        EndTag(7:7): root
                        """;
        expecting = NormalizeNewLines(expecting);
        Assert.AreEqual(expecting, result);

        // Check \r
        handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        XmlParser.Parse(GetXml("\r"), ref handler);
        result = NormalizeNewLines(handler.Writer.ToString()!);
        expecting = """
                    XmlDeclaration(1:2): version="1.0" encoding="utf-8" standalone=""
                    Content(1:39): \r
                    Comment(2:5):  comment\r 
                    BeginTag(3:6): root
                    Attribute(4:1)-(4:6): attr="hello\nworld"
                    Content(5:8): content\r
                    CData(6:10):  \r 
                    EndTag(7:7): root
                    """;
        expecting = NormalizeNewLines(expecting);
        Assert.AreEqual(expecting, result);

        static string GetXml(string nl)
            => $"<?xml version=\"1.0\" encoding=\"utf-8\"?>{nl}<!-- comment{nl} --><root{nl}attr=\"hello{nl}world\">content{nl}<![CDATA[ {nl} ]]></root>";
    }

    [TestMethod]
    public void TestInvalidCharacterInContent()
    {
        var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        var xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><root>\x00;</root>";
        XmlParser.Parse(xml, ref handler);
        var result = NormalizeNewLines(handler.Writer.ToString()!);
        var expecting = """
                        XmlDeclaration(1:2): version="1.0" encoding="utf-8" standalone=""
                        BeginTag(1:40): root
                        Error(1:45): Invalid character found
                        """;
        expecting = NormalizeNewLines(expecting);
        Assert.AreEqual(expecting, result);
    }

    [TestMethod]
    public void TestInvalidCharacterInName()
    {
        var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        Span<char> xml = stackalloc char[] { '<', (char)0xD800, (char)0x1, 'o', 't', '>', '<', '/', 'r', 'o', 'o', 't', '>' };

        XmlParser.Parse(xml.ToString(), ref handler);
        var result = NormalizeNewLines(handler.Writer.ToString()!);
        var expecting = """
                        Error(1:2): Invalid character found. Expecting a low surrogate
                        """;
        expecting = NormalizeNewLines(expecting);
        Assert.AreEqual(expecting, result);
    }

    [TestMethod]
    public void TestClassHandler()
    {
        // This is just used for coverage
        var handler = new EmptyReadHandler()
        {
            Writer = new StringWriter()
        };
        XmlParser.Parse("<?xml version=\"1.0\"?>", handler);
        var result = NormalizeNewLines(handler.Writer.ToString()!);
        var expecting = "XmlDeclaration(1:2): version=\"1.0\" encoding=\"\" standalone=\"\"";
        Assert.AreEqual(expecting, result);

        handler.Writer = new StringWriter();
        XmlParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes("<?xml version=\"1.0\"?>")), handler);
        result = NormalizeNewLines(handler.Writer.ToString()!);
        Assert.AreEqual(expecting, result);
    }

    [TestMethod]
    public void TestInvalidSurrogate()
    {
        var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        Span<char> xml = stackalloc char[] { (char)0xD800 };

        XmlParser.Parse(xml.ToString(), ref handler);
        var result = NormalizeNewLines(handler.Writer.ToString()!);
        var expecting = """
                        Error(1:1): Invalid end of XML stream
                        """;
        expecting = NormalizeNewLines(expecting);
        Assert.AreEqual(expecting, result);
    }

    [TestMethod]
    public void TestInvalidSurrogate2()
    {
        var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        Span<char> xml = stackalloc char[] { (char)0xD800, (char)0x0001 };

        XmlParser.Parse(xml.ToString(), ref handler);
        var result = NormalizeNewLines(handler.Writer.ToString()!);
        var expecting = """
                        Error(1:1): Invalid character found. Expecting a low surrogate
                        """;
        expecting = NormalizeNewLines(expecting);
        Assert.AreEqual(expecting, result);
    }

    [TestMethod]
    public void TestDefaultError()
    {
        var handler = new EmptyReadHandler();
        Assert.ThrowsException<XmlException>(() =>
            ((IXmlReadHandler)handler).OnError("error", 1, 1));
    }

    private static string NormalizeNewLines(string text)
    {
        return text.ReplaceLineEndings("\n").TrimEnd();
    }

    private static void CheckCharRange((int, int)[] ranges, Func<char, bool> check)
    {
        var builder = new StringBuilder();

        foreach (var range in ranges)
        {
            for (int i = range.Item1; i <= range.Item2; i++)
            {
                var rune = (char)(i);
                if (!check(rune))
                {
                    builder.AppendLine($"Error with {i:X} - Rune Category: {char.GetUnicodeCategory(rune)}");
                }
            }
        }

        var text = builder.ToString();
        Assert.AreEqual("", text);
    }

    /// <summary>
    /// Use a class to test this handler
    /// </summary>
    private class EmptyReadHandler : IXmlReadHandler
    {
        public TextWriter? Writer { get; set; }

        public void OnXmlDeclaration(ReadOnlySpan<char> version, ReadOnlySpan<char> encoding, ReadOnlySpan<char> standalone, int line, int column)
        {
            if (Writer is null) return;
            Writer.WriteLine($"XmlDeclaration({line + 1}:{column + 1}): version=\"{version}\" encoding=\"{encoding}\" standalone=\"{standalone}\"");
        }
    }
}