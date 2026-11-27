// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
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

    [TestMethod]
    public void TestXPacketProcessingInstructionsAreIgnored()
    {
        var xml = "<?xpacket begin=\"id\" id=\"W5M0MpCehiHzreSzNTczkc9d\"?><x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/><?xpacket end=\"w\"?>";

        var result = ParseEvents(xml);
        var expecting = """
                        BeginTag(x:xmpmeta)
                        Attribute(xmlns:x="adobe:ns:meta/")
                        EndTagEmpty
                        """;

        Assert.AreEqual(NormalizeNewLines(expecting), result);
    }

    [TestMethod]
    public void TestXmlStylesheetProcessingInstructionIsIgnored()
    {
        var xml = """<?xml version="1.0"?><?xml-stylesheet type="text/xsl" href="style.xsl"?><root>ok</root>""";

        var result = ParseEvents(xml);
        var expecting = """
                        XmlDeclaration(version="1.0", encoding="", standalone="")
                        BeginTag(root)
                        Content(ok)
                        EndTag(root)
                        """;

        Assert.AreEqual(NormalizeNewLines(expecting), result);
    }

    [TestMethod]
    public void TestProcessingInstructionInsideElementContentIsIgnored()
    {
        var xml = """<root><?custom keep="ignored"?><child /></root>""";

        var result = ParseEvents(xml);
        var expecting = """
                        BeginTag(root)
                        BeginTag(child)
                        EndTagEmpty
                        EndTag(root)
                        """;

        Assert.AreEqual(NormalizeNewLines(expecting), result);
    }

    [TestMethod]
    public void TestProcessingInstructionDataCanReconstructSource()
    {
        const string xml = "<?custom \tdata?\r\nhere?>";
        var handler = new ProcessingInstructionHandler();

        XmlParser.Parse(xml, handler);

        Assert.HasCount(1, handler.Instructions);
        var instruction = handler.Instructions[0];
        Assert.AreEqual("custom", instruction.Target);
        Assert.AreEqual(" \tdata?\r\nhere", instruction.Data);
        Assert.AreEqual(xml, $"<?{instruction.Target}{instruction.Data}?>");
        Assert.AreEqual(0, instruction.Line);
        Assert.AreEqual(1, instruction.Column);
    }

    [TestMethod]
    public void TestProcessingInstructionWithoutDataIsReported()
    {
        var handler = new ProcessingInstructionHandler();

        XmlParser.Parse("<?before?>", handler);

        Assert.HasCount(1, handler.Instructions);
        Assert.AreEqual("before", handler.Instructions[0].Target);
        Assert.AreEqual(string.Empty, handler.Instructions[0].Data);
    }

    [TestMethod]
    public void TestProcessingInstructionInsideElementIsReported()
    {
        var handler = new ProcessingInstructionHandler();

        XmlParser.Parse("""<root><?custom keep="data"?><child /></root>""", handler);

        CollectionAssert.AreEqual(new[] { "root", "child" }, handler.BeginTags);
        Assert.HasCount(1, handler.Instructions);
        Assert.AreEqual("custom", handler.Instructions[0].Target);
        Assert.AreEqual(" keep=\"data\"", handler.Instructions[0].Data);
    }

    [TestMethod]
    public void TestMalformedProcessingInstructionIsRejected()
    {
        var result = ParseEvents("<root><?custom!?></root>");
        var expecting = """
                        BeginTag(root)
                        Error(Invalid processing instruction. Expecting whitespace or ?> after the target)
                        """;

        Assert.AreEqual(NormalizeNewLines(expecting), result);
    }

    [TestMethod]
    public void TestXmlDeclarationDoesNotReportProcessingInstruction()
    {
        var handler = new ProcessingInstructionHandler();

        XmlParser.Parse("""<?xml version="1.0"?><root/>""", handler);

        Assert.AreEqual(1, handler.XmlDeclarationCount);
        Assert.IsEmpty(handler.Instructions);
    }

    [TestMethod]
    public void TestDocumentTypeDeclarationIsRejectedByDefault()
    {
        var result = ParseEvents("""<!DOCTYPE root SYSTEM "file:///does-not-exist.dtd"><root/>""");

        Assert.AreEqual("Error(Unsupported XML directive starting with !)", result);
    }

    [TestMethod]
    public void TestDocumentTypeDeclarationCanBeIgnoredFromStringsAndStreams()
    {
        var options = new XmlParserOptions { IgnoreDtd = true };
        var documentTypeDeclarations = new[]
        {
            "<!DOCTYPE root>",
            """<!DOCTYPE root SYSTEM "file:///does-not-exist.dtd">""",
            """<!DOCTYPE root PUBLIC "-//Example//DTD Root 1.0//EN" "file:///does-not-exist.dtd">""",
            """<!DOCTYPE root [<!ELEMENT root (#PCDATA)><!ENTITY ignored "]>"><!-- ] --><?dtd-pi data?>]>""",
        };
        var expected = NormalizeNewLines(
            """
            BeginTag(root)
            EndTagEmpty
            """);

        foreach (var documentTypeDeclaration in documentTypeDeclarations)
        {
            var xml = $"{documentTypeDeclaration}<root/>";
            Assert.AreEqual(expected, ParseEvents(xml, options), documentTypeDeclaration);
            Assert.AreEqual(expected, ParseStreamEvents(xml, options), documentTypeDeclaration);
        }
    }

    [TestMethod]
    public void TestProcessingInstructionInsideIgnoredDocumentTypeIsNotReported()
    {
        var handler = new ProcessingInstructionHandler();
        const string xml = "<!DOCTYPE root [<?dtd-pi ignored?>]><?document-pi reported?><root/>";

        XmlParser.Parse(xml, handler, new XmlParserOptions { IgnoreDtd = true });

        Assert.HasCount(1, handler.Instructions);
        Assert.AreEqual("document-pi", handler.Instructions[0].Target);
        Assert.AreEqual(" reported", handler.Instructions[0].Data);
    }

    [TestMethod]
    public void TestIgnoredDocumentTypeDoesNotDeclareEntities()
    {
        var result = ParseEvents(
            """<!DOCTYPE root [<!ENTITY value "expanded">]><root>&value;</root>""",
            new XmlParserOptions { IgnoreDtd = true });

        Assert.AreEqual(
            NormalizeNewLines(
                """
                BeginTag(root)
                Error(Invalid entity name. Only &lt; or &gt; or &amp; or &apos; or &quot; are supported)
                """),
            result);
    }

    [TestMethod]
    public void TestMalformedDocumentTypeDeclarationsAreRejectedWhenIgnored()
    {
        var options = new XmlParserOptions { IgnoreDtd = true };
        var documents = new[]
        {
            "<!DOCTYPEroot><root/>",
            "<!DOCTYPE root SYSTEM><root/>",
            "<!DOCTYPE root [<!ELEMENT root ANY><root/>",
            "<root/><!DOCTYPE root>",
        };

        foreach (var document in documents)
        {
            var result = ParseEvents(document, options);
            Assert.IsTrue(result.Contains("Error(", StringComparison.Ordinal), result);
        }
    }

    [TestMethod]
    public void TestXmlDeclarationMustRemainFirstAfterProcessingInstruction()
    {
        var xml = """<?xpacket begin="id"?><?xml version="1.0"?><root/>""";

        var result = ParseEvents(xml);
        var expecting = """Error(Invalid XML declaration. It must be the first node in the document)""";

        Assert.AreEqual(expecting, result);
    }

    [TestMethod]
    public void TestCommonCharSimd()
    {
        if (!Vector128.IsHardwareAccelerated) return;

        //                012345670123456701234567012345670123456701234567012345670123456701234567
        var validNames = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ_0123456789:--------";

        var span = MemoryMarshal.Cast<char, Vector128<ushort>>(validNames.AsSpan());
        for (int i = 0; i < span.Length; i++)
        {
            Assert.IsTrue(XmlChar.IsCommonNameChar(span[i]), "Characters should match CommonNameChar");
        }

        //                  012345670123456701234567012345670123456701234567012345670123456701234567
        var invalidNames = ">=1abcde";
        span = MemoryMarshal.Cast<char, Vector128<ushort>>(invalidNames.AsSpan());
        Assert.IsFalse(XmlChar.IsCommonNameChar(span[0]), "Character should not match CommonNameChar");
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
                        Content(1:39): \n
                        Comment(2:5):  comment\n 
                        BeginTag(3:6): root
                        Attribute(4:1)-(4:6): attr="hello\nworld"
                        Content(5:8): content\n
                        CData(6:10):  \n 
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
                    Content(1:39): \n
                    Comment(2:5):  comment\n 
                    BeginTag(3:6): root
                    Attribute(4:1)-(4:6): attr="hello\nworld"
                    Content(5:8): content\n
                    CData(6:10):  \n 
                    EndTag(7:7): root
                    """;
        expecting = NormalizeNewLines(expecting);
        Assert.AreEqual(expecting, result);

        static string GetXml(string nl)
            => $"<?xml version=\"1.0\" encoding=\"utf-8\"?>{nl}<!-- comment{nl} --><root{nl}attr=\"hello{nl}world\">content{nl}<![CDATA[ {nl} ]]></root>";
    }

    [TestMethod]
    public void TestColumnAfterLongEndTag()
    {
        // A name of 40 characters goes through the vectorized end tag comparison
        var name = new string('n', 40);
        var xml = $"<a><{name}></{name}><b/></a>";
        var expecting = $"""
                         BeginTag(1:2): a
                         BeginTag(1:5): {name}
                         EndTag(1:48): {name}
                         BeginTag(1:90): b
                         EndTagEmpty
                         EndTag(1:95): a
                         """;
        expecting = NormalizeNewLines(expecting);

        var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        XmlParser.Parse(xml, ref handler);
        Assert.AreEqual(expecting, NormalizeNewLines(handler.Writer.ToString()!));

        handler.Writer = new StringWriter();
        XmlParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), ref handler);
        Assert.AreEqual(expecting, NormalizeNewLines(handler.Writer.ToString()!));
    }

    [TestMethod]
    public void TestLongAttributeValueAndTextGrowBuffer()
    {
        // 1000 characters are more than the first buffer of the parser, so the vectorized appends grow it several times
        var value = string.Concat(Enumerable.Range(0, 100).Select(i => $"value{i:D5}"));
        var text = string.Concat(Enumerable.Range(0, 100).Select(i => $"text{i:D6}"));
        var xml = $"<a b=\"{value}\">{text}</a>";
        var expecting = $"""
                         BeginTag(1:2): a
                         Attribute(1:4)-(1:6): b="{value}"
                         Content(1:1009): {text}
                         EndTag(1:2011): a
                         """;
        expecting = NormalizeNewLines(expecting);

        var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        XmlParser.Parse(xml, ref handler);
        Assert.AreEqual(expecting, NormalizeNewLines(handler.Writer.ToString()!));

        handler.Writer = new StringWriter();
        XmlParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), ref handler);
        Assert.AreEqual(expecting, NormalizeNewLines(handler.Writer.ToString()!));
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
    public void TestColumnCountsSurrogatePairAsOneColumn()
    {
        // 𝄞 (U+1D11E) and 𐀀 (U+10000) are each one surrogate pair
        var xml = "<a>𝄞<b c=\"𝄞\" d=\"\"/><!--𝄞--><![CDATA[𝄞]]><𐀀></𐀀><e/></a>";
        var expecting = """
                        BeginTag(1:2): a
                        Content(1:4): 𝄞
                        BeginTag(1:6): b
                        Attribute(1:8)-(1:10): c="𝄞"
                        Attribute(1:14)-(1:16): d=""
                        EndTagEmpty
                        Comment(1:24): 𝄞
                        CData(1:37): 𝄞
                        BeginTag(1:42): 𐀀
                        EndTag(1:46): 𐀀
                        BeginTag(1:49): e
                        EndTagEmpty
                        EndTag(1:54): a
                        """;
        expecting = NormalizeNewLines(expecting);

        var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        XmlParser.Parse(xml, ref handler);
        Assert.AreEqual(expecting, NormalizeNewLines(handler.Writer.ToString()!));

        handler.Writer = new StringWriter();
        XmlParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), ref handler);
        Assert.AreEqual(expecting, NormalizeNewLines(handler.Writer.ToString()!));
    }

    [TestMethod]
    public void TestColumnAfterEndTagWithSurrogatePair()
    {
        // Names of 16 or more characters go through the vectorized end tag comparison
        foreach (var name in new[] { "𐀀", new string('n', 15) + "𐀀", "𐀀" + new string('n', 30), new string('n', 20) + "𐀀" + new string('n', 20) })
        {
            var xml = $"<a><{name}></{name}><b/></a>";
            var nameColumns = name.EnumerateRunes().Count();
            var expecting = $"""
                             BeginTag(1:2): a
                             BeginTag(1:5): {name}
                             EndTag(1:{8 + nameColumns}): {name}
                             BeginTag(1:{10 + 2 * nameColumns}): b
                             EndTagEmpty
                             EndTag(1:{15 + 2 * nameColumns}): a
                             """;
            expecting = NormalizeNewLines(expecting);

            var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
            XmlParser.Parse(xml, ref handler);
            Assert.AreEqual(expecting, NormalizeNewLines(handler.Writer.ToString()!), name);

            handler.Writer = new StringWriter();
            XmlParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), ref handler);
            Assert.AreEqual(expecting, NormalizeNewLines(handler.Writer.ToString()!), name);
        }
    }

    [TestMethod]
    public void TestEndOfStreamInEndTagWithSurrogatePair()
    {
        var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        XmlParser.Parse("<a𝄞b></a𝄞", ref handler);
        var expecting = """
                        BeginTag(1:2): a𝄞b
                        Error(1:10): Invalid end of XML stream
                        """;
        Assert.AreEqual(NormalizeNewLines(expecting), NormalizeNewLines(handler.Writer.ToString()!));
    }

    [TestMethod]
    public void TestRejectFragments()
    {
        var options = new XmlParserOptions { RejectFragments = true };
        var cases = new (string Xml, string Expecting)[]
        {
            ("<a/><b/>", """
                         BeginTag(1:2): a
                         EndTagEmpty
                         Error(1:6): Invalid begin tag. A document has only one root element
                         """),
            ("", "Error(1:1): Invalid document. Expecting a root element"),
            ("<!-- c --> \n", """
                             Comment(1:5):  c 
                             Content(1:11):  \n
                             Error(2:1): Invalid document. Expecting a root element
                             """),
            ("\r", """
                    Content(1:1): \n
                    Error(2:1): Invalid document. Expecting a root element
                    """),
            ("x<a/>", "Error(1:1): Invalid text outside the root element. Only whitespace is allowed"),
            ("<a/> x", """
                       BeginTag(1:2): a
                       EndTagEmpty
                       Error(1:5): Invalid text outside the root element. Only whitespace is allowed
                       """),
            ("<a/>x\r", """
                       BeginTag(1:2): a
                       EndTagEmpty
                       Error(1:5): Invalid text outside the root element. Only whitespace is allowed
                       """),
            ("<a/>&#32;", """
                          BeginTag(1:2): a
                          EndTagEmpty
                          Error(1:5): Invalid text outside the root element. Only whitespace is allowed
                          """),
            ("<a/><![CDATA[x]]>", """
                                  BeginTag(1:2): a
                                  EndTagEmpty
                                  Error(1:7): Invalid text outside the root element. Only whitespace is allowed
                                  """),
            ("<?xml version=\"1.0\"?>\n<!-- c --><a><b/>x<![CDATA[y]]>&amp;</a>\n<!-- d --><?pi?>\n", """
                                                                                                XmlDeclaration(1:2): version="1.0" encoding="" standalone=""
                                                                                                Content(1:22): \n
                                                                                                Comment(2:5):  c 
                                                                                                BeginTag(2:12): a
                                                                                                BeginTag(2:15): b
                                                                                                EndTagEmpty
                                                                                                Content(2:18): x
                                                                                                CData(2:28): y
                                                                                                Content(2:32): &
                                                                                                EndTag(2:39): a
                                                                                                Content(2:41): \n
                                                                                                Comment(3:5):  d 
                                                                                                Content(3:17): \n
                                                                                                """),
        };

        foreach (var (xml, expecting) in cases)
        {
            var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
            XmlParser.Parse(xml, ref handler, options);
            Assert.AreEqual(NormalizeNewLines(expecting), NormalizeNewLines(handler.Writer.ToString()!), xml);

            handler.Writer = new StringWriter();
            XmlParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), ref handler, options);
            Assert.AreEqual(NormalizeNewLines(expecting), NormalizeNewLines(handler.Writer.ToString()!), xml);
        }
    }

    [TestMethod]
    public void TestFragmentsAreAcceptedByDefault()
    {
        var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        XmlParser.Parse("x<a/><b/>", ref handler);
        var expecting = """
                        Content(1:1): x
                        BeginTag(1:3): a
                        EndTagEmpty
                        BeginTag(1:7): b
                        EndTagEmpty
                        """;
        Assert.AreEqual(NormalizeNewLines(expecting), NormalizeNewLines(handler.Writer.ToString()!));
    }

    [TestMethod]
    public void TestEndOfStreamAfterCarriageReturn()
    {
        var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        XmlParser.Parse("<a>x\r", ref handler);
        var expecting = """
                        BeginTag(1:2): a
                        Content(1:4): x\n
                        Error(2:1): Invalid tag a not closed at the end of the document.
                        """;
        Assert.AreEqual(NormalizeNewLines(expecting), NormalizeNewLines(handler.Writer.ToString()!));
    }

    [TestMethod]
    public void TestCharacterReferenceToCarriageReturnIsNotNormalized()
    {
        var xml = "<a>1\r\n2\r3&#xD;&#xD;&#xA;</a>";
        var expecting = """
                        BeginTag(1:2): a
                        Content(1:4): 1\n2\n3\r\r\n
                        EndTag(3:19): a
                        """;

        var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        XmlParser.Parse(xml, ref handler);
        Assert.AreEqual(NormalizeNewLines(expecting), NormalizeNewLines(handler.Writer.ToString()!));

        handler.Writer = new StringWriter();
        XmlParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), ref handler);
        Assert.AreEqual(NormalizeNewLines(expecting), NormalizeNewLines(handler.Writer.ToString()!));
    }

    [TestMethod]
    public void TestEndOfStreamAfterClosingBrackets()
    {
        foreach (var text in new[] { "]", "]]", "x]" })
        {
            var xml = $"<a>{text}";
            var expecting = $"""
                             BeginTag(1:2): a
                             Content(1:4): {text}
                             Error(1:{4 + text.Length}): Invalid tag a not closed at the end of the document.
                             """;

            var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
            XmlParser.Parse(xml, ref handler);
            Assert.AreEqual(NormalizeNewLines(expecting), NormalizeNewLines(handler.Writer.ToString()!), xml);

            handler.Writer = new StringWriter();
            XmlParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), ref handler);
            Assert.AreEqual(NormalizeNewLines(expecting), NormalizeNewLines(handler.Writer.ToString()!), xml);
        }
    }

    [TestMethod]
    public void TestClosingBracketsInsideText()
    {
        var xml = "<a>x]y]]z]</a><b>]</b>";
        var expecting = """
                        BeginTag(1:2): a
                        Content(1:4): x]y]]z]
                        EndTag(1:13): a
                        BeginTag(1:16): b
                        Content(1:18): ]
                        EndTag(1:21): b
                        """;

        var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        XmlParser.Parse(xml, ref handler);
        Assert.AreEqual(NormalizeNewLines(expecting), NormalizeNewLines(handler.Writer.ToString()!));

        handler.Writer = new StringWriter();
        XmlParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), ref handler);
        Assert.AreEqual(NormalizeNewLines(expecting), NormalizeNewLines(handler.Writer.ToString()!));
    }

    [TestMethod]
    public void TestDefaultError()
    {
        var handler = new EmptyReadHandler();
        Assert.Throws<XmlException>(() =>
            ((IXmlReadHandler)handler).OnError("error", 1, 1));
    }

    private static string NormalizeNewLines(string text)
    {
        return text.ReplaceLineEndings("\n").TrimEnd();
    }

    private static string ParseEvents(string xml, XmlParserOptions? options = null)
    {
        var handler = new XmlEventHandler { Writer = new StringWriter() };
        if (options.HasValue)
        {
            XmlParser.Parse(xml, ref handler, options.Value);
        }
        else
        {
            XmlParser.Parse(xml, ref handler);
        }

        return NormalizeNewLines(handler.Writer.ToString()!);
    }

    private static string ParseStreamEvents(string xml, XmlParserOptions options)
    {
        var handler = new XmlEventHandler { Writer = new StringWriter() };
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(xml));
        XmlParser.Parse(stream, ref handler, options);
        return NormalizeNewLines(handler.Writer.ToString()!);
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

    [TestMethod]
    public void TestStringInputContentPointsIntoInput()
    {
        var longText = new string('t', 100);
        var xml = $"<?pi data?><!-- comment --><root a=\"{longText}\" b='value'>{longText}<![CDATA[ cdata ]]></root>end";
        var handler = new ContentSpanHandler(xml);
        XmlParser.Parse(xml, handler);

        AssertEvents(new[]
        {
            "PI(pi): data:input",
            "Comment: comment :input",
            $"Attribute(a):{longText}:input",
            "Attribute(b):value:input",
            $"Text:{longText}:input",
            "CData: cdata :input",
            "Text:end:input",
        }, handler.Events);
    }

    [TestMethod]
    [DataRow("<root>a&amp;b</root>", "Text:a&b:copy")]
    [DataRow("<root>&amp;ab</root>", "Text:&ab:copy")]
    // The decoded character is the same as the first character of the entity, and the content ends there.
    [DataRow("<root>ab&amp;</root>", "Text:ab&:input")]
    [DataRow("<root>ab&#x41;</root>", "Text:abA:copy")]
    [DataRow("<root>ab&#x1F600;cd</root>", "Text:ab\U0001F600cd:copy")]
    [DataRow("<root/>a&lt;b", "Text:a<b:copy")]
    [DataRow("<root a=\"x&quot;y\"/>", "Attribute(a):x\"y:copy")]
    [DataRow("<root a=\"x\r\ny\"/>", "Attribute(a):x\ny:copy")]
    [DataRow("<root a=\"\"/>", "Attribute(a)::input")]
    [DataRow("<root><![CDATA[a]]b]c]]></root>", "CData:a]]b]c:input")]
    [DataRow("<root><![CDATA[]]></root>", "CData::input")]
    [DataRow("<!--a-b-c--><root/>", "Comment:a-b-c:input")]
    [DataRow("<!----><root/>", "Comment::input")]
    [DataRow("<?pi a?b??><root/>", "PI(pi): a?b?:input")]
    [DataRow("<root>\U0001F600</root>", "Text:\U0001F600:input")]
    public void TestStringInputContentChangesToCopy(string xml, string expected)
    {
        var handler = new ContentSpanHandler(xml);
        XmlParser.Parse(xml, handler);
        AssertEvents(new[] { expected }, handler.Events);
        AssertSameContentAsStream(xml, handler);
    }

    [TestMethod]
    public void TestStringInputLongContentWithEntityAfterVectors()
    {
        // The entity comes after a run of characters that the parser reads in vectors.
        foreach (var length in new[] { 1, 15, 16, 17, 31, 32, 33, 63, 64, 65, 200 })
        {
            var run = new string('x', length);
            var xml = $"<root a=\"{run}&amp;{run}\">{run}&amp;{run}<![CDATA[{run}]]{run}]]><!--{run}-{run}--></root>{run}&amp;";
            var handler = new ContentSpanHandler(xml);
            XmlParser.Parse(xml, handler);
            AssertEvents(new[]
            {
                $"Attribute(a):{run}&{run}:copy",
                $"Text:{run}&{run}:copy",
                $"CData:{run}]]{run}:input",
                $"Comment:{run}-{run}:input",
                $"Text:{run}&:input",
            }, handler.Events, $"Length {length}");
            AssertSameContentAsStream(xml, handler);
        }
    }

    private static void AssertSameContentAsStream(string xml, ContentSpanHandler stringHandler)
    {
        var streamHandler = new ContentSpanHandler(xml);
        XmlParser.Parse(new MemoryStream(Encoding.UTF8.GetBytes(xml)), streamHandler);
        AssertEvents(stringHandler.Values, streamHandler.Values);
    }

    private static void AssertEvents(IEnumerable<string> expected, IEnumerable<string> actual, string? message = null)
        => Assert.AreEqual(string.Join("\n", expected), string.Join("\n", actual), message);

    /// <summary>
    /// Records the content that the parser gives, and if the span points into the input string.
    /// </summary>
    private sealed class ContentSpanHandler(string input) : IXmlReadHandler
    {
        public List<string> Events { get; } = [];

        public List<string> Values { get; } = [];

        public void OnProcessingInstruction(ReadOnlySpan<char> target, ReadOnlySpan<char> data, int line, int column)
            => Add($"PI({target})", data);

        public void OnAttribute(ReadOnlySpan<char> name, ReadOnlySpan<char> value, int nameLine, int nameColumn, int valueLine, int valueColumn)
            => Add($"Attribute({name})", value);

        public void OnText(ReadOnlySpan<char> text, int line, int column)
            => Add("Text", text);

        public void OnComment(ReadOnlySpan<char> comment, int line, int column)
            => Add("Comment", comment);

        public void OnCData(ReadOnlySpan<char> cdata, int line, int column)
            => Add("CData", cdata);

        public void OnError(string message, int line, int column)
            => Events.Add($"Error:{message}");

        private void Add(string kind, ReadOnlySpan<char> content)
        {
            var source = content.IsEmpty || content.Overlaps(input.AsSpan()) ? "input" : "copy";
            Events.Add($"{kind}:{content}:{source}");
            Values.Add($"{kind}:{content}");
        }
    }

    private struct XmlEventHandler : IXmlReadHandler
    {
        public required TextWriter Writer { get; init; }

        public void OnXmlDeclaration(ReadOnlySpan<char> version, ReadOnlySpan<char> encoding, ReadOnlySpan<char> standalone, int line, int column)
            => Writer.WriteLine($"XmlDeclaration(version=\"{version}\", encoding=\"{encoding}\", standalone=\"{standalone}\")");

        public void OnBeginTag(ReadOnlySpan<char> name, int line, int column)
            => Writer.WriteLine($"BeginTag({name})");

        public void OnEndTagEmpty()
            => Writer.WriteLine("EndTagEmpty");

        public void OnEndTag(ReadOnlySpan<char> name, int line, int column)
            => Writer.WriteLine($"EndTag({name})");

        public void OnAttribute(ReadOnlySpan<char> name, ReadOnlySpan<char> value, int nameLine, int nameColumn, int valueLine, int valueColumn)
            => Writer.WriteLine($"Attribute({name}=\"{value}\")");

        public void OnText(ReadOnlySpan<char> text, int line, int column)
            => Writer.WriteLine($"Content({text})");

        public void OnError(string message, int line, int column)
            => Writer.WriteLine($"Error({message})");
    }

    private sealed class ProcessingInstructionHandler : IXmlReadHandler
    {
        public List<ProcessingInstruction> Instructions { get; } = [];

        public List<string> BeginTags { get; } = [];

        public int XmlDeclarationCount { get; private set; }

        public void OnXmlDeclaration(ReadOnlySpan<char> version, ReadOnlySpan<char> encoding, ReadOnlySpan<char> standalone, int line, int column)
            => XmlDeclarationCount++;

        public void OnProcessingInstruction(ReadOnlySpan<char> target, ReadOnlySpan<char> data, int line, int column)
            => Instructions.Add(new ProcessingInstruction(target.ToString(), data.ToString(), line, column));

        public void OnBeginTag(ReadOnlySpan<char> name, int line, int column)
            => BeginTags.Add(name.ToString());
    }

    private readonly record struct ProcessingInstruction(string Target, string Data, int Line, int Column);
}
