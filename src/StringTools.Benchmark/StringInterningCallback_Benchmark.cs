using BenchmarkDotNet.Attributes;

namespace Microsoft.StringTools.Benchmark
{
    [MemoryDiagnoser]
    public class StringInterningCallback_Benchmark
    {
        [Params(
            "C#", "TRUE", "ResolveAssemblyReference",
            "12", "1234", "123456789012345678901234",
            "12345"
        )]
        public string StringToIntern { get; set; }

        private static bool TryInternHardcodedString(ref InternableString candidate, string str, ref string interned)
        {
            if (candidate.StartsWithStringByOrdinalComparison(str))
            {
                interned = str;
                return true;
            }
            return false;
        }

        /// <summary>
        /// Try to match the candidate with small number of hardcoded interned string literals.
        /// The return value indicates how the string was interned (if at all).
        /// </summary>
        /// <returns>
        /// True if the candidate matched a hardcoded literal or should not be interned, false otherwise.
        /// </returns>
        private static bool TryMatchHardcodedStrings(ref InternableString candidate, out string interned)
        {
            int length = candidate.Length;
            interned = null;

            // Each of the hard-coded small strings below showed up in a profile run with considerable duplication in memory.
            if (length == 2)
            {
                if (candidate[1] == '#')
                {
                    if (candidate[0] == 'C')
                    {
                        interned = "C#";
                        return true;
                    }

                    if (candidate[0] == 'F')
                    {
                        interned = "F#";
                        return true;
                    }
                }

                if (candidate[0] == 'V' && candidate[1] == 'B')
                {
                    interned = "VB";
                    return true;
                }
            }
            else if (length == 4)
            {
                if (TryInternHardcodedString(ref candidate, "TRUE", ref interned) ||
                    TryInternHardcodedString(ref candidate, "True", ref interned) ||
                    TryInternHardcodedString(ref candidate, "Copy", ref interned) ||
                    TryInternHardcodedString(ref candidate, "true", ref interned) ||
                    TryInternHardcodedString(ref candidate, "v4.0", ref interned))
                {
                    return true;
                }
            }
            else if (length == 5)
            {
                if (TryInternHardcodedString(ref candidate, "FALSE", ref interned) ||
                    TryInternHardcodedString(ref candidate, "false", ref interned) ||
                    TryInternHardcodedString(ref candidate, "Debug", ref interned) ||
                    TryInternHardcodedString(ref candidate, "Build", ref interned) ||
                    TryInternHardcodedString(ref candidate, "Win32", ref interned))
                {
                    return true;
                }
            }
            else if (length == 6)
            {
                if (TryInternHardcodedString(ref candidate, "''!=''", ref interned) ||
                    TryInternHardcodedString(ref candidate, "AnyCPU", ref interned))
                {
                    return true;
                }
            }
            else if (length == 7)
            {
                if (TryInternHardcodedString(ref candidate, "Library", ref interned) ||
                    TryInternHardcodedString(ref candidate, "MSBuild", ref interned) ||
                    TryInternHardcodedString(ref candidate, "Release", ref interned))
                {
                    return true;
                }
            }
            else if (length == 24)
            {
                if (TryInternHardcodedString(ref candidate, "ResolveAssemblyReference", ref interned))
                {
                    return true;
                }
            }
            return false;
        }

        [GlobalSetup(Target = nameof(CallbackWithHardcodedStrings))]
        public void CallbackWithHardcodedStrings_Setup()
        {
            Strings.RegisterStringInterningCallback(TryMatchHardcodedStrings);
        }

        [GlobalCleanup(Target = nameof(CallbackWithHardcodedStrings))]
        public void CallbackWithHardcodedStrings_Cleanup()
        {
            Strings.UnregisterStringInterningCallback(TryMatchHardcodedStrings);
        }

        [Benchmark]
        public void CallbackWithHardcodedStrings()
        {
            Strings.TryIntern(StringToIntern);
        }

        [Benchmark]
        public void NoCallback()
        {
            Strings.TryIntern(StringToIntern);
        }
    }
}
