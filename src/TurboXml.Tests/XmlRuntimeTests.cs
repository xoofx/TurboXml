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
        const string xml = "<?custom \tdata?\r\nhere\rend?>";
        var handler = new ProcessingInstructionHandler();

        XmlParser.Parse(xml, handler);

        Assert.HasCount(1, handler.Instructions);
        var instruction = handler.Instructions[0];
        Assert.AreEqual("custom", instruction.Target);
        // The data keeps its line ends: \r\n and \r are not normalized to \n
        Assert.AreEqual(" \tdata?\r\nhere\rend", instruction.Data);
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
    public void TestXmlDeclarationAndDocumentTypeAfterText()
    {
        // Text of 8 or 16 characters goes through the vectorized text loop
        foreach (var text in new[] { " ", new string(' ', 8), new string(' ', 16), "a", "abcdefgh", "abcdefghijklmnop" })
        {
            var xml = $"""{text}<?xml version="1.0"?><a/>""";
            var expecting = NormalizeNewLines(
                $"""
                 Content({text})
                 Error(Invalid XML declaration. It must be the first node in the document)
                 """);
            Assert.AreEqual(expecting, ParseEvents(xml), xml);
            Assert.AreEqual(expecting, ParseStreamEvents(xml, new XmlParserOptions()), xml);
        }

        var options = new XmlParserOptions { IgnoreDtd = true };
        foreach (var text in new[] { "a", "abcdefgh", "abcdefghijklmnop", "       a" })
        {
            var xml = $"{text}<!DOCTYPE a><a/>";
            var expecting = NormalizeNewLines(
                $"""
                 Content({text})
                 Error(Invalid document type declaration)
                 """);
            Assert.AreEqual(expecting, ParseEvents(xml, options), xml);
            Assert.AreEqual(expecting, ParseStreamEvents(xml, options), xml);
        }

        // [27] Misc ::= Comment | PI | S, so whitespace may come before the document type declaration
        foreach (var text in new[] { " ", new string(' ', 8), new string(' ', 16) })
        {
            var xml = $"{text}<!DOCTYPE a><a/>";
            var expecting = NormalizeNewLines(
                $"""
                 Content({text})
                 BeginTag(a)
                 EndTagEmpty
                 """);
            Assert.AreEqual(expecting, ParseEvents(xml, options), xml);
            Assert.AreEqual(expecting, ParseStreamEvents(xml, options), xml);
        }
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
    public void TestStreamReturningOneBytePerRead()
    {
        // A read of the first bytes of a multi-byte character decodes no character
        foreach (var xml in new[] { "<a>€€</a>", "<a/>€", "<a b=\"€𝄞\">𝄞x€</a>", $"<a>{new string('x', 20)}€{new string('y', 20)}</a>" })
        {
            var expecting = ParsePrint(xml);
            Assert.AreEqual(expecting, ParsePrint(new OneByteReadStream(Encoding.UTF8.GetBytes(xml))), xml);
        }
    }

    [TestMethod]
    public void TestStreamWithCharacterAcrossBufferBoundary()
    {
        // The stream is read by blocks of 16384 bytes: put each byte boundary of a multi-byte character at the end of a block
        foreach (var character in new[] { "€", "𝄞" })
        {
            var characterByteCount = Encoding.UTF8.GetByteCount(character);
            for (int bytesInFirstBlock = 1; bytesInFirstBlock < characterByteCount; bytesInFirstBlock++)
            {
                var xml = $"<a>{new string('x', 16384 - "<a>".Length - bytesInFirstBlock)}{character}</a>";
                var expecting = ParsePrint(xml);
                Assert.AreEqual(expecting, ParsePrint(new MemoryStream(Encoding.UTF8.GetBytes(xml))), $"{character} {bytesInFirstBlock}");
            }
        }
    }

    [TestMethod]
    public void TestStreamEndingWithIncompleteCharacter()
    {
        // The first two bytes of the three bytes of €
        byte[] bytes = [.. "<a/>"u8, 0xE2, 0x82];
        // The decoder replaces the incomplete character at the end of the stream
        var expecting = "BeginTag(1:2): a\nEndTagEmpty\nContent(1:5): \uFFFD";

        Assert.AreEqual(expecting, ParsePrint(new MemoryStream(bytes)));
        Assert.AreEqual(expecting, ParsePrint(new OneByteReadStream(bytes)));
    }

    [TestMethod]
    public void TestStreamEndingWithIncompleteCharacterAndStrictEncoding()
    {
        // XML 1.0 4.3.3: an encoding error is fatal, so a strict decoder throws when it is flushed at the end of the stream
        var options = new XmlParserOptions(new UTF8Encoding(false, true));
        byte[] bytes = [.. "<a/>"u8, 0xE2, 0x82];

        Assert.Throws<DecoderFallbackException>(() => ParsePrint(new MemoryStream(bytes), options));
        Assert.Throws<DecoderFallbackException>(() => ParsePrint(new OneByteReadStream(bytes), options));
    }

    [TestMethod]
    public void TestStreamReturningOneBytePerReadWithStrictEncoding()
    {
        // A read that ends inside a multi-byte character does not flush the decoder, so a strict decoder does not throw
        var options = new XmlParserOptions(new UTF8Encoding(false, true));
        var xml = "<a>€𝄞</a>";

        Assert.AreEqual(ParsePrint(xml), ParsePrint(new OneByteReadStream(Encoding.UTF8.GetBytes(xml)), options));
    }

    [TestMethod]
    public void TestByteOrderMarkWithGivenEncoding()
    {
        foreach (var (encoding, encodingName) in new (Encoding, string)[]
                 {
                     (new UTF8Encoding(true), "UTF-8"),
                     (new UTF8Encoding(false), "UTF-8 without preamble"),
                     (new UnicodeEncoding(false, true), "UTF-16"),
                     (new UnicodeEncoding(true, false), "UTF-16 be without preamble"),
                     (new UTF32Encoding(false, true), "UTF-32"),
                     (new UTF32Encoding(true, false), "UTF-32 be without preamble"),
                 })
        {
            var options = new XmlParserOptions(encoding);
            var byteOrderMark = encoding.GetBytes("\uFEFF");
            foreach (var xml in new[] { """<?xml version="1.0"?><a/>""", "<a>x</a>" })
            {
                var expecting = ParsePrint(xml);
                var bytes = encoding.GetBytes(xml);
                Assert.AreEqual(expecting, ParsePrint(new MemoryStream([.. byteOrderMark, .. bytes]), options), $"{encodingName} with BOM: {xml}");
                Assert.AreEqual(expecting, ParsePrint(new OneByteReadStream([.. byteOrderMark, .. bytes]), options), $"{encodingName} with BOM, one byte per read: {xml}");
                Assert.AreEqual(expecting, ParsePrint(new MemoryStream(bytes), options), $"{encodingName} without BOM: {xml}");
            }
        }
    }

    [TestMethod]
    public void TestByteOrderMarkAfterShortDetectionRead()
    {
        // The detection of the encoding reads one byte, too few to see the UTF-8 byte order mark
        var xml = """<?xml version="1.0"?><a/>""";
        byte[] bytes = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(xml)];
        Assert.AreEqual(ParsePrint(xml), ParsePrint(new OneByteReadStream(bytes)));
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
                        Attribute(4:1)-(4:6): attr="hello world"
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
                    Attribute(4:1)-(4:6): attr="hello world"
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
    public void TestAttributeValueWhitespaceNormalization()
    {
        // XML 1.0 3.3.3: each literal tab, line feed, carriage return or \r\n is passed as a space, a character reference as its character.
        // 32 characters go through the vectorized attribute value loops before the tab.
        var block = "0123456789abcdef0123456789abcdef";
        var xml = $"<a b='x\r\ny\tz\nw&#xD;' c=\"1\r2&#x9;&#xA;&#xD;3\" d=\"{block}\t{block}\"/>";
        var expecting = NormalizeNewLines(
            $"""
             BeginTag(1:2): a
             Attribute(1:4)-(1:6): b="x y z w\r"
             Attribute(3:9)-(3:11): c="1 2\t\n\r3"
             Attribute(4:20)-(4:22): d="{block} {block}"
             EndTagEmpty
             """);

        Assert.AreEqual(expecting, ParsePrint(xml));
        Assert.AreEqual(expecting, ParsePrint(new MemoryStream(Encoding.UTF8.GetBytes(xml))));
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

    private static string ParsePrint(string xml)
    {
        var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        XmlParser.Parse(xml, ref handler);
        return NormalizeNewLines(handler.Writer.ToString()!);
    }

    private static string ParsePrint(Stream stream, XmlParserOptions options = default)
    {
        var handler = new XmlReadPrintHandler() { Writer = new StringWriter() };
        using (stream)
        {
            XmlParser.Parse(stream, ref handler, options);
        }

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

    /// <summary>
    /// A stream that returns at most one byte per read, as a network stream or a pipe can.
    /// </summary>
    private sealed class OneByteReadStream(byte[] bytes) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));

        public override int Read(Span<byte> buffer) => base.Read(buffer.Slice(0, Math.Min(buffer.Length, 1)));
    }
}
