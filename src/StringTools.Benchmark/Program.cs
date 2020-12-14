using BenchmarkDotNet.Running;

namespace Microsoft.StringTools.Benchmark
{
    public class Program
    {
        public static void Main(string[] args)
        {
            BenchmarkRunner.Run<SpanBasedStringBuilder_Benchmark>();
        }
    }
}
