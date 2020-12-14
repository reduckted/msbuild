using BenchmarkDotNet.Attributes;
using System.Collections.Generic;
using System;
using System.IO;
using System.Linq;
using System.Net;

namespace Microsoft.StringTools.Benchmark
{
    [MemoryDiagnoser]
    public class StringInterningCallback_Benchmark
    {
        private List<string> _stringsFromLogFile;

        [Params(
            "C#", "TRUE", "ResolveAssemblyReference",
            "12", "1234", "123456789012345678901234",
            "12345"
        )]
        public string StringToIntern { get; set; }

        [Params(
            @"C:\temp\MSBuild_strings_MSBuild.txt",
            @"C:\temp\MSBuild_strings_OrchardCore.txt"
        )]
        public string LogFilePath { get; set; }

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

        [GlobalSetup(Targets = new string[] { nameof(CallbackWithHardcodedStrings_Micro), nameof(CallbackWithHardcodedStrings_Replay) })]
        public void CallbackWithHardcodedStrings_Setup()
        {
            Strings.RegisterStringInterningCallback(TryMatchHardcodedStrings);
        }

        [GlobalCleanup(Targets = new string[] { nameof(CallbackWithHardcodedStrings_Micro), nameof(CallbackWithHardcodedStrings_Replay) })]
        public void CallbackWithHardcodedStrings_Cleanup()
        {
            Strings.UnregisterStringInterningCallback(TryMatchHardcodedStrings);
        }

        [Benchmark]
        public void CallbackWithHardcodedStrings_Micro()
        {
            Strings.TryIntern(StringToIntern);
        }

        [Benchmark]
        public void NoCallback_Micro()
        {
            Strings.TryIntern(StringToIntern);
        }

        [Benchmark]
        public void CallbackWithHardcodedStrings_Replay()
        {
            ReplayStringInterning();
        }

        [Benchmark]
        public void MacroNoCallback_Replay ()
        {
            ReplayStringInterning();
        }

        private void ReplayStringInterning()
        {
            if (_stringsFromLogFile == null)
            {
                // First-time setup.
                _stringsFromLogFile = new List<string>();
                using (StreamReader reader = new StreamReader(LogFilePath))
                {
                    while (reader.Peek() >= 0)
                    {
                        string encodedString = reader.ReadLine();
                        string s = WebUtility.UrlDecode(encodedString);
                        _stringsFromLogFile.Add(s);
                    }
                }

                Dictionary<string, int> stringsByOccurrence = new Dictionary<string, int>();
                foreach (string s in _stringsFromLogFile)
                {
                    if (!stringsByOccurrence.TryGetValue(s, out int occurrences))
                    {
                        occurrences = 0;
                    }
                    stringsByOccurrence[s] = occurrences + 1;
                }
                int counter = 0;
                foreach (var item in stringsByOccurrence.OrderByDescending(item => item.Value))
                {
                    counter++;
                    Console.WriteLine("### {0}. [{1}] \"{2}\"", counter, item.Value, item.Key);
                    if (counter > 10)
                    {
                        break;
                    }
                }
            }

            foreach (string s in _stringsFromLogFile)
            {
                Strings.TryIntern(s);
            }
        }
    }
}
