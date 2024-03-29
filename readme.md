# TurboXml [![ci](https://github.com/xoofx/TurboXml/actions/workflows/ci.yml/badge.svg)](https://github.com/xoofx/TurboXml/actions/workflows/ci.yml) ![coverage](https://gist.githubusercontent.com/xoofx/4b1dc8d0fa14dd6a3846e78e5f0eafae/raw/dotnet-releaser-coverage-badge-xoofx-TurboXml.svg) [![NuGet](https://img.shields.io/nuget/v/TurboXml.svg)](https://www.nuget.org/packages/TurboXml/)

<img align="right" width="160px" src="https://raw.githubusercontent.com/xoofx/TurboXml/main/img/TurboXml.png">

TurboXml is a .NET library that provides a lightweight and fast [SAX - Simple API XML parser](https://en.wikipedia.org/wiki/Simple_API_for_XML) by using callbacks.

> This is the equivalent of `System.Xml.XmlReader` but faster with no allocations. 🚀

## ✨ Features 

- Should be slightly faster than `System.Xml.XmlReader`
- **Zero Allocation XML Parser**
  - Callbacks received `ReadOnlySpan<char>` for the parsed elements.
  - Parse from small to very large XML documents, without allocating!
- **Optimized with SIMD**
  - TurboXml is using some SIMD to improve parsing of large portions of XML documents.
- Provide **precise source location** of the XML elements parsed (to report warning/errors)
- Compatible with `net8.0+`
- NativeAOT ready

## 📃 User Guide

TurboXML is in the family of the [SAX parsers](https://en.wikipedia.org/wiki/Simple_API_for_XML) and so you need to implement the callbacks defined by [`IXmlReadHandler`](https://github.com/xoofx/TurboXml/blob/main/src/TurboXml/IXmlReadHandler.cs).

By default this handler implements empty interface methods that you can easily override:

```c#
var xml = "<?xml version=\"1.0\"?><root enabled=\"true\">Hello World!</root>";
var handler = new MyXmlHandler();
XmlParser.Parse(xml, ref handler);
// Will print:
//
// BeginTag(1:23): root
// Attribute(1:28)-(1:36): enabled="true"
// Content(1:43): Hello World!
// EndTag(1:57): root

struct MyXmlHandler : IXmlReadHandler
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
```
## 📊 Benchmarks

The solution contains 2 benchmarks:

- `BenchStream` that parses 240+ MSBuild xml files (targets and props) from the .NET 8 (or latest SDK) installed
- `BenchString` that parses the `Tiger.svg` in memory from a string.

In general, the advantages of `TurboXml` over `System.Xml.XmlReader`:

- It should be slightly faster - from 10% to 30% - or more, specially if tag names, attributes or even content are bigger than 8 consecutive characters by using SIMD instructions.
- It will make almost **zero allocations** - apart for the internal buffers used to pass data as `ReadOnlySpan<char>` back the the XML Handler.

### Stream Results

```
BenchmarkDotNet v0.13.12, Windows 11 (10.0.22631.3085/23H2/2023Update/SunValley3)
AMD Ryzen 9 7950X, 1 CPU, 32 logical and 16 physical cores
.NET SDK 8.0.101
  [Host]     : .NET 8.0.1 (8.0.123.58001), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
  DefaultJob : .NET 8.0.1 (8.0.123.58001), X64 RyuJIT AVX-512F+CD+BW+DQ+VL+VBMI
```

| Method                          | Mean     | Error     | StdDev    | Gen0     | Gen1    | Allocated  |
|-------------------------------- |---------:|----------:|----------:|---------:|--------:|-----------:|
| TurboXml - Stream               | 3.881 ms | 0.0151 ms | 0.0126 ms |        - |       - |   13.18 KB |
| System.Xml.XmlReader - Stream   | 4.409 ms | 0.0431 ms | 0.0382 ms | 375.0000 | 46.8750 | 6248.56 KB |

## String Results

| Method               | Mean     | Error    | StdDev   | Gen0    | Gen1   | Allocated |
|--------------------- |---------:|---------:|---------:|--------:|-------:|----------:|
| TurboXml             | 54.60 us | 0.535 us | 0.501 us |       - |      - |         - |
| System.Xml.XmlReader | 75.34 us | 0.381 us | 0.357 us | 11.5967 | 0.8545 |  194384 B |

## 🚨 XML Conformance and Known Limitations 

This parser is following the [Extensible Markup Language (XML) 1.0 (Fifth Edition)](https://www.w3.org/TR/xml/) and **should support any XML valid documents**, except for the known limitations described below:

- For simplicity of the implementation, this parser does not support DTD, custom entities and XML directives (`<!DOCTYPE ...>`). If you are looking for this, you should instead use `System.Xml.XmlReader`.
- This parser checks for well formed XML, matching begin and end tags and report an error if they are not matching
- This parser does not check for duplicated attributes.
  - It is the responsibility of the XML handler to implement such a check. The rationale is that the check can be performed more efficiently depending on user scenarios (e.g bit flags...etc.)

## 🏗️ Build

You need to install the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). Then from the root folder:

```console
$ dotnet build src -c Release
```

## 🪪 License

This software is released under the [BSD-2-Clause license](https://opensource.org/licenses/BSD-2-Clause). 

## 🤗 Author

Alexandre Mutel aka [xoofx](https://xoofx.github.io).
