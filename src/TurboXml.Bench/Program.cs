using System.Diagnostics;
using System.Xml;
using BenchmarkDotNet.Running;

namespace TurboXml.Bench;

internal class Program
{
    static void Main(string[] args)
    {
        //var bench = new BenchString();
        //var clock = Stopwatch.StartNew();
        //const int count = 60000;
        //for (int i = 0; i < count; i++)
        //{
        //    bench.BenchTurboXml();
        //    //bench.BenchTurboXmlSimdDisabled();
        //    //bench.BenchXmlReaderStream();
        //}
        //var elapsed = clock.Elapsed;
        //Console.WriteLine($"Elapsed: {elapsed.TotalMilliseconds / count}ms");

        //var bench = new BenchStream();
        //var clock = Stopwatch.StartNew();
        //const int count = 2000;
        //for (int i = 0; i < count; i++)
        //{
        //    bench.BenchTurboXmlStream();
        //    //bench.BenchTurboXmlStreamSimdDisabled();
        //    //bench.BenchXmlReaderStream();
        //}
        //var elapsed = clock.Elapsed;
        //Console.WriteLine($"Elapsed: {elapsed.TotalMilliseconds / count}ms");
        //bench.Cleanup();


        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, null);
    }

}