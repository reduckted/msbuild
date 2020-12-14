// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#if NET35_UNITTEST
extern alias StringToolsNet35;
#endif

using System;

using Shouldly;
using Xunit;

#if NET35_UNITTEST
using StringToolsNet35::Microsoft.StringTools;
using Shouldly.Configuration;
#else
using Microsoft.StringTools;
#endif

namespace Microsoft.StringTools.Tests
{
    public class StringTools_Tests
    {
        [Fact]
        public void CreatesDiagnosticReport()
        {
            string statisticsNotEnabledString = "EnableStatisticsGathering() has not been called";

            Strings.CreateDiagnosticReport().ShouldContain(statisticsNotEnabledString);

            Strings.EnableDiagnostics();
            string report = Strings.CreateDiagnosticReport();

            report.ShouldNotContain(statisticsNotEnabledString);
            report.ShouldContain("Eliminated Strings");
        }
    }
}
