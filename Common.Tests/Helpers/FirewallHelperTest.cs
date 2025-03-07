﻿using System;
using System.Collections.Generic;
using static Wokhan.WindowsFirewallNotifier.Common.Net.WFP.FirewallHelper;
using System.Linq;
using NetFwTypeLib;
using NUnit.Framework;
using Wokhan.WindowsFirewallNotifier.Console.Tests.NUnit;
using Wokhan.WindowsFirewallNotifier.Common.IO.Files;
using Wokhan.WindowsFirewallNotifier.Common.Net.WFP;
using Wokhan.WindowsFirewallNotifier.Common.Net.WFP.Rules;

namespace Wokhan.WindowsFirewallNotifier.Common.Helpers;

public class FirewallHelperTest : NUnitTestBase
{
    [Test, IntegrationTestCategory]
    public void TestGetMatchingRulesForEvent()
    {
        string exePath = @"C:\Windows\System32\svchost.exe";
        IEnumerable<Rule> results = FirewallHelper.GetMatchingRulesForEvent(pid: 0, path: exePath, target: "*", targetPort: "*", blockOnly: false);
        Assert.NotNull(results);
        Assert.True(results.ToList().Count >= 1, "Has no results or number of results does not match");
        foreach (Rule rule in results) {
            WriteDebugOutput($"{rule.Name}, {rule.RemoteAddresses}");
        }
    }

    [Test, IntegrationTestCategory]
    public void TestRuleMatchesEvent()
    {
        IEnumerable<Rule> ret = GetRules(AlsoGetInactive: false);
        string exePath = @"C:\Windows\System32\svchost.exe";
        const int PROF_ALL = (int)NET_FW_PROFILE_TYPE2_.NET_FW_PROFILE2_ALL;
        WriteDebugOutput($"{exePath}");
        int cntMatch = 0;
        foreach (Rule rule in ret)
        {
            bool matches = rule.MatchesEvent(currentProfile: PROF_ALL, appPkgId: null, service: "*", path: exePath, target: "*", remoteport: "*");
            if (matches)
            {
                string ruleFriendlyPath = String.IsNullOrWhiteSpace(rule.ApplicationName) ? rule.ApplicationName : PathResolver.ResolvePath(rule.ApplicationName);
                Assert.True(String.IsNullOrWhiteSpace(ruleFriendlyPath) || exePath.Equals(ruleFriendlyPath, StringComparison.OrdinalIgnoreCase));
                WriteDebugOutput($"match found={matches}, rule={rule.Name}");
                cntMatch++;
            }
        }
        Assert.True(cntMatch > 0);

    }
}
