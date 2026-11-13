using System;
using System.Collections.Generic;
using BepInEx.Logging;

namespace BlockStoryMod
{
    // every mod logs under its own name in BepInEx/LogOutput.log, so grep the mod name and you see its whole story
    internal static class L
    {
        private static readonly ManualLogSource _s = Logger.CreateLogSource(typeof(L).Assembly.GetName().Name);
        private static readonly HashSet<string> _seen = new HashSet<string>();

        public static void Info(string m) => _s.LogInfo(m);
        public static void Warn(string m) => _s.LogWarning(m);

        // things in Update can fail 60 times a second. first time in full, then stay quiet
        public static void Err(string where, Exception e)
        {
            if (_seen.Add(where + e.GetType().Name + e.Message)) _s.LogError(where + ": " + e);
        }
    }
}
