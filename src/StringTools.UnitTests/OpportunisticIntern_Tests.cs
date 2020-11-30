// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NET35_UNITTEST
extern alias StringToolsNet35;
#endif

using Shouldly;
using Xunit;

#if NET35_UNITTEST
using StringToolsNet35::System;
using StringToolsNet35::Microsoft.StringTools;
#else
using System;
#endif

namespace Microsoft.StringTools.Tests
{
    public class OpportunisticInternTestBase
    {
        private static bool IsInternable(ref InternableString internable)
        {
            string i1 = OpportunisticIntern.Instance.InternableToString(ref internable);
            string i2 = OpportunisticIntern.Instance.InternableToString(ref internable);
            i1.ShouldBe(i2); // No matter what, the same string value should return.
            return System.Object.ReferenceEquals(i1, i2);
        }

        private static void AssertInternable(ref InternableString internable)
        {
            IsInternable(ref internable).ShouldBeTrue();
        }

        private static string AssertInternable(char[] ch, int startIndex, int count)
        {
            var target = new InternableString(ch.AsSpan(startIndex, count));
            AssertInternable(ref target);
            target.Length.ShouldBe(count);

            return target.ExpensiveConvertToString();
        }

        private static void AssertInternable(string value)
        {
            string result = AssertInternable(value.ToCharArray(), 0, value.ToCharArray().Length);
            result.ShouldBe(value);
        }

        private static void AssertNotInternable(InternableString internable)
        {
            IsInternable(ref internable).ShouldBeFalse();
        }

        private static void AssertNotInternable(char[] ch)
        {
            AssertNotInternable(new InternableString(ch.AsSpan(0, ch.Length)));
        }

        protected static void AssertNotInternable(string value)
        {
            AssertNotInternable(value.ToCharArray());
        }

        /// <summary>
        /// Test interning segment of char array
        /// </summary>
        [Fact]
        public void SubArray()
        {
            var result = AssertInternable(new char[] { 'a', 't', 'r', 'u', 'e' }, 1, 4);
            result.ShouldBe("true");
        }

        /// <summary>
        /// Test interning segment of char array
        /// </summary>
        [Fact]
        public void SubArray2()
        {
            var result = AssertInternable(new char[] { 'a', 't', 'r', 'u', 'e', 'x' }, 1, 4);
            result.ShouldBe("true");
        }

        /// <summary>
        /// This is the list of hard-coded interns. They should report interned even though they are too small for normal interning.
        /// </summary>
        [Fact]
        public void KnownInternableTinyStrings()
        {
            AssertInternable("C#");
            AssertInternable("F#");
            AssertInternable("VB");
            AssertInternable("True");
            AssertInternable("TRUE");
            AssertInternable("Copy");
            AssertInternable("v4.0");
            AssertInternable("true");
            AssertInternable("FALSE");
            AssertInternable("false");
            AssertInternable("Debug");
            AssertInternable("Build");
            AssertInternable("''!=''");
            AssertInternable("AnyCPU");
            AssertInternable("Library");
            AssertInternable("MSBuild");
            AssertInternable("Release");
            AssertInternable("ResolveAssemblyReference");
        }

        /// <summary>
        /// Test a set of strings that are similar to each other
        /// </summary>
        [Fact]
        public void InternableDifferingOnlyByNthCharacter()
        {
            string test = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ01234567890!@#$%^&*()_+ABCDEFGHIJKLMNOPQRSTUVabcdefghijklmnopqrstuvwxyz0150";
            for (int i = 0; i < test.Length; ++i)
            {
                string mutated = test.Substring(0, i) + " " + test.Substring(i + 1);
                AssertInternable(mutated);
            }
        }

        /// <summary>
        /// Test The empty string
        /// </summary>
        [Fact]
        public void StringDotEmpty()
        {
            AssertInternable(string.Empty);
        }

        /// <summary>
        /// Test an empty string.
        /// </summary>
        [Fact]
        public void DoubleDoubleQuotes()
        {
            AssertInternable("");
        }
    }
}
