using BenchmarkDotNet.Running;

namespace Microsoft.StringTools.Benchmark
{
    public class Program
    {
        public static void Main(string[] args)
        {
            //var b = new SpanBasedStringBuilder_Benchmark();
            //b.NumSubstrings = 4;
            //b.SubstringLengths = 128;
            //b.GlobalSetup();

            //b.SpanBasedToString();
            //b.SpanBasedToString();

            //BenchmarkRunner.Run<SpanBasedStringBuilder_Benchmark>();
            BenchmarkRunner.Run<StringInterningCallback_Benchmark>();
        }
    }
}
