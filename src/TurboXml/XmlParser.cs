// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System;
using System.IO;
using System.Text;

namespace TurboXml;

/// <summary>
/// Parsing options for the <see cref="XmlParser"/> class.
/// </summary>
/// <param name="Encoding">Force using this encoding when parsing a stream. By default, TurboXml will detect the encoding by following the XML specs.</param>
public readonly record struct XmlParserOptions(Encoding? Encoding = null)
{
    /// <summary>
    /// Default constructor.
    /// </summary>
    public XmlParserOptions() : this(null)
    {
    }

    /// <summary>Force using this encoding when parsing a stream. By default, TurboXml will detect the encoding by following the XML specs.</summary>
    public Encoding? Encoding { get; init; } = Encoding;
}

/// <summary>
/// The TurboXML main parser. Use the static methods to parse XML from a string or a stream.
/// </summary>
public static class XmlParser
{
    /// <summary>
    /// Parses the specified XML stream using the specified handler.
    /// </summary>
    /// <typeparam name="TXmlHandler">The type of the XML handler.</typeparam>
    /// <param name="stream">The XML stream to parse.</param>
    /// <param name="handler">The handler to use to parse the XML.</param>
    public static void Parse<TXmlHandler>(Stream stream, TXmlHandler handler)
        where TXmlHandler : class, IXmlReadHandler
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(handler);

        var charProvider = new StreamCharProvider(stream, null);
        try
        {
            using var parser = new XmlParserInternal<TXmlHandler, StreamCharProvider>(ref handler, ref charProvider);
            parser.Parse();
        }
        finally
        {
            charProvider.Dispose();
        }
    }

    /// <summary>
    /// Parses the specified XML stream using the specified handler.
    /// </summary>
    /// <typeparam name="TXmlHandler">The type of the XML handler.</typeparam>
    /// <param name="stream">The XML stream to parse.</param>
    /// <param name="handler">The handler to use to parse the XML.</param>
    public static void Parse<TXmlHandler>(Stream stream, ref TXmlHandler handler)
        where TXmlHandler : struct, IXmlReadHandler
    {
        ArgumentNullException.ThrowIfNull(stream);

        var charProvider = new StreamCharProvider(stream, null);
        try
        {
            using var parser = new XmlParserInternal<TXmlHandler, StreamCharProvider>(ref handler, ref charProvider);
            parser.Parse();
        }
        finally
        {
            charProvider.Dispose();
        }
    }

    /// <summary>
    /// Parses the specified XML string using the specified handler.
    /// </summary>
    /// <typeparam name="TXmlHandler">The type of the XML handler.</typeparam>
    /// <param name="text">The XML text to parse.</param>
    /// <param name="handler">The handler to use to parse the XML.</param>
    public static void Parse<TXmlHandler>(string text, TXmlHandler handler)
        where TXmlHandler : class, IXmlReadHandler
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(handler);

        var charProvider = new StringCharProvider(text);
        using var parser = new XmlParserInternal<TXmlHandler, StringCharProvider>(ref handler, ref charProvider);
        parser.Parse();
    }

    /// <summary>
    /// Parses the specified XML string using the specified handler.
    /// </summary>
    /// <typeparam name="TXmlHandler">The type of the XML handler.</typeparam>
    /// <param name="text">The XML text to parse.</param>
    /// <param name="handler">The handler to use to parse the XML.</param>
    public static void Parse<TXmlHandler>(string text, ref TXmlHandler handler)
        where TXmlHandler : struct, IXmlReadHandler
    {
        ArgumentNullException.ThrowIfNull(text);

        var charProvider = new StringCharProvider(text);
        using var parser = new XmlParserInternal<TXmlHandler, StringCharProvider>(ref handler, ref charProvider);
        parser.Parse();
    }

    /// <summary>
    /// Parses the specified XML stream using the specified handler.
    /// </summary>
    /// <typeparam name="TXmlHandler">The type of the XML handler.</typeparam>
    /// <param name="stream">The XML stream to parse.</param>
    /// <param name="handler">The handler to use to parse the XML.</param>
    /// <param name="options">The options to use to parse the XML.</param>
    public static void Parse<TXmlHandler>(Stream stream, TXmlHandler handler, XmlParserOptions options)where TXmlHandler : class, IXmlReadHandler
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(handler);
        
        var charProvider = new StreamCharProvider(stream, options.Encoding);
        try
        {
            using var parser = new XmlParserInternal<TXmlHandler, StreamCharProvider>(ref handler, ref charProvider);
            parser.Parse();
        }
        finally
        {
            charProvider.Dispose();
        }
    }

    /// <summary>
    /// Parses the specified XML stream using the specified handler.
    /// </summary>
    /// <typeparam name="TXmlHandler">The type of the XML handler.</typeparam>
    /// <param name="stream">The XML stream to parse.</param>
    /// <param name="handler">The handler to use to parse the XML.</param>
    /// <param name="options">The options to use to parse the XML.</param>
    public static void Parse<TXmlHandler>(Stream stream, ref TXmlHandler handler, XmlParserOptions options) where TXmlHandler : struct, IXmlReadHandler
    {
        ArgumentNullException.ThrowIfNull(stream);

        var charProvider = new StreamCharProvider(stream, options.Encoding);
        try
        {
            using var parser = new XmlParserInternal<TXmlHandler, StreamCharProvider>(ref handler, ref charProvider);
            parser.Parse();
        }
        finally
        {
            charProvider.Dispose();
        }
    }
}
