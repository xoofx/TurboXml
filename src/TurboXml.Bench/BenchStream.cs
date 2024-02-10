using System.Xml;
using BenchmarkDotNet.Attributes;
using TurboXml.Tests;

namespace TurboXml.Bench;

/// <summary>
/// Benchmarks for the Stream parsing
/// </summary>
/// <remarks>
/// Use the latest SDK installed to find and parse all the XML MSBuild targets and props files.
/// </remarks>
[MemoryDiagnoser]
public class BenchStream
{
    private readonly List<Stream> _streams;

    public BenchStream()
    {
        // Allocate all streams before to avoid polluting the benchmark
        _streams = new List<Stream>();
        foreach (var file in DotNetHelper.GetTargetsAndPropsFromLatestSdkInstalled())
        {
            _streams.Add(new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read, 0));
        }
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (var stream in _streams)
        {
            stream.Dispose();
        }
    }

    [Benchmark(Description = "TurboXml - Stream")]
    public long BenchTurboXmlStream()
    {
        long length = 0;
        foreach (var stream in _streams)
        {
            stream.Position = 0;
            var parser = new BenchString.DefaultReadHandler();
            XmlParser.Parse(stream, ref parser);
            length += parser.Length;
        }

        return length;
    }

    [Benchmark(Description = "System.Xml.XmlReader - Stream")]
    public long BenchXmlReaderStream()
    {
        long length = 0;
        foreach (var stream in _streams)
        {
            stream.Position = 0;
            var reader = new XmlTextReader(stream);
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
        }

        return length;
    }
}