// Copyright (c) Alexandre Mutel. All rights reserved.
// Licensed under the BSD-Clause 2 license.
// See license.txt file in the project root for full license information.

using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using BenchmarkDotNet.Attributes;

namespace TurboXml.Bench;

/// <summary>
/// Benchmarks for the string parsing
/// </summary>
[MemoryDiagnoser]
public class BenchString
{
    private readonly string _xmlInput;
    private const string InputPath = @"tiger.svg";

    public BenchString()
    {
        _xmlInput = File.ReadAllText(InputPath, Encoding.UTF8);
    }

    [Benchmark(Description = "TurboXml")]
    public long BenchTurboXml()
    {
        var parser = new DefaultReadHandler();
        XmlParser.Parse(_xmlInput, ref parser);
        return parser.Length;
    }


    [Benchmark(Description = "TurboXml - SIMD Disabled")]
    public long BenchTurboXmlSimdDisabled()
    {
        var parser = new DefaultReadHandler();
        XmlParser.Parse(_xmlInput, ref parser, new(UseSimd:false));
        return parser.Length;
    }

    [Benchmark(Description = "XmlReader")]
    public long BenchXmlReader()
    {
        var reader = new XmlTextReader(new StringReader(_xmlInput));
        long length = 0;
        while (reader.Read())
        {
            switch (reader.NodeType)
            {
                case XmlNodeType.Element:
                    length += reader.Name.Length;
                    //Console.WriteLine($"StartTag: {reader.Name}");
                    for (int i = 0; i < reader.AttributeCount; i++)
                    {
                        reader.MoveToAttribute(i);
                        //length += reader.Name.Length + reader.Value.Length;
                        //Console.WriteLine($"Attribute: {reader.Name}={reader.Value}");
                    }
                    break;
                case XmlNodeType.Text:
                    //Console.WriteLine($"Text: {reader.Value}");
                    length += reader.Value.Length;
                    break;
                case XmlNodeType.EndElement:
                    //Console.WriteLine($"EndTag: {reader.Name}");
                    break;
            }
        }

        return length;
    }
    
    public struct DefaultReadHandler : IXmlReadHandler
    {
        public long Length;

        public void OnCData(ReadOnlySpan<char> comment, int line, int column)
        {
            //Console.WriteLine($"CDATA({line + 1}:{column + 1}): \"{comment.ToString()}\"");
        }

        public void OnComment(ReadOnlySpan<char> comment, int line, int column)
        {
            //Console.WriteLine($"Comment({line + 1}:{column + 1}): \"{comment.ToString()}\"");
        }

        public void OnXmlDeclaration(ReadOnlySpan<char> version, ReadOnlySpan<char> encoding, ReadOnlySpan<char> standalone, int line, int column)
        {
            //Console.WriteLine($"Xml({line + 1}:{column + 1}): version=\"{version}\" encoding=\"{encoding}\" standalone=\"{standalone}\"");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnBeginTag(ReadOnlySpan<char> name, int line, int column)
        {
            Length += name.Length;
            //Console.WriteLine($"StartTag({line + 1}:{column + 1}): {name.ToString()}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnAttribute(ReadOnlySpan<char> name, ReadOnlySpan<char> value, int nameLine, int nameColumn, int valueLine, int valueColumn)
        {
            Length += name.Length + value.Length;
            //Console.WriteLine($"Attribute({nameLine + 1}:{nameColumn + 1}) - ({valueLine + 1}:{valueColumn + 1}):  {name.ToString()} = {value.ToString()}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnEndTag(ReadOnlySpan<char> name, int line, int column)
        {
            //Console.WriteLine($"EndTag({line + 1}:{column + 1}): {name.ToString()}");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void OnText(ReadOnlySpan<char> text, int line, int column)
        {
            Length += text.Length;
            //Console.WriteLine($"Text({line + 1}:{column + 1}): {text.ToString()}");
        }
    }
}