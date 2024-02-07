using BenchmarkDotNet.Running;

namespace TurboXml.Bench;

internal class Program
{
    static void Main(string[] args)
        => BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args, null);
}