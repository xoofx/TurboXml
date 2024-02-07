// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System;
using System.Xml;

namespace TurboXml;

/// <summary>
/// Interface to handle events from the XML parser.
/// </summary>
public interface IXmlReadHandler
{
    /// <summary>
    /// Called when the XML declaration has been parsed.
    /// </summary>
    /// <param name="version">The version attribute.</param>
    /// <param name="encoding">The encoding attribute (Might be empty).</param>
    /// <param name="standalone">The standalone attribute (Might be empty).</param>
    /// <param name="line">The line this declaration occured.</param>
    /// <param name="column">The column this declaration occured.</param>
    void OnXmlDeclaration(ReadOnlySpan<char> version, ReadOnlySpan<char> encoding, ReadOnlySpan<char> standalone, int line, int column)
    {
    }

    /// <summary>
    /// Called when an open tag is parsed.
    /// </summary>
    /// <param name="name">The name of the tag.</param>
    /// <param name="line">The line this declaration occured.</param>
    /// <param name="column">The column this declaration occured.</param>
    void OnBeginTag(ReadOnlySpan<char> name, int line, int column)
    {
    }

    /// <summary>
    /// Called when an empty tag has been parsed.
    /// </summary>
    void OnEndTagEmpty()
    {
    }

    /// <summary>
    /// Called when a close tag has been parsed.
    /// </summary>
    /// <param name="name">The name of the tag.</param>
    /// <param name="line">The line this declaration occured.</param>
    /// <param name="column">The column this declaration occured.</param>
    void OnEndTag(ReadOnlySpan<char> name, int line, int column)
    {
    }

    /// <summary>
    /// Called when an attribute has been parsed.
    /// </summary>
    /// <param name="name">The name of the attribute.</param>
    /// <param name="value">The value of the attribute.</param>
    /// <param name="nameLine">The line the name declaration occured.</param>
    /// <param name="nameColumn">The column the name declaration occured.</param>
    /// <param name="valueLine">The line the value declaration occured.</param>
    /// <param name="valueColumn">The column the value declaration occured.</param>
    void OnAttribute(ReadOnlySpan<char> name, ReadOnlySpan<char> value, int nameLine, int nameColumn, int valueLine, int valueColumn)
    {
    }

    /// <summary>
    /// Called when a text content has been parsed.
    /// </summary>
    /// <param name="text">The text content.</param>
    /// <param name="line">The line this text occured.</param>
    /// <param name="column">The column this text occured.</param>
    void OnText(ReadOnlySpan<char> text, int line, int column)
    {
    }

    /// <summary>
    /// Called when a comment has been parsed.
    /// </summary>
    /// <param name="comment">The content of the comment.</param>
    /// <param name="line">The line this comment occured.</param>
    /// <param name="column">The column this comment occured.</param>
    void OnComment(ReadOnlySpan<char> comment, int line, int column)
    {
    }

    /// <summary>
    /// Called when a CDATA section has been parsed.
    /// </summary>
    /// <param name="cdata">The content of a CDATA section.</param>
    /// <param name="line">The line this CDATA occured.</param>
    /// <param name="column">The column this CDATA occured.</param>
    void OnCData(ReadOnlySpan<char> cdata, int line, int column)
    {
    }

    /// <summary>
    /// Called when an error has been detected.
    /// </summary>
    /// <param name="message">The error message.</param>
    /// <param name="line">The line this error occured.</param>
    /// <param name="column">The column this error occured.</param>
    /// <exception cref="XmlException">The default implementation throws a <see cref="System.Xml.XmlException"/></exception>
    void OnError(string message, int line, int column)
    {
        throw new XmlException(message, null, line + 1, column + 1);
    }
}
