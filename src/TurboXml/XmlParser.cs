// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.IO;
using System.Text;

namespace TurboXml;

/// <summary>
/// Parsing options for the <see cref="XmlParser"/> class.
/// </summary>
/// <param name="Encoding">Force using this encoding when parsing a stream. By default, TurboXml will detect the encoding by following the XML specs.</param>
/// <param name="UseSimd">A flag to enable or disable the usage SIMD when parsing. Default is enabled with <c>true</c>.</param>
/// <param name="CheckBeginEndTag">A flag to enable or disable the check for matching begin/end tags when parsing. Default is enabled with <c>true</c>.</param>
public readonly record struct XmlParserOptions(Encoding? Encoding = null, bool UseSimd = true, bool CheckBeginEndTag = true);

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
        var charProvider = new StreamCharProvider(stream, null);
        try
        {
            using var parser = new XmlParserInternal<TXmlHandler, StreamCharProvider, IXmlConfigItem.Active, IXmlConfigItem.Active>(ref handler, ref charProvider);
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
        var charProvider = new StreamCharProvider(stream, null);
        try
        {
            using var parser = new XmlParserInternal<TXmlHandler, StreamCharProvider, IXmlConfigItem.Active, IXmlConfigItem.Active>(ref handler, ref charProvider);
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
        var charProvider = new StringCharProvider(text);
        using var parser = new XmlParserInternal<TXmlHandler, StringCharProvider, IXmlConfigItem.Active, IXmlConfigItem.Active>(ref handler, ref charProvider);
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
        var charProvider = new StringCharProvider(text);
        using var parser = new XmlParserInternal<TXmlHandler, StringCharProvider, IXmlConfigItem.Active, IXmlConfigItem.Active>(ref handler, ref charProvider);
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
        var charProvider = new StreamCharProvider(stream, options.Encoding);
        try
        {
            switch (options.UseSimd)
            {
                case true when options.CheckBeginEndTag:
                {
                    using var parser = new XmlParserInternal<TXmlHandler, StreamCharProvider, IXmlConfigItem.Active, IXmlConfigItem.Active>(ref handler, ref charProvider);
                    parser.Parse();
                    break;
                }
                case true when !options.CheckBeginEndTag:
                {
                    using var parser = new XmlParserInternal<TXmlHandler, StreamCharProvider, IXmlConfigItem.Active, IXmlConfigItem.Inactive>(ref handler, ref charProvider);
                    parser.Parse();
                    break;
                }
                case false when options.CheckBeginEndTag:
                {
                    using var parser = new XmlParserInternal<TXmlHandler, StreamCharProvider, IXmlConfigItem.Inactive, IXmlConfigItem.Active>(ref handler, ref charProvider);
                    parser.Parse();
                    break;
                }
                default:
                {
                    using var parser = new XmlParserInternal<TXmlHandler, StreamCharProvider, IXmlConfigItem.Inactive, IXmlConfigItem.Inactive>(ref handler, ref charProvider);
                    parser.Parse();
                    break;
                }
            }
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
        var charProvider = new StreamCharProvider(stream, options.Encoding);
        try
        {
            switch (options.UseSimd)
            {
                case true when options.CheckBeginEndTag:
                {
                    using var parser = new XmlParserInternal<TXmlHandler, StreamCharProvider, IXmlConfigItem.Active, IXmlConfigItem.Active>(ref handler, ref charProvider);
                    parser.Parse();
                    break;
                }
                case true when !options.CheckBeginEndTag:
                {
                    using var parser = new XmlParserInternal<TXmlHandler, StreamCharProvider, IXmlConfigItem.Active, IXmlConfigItem.Inactive>(ref handler, ref charProvider);
                    parser.Parse();
                    break;
                }
                case false when options.CheckBeginEndTag:
                {
                    using var parser = new XmlParserInternal<TXmlHandler, StreamCharProvider, IXmlConfigItem.Inactive, IXmlConfigItem.Active>(ref handler, ref charProvider);
                    parser.Parse();
                    break;
                }
                default:
                {
                    using var parser = new XmlParserInternal<TXmlHandler, StreamCharProvider, IXmlConfigItem.Inactive, IXmlConfigItem.Inactive>(ref handler, ref charProvider);
                    parser.Parse();
                    break;
                }
            }
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
    /// <param name="options">The options to use to parse the XML.</param>
    public static void Parse<TXmlHandler>(string text, TXmlHandler handler, XmlParserOptions options) where TXmlHandler : class, IXmlReadHandler
    {
        var charProvider = new StringCharProvider(text);
        switch (options.UseSimd)
        {
            case true when options.CheckBeginEndTag:
            {
                using var parser = new XmlParserInternal<TXmlHandler, StringCharProvider, IXmlConfigItem.Active, IXmlConfigItem.Active>(ref handler, ref charProvider);
                parser.Parse();
                break;
            }
            case true when !options.CheckBeginEndTag:
            {
                using var parser = new XmlParserInternal<TXmlHandler, StringCharProvider, IXmlConfigItem.Active, IXmlConfigItem.Inactive>(ref handler, ref charProvider);
                parser.Parse();
                break;
            }
            case false when options.CheckBeginEndTag:
            {
                using var parser = new XmlParserInternal<TXmlHandler, StringCharProvider, IXmlConfigItem.Inactive, IXmlConfigItem.Active>(ref handler, ref charProvider);
                parser.Parse();
                break;
            }
            default:
            {
                using var parser = new XmlParserInternal<TXmlHandler, StringCharProvider, IXmlConfigItem.Inactive, IXmlConfigItem.Inactive>(ref handler, ref charProvider);
                parser.Parse();
                break;
            }
        }
    }

    /// <summary>
    /// Parses the specified XML string using the specified handler.
    /// </summary>
    /// <typeparam name="TXmlHandler">The type of the XML handler.</typeparam>
    /// <param name="text">The XML text to parse.</param>
    /// <param name="handler">The handler to use to parse the XML.</param>
    /// <param name="options">The options to use to parse the XML.</param>
    public static void Parse<TXmlHandler>(string text, ref TXmlHandler handler, XmlParserOptions options) where TXmlHandler : struct, IXmlReadHandler
    {
        var charProvider = new StringCharProvider(text);
        switch (options.UseSimd)
        {
            case true when options.CheckBeginEndTag:
            {
                using var parser = new XmlParserInternal<TXmlHandler, StringCharProvider, IXmlConfigItem.Active, IXmlConfigItem.Active>(ref handler, ref charProvider);
                parser.Parse();
                break;
            }
            case true when !options.CheckBeginEndTag:
            {
                using var parser = new XmlParserInternal<TXmlHandler, StringCharProvider, IXmlConfigItem.Active, IXmlConfigItem.Inactive>(ref handler, ref charProvider);
                parser.Parse();
                break;
            }
            case false when options.CheckBeginEndTag:
            {
                using var parser = new XmlParserInternal<TXmlHandler, StringCharProvider, IXmlConfigItem.Inactive, IXmlConfigItem.Active>(ref handler, ref charProvider);
                parser.Parse();
                break;
            }
            default:
            {
                using var parser = new XmlParserInternal<TXmlHandler, StringCharProvider, IXmlConfigItem.Inactive, IXmlConfigItem.Inactive>(ref handler, ref charProvider);
                parser.Parse();
                break;
            }
        }
    }
}
