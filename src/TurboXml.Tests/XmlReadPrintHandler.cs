// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Text;

namespace TurboXml.Tests;

/// <summary>
/// Default handler used for verifying the XML parsing.
/// </summary>
public struct XmlReadPrintHandler : IXmlReadHandler
{
    public TextWriter Writer { get; set; }

    public void OnXmlDeclaration(ReadOnlySpan<char> version, ReadOnlySpan<char> encoding, ReadOnlySpan<char> standalone, int line, int column)
    {
        Writer.WriteLine($"XmlDeclaration({line + 1}:{column + 1}): version=\"{version}\" encoding=\"{encoding}\" standalone=\"{standalone}\"");
    }

    public void OnBeginTag(ReadOnlySpan<char> name, int line, int column)
    {
        Writer.WriteLine($"BeginTag({line + 1}:{column + 1}): {name}");
    }

    public void OnEndTagEmpty()
    {
        Writer.WriteLine($"EndTagEmpty");
    }

    public void OnEndTag(ReadOnlySpan<char> name, int line, int column)
    {
        Writer.WriteLine($"EndTag({line + 1}:{column + 1}): {name}");
    }

    public void OnAttribute(ReadOnlySpan<char> name, ReadOnlySpan<char> value, int nameLine, int nameColumn, int valueLine, int valueColumn)
    {
        Writer.WriteLine($"Attribute({nameLine + 1}:{nameColumn + 1})-({valueLine + 1}:{valueColumn + 1}): {name}=\"{Normalize(value)}\"");
    }

    public void OnText(ReadOnlySpan<char> text, int line, int column)
    {
        Writer.WriteLine($"Content({line + 1}:{column + 1}): {Normalize(text)}");
    }

    public void OnComment(ReadOnlySpan<char> comment, int line, int column)
    {
        Writer.WriteLine($"Comment({line + 1}:{column + 1}): {Normalize(comment)}");
    }

    public void OnCData(ReadOnlySpan<char> cdata, int line, int column)
    {
        Writer.WriteLine($"CData({line + 1}:{column + 1}): {Normalize(cdata)}");
    }

    public void OnError(string message, int line, int column)
    {
        Writer.WriteLine($"Error({line + 1}:{column + 1}): {message}");
    }

    private static string Normalize(ReadOnlySpan<char> text)
    {
        // Replace control characters by their escaped version
        var builder = new StringBuilder();
        foreach (var c in text)
        {
            if (c < 32)
            {
                if (c == '\n') builder.Append("\\n");
                else if (c == '\r') builder.Append("\\r");
                else if (c == '\t') builder.Append("\\t");
                else builder.Append($"\\x{(byte)c:X2}");
            }
            else
            {
                builder.Append(c);
            }
        }
        return builder.ToString();
    }
}