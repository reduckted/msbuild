// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Microsoft.StringTools
{
    /// <summary>
    /// Interns strings by holding weak references.
    /// </summary>
    internal sealed class OpportunisticIntern : IDisposable
    {
        /// <summary>
        /// The singleton instance of OpportunisticIntern.
        /// </summary>
        private static OpportunisticIntern _instance = new OpportunisticIntern();
        internal static OpportunisticIntern Instance => _instance;

        /// <summary>
        /// The interner implementation in use.
        /// </summary>
        private WeakStringCacheInterner _interner;

        private OpportunisticIntern()
        {
            _interner = new WeakStringCacheInterner(gatherStatistics: false);
        }

        /// <summary>
        /// Recreates the singleton instance based on the current environment (test only).
        /// </summary>
        internal static void ResetForTests()
        {
            _instance.Dispose();
            _instance = new OpportunisticIntern();
        }

        /// <summary>
        /// Turn on statistics gathering.
        /// </summary>
        internal void EnableStatisticsGathering()
        {
            _interner = new WeakStringCacheInterner(gatherStatistics: true);
        }

        /// <summary>
        /// Intern the given internable.
        /// </summary>
        internal string InternableToString(ref InternableString candidate)
        {
            if (candidate.Length == 0)
            {
                return string.Empty;
            }

            string result = _interner.InterningToString(ref candidate);
#if DEBUG
            string expected = candidate.ExpensiveConvertToString();
            if (!String.Equals(result, expected))
            {
                throw new InvalidOperationException(String.Format("Interned string {0} should have been {1}", result, expected));
            }
#endif
            return result;
        }

        /// <summary>
        /// Returns a string with human-readable statistics. Make sure to call <see cref="EnableStatisticsGathering"/> beforehand.
        /// </summary>
        internal string FormatStatistics()
        {
            return _interner.FormatStatistics();
        }

        /// <summary>
        /// Releases all strings from the underlying intern table.
        /// </summary>
        public void Dispose()
        {
            _interner.Dispose();
        }

        /// <summary>
        /// Implements interning based on a WeakStringCache.
        /// </summary>
        private class WeakStringCacheInterner : IDisposable
        {
            /// <summary>
            /// Enumerates the possible interning results.
            /// </summary>
            private enum InternResult
            {
                FoundInWeakStringCache,
                AddedToWeakStringCache,
            }

            /// <summary>
            /// The cache to keep strings in.
            /// </summary>
            private readonly WeakStringCache _weakStringCache = new WeakStringCache();

#region Statistics
            /// <summary>
            /// Whether or not to gather statistics.
            /// </summary>
            private readonly bool _gatherStatistics;

            /// <summary>
            /// Number of times the regular interning path found the string in the cache.
            /// </summary>
            private int _regularInternHits;

            /// <summary>
            /// Number of times the regular interning path added the string to the cache.
            /// </summary>
            private int _regularInternMisses;

            /// <summary>
            /// Total number of strings eliminated by interning.
            /// </summary>
            private int _internEliminatedStrings;

            /// <summary>
            /// Total number of chars eliminated across all strings.
            /// </summary>
            private int _internEliminatedChars;

            /// <summary>
            /// Maps strings that went though the interning path to the number of times they have been
            /// seen. The higher the number the better the payoff of interning.
            /// </summary>
            private Dictionary<string, int> _internCallCountsByString;

#endregion

            public WeakStringCacheInterner(bool gatherStatistics)
            {
                if (gatherStatistics)
                {
                    _internCallCountsByString = new Dictionary<string, int>();
                }
                _gatherStatistics = gatherStatistics;
            }

            /// <summary>
            /// Intern the given internable.
            /// </summary>
            public string InterningToString(ref InternableString candidate)
            {
                if (_gatherStatistics)
                {
                    return InternWithStatistics(ref candidate);
                }
                else
                {
                    TryIntern(ref candidate, out string result);
                    return result;
                }
            }

            /// <summary>
            /// Returns a string with human-readable statistics.
            /// </summary>
            public string FormatStatistics()
            {
                StringBuilder result = new StringBuilder(1024);

                string title = "Opportunistic Intern";

                if (_gatherStatistics)
                {
                    result.AppendLine(string.Format("\n{0}{1}{0}", new string('=', 41 - (title.Length / 2)), title));
                    result.AppendLine(string.Format("||{0,50}|{1,20:N0}|{2,8}|", "WeakStringCache Hits", _regularInternHits, "hits"));
                    result.AppendLine(string.Format("||{0,50}|{1,20:N0}|{2,8}|", "WeakStringCache Misses", _regularInternMisses, "misses"));
                    result.AppendLine(string.Format("||{0,50}|{1,20:N0}|{2,8}|", "Eliminated Strings*", _internEliminatedStrings, "strings"));
                    result.AppendLine(string.Format("||{0,50}|{1,20:N0}|{2,8}|", "Eliminated Chars", _internEliminatedChars, "chars"));
                    result.AppendLine(string.Format("||{0,50}|{1,20:N0}|{2,8}|", "Estimated Eliminated Bytes", _internEliminatedChars * 2, "bytes"));
                    result.AppendLine("Elimination assumes that strings provided were unique objects.");
                    result.AppendLine("|---------------------------------------------------------------------------------|");

                    IEnumerable<string> topMissingHardcodedString =
                        _internCallCountsByString
                        .OrderByDescending(kv => kv.Value * kv.Key.Length)
                        .Take(15)
                        .Where(kv => kv.Value > 1)
                        .Select(kv => string.Format(CultureInfo.InvariantCulture, "({1} instances x each {2} chars)\n{0}", kv.Key, kv.Value, kv.Key.Length));

                    result.AppendLine(string.Format("##########Top Missing Hardcoded Strings:  \n{0} ", string.Join("\n==============\n", topMissingHardcodedString.ToArray())));
                    result.AppendLine();

                    WeakStringCache.DebugInfo debugInfo = _weakStringCache.GetDebugInfo();
                    result.AppendLine("WeakStringCache statistics:");
                    result.AppendLine(string.Format("String count live/collected/total = {0}/{1}/{2}", debugInfo.LiveStringCount, debugInfo.CollectedStringCount, debugInfo.LiveStringCount + debugInfo.CollectedStringCount));
                }
                else
                {
                    result.Append(title);
                    result.AppendLine(" - EnableStatisticsGathering() has not been called");
                }

                return result.ToString();
            }

            /// <summary>
            /// Try to intern the string.
            /// The return value indicates the how the string was interned (if at all).
            /// </summary>
            private InternResult TryIntern(ref InternableString candidate, out string interned)
            {
                interned = _weakStringCache.GetOrCreateEntry(ref candidate, out bool cacheHit);
                return cacheHit ? InternResult.FoundInWeakStringCache : InternResult.AddedToWeakStringCache;
            }

            /// <summary>
            /// Version of Intern that gathers statistics
            /// </summary>
            private string InternWithStatistics(ref InternableString candidate)
            {
                lock (_internCallCountsByString)
                {
                    InternResult internResult = TryIntern(ref candidate, out string result);

                    switch (internResult)
                    {
                        case InternResult.FoundInWeakStringCache:
                            _regularInternHits++;
                            break;
                        case InternResult.AddedToWeakStringCache:
                            _regularInternMisses++;
                            break;
                    }

                    _internCallCountsByString.TryGetValue(result, out int priorCount);
                    _internCallCountsByString[result] = priorCount + 1;

                    if (!candidate.ReferenceEquals(result))
                    {
                        // Reference changed so 'candidate' is now released and should save memory.
                        _internEliminatedStrings++;
                        _internEliminatedChars += candidate.Length;
                    }

                    return result;
                }
            }

            /// <summary>
            /// Releases all strings from the underlying intern table.
            /// </summary>
            public void Dispose()
            {
                _weakStringCache.Dispose();
            }
        }
    }
}
